using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Shared per-company dashboard shape (Phase 3). Tabs and tiles are scoped
// only by companyId — every user with access to a company sees the same
// layout. Writes are only invoked from the admin-only edit path on
// MasterDashboard.razor; the read methods are safe for any user that can
// reach the dashboard (the page's access check gates that).
public interface IMasterDashboardService
{
    // ── Tabs ──
    Task<List<MasterDashboardTab>> GetTabsAsync(Guid companyId);
    Task<MasterDashboardTab> AddTabAsync(Guid companyId, string label);
    Task UpdateTabAsync(MasterDashboardTab tab);
    Task RemoveTabAsync(int tabId);
    Task UpdateTabOrderAsync(List<MasterDashboardTab> tabs);

    // ── Tiles ──
    Task<List<MasterDashboardTile>> GetTilesAsync(Guid companyId, int tabId);
    Task<List<SavedReport>> GetAvailableReportsAsync(Guid companyId);
    Task AddTileAsync(Guid companyId, int tabId, Guid reportId, int colSpan = 12);
    Task RemoveTileAsync(int tileId);
    Task UpdateLayoutAsync(List<MasterDashboardTile> tiles);
    // Re-home a tile onto a different tab. Appends the tile at the end of
    // the target tab's sort order. No-op when the tile already lives on
    // the target tab (WHERE tab_id <> @TargetTabId).
    Task MoveTileToTabAsync(int tileId, int targetTabId);

    // Map of report id → the distinct tab labels that report is pinned on
    // in the shared dashboard for the given company. Used by the Report
    // Library's "On Dashboard" column.
    Task<Dictionary<Guid, List<string>>> GetPlacedReportTabsAsync(Guid companyId);
}
