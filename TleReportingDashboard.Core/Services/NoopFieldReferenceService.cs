namespace TleReportingDashboard.Web.Services;

// No-op when there's no DB (in-memory mode) — nothing to rename.
public class NoopFieldReferenceService : IFieldReferenceService
{
    public Task<int> RenameAsync(string oldFieldId, string newFieldId) => Task.FromResult(0);
}
