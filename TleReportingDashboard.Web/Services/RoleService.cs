using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class RoleService : IRoleService
{
    private readonly string _connStr;
    private readonly ILogger<RoleService> _logger;

    public RoleService(IConfiguration configuration, ILogger<RoleService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for RoleService.");
        _logger = logger;
    }

    // Shared SELECT list — every reader pulls scope_rule too.
    private const string RoleSelectSql =
        "SELECT id, name, description, is_active, sort_order, created_at, created_by, updated_at, scope_rule " +
        "FROM EMPOWER.RPT_roles";

    public async Task<List<RoleRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = new List<RoleRecord>();
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            RoleSelectSql + " ORDER BY is_active DESC, sort_order, name;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Read(reader));
        }
        return rows;
    }

    public async Task<RoleRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(RoleSelectSql + " WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<RoleRecord?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(RoleSelectSql + " WHERE name = @name;", conn);
        cmd.Parameters.Add(new SqlParameter("@name", name));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<RoleRecord> CreateAsync(string name, string? description, string scopeRule,
                                                string? createdBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required.", nameof(name));
        if (!ScopeRules.IsValid(scopeRule))
            throw new ArgumentException($"scopeRule must be one of: {string.Join(", ", ScopeRules.Allowed)}.", nameof(scopeRule));

        var id = Guid.NewGuid();
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_roles (id, name, description, sort_order, scope_rule, created_by)
            VALUES (
                @id, @name, @description,
                ISNULL((SELECT MAX(sort_order) + 1 FROM EMPOWER.RPT_roles), 0),
                @scope, @createdBy);", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@name", name.Trim()));
        cmd.Parameters.Add(new SqlParameter("@description", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@scope", scopeRule));
        cmd.Parameters.Add(new SqlParameter("@createdBy", (object?)createdBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Role created: {Name} scope={Scope} by {CreatedBy}", name, scopeRule, createdBy ?? "unknown");
        return (await GetByIdAsync(id, ct))!;
    }

    public async Task UpdateAsync(Guid id, string name, string? description, string scopeRule, bool isActive,
                                   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required.", nameof(name));
        if (!ScopeRules.IsValid(scopeRule))
            throw new ArgumentException($"scopeRule must be one of: {string.Join(", ", ScopeRules.Allowed)}.", nameof(scopeRule));

        // Administrator is always scope='all'. Silently coerce on the way
        // in so a rogue PATCH (or a future UI change that forgets to
        // disable the scope dropdown on the admin row) can't lock admins
        // into self-only scoping. Name match is case-insensitive for
        // rename safety.
        var existing = await GetByIdAsync(id, ct);
        if (existing is not null &&
            string.Equals(existing.Name, RoleRecord.AdministratorName, StringComparison.OrdinalIgnoreCase))
        {
            scopeRule = ScopeRules.All;
        }

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_roles
               SET name        = @name,
                   description = @description,
                   scope_rule  = @scope,
                   is_active   = @isActive,
                   updated_at  = SYSUTCDATETIME()
             WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@name", name.Trim()));
        cmd.Parameters.Add(new SqlParameter("@description", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@scope", scopeRule));
        cmd.Parameters.Add(new SqlParameter("@isActive", isActive));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Role updated: {Id} ({Name}) scope={Scope} active={IsActive}", id, name, scopeRule, isActive);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // FK on RPT_users.role_id is ON DELETE SET NULL — affected users
        // get their role cleared rather than the delete being blocked.
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_roles WHERE id = @id", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Role deleted: {Id}", id);
    }

    public async Task UpdateSortOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            await using var cmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_roles
                   SET sort_order = @order,
                       updated_at = SYSUTCDATETIME()
                 WHERE id = @id;", conn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", orderedIds[i]));
            cmd.Parameters.Add(new SqlParameter("@order", i));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Role sort order updated: {Count} rows", orderedIds.Count);
    }

    private static RoleRecord Read(SqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetBoolean(3),
        r.GetInt32(4),
        r.GetDateTime(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetDateTime(7))
    {
        ScopeRule = r.GetString(8)
    };
}
