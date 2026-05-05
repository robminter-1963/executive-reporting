using System.Text.RegularExpressions;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services;

public static partial class SchemaConfigValidator
{
    private static readonly HashSet<string> AllowedSqlFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CAST", "GETDATE", "DATEADD", "DATEDIFF", "DATEFROMPARTS", "YEAR", "MONTH", "DAY"
    };

    private static readonly string[] DangerousPatterns =
    [
        "DROP", "INSERT", "UPDATE", "DELETE", "EXEC", "xp_"
    ];

    private static readonly char[] DangerousChars = [';'];

    private static readonly string[] DangerousCommentMarkers = ["--", "/*"];

    [GeneratedRegex(@"^(INNER|LEFT)\s+JOIN\s+[\w.]+(\s+AS\s+\w+)?\s+ON\s+[\w.]+\s*=\s*[\w.]+(\s+AND\s+.+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JoinPatternRegex();

    [GeneratedRegex(@"^[A-Za-z_]\w{0,127}(\.[A-Za-z_]\w{0,127})?$", RegexOptions.Compiled)]
    private static partial Regex SimpleIdentifierRegex();

    [GeneratedRegex(@"\b(\w+)\s*\(", RegexOptions.Compiled)]
    private static partial Regex FunctionCallRegex();

    public static IReadOnlyList<string> Validate(SchemaConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var warnings = new List<string>();

        ValidateRelativeDateOperators(config.Settings.RelativeDateOperators);
        ValidateJoins(config.Joins);
        ValidateFieldDefinitions(config.Fields, warnings);

        return warnings;
    }

    private static void ValidateRelativeDateOperators(List<RelativeDateOperator> operators)
    {
        foreach (var op in operators)
        {
            if (op.SqlTemplate is null)
                continue;

            RejectDangerousFragments(op.SqlTemplate, $"RelativeDateOperator '{op.Id}' SqlTemplate");

            var functionCalls = FunctionCallRegex().Matches(op.SqlTemplate);
            foreach (Match match in functionCalls)
            {
                var functionName = match.Groups[1].Value;
                if (!AllowedSqlFunctions.Contains(functionName))
                {
                    throw new InvalidOperationException(
                        $"RelativeDateOperator '{op.Id}' SqlTemplate contains disallowed SQL function '{functionName}'. " +
                        $"Allowed functions: {string.Join(", ", AllowedSqlFunctions)}.");
                }
            }
        }
    }

    private static void ValidateJoins(List<JoinDefinition> joins)
    {
        foreach (var join in joins)
        {
            RejectDangerousFragments(join.Sql, $"Join '{join.Id}' Sql");

            var trimmed = join.Sql.Trim();

            // Complex/nested joins (multiple JOIN keywords) are admin-curated raw SQL —
            // just verify they start with a valid JOIN keyword.
            var joinCount = System.Text.RegularExpressions.Regex.Matches(trimmed, @"\bJOIN\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            if (joinCount > 1)
            {
                if (!trimmed.StartsWith("LEFT", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("INNER", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("RIGHT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Join '{join.Id}' raw SQL must start with LEFT/INNER/RIGHT JOIN.");
                }
                continue;
            }

            if (!JoinPatternRegex().IsMatch(trimmed))
            {
                throw new InvalidOperationException(
                    $"Join '{join.Id}' Sql does not match the required pattern: " +
                    "'(INNER|LEFT) JOIN <TABLE> ON <TABLE>.<COL> = <TABLE>.<COL>'. " +
                    $"Actual value: '{join.Sql}'.");
            }
        }
    }

    private static void ValidateFieldDefinitions(List<FieldDefinition> fields, List<string> warnings)
    {
        foreach (var field in fields)
        {
            // Inline-SQL fields manage their own table/column references inside
            // SqlExpression; SourceTable/SourceColumn are intentionally blank and
            // must not be validated as identifiers.
            if (!string.IsNullOrWhiteSpace(field.SqlExpression))
                continue;

            // Incomplete fields (missing SourceTable or SourceColumn with
            // no SqlExpression to fall back on) are admin-config mistakes,
            // not security concerns. Fatal-failing startup here would lock
            // the admin out of Schema Builder, so we log a warning instead
            // of throwing. Any non-empty side still gets the identifier-
            // safety check. The emitter's runtime sanity check skips
            // filters / selects against these fields at query time, so no
            // broken SQL reaches the DB.
            var missingTable  = string.IsNullOrWhiteSpace(field.SourceTable);
            var missingColumn = string.IsNullOrWhiteSpace(field.SourceColumn);
            if (missingTable || missingColumn)
            {
                var missing = (missingTable, missingColumn) switch
                {
                    (true, true)   => "SourceTable AND SourceColumn",
                    (true, false)  => "SourceTable",
                    (false, true)  => "SourceColumn",
                    _              => ""
                };
                warnings.Add(
                    $"Field '{field.Id}' (label \"{field.Label}\", domain \"{field.Domain}\") is incomplete: " +
                    $"{missing} blank and no SqlExpression. Filters/selects against this field will be skipped. " +
                    $"Fix it in Schema Builder → Fields: either fill in the missing value or add an inline SQL Expression.");
            }

            if (!missingTable)
                ValidateSimpleIdentifier(field.SourceTable, $"Field '{field.Id}' SourceTable");
            if (!missingColumn)
                ValidateSimpleIdentifier(field.SourceColumn, $"Field '{field.Id}' SourceColumn");
        }
    }

    private static void ValidateSimpleIdentifier(string value, string context)
    {
        if (!SimpleIdentifierRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{context} is not a valid identifier. Must be alphanumeric/underscore " +
                $"(optionally schema-qualified as SCHEMA.TABLE), and be at most 128 characters per segment. " +
                $"Actual value: '{value}'.");
        }
    }

    private static void RejectDangerousFragments(string fragment, string context)
    {
        foreach (var ch in DangerousChars)
        {
            if (fragment.Contains(ch))
            {
                throw new InvalidOperationException(
                    $"{context} contains forbidden character '{ch}'.");
            }
        }

        foreach (var marker in DangerousCommentMarkers)
        {
            if (fragment.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{context} contains forbidden comment marker '{marker}'.");
            }
        }

        foreach (var keyword in DangerousPatterns)
        {
            if (Regex.IsMatch(fragment, $@"\b{keyword}\b", RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{context} contains forbidden SQL keyword '{keyword}'.");
            }
        }
    }
}
