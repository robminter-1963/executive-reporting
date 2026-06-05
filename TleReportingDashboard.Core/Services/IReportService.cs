using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IReportService
{
    Task<List<SavedReport>> GetReportsAsync(string userId);
    // Admin-only: every report in the system regardless of owner. Callers
    // must gate this behind an IsAdmin check; nothing in the service layer
    // re-validates, since the UI shell owns the auth story.
    Task<List<SavedReport>> GetAllReportsAsync();
    // Visibility for a non-admin user. Returns reports the user can see
    // under the system's three-tier rule:
    //   1. Reports they own (owner_id = @userId).
    //   2. Reports shared with them — directly or via 'everyone' wildcard.
    //   3. Reports owned by an admin — admin-authored content is
    //      implicitly visible to everyone (no explicit share required).
    // Unshared user-owned reports stay invisible. Admins should call
    // GetAllReportsAsync — this method's purpose is the broader visibility
    // for non-admin authors (e.g., the batch report picker).
    Task<List<SavedReport>> GetVisibleToUserAsync(string userId);
    // Direct by-id lookup, unscoped by owner / share. Intended for the
    // master dashboard's tile loader where the caller has already verified
    // the user's right to view the tile (membership on the shared per-company
    // dashboard = authorization). Returns null if no report matches.
    Task<SavedReport?> GetReportByIdAsync(Guid id);
    Task<SavedReport> SaveReportAsync(SavedReport report);
    Task<SavedReport> UpdateReportAsync(SavedReport report);
    // Admin-only: update a report regardless of owner. Mirrors
    // DeleteReportAsAdminAsync — skips the owner_id check in the SQL so
    // a global admin can edit on behalf of any user. The OwnerId on the
    // saved record is preserved (admin edits don't change ownership).
    // Callers MUST gate this behind an IsAdmin check; the service does
    // not re-validate.
    Task<SavedReport> UpdateReportAsAdminAsync(SavedReport report);
    Task DeleteReportAsync(Guid id, string userId);
    // Admin-only: delete any report regardless of owner. Used to clean up
    // reports left behind when a user departs the company. Callers MUST
    // gate this behind an IsAdmin check; the service skips the owner_id
    // filter so the auth story lives entirely at the call site.
    Task DeleteReportAsAdminAsync(Guid id);
    // Distinct, sorted list of category values used by any existing report.
    // Powers the Builder's autocomplete and the Library's filter dropdown.
    // Empty list when no reports have a category yet — UI degrades to a
    // free-text input. Returns reports across every owner / company since
    // categories are global (admins curate them as a shared taxonomy).
    Task<List<string>> GetDistinctCategoriesAsync();
}
