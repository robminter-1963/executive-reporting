using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class CompanyKpiService : ICompanyKpiService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly ILogger<CompanyKpiService> _logger;

    public CompanyKpiService(
        IConfiguration configuration,
        ConfigDbCache cache,
        ILogger<CompanyKpiService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _logger = logger;
    }

    public Task<List<CompanyKpi>> GetByCompanyAsync(Guid companyId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CompanyKpiService", "ByCompany", companyId),
            async () =>
            {
                var rows = new List<CompanyKpi>();
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                // LEFT-join-equivalent (ISNULL) on the four columns added
                // by the filters migration so a tenant that hasn't run
                // 2026-05-20_company_kpis_filters.sql yet still reads the
                // earlier rows cleanly. Once everyone's migrated the SELECT
                // can be simplified to a plain column list.
                await using var cmd = new SqlCommand(@"
                    SELECT id, company_id, connection_id, primary_table, label,
                           field_id, aggregation, date_field_id, period,
                           compare_previous, col_span, sort_order,
                           created_at, created_by_email,
                           filters, custom_filter_ids, date_from, date_to
                      FROM EMPOWER.RPT_company_kpis
                     WHERE company_id = @CompanyId
                     ORDER BY sort_order, created_at;", conn);
                cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
                try
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        rows.Add(new CompanyKpi
                        {
                            Id              = reader.GetGuid(0),
                            CompanyId       = reader.GetGuid(1),
                            ConnectionId    = reader.GetGuid(2),
                            PrimaryTable    = reader.GetString(3),
                            Label           = reader.IsDBNull(4) ? null : reader.GetString(4),
                            FieldId         = reader.GetString(5),
                            Aggregation     = reader.GetString(6),
                            DateFieldId     = reader.IsDBNull(7) ? null : reader.GetString(7),
                            Period          = reader.IsDBNull(8) ? null : reader.GetString(8),
                            ComparePrevious = reader.GetBoolean(9),
                            ColSpan         = reader.GetInt32(10),
                            SortOrder       = reader.GetInt32(11),
                            CreatedAt       = reader.GetDateTime(12),
                            CreatedByEmail  = reader.IsDBNull(13) ? null : reader.GetString(13),
                            Filters         = DeserializeFilters(reader.IsDBNull(14) ? null : reader.GetString(14)),
                            CustomFilterIds = DeserializeStringList(reader.IsDBNull(15) ? null : reader.GetString(15)),
                            DateFrom        = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                            DateTo          = reader.IsDBNull(17) ? null : reader.GetDateTime(17)
                        });
                    }
                }
                catch (SqlException ex) when (ex.IsObjectMissing())
                {
                    // Migration not applied on this DB yet — return empty so
                    // the dashboard simply skips rendering the band.
                    _logger.LogDebug(ex, "RPT_company_kpis not present yet — returning empty list.");
                }
                return rows;
            });

    public Task<bool> IsBandVisibleAsync(Guid companyId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CompanyKpiService", "BandVisible", companyId),
            async () =>
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(@"
                    SELECT show_kpi_band FROM EMPOWER.RPT_companies WHERE id = @Id;", conn);
                cmd.Parameters.Add(new SqlParameter("@Id", companyId));
                try
                {
                    var result = await cmd.ExecuteScalarAsync();
                    // Default ON for any company predating the migration.
                    return result is bool b ? b : true;
                }
                catch (SqlException ex) when (ex.IsObjectMissing())
                {
                    _logger.LogDebug(ex, "show_kpi_band column missing — defaulting to true.");
                    return true;
                }
            });

    public async Task SetBandVisibleAsync(Guid companyId, bool visible)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_companies
               SET show_kpi_band = @Visible
             WHERE id = @Id;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", companyId));
        cmd.Parameters.Add(new SqlParameter("@Visible", visible));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("CompanyKpiService:");
        // Touch the company registry cache too — the toggle is stored on
        // RPT_companies, so a registry consumer wouldn't see the flip until
        // its own cache expired. Belt-and-suspenders invalidation.
        _cache.Invalidate("CompanyRegistry:");
    }

    public async Task<CompanyKpi> CreateAsync(CompanyKpi kpi, string? createdByEmail)
    {
        if (kpi is null) throw new ArgumentNullException(nameof(kpi));
        if (string.IsNullOrWhiteSpace(kpi.PrimaryTable))
            throw new ArgumentException("Primary table is required.", nameof(kpi));
        if (string.IsNullOrWhiteSpace(kpi.FieldId))
            throw new ArgumentException("Field id is required.", nameof(kpi));

        kpi.Id = Guid.NewGuid();
        kpi.CreatedAt = DateTime.UtcNow;
        kpi.CreatedByEmail = createdByEmail;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Append-to-end: read MAX(sort_order) in the same connection so we
        // don't race against another insert. Cheap because the table is
        // small per company and indexed on (company_id, sort_order).
        await using (var maxCmd = new SqlCommand(@"
            SELECT ISNULL(MAX(sort_order), -1) + 1
              FROM EMPOWER.RPT_company_kpis
             WHERE company_id = @CompanyId;", conn))
        {
            maxCmd.Parameters.Add(new SqlParameter("@CompanyId", kpi.CompanyId));
            var nextOrder = await maxCmd.ExecuteScalarAsync();
            kpi.SortOrder = nextOrder is int i ? i : 0;
        }

        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_company_kpis
                (id, company_id, connection_id, primary_table, label, field_id,
                 aggregation, date_field_id, period, compare_previous,
                 col_span, sort_order, created_at, created_by_email,
                 filters, custom_filter_ids, date_from, date_to)
            VALUES (@Id, @CompanyId, @ConnectionId, @PrimaryTable, @Label, @FieldId,
                    @Aggregation, @DateFieldId, @Period, @ComparePrevious,
                    @ColSpan, @SortOrder, @CreatedAt, @CreatedByEmail,
                    @Filters, @CustomFilterIds, @DateFrom, @DateTo);", conn);
        BindKpiParameters(cmd, kpi);
        await cmd.ExecuteNonQueryAsync();

        _cache.Invalidate("CompanyKpiService:");
        _logger.LogInformation("KPI created: {Id} {Field} {Agg} for company {CompanyId}",
            kpi.Id, kpi.FieldId, kpi.Aggregation, kpi.CompanyId);
        return kpi;
    }

    public async Task UpdateAsync(CompanyKpi kpi)
    {
        if (kpi is null) throw new ArgumentNullException(nameof(kpi));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_company_kpis
               SET connection_id     = @ConnectionId,
                   primary_table     = @PrimaryTable,
                   label             = @Label,
                   field_id          = @FieldId,
                   aggregation       = @Aggregation,
                   date_field_id     = @DateFieldId,
                   period            = @Period,
                   compare_previous  = @ComparePrevious,
                   col_span          = @ColSpan,
                   filters           = @Filters,
                   custom_filter_ids = @CustomFilterIds,
                   date_from         = @DateFrom,
                   date_to           = @DateTo
             WHERE id = @Id;", conn);
        BindKpiParameters(cmd, kpi);
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("CompanyKpiService:");
    }

    public async Task ReorderAsync(Guid companyId, IList<Guid> orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                await using var cmd = new SqlCommand(@"
                    UPDATE EMPOWER.RPT_company_kpis
                       SET sort_order = @Order
                     WHERE id = @Id AND company_id = @CompanyId;", conn, (SqlTransaction)tx);
                cmd.Parameters.Add(new SqlParameter("@Order", i));
                cmd.Parameters.Add(new SqlParameter("@Id", orderedIds[i]));
                cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        _cache.Invalidate("CompanyKpiService:");
    }

    public async Task DeleteAsync(Guid kpiId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            DELETE FROM EMPOWER.RPT_company_kpis WHERE id = @Id;", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", kpiId));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("CompanyKpiService:");
    }

    // Centralized parameter binding so Insert and Update emit identical
    // shapes — easier to keep in sync as columns get added.
    private static void BindKpiParameters(SqlCommand cmd, CompanyKpi kpi)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", kpi.Id));
        cmd.Parameters.Add(new SqlParameter("@CompanyId", kpi.CompanyId));
        cmd.Parameters.Add(new SqlParameter("@ConnectionId", kpi.ConnectionId));
        cmd.Parameters.Add(new SqlParameter("@PrimaryTable", kpi.PrimaryTable));
        cmd.Parameters.Add(new SqlParameter("@Label", (object?)kpi.Label ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@FieldId", kpi.FieldId));
        cmd.Parameters.Add(new SqlParameter("@Aggregation", kpi.Aggregation));
        cmd.Parameters.Add(new SqlParameter("@DateFieldId", (object?)kpi.DateFieldId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Period", (object?)kpi.Period ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ComparePrevious", kpi.ComparePrevious));
        cmd.Parameters.Add(new SqlParameter("@ColSpan", kpi.ColSpan));
        cmd.Parameters.Add(new SqlParameter("@SortOrder", kpi.SortOrder));
        cmd.Parameters.Add(new SqlParameter("@CreatedAt", kpi.CreatedAt));
        cmd.Parameters.Add(new SqlParameter("@CreatedByEmail", (object?)kpi.CreatedByEmail ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@Filters",
            (object?)SerializeFilters(kpi.Filters) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@CustomFilterIds",
            (object?)SerializeStringList(kpi.CustomFilterIds) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@DateFrom", (object?)kpi.DateFrom ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@DateTo", (object?)kpi.DateTo ?? DBNull.Value));
    }

    // ── JSON helpers for the filters + custom_filter_ids columns ──
    // Empty dicts/lists round-trip as NULL to keep the storage compact
    // and let "is anything set?" checks be a simple NULL check.

    private static string? SerializeFilters(Dictionary<string, object?>? filters) =>
        filters is null || filters.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(filters);

    private static Dictionary<string, object?>? DeserializeFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            // JsonElement preserves the value's source type (string/number/array)
            // so downstream filter emitters see "37" as a string vs 37 as a number
            // correctly. Same handling pattern as ReportConfig.Filters.
            var dict = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(json);
            return dict;
        }
        catch
        {
            return null;
        }
    }

    private static string? SerializeStringList(List<string>? items) =>
        items is null || items.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(items);

    private static List<string>? DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json); }
        catch { return null; }
    }
}
