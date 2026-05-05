using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IReportService
{
    Task<List<SavedReport>> GetReportsAsync(string userId);
    // Admin-only: every report in the system regardless of owner. Callers
    // must gate this behind an IsAdmin check; nothing in the service layer
    // re-validates, since the UI shell owns the auth story.
    Task<List<SavedReport>> GetAllReportsAsync();
    // Direct by-id lookup, unscoped by owner / share. Intended for the
    // master dashboard's tile loader where the caller has already verified
    // the user's right to view the tile (membership on the shared per-company
    // dashboard = authorization). Returns null if no report matches.
    Task<SavedReport?> GetReportByIdAsync(Guid id);
    Task<SavedReport> SaveReportAsync(SavedReport report);
    Task<SavedReport> UpdateReportAsync(SavedReport report);
    Task DeleteReportAsync(Guid id, string userId);
    // Distinct, sorted list of category values used by any existing report.
    // Powers the Builder's autocomplete and the Library's filter dropdown.
    // Empty list when no reports have a category yet — UI degrades to a
    // free-text input. Returns reports across every owner / company since
    // categories are global (admins curate them as a shared taxonomy).
    Task<List<string>> GetDistinctCategoriesAsync();
}
