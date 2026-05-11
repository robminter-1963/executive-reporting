namespace TleReportingDashboard.Web.Models;

public class UserPreference
{
    public string UserId { get; set; } = string.Empty; // Entra object ID (PK)
    public bool OnboardingCompleted { get; set; }
    public int DefaultPageSize { get; set; } = 100;
    // Rows-per-page used by the Report Library tables (My Reports, Shared With Me,
    // Templates, Recent). Separate from DefaultPageSize so admins can keep wide
    // report grids large while keeping the library list compact.
    public int ReportLibraryPageSize { get; set; } = 15;
    // Per-report rows-per-page overrides keyed by report id. Falls back to
    // DefaultPageSize when a report isn't in the map. Stored as JSON in the
    // report_page_sizes column.
    public Dictionary<Guid, int> ReportPageSizes { get; set; } = new();
    public bool IsDarkMode { get; set; }
    public string MasterDashboardTitle { get; set; } = "Master Dashboard";
    public string MasterDashboardTitleAlign { get; set; } = "left"; // "left" | "center" | "right"
    public byte[]? MasterDashboardLogo { get; set; }
    public string? MasterDashboardLogoType { get; set; } // MIME type e.g. "image/png"
    // Last company + connection the admin was editing in Schema Builder.
    // Stored as a pair so the company picker can restore even when the user
    // hasn't chosen a connection yet (e.g., the company is newly created
    // and has no connections configured). Null on either means "no
    // preference" — the page falls back to first-company / company-default.
    public Guid? SchemaBuilderCompanyId { get; set; }
    public Guid? SchemaBuilderConnectionId { get; set; }

    // Last-selected company filter on the Report Library. Null = "All
    // companies" (unfiltered). Scoped per user; persists across sessions.
    public Guid? ReportLibraryCompanyId { get; set; }
    // Last time the user landed on the Master Dashboard (UTC). Drives the
    // "X reports updated since you last visited" greeting copy. Null on
    // first-ever visit — the greeting just shows "Welcome" instead of a
    // delta count. Updated once per page mount via a dedicated UPDATE so
    // unrelated preference fields can't be clobbered by a write race.
    public DateTime? LastMasterDashboardSeen { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
