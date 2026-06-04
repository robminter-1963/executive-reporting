namespace TleReportingDashboard.Web.Configuration;

// Admin-authored CTE-style lookup. Used for two purposes today:
//   • Sort: when a field references a lookup whose CTE projects an
//     ORDERBY column, that column drives the field's ORDER BY at query
//     time (lookup-defined sequence vs alphabetical text).
//   • Join: SqlJoin lets the lookup's rows participate in the report
//     query, so other fields / WHERE clauses can reference them.
//
// This is distinct from the "Lookup Types" concept introduced later —
// LookupTypes power filter-chip pickers and admin-authored filter
// predicates. The two coexist; nothing here references LookupTypes.
public sealed class LookupDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>
    /// CTE body without the WITH keyword. E.g.: "LOOKUP(STATUS, EVENTNUM)
    /// AS (SELECT ...)". Optional — kept nullable for forward-compat
    /// with any new lookup flavors that don't need a CTE; consumers in
    /// the query pipeline guard against null before consuming.
    /// </summary>
    public string? SqlPreamble { get; init; }
    /// <summary>Optional JOIN clause to connect the CTE to the query. E.g.: "LEFT JOIN LOOKUP LU ON LU.STATUS = LN_CODES_30.CODEINT"</summary>
    public string? SqlJoin { get; init; }
}
