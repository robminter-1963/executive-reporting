namespace TleReportingDashboard.Web.Configuration;

public sealed class CustomFilterDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    /// <summary>
    /// Optional CTE or preamble SQL placed before the SELECT statement.
    /// When multiple active filters have preambles, they are combined as
    /// comma-separated CTEs in a single WITH clause.
    /// Example: "LOOKUP(STATUS, EVENTNUM) AS (SELECT 1, 346 UNION ALL ...)"
    /// </summary>
    public string? SqlPreamble { get; init; }
    /// <summary>
    /// Reference to an existing join ID from the Joins configuration.
    /// When set, the join's SQL is resolved at query time — no need to duplicate it in SqlJoin.
    /// If both JoinId and SqlJoin are set, JoinId takes precedence.
    /// </summary>
    public string? JoinId { get; init; }
    /// <summary>
    /// Optional inline JOIN clause appended after the schema joins when this filter is active.
    /// Used when the filter needs a custom join not defined in the Joins tab.
    /// Example: "LEFT JOIN LOOKUP ON LOOKUP.STATUS = LN_CODES_30.CODEINT"
    /// </summary>
    public string? SqlJoin { get; init; }
    /// <summary>
    /// IDs of named Lookups whose CTE and JOIN are required when this filter is active.
    /// </summary>
    public List<string>? LookupIds { get; init; }
    /// <summary>
    /// Raw SQL condition appended to the WHERE clause (without AND prefix).
    /// Admin-curated — same trust model as JoinDefinition.Sql and FieldDefinition.SqlExpression.
    /// </summary>
    public required string SqlCondition { get; init; }

    /// <summary>
    /// Optional group name used to bucket filters in the Report Builder UI.
    /// Typically matches a Domain (e.g. "Loan", "Borrower") so presets cluster
    /// with the fields they relate to. Blank = falls into the "Misc" group.
    /// </summary>
    public string? GroupName { get; init; }
}
