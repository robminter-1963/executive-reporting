namespace TleReportingDashboard.Web.Services;

// Dev-without-DB fallback. The Schema History tab isn't meaningful without
// a live ConfigDb (nothing writes history rows), so every read returns
// empty and every mutation is a silent no-op. The UI renders an empty
// state instead of crashing on missing services.
public sealed class NoopSchemaConfigHistoryService : ISchemaConfigHistoryService
{
    public Task<List<SchemaConfigHistoryRecord>> GetAsync(
        Guid? connectionId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
        => Task.FromResult(new List<SchemaConfigHistoryRecord>());

    public Task<string?> GetJsonAsync(long historyId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<int> DeleteAsync(IEnumerable<long> historyIds, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> DeleteByDateRangeAsync(
        Guid? connectionId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
        => Task.FromResult(0);
}
