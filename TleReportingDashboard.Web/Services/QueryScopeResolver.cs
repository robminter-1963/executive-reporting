using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public sealed class QueryScopeResolver : IQueryScopeResolver
{
    private readonly IAdminService _admins;
    private readonly IUserManagementService _users;
    private readonly IRoleService _roles;
    private readonly ICustomPrimaryTableService _primaryTables;
    private readonly ITeamSourceService _teamSources;
    private readonly ILogger<QueryScopeResolver> _logger;

    public QueryScopeResolver(IAdminService admins,
                              IUserManagementService users,
                              IRoleService roles,
                              ICustomPrimaryTableService primaryTables,
                              ITeamSourceService teamSources,
                              ILogger<QueryScopeResolver> logger)
    {
        _admins = admins;
        _users = users;
        _roles = roles;
        _primaryTables = primaryTables;
        _teamSources = teamSources;
        _logger = logger;
    }

    public async Task<QueryScopingInfo?> ResolveAsync(
        string? userEmail,
        Guid? connectionId,
        string? primaryTable,
        CancellationToken ct = default)
    {
        // Unauthenticated / unknown user → let the caller handle auth; not
        // our job to filter. Admins bypass scoping entirely.
        if (string.IsNullOrWhiteSpace(userEmail)) return null;
        if (_admins.IsAdmin(userEmail)) return null;

        // Non-admin: look up the user's role. If missing or scope = 'all',
        // no scoping applies.
        var user = await _users.GetByEmailAsync(userEmail, ct);
        if (user?.RoleId is not Guid roleId) return null;
        var role = await _roles.GetByIdAsync(roleId, ct);
        if (role is null) return null;

        // Team-scope diverges substantially from self-scope — it doesn't
        // need a per-role owner field on the primary, but does need a team
        // list, a members_sql, and a team_type → column map. Branch early
        // and let ResolveTeamScopeAsync handle the whole flow.
        if (role.ScopeRule == ScopeRules.Team)
        {
            return await ResolveTeamScopeAsync(userEmail, role.Name, connectionId, primaryTable, ct);
        }

        if (role.ScopeRule != ScopeRules.Self) return null;

        // At this point the user is definitely self-scoped. Anything that
        // stops us from resolving the predicate safely = force zero rows.
        // Each branch logs + attaches a human-readable Reason the debug
        // dialog surfaces so admins see "why zero rows" without tailing logs.
        if (connectionId is not Guid cid)
        {
            var msg = $"Role '{role.Name}' is self-scoped but the report has no connection set.";
            _logger.LogInformation("Self-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // Find the primary table record. Primary tables are connection-
        // scoped and stored by (table, alias); parse the request string
        // to match both sides.
        var (tableName, alias) = PrimaryTableRef.Parse(primaryTable);
        var primariesForConn = await _primaryTables.GetByConnectionAsync(cid, ct);
        var primaryRec = primariesForConn.FirstOrDefault(p =>
            string.Equals(p.TableName, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Alias ?? string.Empty, alias ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (primaryRec is null)
        {
            var msg = $"Primary table '{primaryTable}' isn't registered in this connection's Table Aliases. Add it in Admin → DB Connections → Table Aliases, then edit it to add a role-scoped owner field for '{role.Name}'.";
            _logger.LogInformation("Self-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // Per-role owner column — the Processor, Loan Officer, etc. each
        // have their own column on the primary. No entry for this user's
        // role = they're not scoped on this primary at all, which in a
        // 'self' role means zero rows.
        var ownerFieldId = await _primaryTables.ResolveOwnerFieldForRoleAsync(primaryRec.Id, roleId, ct);
        if (string.IsNullOrEmpty(ownerFieldId))
        {
            var msg = $"Primary table '{primaryTable}' has no owner field mapped for role '{role.Name}'. Admin → DB Connections → Table Aliases → edit this primary and add a row under 'Role-scoped owner fields'.";
            _logger.LogInformation("Self-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // Look up the user's external id for this connection.
        var externalId = await _users.GetExternalUserIdAsync(userEmail, cid, ct);
        if (string.IsNullOrEmpty(externalId))
        {
            var msg = $"User '{userEmail}' has no LOS/CRM login on this connection. Admin → Users → Companies → set the LOS/CRM login for this connection.";
            _logger.LogInformation("Self-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        return new QueryScopingInfo
        {
            OwnerFieldId = ownerFieldId,
            ExternalUserId = externalId,
            Reason = $"Self-scoped for role '{role.Name}' — filtering on {ownerFieldId}."
        };
    }

    // Team-scope flow. Distinguishes two failure modes:
    //   * "Connection isn't set up for team scope" (no Team Builder config)
    //     → return null = no predicate emitted = behaves like 'all'.
    //     A team-scoped role hitting a connection that doesn't model teams
    //     shouldn't lock the user out of every row — it should just fall
    //     through. The admin only needs to configure Team Builder where
    //     team-scope filtering actually matters.
    //   * "Connection IS set up but the user's data is wrong" (no team
    //     assignments, unmapped team type, stale assignment) → ForceNoMatch
    //     = fail closed. Each case sets a specific Reason so admins can see
    //     which piece is missing in the "Show query" debug dialog.
    private async Task<QueryScopingInfo?> ResolveTeamScopeAsync(
        string userEmail, string roleName, Guid? connectionId, string? primaryTable, CancellationToken ct)
    {
        if (connectionId is not Guid cid)
        {
            var msg = $"Role '{roleName}' is team-scoped but the report has no connection set.";
            _logger.LogInformation("Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // Check team source config FIRST — if the connection has no Team
        // Builder configuration at all, treat team scope as not applicable
        // for this connection and pass through with no predicate.
        var cfg = await _teamSources.GetConfigAsync(cid, ct);
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.MembersSql))
        {
            _logger.LogInformation(
                "Team-scope resolver: no team source configured for connection {ConnectionId} — team scope not applied for {Email}.",
                cid, userEmail);
            return null;
        }

        var (tableName, alias) = PrimaryTableRef.Parse(primaryTable);
        // The owner columns are on the primary; we need a name to qualify
        // them with in the emitted SQL. Prefer the alias, fall back to the
        // bare table name (sans schema) since joining-back-to-schema.name
        // in a WHERE clause works but reads worse.
        var primaryAlias = !string.IsNullOrWhiteSpace(alias) ? alias! : BareTableName(tableName);
        if (string.IsNullOrWhiteSpace(primaryAlias))
        {
            var msg = $"Primary table '{primaryTable}' couldn't be resolved to a SQL alias for the team-scope predicate.";
            _logger.LogInformation("Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // User's team assignments — zero means the user has no team, which
        // for team scope means no rows (fail-closed). Admins fix by assigning
        // teams in Admin → Users → edit.
        var assignments = await _users.GetUserTeamsAsync(userEmail, ct);
        var forThisConn = assignments.Where(a => a.ConnectionId == cid).ToList();
        if (forThisConn.Count == 0)
        {
            var msg = $"User '{userEmail}' has no team assignments on this connection. Admin → Users → edit → Teams.";
            _logger.LogInformation("Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        // Resolve each user team to its team_type (via live teams SQL), then
        // its owner column (via RPT_team_type_columns). A team whose type
        // has no column mapping is a config gap and fails closed — better
        // than silently dropping the team from the OR'd predicate and
        // showing the user fewer rows than they expect.
        List<TeamRecord> teams;
        try
        {
            teams = await _teamSources.QueryTeamsAsync(cid, ct);
        }
        catch (Exception ex)
        {
            var msg = $"Couldn't run the connection's teams SQL to resolve types: {ex.Message}";
            _logger.LogWarning(ex, "Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }
        var teamById = teams.ToDictionary(t => t.TeamId);
        var typeColumns = await _teamSources.GetTypeColumnsAsync(cid, ct);

        var entries = new List<TeamScopeEntry>();
        var unmappedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingTeams = new List<int>();
        foreach (var a in forThisConn)
        {
            if (!teamById.TryGetValue(a.TeamId, out var teamRec))
            {
                // Assigned team no longer returned by the live teams_sql —
                // either the source row was removed or the SQL was changed.
                // Fail closed.
                missingTeams.Add(a.TeamId);
                continue;
            }
            var type = teamRec.TeamType ?? string.Empty;
            if (!typeColumns.TryGetValue(type, out var ownerColumn))
            {
                unmappedTypes.Add(type);
                continue;
            }
            entries.Add(new TeamScopeEntry(a.TeamId, ownerColumn));
        }

        if (missingTeams.Count > 0)
        {
            var msg = $"Team assignment(s) {string.Join(", ", missingTeams)} for this user are no longer returned by the connection's teams SQL. Re-check Admin → Team Builder or Admin → Users.";
            _logger.LogInformation("Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }
        if (unmappedTypes.Count > 0)
        {
            var msg = $"Team type(s) {string.Join(", ", unmappedTypes)} have no owner-column mapping on this connection. Admin → Team Builder → 'Team type → owner column' editor.";
            _logger.LogInformation("Team-scope resolver: {Msg}", msg);
            return new QueryScopingInfo { ForceNoMatch = true, Reason = msg };
        }

        return new QueryScopingInfo
        {
            TeamScope = new TeamScopingInfo
            {
                MembersSql = cfg.MembersSql!,
                PrimaryAlias = primaryAlias,
                Teams = entries
            },
            Reason = $"Team-scoped for role '{roleName}' — filtering on {entries.Count} team(s): "
                     + string.Join(", ", entries.Select(e => $"{e.TeamId}@{e.OwnerColumn}"))
        };
    }

    // Strip schema prefix from "schema.table" — the returned string is used
    // purely to qualify a column in a WHERE clause (e.g., "MYTABLE.COL"),
    // not to reference the table itself. Handles bracketed identifiers by
    // unwrapping them so the emitted SQL stays parseable either way.
    private static string BareTableName(string? rawTable)
    {
        if (string.IsNullOrWhiteSpace(rawTable)) return string.Empty;
        var dot = rawTable.LastIndexOf('.');
        var name = dot >= 0 ? rawTable[(dot + 1)..] : rawTable;
        return name.Trim('[', ']', ' ');
    }
}
