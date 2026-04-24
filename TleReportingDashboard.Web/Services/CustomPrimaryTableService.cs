using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class CustomPrimaryTableService : ICustomPrimaryTableService
{
    private readonly string _connStr;

    public CustomPrimaryTableService(IConfiguration configuration)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
    }

    public async Task<List<CustomPrimaryTableRecord>> GetByConnectionAsync(
        Guid connectionId, CancellationToken ct = default)
    {
        var result = new List<CustomPrimaryTableRecord>();
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // Ordered: defaults first, then other suggested primaries, then the
        // rest. Keeps the admin editor and the builder dropdown visually
        // aligned without requiring a re-sort at the UI layer.
        await using var cmd = new SqlCommand(@"
            SELECT id, connection_id, table_name, alias,
                   is_primary, is_default_primary, owner_field_id,
                   created_at, created_by_id, created_by_email
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
    }

    public async Task<CustomPrimaryTableRecord> AddAsync(
        Guid connectionId, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? ownerFieldId,
        string? createdById, string? createdByEmail,
        CancellationToken ct = default)
    {
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
                await UpdateAsync(existingId, tableName, alias, isPrimary, isDefaultPrimary, ownerFieldId, ct);
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
        var normalizedOwner = string.IsNullOrWhiteSpace(ownerFieldId) ? null : ownerFieldId.Trim();

        await using var insert = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_custom_primary_tables
                (id, connection_id, table_name, alias,
                 is_primary, is_default_primary, owner_field_id,
                 created_at, created_by_id, created_by_email)
            VALUES (@id, @c, @t, @a, @ip, @idp, @ofi, @ca, @cbi, @cbe);", conn);
        insert.Parameters.Add(new SqlParameter("@id", id));
        insert.Parameters.Add(new SqlParameter("@c", connectionId));
        insert.Parameters.Add(new SqlParameter("@t", tableName));
        insert.Parameters.Add(new SqlParameter("@a", alias));
        insert.Parameters.Add(new SqlParameter("@ip", isPrimary));
        insert.Parameters.Add(new SqlParameter("@idp", isDefaultPrimary));
        insert.Parameters.Add(new SqlParameter("@ofi", (object?)normalizedOwner ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@ca", createdAt));
        insert.Parameters.Add(new SqlParameter("@cbi", (object?)createdById ?? DBNull.Value));
        insert.Parameters.Add(new SqlParameter("@cbe", (object?)createdByEmail ?? DBNull.Value));
        await insert.ExecuteNonQueryAsync(ct);

        return new CustomPrimaryTableRecord
        {
            Id = id,
            ConnectionId = connectionId,
            TableName = tableName,
            Alias = alias,
            IsPrimary = isPrimary,
            IsDefaultPrimary = isDefaultPrimary,
            OwnerFieldId = normalizedOwner,
            CreatedAt = createdAt,
            CreatedById = createdById,
            CreatedByEmail = createdByEmail
        };
    }

    public async Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? ownerFieldId,
        CancellationToken ct = default)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));

        if (isDefaultPrimary) isPrimary = true;

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

        var normalizedOwner = string.IsNullOrWhiteSpace(ownerFieldId) ? null : ownerFieldId.Trim();

        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_custom_primary_tables
               SET table_name = @t, alias = @a,
                   is_primary = @ip, is_default_primary = @idp,
                   owner_field_id = @ofi
             WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        cmd.Parameters.Add(new SqlParameter("@a", normalizedAlias));
        cmd.Parameters.Add(new SqlParameter("@ip", isPrimary));
        cmd.Parameters.Add(new SqlParameter("@idp", isDefaultPrimary));
        cmd.Parameters.Add(new SqlParameter("@ofi", (object?)normalizedOwner ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_custom_primary_tables WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
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
        OwnerFieldId = r.IsDBNull(6) ? null : r.GetString(6),
        CreatedAt = r.GetDateTime(7),
        CreatedById = r.IsDBNull(8) ? null : r.GetString(8),
        CreatedByEmail = r.IsDBNull(9) ? null : r.GetString(9)
    };

    private async Task<CustomPrimaryTableRecord?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT id, connection_id, table_name, alias,
                   is_primary, is_default_primary, owner_field_id,
                   created_at, created_by_id, created_by_email
            FROM EMPOWER.RPT_custom_primary_tables
            WHERE id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRecord(r) : null;
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
}
