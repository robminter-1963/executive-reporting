namespace TleReportingDashboard.Web.Configuration;

public sealed class LookupDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>CTE body without the WITH keyword. E.g.: "LOOKUP(STATUS, EVENTNUM) AS (SELECT ...)"</summary>
    public required string SqlPreamble { get; init; }
    /// <summary>Optional JOIN clause to connect the CTE to the query. E.g.: "LEFT JOIN LOOKUP LU ON LU.STATUS = LN_CODES_30.CODEINT"</summary>
    public string? SqlJoin { get; init; }
}
