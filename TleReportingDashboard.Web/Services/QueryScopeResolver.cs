using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public sealed class QueryScopeResolver : IQueryScopeResolver
{
    private readonly IAdminService _admins;
    private readonly IUserManagementService _users;
    private readonly IRoleService _roles;
    private readonly ICustomPrimaryTableService _primaryTables;
    private readonly ILogger<QueryScopeResolver> _logger;

    public QueryScopeResolver(IAdminService admins,
                              IUserManagementService users,
                              IRoleService roles,
                              ICustomPrimaryTableService primaryTables,
                              ILogger<QueryScopeResolver> logger)
    {
        _admins = admins;
        _users = users;
        _roles = roles;
        _primaryTables = primaryTables;
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
        if (role is null || role.ScopeRule != ScopeRules.Self) return null;

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
}
