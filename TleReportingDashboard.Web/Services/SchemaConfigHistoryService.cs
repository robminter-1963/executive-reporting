using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class SchemaConfigHistoryService : ISchemaConfigHistoryService
{
    private readonly string _connStr;
    private readonly ILogger<SchemaConfigHistoryService> _logger;

    public SchemaConfigHistoryService(
        IConfiguration configuration,
        ILogger<SchemaConfigHistoryService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required for SchemaConfigHistoryService.");
        _logger = logger;
    }

    public async Task<List<SchemaConfigHistoryRecord>> GetAsync(
        Guid? connectionId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        // Single SELECT that joins the connection + company name in one
        // round trip. JSON payload excluded from the list view — admins
        // fetch the full blob via GetJsonAsync only when they preview a
        // specific row. DATALENGTH(json) / 2 converts the nvarchar byte
        // count into character count for the size column.
        var sql = @"
            SELECT h.history_id, h.connection_id, h.company_id,
                   c.name  AS connection_name,
                   co.name AS company_name,
                   h.updated_by, h.updated_at,
                   (DATALENGTH(h.json) / 2) AS json_chars
            FROM EMPOWER.RPT_schema_config_history h
            LEFT JOIN EMPOWER.RPT_company_connections c ON c.id = h.connection_id
            LEFT JOIN EMPOWER.RPT_companies co           ON co.id = h.company_id
            WHERE (@cid IS NULL OR h.connection_id = @cid)
              AND (@from IS NULL OR h.updated_at >= @from)
              AND (@to   IS NULL OR h.updated_at <= @to)
            ORDER BY h.updated_at DESC, h.history_id DESC;";

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@cid", (object?)connectionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@from", (object?)fromUtc ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@to", (object?)toUtc ?? DBNull.Value));

        var rows = new List<SchemaConfigHistoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SchemaConfigHistoryRecord
            {
                HistoryId = reader.GetInt64(0),
                ConnectionId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                CompanyId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                ConnectionName = reader.IsDBNull(3) ? null : reader.GetString(3),
                CompanyName = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                UpdatedAt = reader.GetDateTime(6),
                // DATALENGTH / 2 yields char count for nvarchar; cast to int
                // is safe because any single JSON blob is far below 2^31.
                JsonSize = (int)(reader.IsDBNull(7) ? 0 : reader.GetInt64(7))
            });
        }
        return rows;
    }

    public async Task<string?> GetJsonAsync(long historyId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT json FROM EMPOWER.RPT_schema_config_history WHERE history_id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", historyId));
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw as string;
    }

    public async Task<int> DeleteAsync(IEnumerable<long> historyIds, CancellationToken ct = default)
    {
        // Materialize and dedupe. Empty input is a legitimate no-op.
        var ids = historyIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return 0;

        // Parameterize each id individually. SQL Server has a 2100-param
        // cap so this is safe up to ~2000 rows per call — a single admin
        // purge should comfortably fit, and the UI will in practice cap
        // multi-select at the visible page size.
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        var paramNames = ids.Select((_, i) => "@i" + i).ToList();
        var sql = $"DELETE FROM EMPOWER.RPT_schema_config_history WHERE history_id IN ({string.Join(",", paramNames)});";
        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.Add(new SqlParameter(paramNames[i], ids[i]));

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Schema history bulk delete: {Requested} ids, {Deleted} rows affected",
            ids.Count, deleted);
        return deleted;
    }

    public async Task<int> DeleteByDateRangeAsync(
        Guid? connectionId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc < fromUtc)
            throw new ArgumentException("`toUtc` must be at or after `fromUtc`.");

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            DELETE FROM EMPOWER.RPT_schema_config_history
            WHERE updated_at >= @from AND updated_at <= @to
              AND (@cid IS NULL OR connection_id = @cid);", conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromUtc));
        cmd.Parameters.Add(new SqlParameter("@to", toUtc));
        cmd.Parameters.Add(new SqlParameter("@cid", (object?)connectionId ?? DBNull.Value));

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation(
            "Schema history date-range purge: conn={Cid} range=[{From:o}, {To:o}] deleted={Deleted}",
            connectionId, fromUtc, toUtc, deleted);
        return deleted;
    }
}
