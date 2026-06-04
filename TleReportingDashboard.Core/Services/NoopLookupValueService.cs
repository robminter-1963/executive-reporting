namespace TleReportingDashboard.Web.Services;

// Dev-mode stub for ILookupValueService. Live mode hits the connection's
// data DB to fetch the lookup's value list; mock mode has no real data
// source, so this returns empty and the filter chip picker just renders
// no options. Better than crashing on a null connection resolver.
public sealed class NoopLookupValueService : ILookupValueService
{
    public Task<List<CodeSetValue>> GetFilterValuesAsync(
        Guid connectionId, string lookupId, CancellationToken ct = default)
        => Task.FromResult(new List<CodeSetValue>());
}
