using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public sealed record DateFilterResult(string? WhereClause, List<SqlParameter> Parameters);

public static partial class DateFilterTranslator
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(\.[A-Za-z_][A-Za-z0-9_]{0,127})?$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex(@"\bCURRENT_TIMESTAMP\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CurrentTimestampRegex();

    [GeneratedRegex(@"\bCURRENT_DATE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CurrentDateRegex();

    public static DateFilterResult TranslateDateFilter(
        string? dateFieldId,
        string? dateOperatorId,
        DateTime? dateFrom,
        DateTime? dateTo,
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<RelativeDateOperator> operators,
        bool isPostgres = false)
    {
        if (string.IsNullOrWhiteSpace(dateFieldId) || string.IsNullOrWhiteSpace(dateOperatorId))
            return new DateFilterResult(null, []);

        // Resolve the date field
        var dateField = fields.FirstOrDefault(f =>
            f.Id.Equals(dateFieldId, StringComparison.OrdinalIgnoreCase));

        if (dateField is null)
            throw new ArgumentException($"Unknown date field ID: '{dateFieldId}'.");

        if (string.IsNullOrWhiteSpace(dateField.SqlExpression))
        {
            ValidateIdentifier(dateField.SourceTable, $"Date field '{dateFieldId}' SourceTable");
            ValidateIdentifier(dateField.SourceColumn, $"Date field '{dateFieldId}' SourceColumn");
        }

        var qualifiedColumn = dateField.GetSqlExpression();

        // Handle custom range
        if (dateOperatorId.Equals("custom_range", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCustomRange(qualifiedColumn, dateFrom, dateTo);
        }

        // Handle relative date operators
        var relativeOp = operators.FirstOrDefault(o =>
            o.Id.Equals(dateOperatorId, StringComparison.OrdinalIgnoreCase));

        if (relativeOp is null)
            throw new ArgumentException($"Unknown date operator ID: '{dateOperatorId}'.");

        if (string.IsNullOrWhiteSpace(relativeOp.SqlTemplate))
            throw new InvalidOperationException(
                $"Date operator '{dateOperatorId}' has no SQL template defined.");

        // The SqlTemplate uses {{column}} as a placeholder for the qualified column name
        var whereClause = relativeOp.SqlTemplate
            .Replace("{{column}}", qualifiedColumn, StringComparison.OrdinalIgnoreCase);

        // Postgres only: the admin's template may reference CURRENT_DATE /
        // CURRENT_TIMESTAMP. Both run in the session zone (typically UTC on
        // managed Postgres), so a late-evening Pacific query computes the
        // wrong "now". When the connection has a DisplayTimezone configured,
        // rewrite both tokens to a NOW()-based wrap that returns the user's
        // wall-clock value in that zone. SQL Server templates use GETDATE()
        // instead of these tokens (CURRENT_TIMESTAMP on SQL Server returns
        // server-local time and doesn't have the UTC drift), so the rewrite
        // is gated on isPostgres to avoid corrupting SQL Server templates.
        if (isPostgres && !string.IsNullOrWhiteSpace(dateField.DisplayTimezone))
        {
            var tz = dateField.DisplayTimezone!.Replace("'", "''");
            whereClause = CurrentTimestampRegex().Replace(whereClause, $"(NOW() AT TIME ZONE '{tz}')");
            whereClause = CurrentDateRegex().Replace(whereClause, $"(NOW() AT TIME ZONE '{tz}')::date");
        }

        return new DateFilterResult(whereClause, []);
    }

    private static DateFilterResult BuildCustomRange(
        string qualifiedColumn,
        DateTime? dateFrom,
        DateTime? dateTo)
    {
        var clauses = new List<string>();
        var parameters = new List<SqlParameter>();

        if (dateFrom.HasValue)
        {
            clauses.Add($"{qualifiedColumn} >= @DateFrom");
            parameters.Add(new SqlParameter("@DateFrom", System.Data.SqlDbType.DateTime2)
            {
                Value = dateFrom.Value
            });
        }

        if (dateTo.HasValue)
        {
            clauses.Add($"{qualifiedColumn} <= @DateTo");
            parameters.Add(new SqlParameter("@DateTo", System.Data.SqlDbType.DateTime2)
            {
                Value = dateTo.Value
            });
        }

        if (clauses.Count == 0)
            throw new ArgumentException(
                "custom_range operator requires at least one of DateFrom or DateTo.");

        var whereClause = string.Join(" AND ", clauses);
        return new DateFilterResult(whereClause, parameters);
    }

    private static void ValidateIdentifier(string value, string context)
    {
        if (!SafeIdentifierRegex().IsMatch(value))
            throw new ArgumentException($"Invalid SQL identifier in {context}: '{value}'");
    }
}
