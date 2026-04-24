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
}
