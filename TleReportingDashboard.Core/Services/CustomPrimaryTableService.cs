using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class CustomPrimaryTableService : ICustomPrimaryTableService
{
    private readonly string _connStr;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly IAuditLogger _audit;

    public CustomPrimaryTableService(
        IConfiguration configuration,
        ConfigDbCache cache,
        EditorModeState editorMode,
        IAuditLogger audit)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _editorMode = editorMode;
        _audit = audit;
    }

    // Audit-safe projection. The full record is already non-secret data
    // (no credentials); this method exists for shape consistency with the
    // other services and to keep the audit-log JSON compact (omit timestamps
    // / created-by since those are already on the audit row itself).
    private static object ForAudit(CustomPrimaryTableRecord r) => new
    {
        r.Id, r.ConnectionId, r.TableName, r.Alias,
        r.IsPrimary, r.IsDefaultPrimary,
        r.TableType, r.PrimaryColumn, r.AdditionalKeyColumns,
        r.Description
    };

    public Task<List<CustomPrimaryTableRecord>> GetByConnectionAsync(
        Guid connectionId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CustomPrimaryTableService", "ByConnection", connectionId),
            async () =>
            {
                var result = new List<CustomPrimaryTableRecord>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                // Ordered: defaults first, then other suggested primaries, then the
                // rest. Keeps the admin editor and the builder dropdown visually
                // aligned without requiring a re-sort at the UI layer.
                await using var cmd = new SqlCommand(@"
                    SELECT id, connection_id, table_name, alias,
                           is_primary, is_default_primary,
                           created_at, created_by_id, created_by_email,
                           table_type, primary_column, additional_key_columns,
                           description
                    FROM EMPOWER.RPT_custom_primary_tables
                    WHERE connection_id = @c
                    ORDER BY is_default_primary DESC, is_primary DESC, table_name, alias;", conn);
                cmd.Parameters.Add(new SqlParameter("@c", connectionId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    result.Add(ReadRecord(reader));
                }
                return result;
            },
            bypass: _editorMode.IsActive);

    public async Task<CustomPrimaryTableRecord> AddAsync(
        Guid connectionId, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? createdById, string? createdByEmail,
        CancellationToken ct = default,
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null,
        string? description = null)
    {
        // Normalize the new typing fields. Blank → NULL so the DB doesn't
        // store empty strings (keeps "is set" checks unambiguous). Column
        // names go through the alias regex (column identifiers and table
        // aliases share the same SQL identifier rules); the type string
        // is open-ended so future LOS adapters can register their own.
        var normalizedTableType = string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim();
        var normalizedPrimaryColumn = NormalizeIdentifier(primaryColumn, "primaryColumn");
        var normalizedAdditionalKeys = NormalizeKeyColumnList(additionalKeyColumns);
        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? null
            // Cap at 500 chars to match the column width; admins occasionally
            // paste long notes and we'd rather silently truncate than throw.
            : description.Trim()[..Math.Min(description.Trim().Length, 500)];

        // Validate shape before hitting the DB — the emitter trusts these
        // values to end up in SQL, so we reject anything outside the safe
        // identifier regex at this boundary. Alias is optional; empty string
        // round-trips as "no alias" and keeps the unique index deterministic.
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));
        alias = normalizedAlias;

        // Can't be a default pick without also being eligible — coerce rather
        // than throw so callers don't have to reason about both flags.
        if (isDefaultPrimary) isPrimary = true;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Idempotent add: if the exact (table, alias) row already exists,
        // update its flags in place and return the refreshed row. Adding the
        // same pair twice shouldn't throw; the caller's latest intent wins.
        await using (var existingCmd = new SqlCommand(@"
            SELECT id
            FROM EMPOWER.RPT_custom_primary_tables
            WHERE connection_id = @c AND table_name = @t AND alias = @a;", conn))
        {
            existingCmd.Parameters.Add(new SqlParameter("@c", connectionId));
            existingCmd.Parameters.Add(new SqlParameter("@t", tableName));
            existingCmd.Parameters.Add(new SqlParameter("@a", alias));
            var existingIdObj = await existingCmd.ExecuteScalarAsync(ct);
            if (existingIdObj is Guid existingId)
            {
                await UpdateAsync(existingId, tableName, alias, isPrimary, isDefaultPrimary, ct,
                    tableType: normalizedTableType,
                    primaryColumn: normalizedPrimaryColumn,
                    additionalKeyColumns: normalizedAdditionalKeys,
                    description: normalizedDescription);
                return (await GetByIdAsync(existingId, ct))!;
            }
        }

        // Alias uniqueness: when the alias is non-empty, reject if another row
        // on the same connection already uses it (with a different table).
        if (alias!.Length > 0)
        {
            await using var aliasCheck = new SqlCommand(@"
                SELECT TOP 1 table_name
                FROM EMPOWER.RPT_custom_primary_tables
                WHERE connection_id = @c AND alias = @a;", conn);
            aliasCheck.Parameters.Add(new SqlParameter("@c", connectionId));
            aliasCheck.Parameters.Add(new SqlParameter("@a", alias));
            var clashingTable = (string?)await aliasCheck.ExecuteScalarAsync(ct);
            if (clashingTable is not null)
            {
                throw new InvalidOperationException(
                    $"Alias \"{alias}\" is already used by {clashingTable} on this connection. Aliases must be unique per connection.");
            }
        }

        // Clear any existing default on this connection if the incoming row
        // is going in as the default. We do this before the INSERT so the
        // filtered unique index doesn't complain.
        if (isDefaultPrimary)
        {
            await ClearDefaultForConnectionAsync(conn, connectionId, excludeId: null, ct);
        }

        var id = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        await using var insert = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_custom_primary_tables
                (id, connection_id, table_name, alias,
                 is_primary, is_default_primary,
                 created_at, created_by_id, created_by_email,
                 table_type, primary_column, additional_key_columns,
                 description)
            VALUES (@id, @c, @t, @a, @ip, @idp, @ca, @cbi, @cbe, @tt, @pc, @ak, @desc);", conn);
        insert.Parameters.Add(new SqlParameter("@id", id));
        insert.Parameters.Add(new SqlParameter("@c", connectionId));
        insert.Parameters.Add(new SqlParameter("@t", tableName));
        insert.Parameters.Add(new SqlParameter("@a", alias));
        insert.Parameters.Add(new SqlParameter("@ip", isPrimary));
        insert.Parameters.Add(new SqlParameter("@idp", isDefaultPrimary));
        insert.Parameters.Add(new SqlParameter("@ca", createdAt));
        insert.Parameters.Add(new SqlParameter("@cbi", (object?)createdById ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@cbe", (object?)createdByEmail ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@tt", (object?)normalizedTableType ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@pc", (object?)normalizedPrimaryColumn ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@ak", (object?)normalizedAdditionalKeys ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@desc", (object?)normalizedDescription ?? DBNull.Value));
        await insert.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("CustomPrimaryTableService:");

        var created = new CustomPrimaryTableRecord
        {
            Id = id,
            ConnectionId = connectionId,
            TableName = tableName,
            Alias = alias,
            IsPrimary = isPrimary,
            IsDefaultPrimary = isDefaultPrimary,
            CreatedAt = createdAt,
            CreatedById = createdById,
            CreatedByEmail = createdByEmail,
            TableType = normalizedTableType,
            PrimaryColumn = normalizedPrimaryColumn,
            AdditionalKeyColumns = normalizedAdditionalKeys,
            Description = normalizedDescription
        };
        // PrimaryColumn / AdditionalKeyColumns drive row-level scoping —
        // changes here directly affect what data self-scoped users see.
        await _audit.LogAsync(
            actorEmail: createdByEmail,
            action: AuditActions.Create,
            resourceType: AuditResources.TableAlias,
            resourceId: id.ToString(),
            resourceLabel: string.IsNullOrEmpty(alias) ? tableName : $"{tableName} AS {alias}",
            before: null,
            after: ForAudit(created));
        return created;
    }

    public async Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        CancellationToken ct = default,
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null,
        string? description = null)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));

        // Same normalization as AddAsync — kept here rather than refactored
        // out so the validation error messages target the right input.
        var normalizedTableType = string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim();
        var normalizedPrimaryColumn = NormalizeIdentifier(primaryColumn, "primaryColumn");
        var normalizedAdditionalKeys = NormalizeKeyColumnList(additionalKeyColumns);
        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? null
            : description.Trim()[..Math.Min(description.Trim().Length, 500)];

        if (isDefaultPrimary) isPrimary = true;

        // Pre-read for the audit-log diff. Captures the existing values so
        // a reviewer can see exactly which scoping-critical field moved
        // (PrimaryColumn / AdditionalKeyColumns most often).
        var existing = await GetByIdAsync(id, ct);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Alias uniqueness check (same connection, different row). Excluded
        // when the incoming alias is empty — empty is free to repeat.
        if (normalizedAlias.Length > 0)
        {
            await using var aliasCheck = new SqlCommand(@"
                SELECT TOP 1 table_name
                FROM EMPOWER.RPT_custom_primary_tables
                WHERE alias = @a
                  AND id <> @id
                  AND connection_id = (SELECT connection_id FROM EMPOWER.RPT_custom_primary_tables WHERE id = @id);", conn);
            aliasCheck.Parameters.Add(new SqlParameter("@a", normalizedAlias));
            aliasCheck.Parameters.Add(new SqlParameter("@id", id));
            var clashingTable = (string?)await aliasCheck.ExecuteScalarAsync(ct);
            if (clashingTable is not null)
            {
                throw new InvalidOperationException(
                    $"Alias \"{normalizedAlias}\" is already used by {clashingTable} on this connection. Aliases must be unique per connection.");
            }
        }

        // If this row is becoming the default, clear any other default on
        // the same connection first. Need the connection_id to target the
        // clear, pulled back in the same query round trip.
        if (isDefaultPrimary)
        {
            await using var cidCmd = new SqlCommand(
                "SELECT connection_id FROM EMPOWER.RPT_custom_primary_tables WHERE id = @id;", conn);
            cidCmd.Parameters.Add(new SqlParameter("@id", id));
            var cidObj = await cidCmd.ExecuteScalarAsync(ct);
            if (cidObj is Guid cid)
            {
                await ClearDefaultForConnectionAsync(conn, cid, excludeId: id, ct);
            }
        }

        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_custom_primary_tables
               SET table_name = @t, alias = @a,
                   is_primary = @ip, is_default_primary = @idp,
                   table_type = @tt, primary_column = @pc,
                   additional_key_columns = @ak,
                   description = @desc
             WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        cmd.Parameters.Add(new SqlParameter("@a", normalizedAlias));
        cmd.Parameters.Add(new SqlParameter("@ip", isPrimary));
        cmd.Parameters.Add(new SqlParameter("@idp", isDefaultPrimary));
        cmd.Parameters.Add(new SqlParameter("@tt", (object?)normalizedTableType ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@pc", (object?)normalizedPrimaryColumn ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ak", (object?)normalizedAdditionalKeys ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@desc", (object?)normalizedDescription ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("CustomPrimaryTableService:");

        var afterRecord = await GetByIdAsync(id, ct);
        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Update,
            resourceType: AuditResources.TableAlias,
            resourceId: id.ToString(),
            resourceLabel: string.IsNullOrEmpty(normalizedAlias) ? tableName : $"{tableName} AS {normalizedAlias}",
            before: existing is null ? null : ForAudit(existing),
            after: afterRecord is null ? null : ForAudit(afterRecord));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Snapshot pre-delete so the audit trail records the row's shape.
        var existing = await GetByIdAsync(id, ct);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_custom_primary_tables WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("CustomPrimaryTableService:");

        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Delete,
            resourceType: AuditResources.TableAlias,
            resourceId: id.ToString(),
            resourceLabel: existing is null
                ? id.ToString()
                : (string.IsNullOrEmpty(existing.Alias) ? existing.TableName : $"{existing.TableName} AS {existing.Alias}"),
            before: existing is null ? null : ForAudit(existing),
            after: null);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static CustomPrimaryTableRecord ReadRecord(SqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ConnectionId = r.GetGuid(1),
        TableName = r.GetString(2),
        Alias = r.GetString(3),
        IsPrimary = !r.IsDBNull(4) && r.GetBoolean(4),
        IsDefaultPrimary = !r.IsDBNull(5) && r.GetBoolean(5),
        CreatedAt = r.GetDateTime(6),
        CreatedById = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedByEmail = r.IsDBNull(8) ? null : r.GetString(8),
        TableType = r.IsDBNull(9) ? null : r.GetString(9),
        PrimaryColumn = r.IsDBNull(10) ? null : r.GetString(10),
        AdditionalKeyColumns = r.IsDBNull(11) ? null : r.GetString(11),
        Description = r.IsDBNull(12) ? null : r.GetString(12)
    };

    private async Task<CustomPrimaryTableRecord?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT id, connection_id, table_name, alias,
                   is_primary, is_default_primary,
                   created_at, created_by_id, created_by_email,
                   table_type, primary_column, additional_key_columns
            FROM EMPOWER.RPT_custom_primary_tables
            WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRecord(r) : null;
    }

    // Validates a single SQL identifier (column name) using the same regex
    // as table aliases — column names follow the same rules. Empty input
    // returns null (= no value); invalid input throws so the editor surfaces
    // the error before the row hits the DB.
    private static string? NormalizeIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!PrimaryTableRef.AliasRegex().IsMatch(trimmed))
            throw new ArgumentException(
                $"\"{trimmed}\" is not a valid column name. Use letters, digits, and underscores only.",
                paramName);
        return trimmed;
    }

    // Splits a comma-separated key-column list, trims each entry, validates
    // each as a SQL identifier, and re-joins with ", " for stable storage.
    // Returns null when the input is blank — keeps "no extras" unambiguous.
    private static string? NormalizeKeyColumnList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0) return null;
        foreach (var p in parts)
        {
            if (!PrimaryTableRef.AliasRegex().IsMatch(p))
                throw new ArgumentException(
                    $"\"{p}\" is not a valid column name. Use letters, digits, and underscores only.",
                    nameof(value));
        }
        return string.Join(", ", parts);
    }

    // Flips off is_default_primary for every row on the given connection,
    // optionally excluding one (the row that's about to become the default).
    // Executed inside the caller's already-open connection so the whole
    // swap happens on one trip.
    private static async Task ClearDefaultForConnectionAsync(
        SqlConnection conn, Guid connectionId, Guid? excludeId, CancellationToken ct)
    {
        const string sql = @"
            UPDATE EMPOWER.RPT_custom_primary_tables
               SET is_default_primary = 0
             WHERE connection_id = @c
               AND is_default_primary = 1
               AND (@excl IS NULL OR id <> @excl);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@c", connectionId));
        cmd.Parameters.Add(new SqlParameter("@excl", (object?)excludeId ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Role-scoped owner fields ───────────────────────────────────────────

    public Task<IReadOnlyDictionary<Guid, string>> GetRoleOwnerFieldsAsync(Guid primaryTableId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CustomPrimaryTableService", "RoleOwners", primaryTableId),
            async () =>
            {
                var map = new Dictionary<Guid, string>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT role_id, owner_field_id
                      FROM EMPOWER.RPT_primary_table_role_owners
                     WHERE primary_table_id = @pid;", conn);
                cmd.Parameters.Add(new SqlParameter("@pid", primaryTableId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    map[reader.GetGuid(0)] = reader.GetString(1);
                }
                return (IReadOnlyDictionary<Guid, string>)map;
            },
            bypass: _editorMode.IsActive);

    public async Task SetRoleOwnerAsync(Guid primaryTableId, Guid roleId, string ownerFieldId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerFieldId))
            throw new ArgumentException("ownerFieldId is required — use ClearRoleOwnerAsync to remove a mapping.", nameof(ownerFieldId));

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_primary_table_role_owners AS t
            USING (SELECT @pid AS primary_table_id, @rid AS role_id) AS s
               ON t.primary_table_id = s.primary_table_id AND t.role_id = s.role_id
            WHEN MATCHED THEN
                UPDATE SET owner_field_id = @fid,
                           updated_at     = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (primary_table_id, role_id, owner_field_id)
                VALUES (@pid, @rid, @fid);", conn);
        cmd.Parameters.Add(new SqlParameter("@pid", primaryTableId));
        cmd.Parameters.Add(new SqlParameter("@rid", roleId));
        cmd.Parameters.Add(new SqlParameter("@fid", ownerFieldId.Trim()));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("CustomPrimaryTableService:RoleOwners:");

        // SOC-2: role-owner mappings drive row-level scoping for self-scoped
        // users. Every set / clear is recorded as a grant / revoke under
        // table_alias so reviewers see the scoping decisions over time.
        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Grant,
            resourceType: AuditResources.TableAlias,
            resourceId: $"{primaryTableId}|role={roleId}",
            resourceLabel: $"Role owner field for primary {primaryTableId}",
            before: null,
            after: new { PrimaryTableId = primaryTableId, RoleId = roleId, OwnerFieldId = ownerFieldId.Trim() },
            notes: "role-owner mapping");
    }

    public async Task ClearRoleOwnerAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            DELETE FROM EMPOWER.RPT_primary_table_role_owners
             WHERE primary_table_id = @pid AND role_id = @rid;", conn);
        cmd.Parameters.Add(new SqlParameter("@pid", primaryTableId));
        cmd.Parameters.Add(new SqlParameter("@rid", roleId));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("CustomPrimaryTableService:RoleOwners:");

        await _audit.LogAsync(
            actorEmail: null,
            action: AuditActions.Revoke,
            resourceType: AuditResources.TableAlias,
            resourceId: $"{primaryTableId}|role={roleId}",
            resourceLabel: $"Role owner field for primary {primaryTableId}",
            before: new { PrimaryTableId = primaryTableId, RoleId = roleId },
            after: null,
            notes: "role-owner mapping");
    }

    public Task<string?> ResolveOwnerFieldForRoleAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CustomPrimaryTableService", "ResolveOwner", primaryTableId, roleId),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT owner_field_id
                      FROM EMPOWER.RPT_primary_table_role_owners
                     WHERE primary_table_id = @pid AND role_id = @rid;", conn);
                cmd.Parameters.Add(new SqlParameter("@pid", primaryTableId));
                cmd.Parameters.Add(new SqlParameter("@rid", roleId));
                var result = await cmd.ExecuteScalarAsync(ct);
                return result as string;
            },
            bypass: _editorMode.IsActive);
}
