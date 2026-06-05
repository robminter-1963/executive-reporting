using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Dev-mode stub for IBatchService. The real implementation requires
// ConfigDb; in dev mode (no ConfigDb conn string), this no-op stands
// in so the UI can render without crashing DI. All reads return empty;
// mutations and Execute throw the same NotSupportedException, signalling
// to the UI that batches aren't available locally.
public sealed class InMemoryBatchService : IBatchService
{
    public Task<List<BatchRecord>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<BatchRecord>());

    public Task<List<BatchRecord>> GetForUserAsync(string userEmail, CancellationToken ct = default) =>
        Task.FromResult(new List<BatchRecord>());

    public Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<BatchRecord?>(null);

    public Task<bool> CanRunAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> CanEditAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<BatchRecord> CreateAsync(BatchRecord batch, string createdBy, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task<BatchRecord> UpdateAsync(BatchRecord batch, string updatedBy, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task SetItemsAsync(Guid batchId, IReadOnlyList<BatchItem> items, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task GrantAccessAsync(Guid batchId, string userEmail, string grantedBy, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task RevokeAccessAsync(Guid batchId, string userEmail, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");

    public Task<(byte[] FileBytes, string FileName)> ExecuteAsync(
        Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default) =>
        throw new NotSupportedException("Batches require ConfigDb (not available in dev mode).");
}
