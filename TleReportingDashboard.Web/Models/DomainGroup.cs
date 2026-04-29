namespace TleReportingDashboard.Web.Models;

public class FieldDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty; // "Dimension" or "Measure"
    public int? CodeSetId { get; set; }
    public string? RolesRequired { get; set; }
    public string? DefaultRedactionValue { get; set; }
    // Surfaced so the UI can detect aggregate fields (SUM/COUNT/AVG/...) and
    // drive the Group By chip bar + Table-view column hiding.
    public string? SqlExpression { get; set; }
    // Surfaced so admin UIs can disambiguate fields with the same Label
    // (e.g. two "Loan Officer" entries from different tables) by showing
    // "<table>.<column>" alongside or under the friendly label. Empty when
    // the source is wholly inside SqlExpression.
    public string SourceTable { get; set; } = string.Empty;
    public string SourceColumn { get; set; } = string.Empty;
}

public class DomainGroup
{
    public string Name { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();
}
