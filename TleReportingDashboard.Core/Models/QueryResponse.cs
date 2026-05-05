namespace TleReportingDashboard.Web.Models;

public class ColumnMeta
{
    public string FieldId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int? MaxLength { get; set; }
    public Dictionary<string, int>? ValueSortOrder { get; set; }
    // Display format (mask or .NET format string) applied at render / export time.
    public string? Format { get; set; }
}

public class QueryResponse
{
    public List<ColumnMeta> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int TotalCount { get; set; }
    public bool IsTruncated { get; set; }
    public long ExecutionMs { get; set; }
    // Rendered SQL + bound parameters from the emitter. Exposed for the
    // dashboard / report-library "Show query" debug dialog so users can
    // see exactly what ran — including any row-level scoping predicate.
    // Populated by QueryService.ExecuteQueryAsync on every call.
    public string? Sql { get; set; }
    public Dictionary<string, object?>? Parameters { get; set; }
    // Human-readable scoping explanation, mirrored from
    // QueryRequest.Scoping?.Reason — surfaced as a banner in the debug
    // dialog so a "zero rows" result is self-explanatory (which scoping
    // step force-matched, or which owner column the predicate used).
    public string? ScopingNote { get; set; }
    // True when scoping forced the predicate to `1 = 0` — lets the dialog
    // choose a warning (vs. info) color without the admin having to read
    // the note to tell.
    public bool ScopingForceNoMatch { get; set; }
}
