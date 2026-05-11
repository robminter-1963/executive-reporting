namespace TleReportingDashboard.Web.Services;

// Per-user favorited reports. Drives the Library star toggle and the
// Master Dashboard's "Pinned" strip. Distinct from admin-curated tiles
// (RPT_master_tiles) — favorites are a personal shortcut bar, not part
// of the shared dashboard layout.
public interface IFavoriteService
{
    // Returns the user's favorited report ids in display order
    // (sort_order, then created_at descending). Cached; invalidated on
    // Add / Remove. Empty list when the user has no favorites OR the
    // migration hasn't been applied yet (defensive fallback).
    Task<List<Guid>> GetReportIdsAsync(string userId);

    Task AddAsync(string userId, Guid reportId);
    Task RemoveAsync(string userId, Guid reportId);
}
