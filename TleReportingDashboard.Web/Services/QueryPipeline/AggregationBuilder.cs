using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public sealed record AggregationResult(List<string> SelectExpressions, List<string> GroupByExpressions);

public static class AggregationBuilder
{
    private static readonly HashSet<string> ValidAggregateFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "SUM", "AVG", "MAX", "MIN", "COUNT" };

    public static AggregationResult BuildAggregation(
        IReadOnlyList<ProjectedColumn> columns,
        IReadOnlyList<FieldDefinition> fields,
        Dictionary<string, string>? aggregations)
    {
        // When no aggregations are requested, pass through the projected columns as-is
        if (aggregations is null || aggregations.Count == 0)
        {
            return new AggregationResult(
                SelectExpressions: columns.Select(c => c.SqlExpression).ToList(),
                GroupByExpressions: []);
        }

        // First-wins dedupe — see SchemaService.GetFieldConfigsAsync.
        var fieldLookup = fields
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var aggLookup = new Dictionary<string, string>(aggregations, StringComparer.OrdinalIgnoreCase);

        var selectExpressions = new List<string>(columns.Count);
        var groupByExpressions = new List<string>();

        foreach (var col in columns)
        {
            var field = fieldLookup[col.FieldId];

            if (aggLookup.TryGetValue(col.FieldId, out var aggFunction))
            {
                // This field is a measure -- wrap in aggregate function
                var upperAgg = aggFunction.ToUpperInvariant();

                if (!ValidAggregateFunctions.Contains(upperAgg))
                {
                    throw new ArgumentException(
                        $"Invalid aggregation function '{aggFunction}' for field '{col.FieldId}'. " +
                        $"Allowed functions: {string.Join(", ", ValidAggregateFunctions)}.");
                }

                if (field.AllowedAggregations is not null &&
                    !field.AllowedAggregations.Any(a => a.Equals(upperAgg, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException(
                        $"Aggregation '{upperAgg}' is not allowed for field '{col.FieldId}'. " +
                        $"Allowed: {string.Join(", ", field.AllowedAggregations)}.");
                }

                if (col.IsRedacted)
                {
                    // For redacted measures, keep the redaction value as-is
                    selectExpressions.Add(col.SqlExpression);
                }
                else
                {
                    var sourceExpr = field.GetSqlExpression();
                    selectExpressions.Add($"{upperAgg}({sourceExpr}) AS [{col.FieldId}]");
                }
            }
            else
            {
                // This field is a dimension -- include in GROUP BY
                if (col.IsRedacted)
                {
                    // Redacted dimensions still participate in SELECT but use the redaction literal
                    selectExpressions.Add(col.SqlExpression);
                    // Group by the literal expression to satisfy SQL Server
                    var redactionValue = field.DefaultRedactionValue ?? "*** REDACTED ***";
                    groupByExpressions.Add($"'{EscapeSqlLiteral(redactionValue)}'");
                }
                else
                {
                    selectExpressions.Add(col.SqlExpression);
                    groupByExpressions.Add(field.GetSqlExpression());
                }
            }
        }

        return new AggregationResult(selectExpressions, groupByExpressions);
    }

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''");
}
