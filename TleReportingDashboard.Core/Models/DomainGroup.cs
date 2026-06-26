namespace TleReportingDashboard.Web.Models;

public class FieldDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty; // "Dimension" or "Measure"
    public int? CodeSetId { get; set; }
    // FK to a LookupTypeDefinition. When set, the header-filter chip
    // picker for this field fetches its values via the lookup type's
    // admin-authored SELECT (through ILookupValueService) and the
    // user's selections feed the field's FilterPredicateSql at query
    // time. Distinct from CodeSetId (Empower-specific SET_CODESETS).
    public string? LookupTypeId { get; set; }
    // Optional alternate column expression for the WHERE comparison when
    // this field is filtered via its LookupType. The picker uses this
    // signal to decide whether to send Code (when set) vs Description
    // (when blank). See Configuration.FieldDefinition.FilterColumn.
    public string? FilterColumn { get; set; }
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
    // Mirrors FieldConfig.IsUnique. Surfaced to the UI so the chip bar /
    // Sort By dropdown can render the (-) marker on admin-asserted unique
    // fields without needing a separate constraint lookup against the
    // target DB.
    public bool IsUnique { get; set; }
    // Mirrors Configuration.FieldDefinition.SearchAliases. Surfaced
    // so the field picker's search can match admin-authored synonyms
    // in addition to Label / Id / Description. Comma-separated; null
    // / blank = no aliases.
    public string? SearchAliases { get; set; }
}

public class DomainGroup
{
    public string Name { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();
}
