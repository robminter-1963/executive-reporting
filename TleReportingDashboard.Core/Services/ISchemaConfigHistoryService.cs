namespace TleReportingDashboard.Web.Services;

// Admin-only reader/purger for EMPOWER.RPT_schema_config_history.
// SchemaConfigStore writes new rows on every SaveAsync; this service lets
// the Admin UI list those rows and prune old ones by multi-select or date
// range. Nothing here re-checks admin privilege — call sites must gate via
// IAdminService.IsGlobalAdmin before invoking.
public interface ISchemaConfigHistoryService
{
    // Lists history rows, optionally narrowed by connection + inclusive
    // date range (both dates in UTC, matching the updated_at column).
    // Returns rows newest-first. The full JSON blob is NOT included —
    // fetch it on demand via GetJsonAsync to keep the list light.
    Task<List<SchemaConfigHistoryRecord>> GetAsync(
        Guid? connectionId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    // Returns the full JSON blob for a single history row, or null when
    // the id isn't found. Used by the Admin preview dialog.
    Task<string?> GetJsonAsync(long historyId, CancellationToken ct = default);

    // Deletes the specified history rows in one round trip. Returns the
    // number of rows actually removed. Safe to pass an empty list (0).
    Task<int> DeleteAsync(IEnumerable<long> historyIds, CancellationToken ct = default);

    // Deletes every history row whose updated_at falls in the given
    // inclusive range. Optional connectionId narrows the purge to a
    // single connection's history. Returns the number of rows deleted.
    Task<int> DeleteByDateRangeAsync(
        Guid? connectionId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);
}

// Row-shape for the Admin list view. JSON payload excluded on purpose —
// history blobs can be large and we only fetch them on demand for preview.
public sealed class SchemaConfigHistoryRecord
{
    public long HistoryId { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid? CompanyId { get; set; }
    // Friendly labels joined from RPT_company_connections + RPT_companies
    // at SELECT time so the UI doesn't have to resolve them row-by-row.
    public string? ConnectionName { get; set; }
    public string? CompanyName { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Byte length of the stored json blob — surfaced in the list so admins
    // can spot outsized entries without fetching the full payload.
    public int JsonSize { get; set; }
}
