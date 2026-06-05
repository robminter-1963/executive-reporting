namespace TleReportingDashboard.Web.Models;

// Admin-authored collection of reports packaged into a single multi-sheet
// Excel workbook. Backed by RPT_report_batches (the batch itself),
// RPT_report_batch_items (its ordered reports), and RPT_report_batch_access
// (per-user run permissions). Cross-company by design — a single batch
// can pull reports from any companies the admin has access to.
public sealed class BatchRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    // Hydrated views — populated by the get-by-id read so the editor has
    // everything in one round-trip. List queries leave them empty for cost.
    public List<BatchItem> Items { get; set; } = new();
    public List<BatchAccessGrant> Access { get; set; } = new();
}

// One report in a batch. SortOrder controls worksheet position in the
// generated workbook. SheetName overrides the default (which is the
// report's name truncated to Excel's 31-char limit + de-duplicated).
public sealed class BatchItem
{
    public int Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid ReportId { get; set; }
    public int SortOrder { get; set; }
    public string? SheetName { get; set; }

    // Display-only — populated by joins on read so the editor can show
    // the report's label / owning company without a second lookup.
    public string? ReportName { get; set; }
    public string? ReportOwnerEmail { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    // Short code (e.g. "tle"). Prefixed onto each worksheet label by
    // ExecuteAsync so a cross-company workbook reads as
    // "tle - Loans By Status" / "abc - Loans By Status" at a glance.
    public string? CompanyCode { get; set; }
}

// Per-user run permission. user_email = the login (preferred_username claim).
public sealed class BatchAccessGrant
{
    public int Id { get; set; }
    public Guid BatchId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateTime GrantedAt { get; set; }
    public string? GrantedBy { get; set; }
}
