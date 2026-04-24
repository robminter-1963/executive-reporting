using System.Text.RegularExpressions;
using TleReportingDashboard.Web.Components.Pages;

namespace TleReportingDashboard.Web.Components.Shared;

// Builds the dropdown label for a JoinEntry:
//   "<id> — <TABLE> <alias> (sub1, sub2, …)"
// Sub-aliases are extracted from nested JOINs inside the raw SQL so a field
// editor can see which tables a given JOIN actually brings into scope.
public static class JoinLabelFormatter
{
    // Case-insensitive match for "JOIN <table> [AS] <alias>". Captures the alias.
    // Intentionally permissive — we filter out SQL keywords after the fact.
    private static readonly Regex JoinAliasRegex = new(
        @"\bJOIN\s+[\w\.\[\]""]+(?:\s+AS)?\s+([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL", "WHERE", "AND", "OR"
    };

    public static string Format(SchemaBuilder.JoinEntry j)
    {
        string head;
        var aliases = new List<string>();

        if (j.UseRawSql || string.IsNullOrWhiteSpace(j.TargetTable))
        {
            var firstLine = (j.RawSql ?? string.Empty)
                .Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            if (firstLine.Length > 60) firstLine = firstLine[..60] + "…";
            head = string.IsNullOrWhiteSpace(firstLine) ? j.Id : $"{j.Id} — {firstLine}";
        }
        else
        {
            var alias = string.IsNullOrWhiteSpace(j.Alias) ? string.Empty : $" {j.Alias}";
            head = $"{j.Id} — {j.TargetTable}{alias}";
            if (!string.IsNullOrWhiteSpace(j.Alias)) aliases.Add(j.Alias);
        }

        // Scan raw SQL for nested JOIN aliases and the leading alias (which the
        // top-level "JOIN <table> <alias>" pattern also emits — we dedup below).
        foreach (var a in ExtractAliases(j.RawSql))
            aliases.Add(a);

        // Dedup (case-insensitive), preserve order, drop whatever already shows in head.
        var headAlias = j.Alias;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(headAlias)) seen.Add(headAlias);

        var extras = new List<string>();
        foreach (var a in aliases)
        {
            if (seen.Add(a)) extras.Add(a);
        }

        return extras.Count > 0
            ? $"{head} ({string.Join(", ", extras)})"
            : head;
    }

    private static IEnumerable<string> ExtractAliases(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) yield break;
        foreach (Match m in JoinAliasRegex.Matches(sql))
        {
            var alias = m.Groups[1].Value;
            if (!SqlKeywords.Contains(alias))
                yield return alias;
        }
    }
}
