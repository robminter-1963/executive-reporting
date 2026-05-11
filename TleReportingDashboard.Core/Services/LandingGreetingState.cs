namespace TleReportingDashboard.Web.Services;

// Per-circuit "has the landing greeting already shown once?" flag.
// Pages that can host the greeting (CompanyPicker, MasterDashboard)
// claim the slot on mount: if HasShown is false, flip it to true and
// render the strip; if it's already true, skip. This makes
// "show once per circuit" robust even when the user navigates between
// hosting pages BEFORE the greeting auto-dismisses (e.g., picker shows
// it, user clicks a company tile a second later — the master dashboard
// must NOT re-show it). Reset implicitly when the user starts a new
// circuit (browser refresh / app restart).
public class LandingGreetingState
{
    public bool HasShown { get; set; }
}
