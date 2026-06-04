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

    // True when this tile came from RPT_master_dashboard_personal_tiles
    // (the per-user pin layer) rather than the shared
    // RPT_master_dashboard_tiles table. The dashboard uses one merged
    // list in memory and discriminates by this flag for the
    // per-user "Unpin" affordance and the small "personal" indicator,
    // and to route mutations to the right service method. False = shared
    // canonical tile (the default for rows the admin's flow creates).
    // Personal tile ids are scoped to a separate IDENTITY column, so
    // (Id, IsPersonal) is the de-facto composite key in app code.
    public bool IsPersonal { get; set; }
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
