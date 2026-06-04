namespace TleReportingDashboard.Web.Configuration;

// Admin-authored source of values for a filter chip picker.
//
// Distinct from the older LookupDefinition (which produces a CTE for
// sort/join uses in the report query) — a LookupType is purely about
// "where does the filter picker get its options from."
//
// Example: a "Loan Type" LookupType might carry
//   SelectSql      = "SELECT CODENUM, CODEDESC FROM EMPOWER.SET_CODESETS WHERE CODEINT=1"
//   ValueColumn    = "CODENUM"
//   DisplayColumn  = "CODEDESC"
//   SourceTableRef = "EMPOWER.SET_CODESETS"
//
// A field can then bind to this LookupType via FieldDefinition.LookupTypeId
// and pair it with FieldDefinition.FilterPredicateSql — the admin-authored
// WHERE fragment emitted when the user filters on the field.
//
// Nothing about the SELECT, the columns, or the table reference is
// hardcoded — each LookupType targets whatever table the admin needs.
// The Lookup Types tab in Schema Builder owns this catalog per-connection.
public sealed class LookupTypeDefinition
{
    /// <summary>Stable identifier referenced by FieldDefinition.LookupTypeId.</summary>
    public required string Id { get; init; }

    /// <summary>Display label for the editor list and field picker.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Admin-authored SELECT executed by ILookupValueService when the
    /// chip picker opens. Must project the value column AND the display
    /// column (named via the next two properties). The query runs once
    /// per (connection, lookup-type) and the result is cached 30 min.
    /// Trust model: SchemaConfig is admin-authored end-to-end (same as
    /// inline SqlExpression and CTE preambles), so the SQL ships verbatim.
    /// </summary>
    public required string SelectSql { get; init; }

    /// <summary>
    /// Column name in the SELECT result that becomes the filter value
    /// (passed back to the query as the selected value). NOT the display
    /// text. Required.
    /// </summary>
    public required string ValueColumn { get; init; }

    /// <summary>
    /// Column name in the SELECT result shown in the picker. Required.
    /// Equal to ValueColumn is allowed but usually a misconfiguration.
    /// </summary>
    public required string DisplayColumn { get; init; }

    /// <summary>
    /// Optional table reference used by the implicit IN-append path on
    /// the field's FilterPredicateSql. When the admin's predicate
    /// doesn't contain the "{values}" placeholder, the system appends
    /// "AND &lt;SourceTableRef&gt;.&lt;ValueColumn&gt; IN (@p0, @p1, ...)"
    /// — SourceTableRef tells the system how the admin refers to the
    /// lookup table in their predicate (could be the bare table name
    /// "EMPOWER.SET_CODESETS" or a JOIN alias). When the predicate uses
    /// {values} the field-level admin handles binding entirely and this
    /// property is unused.
    /// </summary>
    public string? SourceTableRef { get; init; }
}
