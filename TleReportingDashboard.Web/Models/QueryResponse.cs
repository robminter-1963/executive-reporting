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
}
