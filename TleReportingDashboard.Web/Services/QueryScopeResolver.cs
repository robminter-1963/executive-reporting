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
        if (connectionId is not Guid cid)
        {
            _logger.LogInformation("Self-scope resolver: user {Email} has no connection on request → ForceNoMatch", userEmail);
            return new QueryScopingInfo { ForceNoMatch = true };
        }

        // Find the primary table's owner_field_id. Primary tables are
        // connection-scoped and stored by (table, alias); parse the request
        // string to match both sides.
        var (tableName, alias) = PrimaryTableRef.Parse(primaryTable);
        var primariesForConn = await _primaryTables.GetByConnectionAsync(cid, ct);
        var primaryRec = primariesForConn.FirstOrDefault(p =>
            string.Equals(p.TableName, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Alias ?? string.Empty, alias ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (primaryRec?.OwnerFieldId is not { Length: > 0 } ownerFieldId)
        {
            _logger.LogInformation("Self-scope resolver: primary table {Primary} on connection {Cid} has no owner_field_id → ForceNoMatch", primaryTable, cid);
            return new QueryScopingInfo { ForceNoMatch = true };
        }

        // Look up the user's external id for this connection.
        var externalId = await _users.GetExternalUserIdAsync(userEmail, cid, ct);
        if (string.IsNullOrEmpty(externalId))
        {
            _logger.LogInformation("Self-scope resolver: user {Email} has no external_user_id for connection {Cid} → ForceNoMatch", userEmail, cid);
            return new QueryScopingInfo { ForceNoMatch = true };
        }

        return new QueryScopingInfo
        {
            OwnerFieldId = ownerFieldId,
            ExternalUserId = externalId
        };
    }
}
