namespace TleReportingDashboard.Web.Models;

// Admin-curated bucket for grouping saved reports in the Report Library's
// "All Reports" tab. Mirrors the Master Dashboard's per-tab Sections idea —
// named, sortable, optional. Reports without a section render in a synthetic
// catch-all bucket at display time.
public class LibrarySection
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
