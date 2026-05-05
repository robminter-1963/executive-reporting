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

    // Table-view calculated columns. Each is a small formula evaluated
    // per-row at render time, referencing other selected fields by label
    // or id. Independent of the dashboard's calc columns (which are
    // stored on DashboardConfig and evaluate per-group). Null = none.
    // Calc columns are always editable, even when a grid template is
    // applied — the template owns the field set, not derivations on top.
    public List<TableCalcColumnDef>? TableCalculatedColumns { get; set; }

    // When true, the emitted SELECT carries DISTINCT — useful when the
    // report's join chain produces row-multiplication and the admin
    // only cares about the parent row's distinct values. Defaults to
    // **true** so new reports start safe against duplicate rows; admins
    // can turn it off in the Report Builder when they explicitly need
    // raw row counts. Persisted in column_state JSON as "Distinct" so
    // the toggle survives reloads. Existing reports load whatever value
    // is in the JSON (legacy reports default to false on the load path
    // since their ColumnState predates this key).
    public bool Distinct { get; set; } = true;
}

public class TableCalcColumnDef
{
    // Stable identifier within the report. Survives label rename so any
    // downstream references (sort, ColumnOrder slot) keep working.
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    // Tiny expression in the same language as the dashboard's calc
    // columns: + - * /, parens, unary minus, [bracketed identifiers].
    // Identifiers resolve against selected field labels (case-insensitive)
    // and field ids. Values come from the row dictionary (typed double).
    public string Formula { get; set; } = string.Empty;
    // Optional raw SQL expression evaluated server-side. When set, this
    // takes precedence over Formula: the expression is appended to the
    // query's SELECT as `<sql> AS <Key>`, and ReportGrid reads the value
    // out of the row dict by Key (no client-side eval). Use for anything
    // beyond the formula language — CASE, COALESCE, subqueries, function
    // calls. Same trust model as FieldDefinition.SqlExpression: admin-
    // authored, embedded as-is.
    public string? SqlExpression { get; set; }
    // Schema join IDs this calc's SqlExpression references. Pulled into
    // the FROM/JOIN chain so aliases like C / L resolve. Ignored when
    // SqlExpression is empty. Each id matches a JoinDefinition.Id from
    // the connection's schema config.
    public List<string>? JoinIds { get; set; }
    // Optional .NET format ("N1", "P1", "C2") or mask. Routed through
    // the same FieldFormatter every other column uses.
    public string? Format { get; set; }
    // Drives column width and default alignment in the table grid.
    // percent | currency | decimal | integer | (blank = decimal).
    public string? DataType { get; set; }
}

public class TableSortSpec
{
    public string Field { get; set; } = string.Empty;
    public string Direction { get; set; } = "asc"; // "asc" or "desc"
}
