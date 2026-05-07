namespace TleReportingDashboard.Web.Services;

// Per-circuit scoped state so Report Library restores the user's last-selected
// tab when they navigate back from any child page (Builder, Viewer, Grid Templates, etc.).
public class LibraryNavState
{
    public int LastActiveTab { get; set; }
    // (The admin "broaden the My Reports tab" toggle was retired —
    // admins now see other users' reports on a dedicated "Other Users'
    // Reports" tab. My Reports stays strictly self-scoped. The IsAdminView
    // / IsAdminViewExplicitlySet fields lived here previously.)
    // Per-tab paging position so editing a report from page 3, then
    // returning, lands the user back on page 3 instead of page 1.
    // Keyed by tab index (0 = My, 1 = Shared, 2 = Recent). Not persisted
    // beyond the circuit — refreshing the browser resets to page 0.
    public Dictionary<int, int> PageByTab { get; } = new();
    // The report the user last clicked Run / Edit on. Library uses this on
    // its OnAfterRender pass to scroll the matching row into view, so a
    // round-trip to the Builder / Detail Viewer doesn't dump the user back
    // at the top of the list. Cleared after one consume so a manual
    // refresh of the Library doesn't auto-scroll.
    public Guid? LastSelectedReportId { get; set; }
    // Per-section expand state for the My Reports tab. Key = section name
    // (case-insensitive), value = expanded?. Persists across page remounts
    // within a circuit so navigating to Builder / Detail Viewer and back
    // restores whichever sections the admin had open. The "(Uncategorized)"
    // catch-all defaults to expanded on first init; everything else
    // defaults to collapsed. Once SectionExpansionInitialized is true,
    // the defaults stop overriding the user's explicit picks.
    public Dictionary<string, bool> ExpandedSections { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool SectionExpansionInitialized { get; set; }
}
