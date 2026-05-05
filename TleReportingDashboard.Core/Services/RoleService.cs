using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class RoleService : IRoleService
{
    private readonly string _connStr;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<RoleService> _logger;

    public RoleService(
        IConfiguration configuration,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<RoleService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for RoleService.");
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

    // Shared SELECT list — every reader pulls scope_rule + admin_sections.
    private const string RoleSelectSql =
        "SELECT id, name, description, is_active, sort_order, created_at, created_by, updated_at, scope_rule, admin_sections " +
        "FROM EMPOWER.RPT_roles";

    public Task<List<RoleRecord>> GetAllAsync(CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("RoleService", "All"),
            async () =>
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
            },
            bypass: _editorMode.IsActive);

    public Task<RoleRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("RoleService", "ById", id),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(RoleSelectSql + " WHERE id = @id;", conn);
                cmd.Parameters.Add(new SqlParameter("@id", id));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct) ? Read(reader) : null;
            },
            bypass: _editorMode.IsActive);

    public Task<RoleRecord?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult<RoleRecord?>(null);
        return _cache.GetOrAddAsync(
            ConfigDbCache.Key("RoleService", "ByName", name),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(RoleSelectSql + " WHERE name = @name;", conn);
                cmd.Parameters.Add(new SqlParameter("@name", name));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct) ? Read(reader) : null;
            },
            bypass: _editorMode.IsActive);
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
        // Wrap the INSERT and the alphabetic re-sort in one transaction
        // so the list either ends up consistent (new role inserted +
        // every sort_order renumbered to alphabetic position with
        // Administrator pinned first) or rolls back together. Without
        // this, an admin who adds three roles in a row would see the
        // list grow disordered each time.
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_roles (id, name, description, sort_order, scope_rule, created_by)
            VALUES (
                @id, @name, @description,
                ISNULL((SELECT MAX(sort_order) + 1 FROM EMPOWER.RPT_roles), 0),
                @scope, @createdBy);", conn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@id", id));
            cmd.Parameters.Add(new SqlParameter("@name", name.Trim()));
            cmd.Parameters.Add(new SqlParameter("@description", (object?)description ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@scope", scopeRule));
            cmd.Parameters.Add(new SqlParameter("@createdBy", (object?)createdBy ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await RenumberSortOrderAlphabeticallyAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        _cache.Invalidate("RoleService:");

        _logger.LogInformation("Role created: {Name} scope={Scope} by {CreatedBy}", name, scopeRule, createdBy ?? "unknown");
        return (await GetByIdAsync(id, ct))!;
    }

    // Renumbers every role's sort_order to its alphabetic position with
    // Administrator pinned at 0. Called from CreateAsync so a freshly-
    // added role lands in the right place without an admin having to
    // click Move Up repeatedly. UpdateSortOrderAsync (the manual Move
    // Up / Move Down handler) overrides this on demand — auto-sort
    // only fires on Create.
    private async Task RenumberSortOrderAlphabeticallyAsync(
        SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using (var read = new SqlCommand(@"
            SELECT id FROM EMPOWER.RPT_roles
            ORDER BY CASE WHEN name = @adminName THEN 0 ELSE 1 END, name;", conn, tx))
        {
            read.Parameters.Add(new SqlParameter("@adminName", RoleRecord.AdministratorName));
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetGuid(0));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            await using var cmd = new SqlCommand(@"
                UPDATE EMPOWER.RPT_roles
                   SET sort_order = @order,
                       updated_at = SYSUTCDATETIME()
                 WHERE id = @id;", conn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", ids[i]));
            cmd.Parameters.Add(new SqlParameter("@order", i));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task UpdateAsync(Guid id, string name, string? description, string scopeRule, bool isActive,
                                   IReadOnlyList<string>? adminSections,
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
        var isAdminRole = existing is not null
            && string.Equals(existing.Name, RoleRecord.AdministratorName, StringComparison.OrdinalIgnoreCase);
        if (isAdminRole)
        {
            scopeRule = ScopeRules.All;
        }

        // Built-in roles (Administrator, System Support) can't be renamed.
        // The Roles tab disables the name field for these rows; this is
        // the defense-in-depth guard so a forged request hitting the
        // service directly still can't slip a rename through.
        if (existing is not null
            && RoleRecord.IsBuiltInName(existing.Name)
            && !string.Equals(existing.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The '{existing.Name}' role is built-in and can't be renamed.");
        }

        // Filter to known section keys + serialize. Null/empty list →
        // store NULL so the column reads as "no admin access" by
        // default. Administrator row always stores NULL since it
        // bypasses the column entirely at access-check time; saving a
        // list there would imply a finite scope and mislead anyone
        // reading the row.
        string? adminSectionsJson = null;
        if (!isAdminRole && adminSections is { Count: > 0 })
        {
            var filtered = adminSections.Where(AdminSections.IsValid).Distinct().ToList();
            if (filtered.Count > 0)
                adminSectionsJson = System.Text.Json.JsonSerializer.Serialize(filtered);
        }

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_roles
               SET name           = @name,
                   description    = @description,
                   scope_rule     = @scope,
                   is_active      = @isActive,
                   admin_sections = @adminSections,
                   updated_at     = SYSUTCDATETIME()
             WHERE id = @id;", conn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@id", id));
            cmd.Parameters.Add(new SqlParameter("@name", name.Trim()));
            cmd.Parameters.Add(new SqlParameter("@description", (object?)description ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@scope", scopeRule));
            cmd.Parameters.Add(new SqlParameter("@isActive", isActive));
            cmd.Parameters.Add(new SqlParameter("@adminSections", (object?)adminSectionsJson ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Re-alphabetize after the rename so the row lands in the right
        // slot. Without this, renaming "Sales" → "Account Manager" leaves
        // the row sitting where "Sales" used to be in sort_order. Wrapped
        // in the same tx as the UPDATE so a partial save doesn't leave the
        // ordering half-applied. Manual Move Up / Move Down via
        // UpdateSortOrderAsync still overrides this on demand.
        await RenumberSortOrderAlphabeticallyAsync(conn, tx, ct);

        await tx.CommitAsync(ct);
        _cache.Invalidate("RoleService:");

        _logger.LogInformation("Role updated: {Id} ({Name}) scope={Scope} active={IsActive}", id, name, scopeRule, isActive);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Built-in roles (Administrator, System Support) can't be deleted.
        // UI hides the delete button for them; this is the defense-in-
        // depth guard so a direct service call still refuses.
        var existing = await GetByIdAsync(id, ct);
        if (existing is not null && RoleRecord.IsBuiltInName(existing.Name))
        {
            throw new InvalidOperationException(
                $"The '{existing.Name}' role is built-in and can't be deleted.");
        }

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // FK on RPT_users.role_id is ON DELETE SET NULL — affected users
        // get their role cleared rather than the delete being blocked.
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_roles WHERE id = @id", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("RoleService:");
        // Users may have had their role cleared (FK ON DELETE SET NULL) —
        // drop user caches too so the change is visible.
        _cache.Invalidate("UserManagementService:");

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
        _cache.Invalidate("RoleService:");
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
        ScopeRule = r.GetString(8),
        AdminSections = r.IsDBNull(9) ? null : ParseSectionsJson(r.GetString(9))
    };

    // Parses the JSON array stored in admin_sections. Filters to known
    // section keys so a stale entry from a previous catalog version
    // doesn't grant access to a section that was renamed/removed; the
    // forward path (add a new key) is unaffected since unknown keys
    // simply aren't filtered in. Returns null on parse failure rather
    // than throwing — the row stays usable, just with no admin access.
    private static List<string>? ParseSectionsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return arr?.Where(AdminSections.IsValid).ToList();
        }
        catch
        {
            return null;
        }
    }
}
