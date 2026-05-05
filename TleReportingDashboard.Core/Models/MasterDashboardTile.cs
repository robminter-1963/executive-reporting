namespace TleReportingDashboard.Web.Models;

public class MasterDashboardTab
{
    public int Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Label { get; set; } = "Dashboard";
    public int SortOrder { get; set; }
    public string TitleAlign { get; set; } = "left"; // "left" | "center" | "right"
}

public class MasterDashboardTile
{
    public int Id { get; set; }
    public Guid CompanyId { get; set; }
    public int TabId { get; set; }
    public Guid ReportId { get; set; }
    public string ReportName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int ColSpan { get; set; } = 12;
    public int Height { get; set; } = 500;
    public string TitleAlign { get; set; } = "left"; // "left" | "center" | "right"
    // null = "(no section)" bucket — preserves the pre-sections layout for
    // tabs whose admin hasn't created any sections yet. Backed by the
    // nullable section_id column added in 2026-04-28_14-00_master_dashboard_sections.sql.
    public int? SectionId { get; set; }
}

public class MasterDashboardSection
{
    public int Id { get; set; }
    public int TabId { get; set; }
    public string Label { get; set; } = "Section";
    public int SortOrder { get; set; }
    public string TitleAlign { get; set; } = "left"; // "left" | "center" | "right"
    public bool Collapsed { get; set; }
}
