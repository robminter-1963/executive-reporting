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
    // Picker source for the master-dashboard "Add Report" / "Pin to my view"
    // flows. Returns reports with ShowOnMaster=true that the user is allowed
    // to pin: company-scoped reports (any owner in the user's current
    // company) PLUS reports shared directly with the user (regardless of
    // the report's home company). userId is optional — pass null/empty to
    // skip the shared-with-me branch and get only the company-scoped
    // reports (back-compat behavior for callers without a user context).
    Task<List<SavedReport>> GetAvailableReportsAsync(Guid companyId, string? userId = null);
    Task AddTileAsync(Guid companyId, int tabId, Guid reportId, string? userEmail, int colSpan = 12, int? sectionId = null);
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

    // ── Sections ── Sub-grouping under each tab. Optional per tab; tiles
    // with section_id = NULL render under a "(no section)" header so the
    // pre-sections layout is preserved for tabs that haven't adopted them.
    Task<List<MasterDashboardSection>> GetSectionsAsync(int tabId);
    Task<MasterDashboardSection> AddSectionAsync(int tabId, string label, string? userEmail);
    Task RenameSectionAsync(int sectionId, string label, string? userEmail);
    Task UpdateSectionOrderAsync(List<MasterDashboardSection> sections, string? userEmail);
    Task RemoveSectionAsync(int sectionId, string? userEmail);
    Task SetSectionCollapsedAsync(int sectionId, bool collapsed, string? userEmail);
    Task SetSectionAlignAsync(int sectionId, string align, string? userEmail);
    Task MoveTileToSectionAsync(int tileId, int? sectionId, string? userEmail);
    // Cross-tab section drag. Moves the section row AND every tile that
    // references it to the target tab. Both tabs must belong to the same
    // company; the implementation enforces this and runs both updates in
    // one transaction so a half-moved section can never persist.
    Task MoveSectionToTabAsync(int sectionId, int targetTabId, string? userEmail);

    // Soft cap surfaced to the UI so the "+ Section" button can disable past
    // the limit, and enforced server-side in AddSectionAsync so a bypassed
    // UI can't blow past it.
    const int MaxSectionsPerTab = 10;

    // ── Per-user personal tile pins ────────────────────────────────────
    // The "personal layer": every user can pin reports to their own view
    // of a tab without touching the shared canonical layout. Pins are
    // scoped to (user, company, tab); only the owning user sees them.
    // Solves the non-admin report-owner case where they want their own
    // report on a dashboard tab but aren't allowed to mutate the shared
    // layout. No admin gate on these methods — every user can manage
    // their own pins.

    // Returns the user's personal tiles for the given tab. The
    // MasterDashboard merges these with GetTilesAsync and renders the
    // combined list ordered by sort_order; the IsPersonal flag drives
    // the per-user unpin affordance and visual indicator.
    Task<List<MasterDashboardTile>> GetPersonalTilesAsync(string userId, Guid companyId, int tabId);

    // Creates a personal pin. Idempotent on the (user, tab, report)
    // unique index — re-adding the same report on the same tab is a
    // silent no-op. Returns the inserted (or existing) tile row.
    Task<MasterDashboardTile> AddPersonalTileAsync(
        string userId, Guid companyId, int tabId, Guid reportId,
        int colSpan = 12, int? sectionId = null);

    // Removes a single personal pin. user_id check in the WHERE clause
    // means a user can only remove their OWN pins — calling with someone
    // else's tile id is a silent no-op (zero-row UPDATE / DELETE).
    Task RemovePersonalTileAsync(string userId, int personalTileId);

    // Updates the layout properties of a single personal pin (size + title
    // alignment). user_id check in the WHERE clause means a user can only
    // edit their OWN pins. Shared/canonical tiles go through
    // UpdateLayoutAsync instead — that's admin-only and batch-shaped.
    Task UpdatePersonalTileLayoutAsync(string userId, int personalTileId,
        int colSpan, int height, string? titleAlign);

    // Distinct set of report ids the user has personal-pinned anywhere
    // (across every tab in any company). Used by the "Pin to my view"
    // picker to filter out reports the user has already pinned, so they
    // don't see duplicates in the candidate list.
    Task<HashSet<Guid>> GetPersonalPlacedReportIdsAsync(string userId);

    // ── Per-user tab visibility ── Hidden tabs are scoped to (user, tab).
    // Tabs themselves remain shared at the company level — this is purely a
    // view filter. Admin/edit mode bypasses the filter so admins can still
    // see and mutate hidden tabs. Cascading FK on tab_id means admin-removed
    // tabs don't leave dangling hidden rows.
    //
    // GetHiddenTabIdsAsync returns the set of tab IDs this user has hidden
    // within the given company; callers filter their _tabs list against it.
    // SetTabHiddenAsync inserts/deletes a row based on `hidden`. Idempotent
    // — re-hiding an already-hidden tab is a no-op.
    Task<HashSet<int>> GetHiddenTabIdsAsync(string userId, Guid companyId);
    Task SetTabHiddenAsync(string userId, int tabId, bool hidden);
}
