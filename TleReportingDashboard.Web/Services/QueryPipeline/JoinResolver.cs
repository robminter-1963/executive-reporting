using System.Text.RegularExpressions;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public static partial class JoinResolver
{
    // Matches: [LEFT|RIGHT|INNER|CROSS|FULL] [OUTER] JOIN <TableName> ...
    [GeneratedRegex(@"\bJOIN\s+(\[?[A-Za-z_][A-Za-z0-9_]*\]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JoinTargetTableRegex();

    public static List<JoinDefinition> ResolveJoins(
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<JoinDefinition> schemaJoins,
        string primaryTable,
        string? primaryAlias = null)
    {
        if (string.IsNullOrWhiteSpace(primaryTable))
        {
            throw new ArgumentException(
                "Primary Table is required. Set it on the report before running.",
                nameof(primaryTable));
        }
        // Collect all tables referenced by the resolved fields.
        // Fields with an inline SqlExpression manage their own table references inside
        // the expression — their SourceTable is not authoritative and must not drive
        // join inclusion.
        var requiredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.SqlExpression))
                continue;
            requiredTables.Add(field.SourceTable);
        }

        // The primary table never needs a JOIN. Remove both the full name and
        // the alias (if set) — admins typically author fields to reference the
        // alias (e.g. SourceTable="L" when the root is "EMPOWER.LN_MTGTERMS L").
        requiredTables.Remove(primaryTable);
        if (!string.IsNullOrWhiteSpace(primaryAlias))
            requiredTables.Remove(primaryAlias);

        if (requiredTables.Count == 0)
            return [];

        // Build a mapping from target table/alias -> JoinDefinition by parsing each join's SQL
        var joinByTable = new Dictionary<string, JoinDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var join in schemaJoins)
        {
            var targetTable = ExtractTargetTable(join.Sql);
            if (targetTable is not null)
                joinByTable.TryAdd(targetTable, join);

            // For complex/nested joins, also map all inner aliases to this join
            foreach (var alias in ExtractAllAliases(join.Sql))
                joinByTable.TryAdd(alias, join);
        }

        var result = new List<JoinDefinition>();
        foreach (var table in requiredTables)
        {
            if (joinByTable.TryGetValue(table, out var join))
            {
                result.Add(join);
            }
            else
            {
                throw new InvalidOperationException(
                    $"No JOIN definition found for required table '{table}'. " +
                    "Ensure the schema configuration includes a join for this table.");
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts all table aliases from a JOIN SQL, including nested joins.
    /// Matches patterns like "JOIN SCHEMA.TABLE ALIAS" or "JOIN SCHEMA.TABLE AS ALIAS".
    /// </summary>
    private static List<string> ExtractAllAliases(string joinSql)
    {
        var aliases = new List<string>();
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ON", "WITH", "LEFT", "INNER", "RIGHT", "CROSS", "FULL", "OUTER", "AND", "OR", "WHERE", "SET" };

        var matches = System.Text.RegularExpressions.Regex.Matches(joinSql,
            @"\bJOIN\s+[\w.]+\s+(?:AS\s+)?(\w+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var alias = m.Groups[1].Value;
            if (!keywords.Contains(alias))
                aliases.Add(alias);
        }
        return aliases;
    }

    private static string? ExtractTargetTable(string joinSql)
    {
        var match = JoinTargetTableRegex().Match(joinSql);
        if (!match.Success)
            return null;

        // Strip brackets if present (e.g., [BORROWER] -> BORROWER)
        var tableName = match.Groups[1].Value;
        return tableName.Trim('[', ']');
    }
}
