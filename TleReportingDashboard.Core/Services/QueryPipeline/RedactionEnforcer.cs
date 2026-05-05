using System.Text.RegularExpressions;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public sealed record ProjectedColumn(string FieldId, string SqlExpression, bool IsRedacted);

public static partial class RedactionEnforcer
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(\.[A-Za-z_][A-Za-z0-9_]{0,127})?$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    public static List<ProjectedColumn> EnforceRedaction(
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyCollection<string> userRoles)
    {
        var roleSet = new HashSet<string>(userRoles, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectedColumn>(fields.Count);

        foreach (var field in fields)
        {
            // Inline-SQL fields manage their own table/column references; skip identifier validation.
            if (string.IsNullOrWhiteSpace(field.SqlExpression))
            {
                ValidateIdentifier(field.SourceTable, $"Field '{field.Id}' SourceTable");
                ValidateIdentifier(field.SourceColumn, $"Field '{field.Id}' SourceColumn");
            }

            if (!string.IsNullOrWhiteSpace(field.RolesRequired) && !HasRequiredRole(field.RolesRequired, roleSet))
            {
                var redactionValue = field.DefaultRedactionValue ?? "*** REDACTED ***";
                var sql = $"'{EscapeSqlLiteral(redactionValue)}' AS [{field.Id}]";
                result.Add(new ProjectedColumn(field.Id, sql, IsRedacted: true));
            }
            else
            {
                var sql = $"{field.GetSqlExpression()} AS [{field.Id}]";
                result.Add(new ProjectedColumn(field.Id, sql, IsRedacted: false));
            }
        }

        return result;
    }

    private static bool HasRequiredRole(string rolesRequired, HashSet<string> userRoles)
    {
        // RolesRequired can be comma-separated; user must have at least one
        var required = rolesRequired.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var role in required)
        {
            if (userRoles.Contains(role))
                return true;
        }
        return false;
    }

    private static void ValidateIdentifier(string value, string context)
    {
        if (!SafeIdentifierRegex().IsMatch(value))
            throw new ArgumentException($"Invalid SQL identifier in {context}: '{value}'");
    }

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''");
}
