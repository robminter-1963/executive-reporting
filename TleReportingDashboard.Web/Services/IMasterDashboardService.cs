using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Shared per-company dashboard shape (Phase 3). Tabs and tiles are scoped
// only by companyId — every user with access to a company sees the same
// layout, so a write replaces the canonical for every viewer.
//
// Mutation methods enforce admin role server-side. Editor / Scheduler /
// Viewer roles are intentionally blocked from changing the layout —
// product hasn't decided whether non-admins should get personal layouts,
// a "propose tile" approval queue, or some other model. Until that lands,
// all mutating methods throw UnauthorizedAccessException for non-admins
// regardless of UI gating, so a UI bypass (dev tools, missing
// `@if (_userRoleForCompany == UserRoles.Admin)` on a future button)
// can't silently overwrite the admin's canonical layout.
public interface IMasterDashboardService
{
    // ── Tabs ──
    Task<List<MasterDashboardTab>> GetTabsAsync(Guid companyId);
    Task<MasterDashboardTab> AddTabAsync(Guid companyId, string label, string? userEmail);
    Task UpdateTabAsync(MasterDashboardTab tab, string? userEmail);
    Task RemoveTabAsync(int tabId, string? userEmail);
    Task UpdateTabOrderAsync(List<MasterDashboardTab> tabs, string? userEmail);

    // ── Tiles ──
    Task<List<MasterDashboardTile>> GetTilesAsync(Guid companyId, int tabId);
    Task<List<SavedReport>> GetAvailableReportsAsync(Guid companyId);
    Task AddTileAsync(Guid companyId, int tabId, Guid reportId, string? userEmail, int colSpan = 12);
    Task RemoveTileAsync(int tileId, string? userEmail);
    Task UpdateLayoutAsync(List<MasterDashboardTile> tiles, string? userEmail);
    // Re-home a tile onto a different tab. Appends the tile at the end of
    // the target tab's sort order. No-op when the tile already lives on
    // the target tab (WHERE tab_id <> @TargetTabId).
    Task MoveTileToTabAsync(int tileId, int targetTabId, string? userEmail);

    // Map of report id → the distinct tab labels that report is pinned on
    // in the shared dashboard for the given company. Used by the Report
    // Library's "On Dashboard" column.
    Task<Dictionary<Guid, List<string>>> GetPlacedReportTabsAsync(Guid companyId);
}
