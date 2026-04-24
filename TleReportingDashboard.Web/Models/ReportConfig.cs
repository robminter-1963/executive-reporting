namespace TleReportingDashboard.Web.Models;

public class ReportConfig
{
    public List<string> SelectedFieldIds { get; set; } = new();
    public string? ChartType { get; set; }
    public string? ChartXField { get; set; }
    public string? ChartYField { get; set; }
    public Dictionary<string, object?> Filters { get; set; } = new();
    public string? SortField { get; set; }
    public string? SortDirection { get; set; }
    public string? DashboardConfigJson { get; set; }
    public List<string>? CustomFilterIds { get; set; }
    public Dictionary<string, int>? ColumnWidths { get; set; }
    public List<string>? ColumnOrder { get; set; }
    public List<string>? HiddenColumns { get; set; }
    public string? DefaultSortField { get; set; }
    public string? DefaultSortDirection { get; set; } // "asc" or "desc"
    // Multi-column sort — primary first, then secondary, etc. Captured from
    // the grid on save and seeded back on load. When this list is populated
    // it takes precedence over DefaultSortField/Direction.
    public List<TableSortSpec> TableSort { get; set; } = new();

    // FK to RPT_company_connections. The editor initializes this to the
    // company's is_default connection for new reports; the user can change
    // it via the Connection picker. Every query routes through this id.
    public Guid? ConnectionId { get; set; }

    // Overrides the schema's Settings.PrimaryTable for this report. Blank
    // means inherit. Lets a single Postgres connection host reports that
    // start from different root tables (lead vs opportunity vs account).
    public string? PrimaryTable { get; set; }

    // Ordered list of field ids to GROUP BY when the report has aggregate
    // fields. Null → auto-default to all selected non-aggregate fields in
    // the order they were picked. Customized via the Group By chip bar.
    public List<string>? GroupByFieldIds { get; set; }
}

public class TableSortSpec
{
    public string Field { get; set; } = string.Empty;
    public string Direction { get; set; } = "asc"; // "asc" or "desc"
}
