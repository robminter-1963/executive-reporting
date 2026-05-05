namespace TleReportingDashboard.Web.Services;

// Per-circuit scoped state so Report Library restores the user's last-selected
// tab when they navigate back from any child page (Builder, Viewer, Grid Templates, etc.).
public class LibraryNavState
{
    public int LastActiveTab { get; set; }
    // Session-scoped admin toggle for Report Library. When true, the "My
    // Reports" tab broadens to every user's reports and exposes an Owner
    // column. Session scope (not per-user prefs) because admins usually
    // flip it on, do something, and flip it off.
    public bool IsAdminView { get; set; }
    // Per-tab paging position so editing a report from page 3, then
    // returning, lands the user back on page 3 instead of page 1.
    // Keyed by tab index (0 = My, 1 = Shared, 2 = Recent). Not persisted
    // beyond the circuit — refreshing the browser resets to page 0.
    public Dictionary<int, int> PageByTab { get; } = new();
}
