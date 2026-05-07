namespace TleReportingDashboard.Web.Models;

public class SavedReport
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // Admin-facing label. Shown in the Master Dashboard's "Add Report"
    // picker so reports that share a public Name (one per loan type, etc.)
    // can be told apart. Empty/null falls back to Name in every consumer
    // — no backfill needed for existing reports.
    public string? InternalName { get; set; }
    // Optional admin-curated grouping for the Report Library — drives a
    // filter dropdown there + a chip on each report row. Free-text so
    // admins aren't locked into a fixed taxonomy; the Builder offers an
    // autocomplete fed by existing values to nudge consistency without
    // enforcing it. Null = uncategorized.
    public string? Category { get; set; }
    // Optional FK to RPT_library_sections — drives row grouping in the
    // Report Library's "All Reports" tab. Null = catch-all "(Uncategorized)"
    // bucket at render time. ON DELETE SET NULL on the FK so deleting a
    // section never orphans reports.
    public Guid? LibrarySectionId { get; set; }
    public string OwnerId { get; set; } = string.Empty; // Entra object ID
    public string OwnerEmail { get; set; } = string.Empty;
    // Owning company. Authorization for Phase 3+ is "user has access to this
    // company" — see ReportViewer / DetailViewer / MasterDashboard for the
    // check pattern.
    public Guid CompanyId { get; set; }
    public string FieldIds { get; set; } = string.Empty; // JSON array of selected field IDs
    public string? Filters { get; set; } // JSON array of applied filters
    public string? Aggregations { get; set; } // JSON array of aggregation config
    public string? ColumnState { get; set; } // JSON: column order, widths, visibility
    public Guid? GridTemplateId { get; set; } // linked grid template (fields, column order, widths, hidden, sort)
    // FK to RPT_company_connections. Identifies the data-source connection
    // this report queries against. Null means "use the company's is_default
    // connection at runtime" (legacy path during migration).
    public Guid? ConnectionId { get; set; }

    // Overrides the schema's default primary (FROM) table for this report.
    // Null means inherit the schema's Settings.PrimaryTable. Lets different
    // reports against the same connection query from different root tables.
    public string? PrimaryTable { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
