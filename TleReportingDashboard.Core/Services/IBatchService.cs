using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Batches of reports — run on demand to produce a single multi-sheet
// Excel workbook spanning multiple companies. Three caller tiers:
//
// Authorization model:
//   * Admin (any IsAdmin signal) — full CRUD on every batch, runs any.
//   * Author (UserRecord.CanCreateBatches = true) — full CRUD on batches
//     THEY own (created_by = their email). Can grant access to other
//     users on their own batches. Can also run any batch granted to them.
//   * Granted user — runs only batches explicitly granted via
//     RPT_report_batch_access.
//   * Anyone else — sees nothing.
//
// The owner is tracked via RPT_report_batches.created_by — stays set to
// the creator regardless of who edits later (updated_by tracks edits).
public interface IBatchService
{
    // Admin list — every batch in the system. UI gates this behind the
    // admin check; the service trusts the caller to enforce it.
    Task<List<BatchRecord>> GetAllAsync(CancellationToken ct = default);

    // Returns the batches the user can see in their list: owned (when
    // they're an author) ∪ granted. Admins should call GetAllAsync.
    Task<List<BatchRecord>> GetForUserAsync(string userEmail, CancellationToken ct = default);

    // Full read of one batch including its items + access grants. Items
    // are projected with display-only ReportName / OwnerEmail / CompanyId
    // / CompanyName joined from RPT_saved_reports + RPT_companies so the
    // editor can render without follow-up lookups.
    Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);

    // True when the user is allowed to run this batch (admin OR owner
    // OR granted in RPT_report_batch_access). Centralised so every
    // run-path uses the same check.
    Task<bool> CanRunAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default);

    // True when the user can edit / delete / grant-access on this batch
    // (admin OR owner). Owners-of-record are tracked via the created_by
    // column — survives edits by anyone else (updated_by tracks those).
    Task<bool> CanEditAsync(Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default);

    Task<BatchRecord> CreateAsync(BatchRecord batch, string createdBy, CancellationToken ct = default);
    Task<BatchRecord> UpdateAsync(BatchRecord batch, string updatedBy, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Replace the entire items list for a batch in a single transaction —
    // simpler than per-item insert/update/delete tracking and matches the
    // "replace on save" UX of the editor.
    Task SetItemsAsync(Guid batchId, IReadOnlyList<BatchItem> items, CancellationToken ct = default);

    // Per-user access grants. ID-less because (batch_id, user_email) is
    // the unique key — grants are idempotent (duplicate grant = no-op).
    Task GrantAccessAsync(Guid batchId, string userEmail, string grantedBy, CancellationToken ct = default);
    Task RevokeAccessAsync(Guid batchId, string userEmail, CancellationToken ct = default);

    // Execute every item in the batch against its report's owning
    // connection, package the results as a single multi-sheet xlsx, and
    // return the bytes + a suggested filename. Each item's report runs
    // with its OWN saved filters (per the design decision); the running
    // user does not provide overrides.
    //
    // Throws UnauthorizedAccessException if the user isn't permitted to
    // run the batch. Per-item failures are captured into the workbook as
    // an error sheet rather than aborting the whole run.
    Task<(byte[] FileBytes, string FileName)> ExecuteAsync(
        Guid batchId, string userEmail, bool isAdmin, CancellationToken ct = default);
}
