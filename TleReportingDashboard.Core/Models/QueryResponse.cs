namespace TleReportingDashboard.Web.Models;

public class ColumnMeta
{
    public string FieldId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int? MaxLength { get; set; }
    // Optional schema-asserted column min-width (px). Flows from
    // FieldDefinition.MinWidth into ColumnMeta so the renderer can apply it
    // without re-reading the schema per column. Null = no override; the
    // grid's data-type / MaxLength heuristics pick a default width.
    public int? MinWidth { get; set; }
    public Dictionary<string, int>? ValueSortOrder { get; set; }
    // Display format (mask or .NET format string) applied at render / export time.
    public string? Format { get; set; }
}

// Helpers for the ColumnMeta list. Used by every export path that needs
// to apply grid-template column-rename overrides to the headers it
// emits — keeps the override logic + the "what counts as an override"
// rule in one place.
public static class ColumnMetaExtensions
{
    public static List<ColumnMeta> WithLabelOverrides(
        this IEnumerable<ColumnMeta> columns,
        Dictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return columns.ToList();
        return columns.Select(c =>
        {
            if (overrides.TryGetValue(c.FieldId, out var v)
                && !string.IsNullOrWhiteSpace(v))
            {
                return new ColumnMeta
                {
                    FieldId = c.FieldId,
                    Label = v,
                    DataType = c.DataType,
                    MaxLength = c.MaxLength,
                    MinWidth = c.MinWidth,
                    ValueSortOrder = c.ValueSortOrder,
                    Format = c.Format
                };
            }
            return c;
        }).ToList();
    }
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
