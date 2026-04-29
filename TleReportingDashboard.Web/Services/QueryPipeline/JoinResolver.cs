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
        string? primaryAlias = null,
        Action<string>? warnings = null)
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

        // Group every join by its effective target — TargetAlias / TargetTable
        // when explicitly set on the JoinDefinition, falling back to the
        // regex-extracted target from the SQL for legacy joins. A target may
        // map to multiple joins (different sources reaching the same table);
        // the precedence pass below picks among them based on the report's
        // primary table.
        var joinsByTarget = new Dictionary<string, List<JoinDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var join in schemaJoins)
        {
            foreach (var targetKey in EffectiveTargetKeys(join))
            {
                if (!joinsByTarget.TryGetValue(targetKey, out var list))
                    joinsByTarget[targetKey] = list = new List<JoinDefinition>();
                if (!list.Contains(join)) list.Add(join);
            }
        }

        // Build a directed adjacency map: source identifier → list of joins
        // that introduce a target. Both SourceAlias / SourceTable register as
        // valid keys, and any alias parsed from the ON clause that isn't the
        // join's own target also registers — so a join authored with no
        // explicit Source still appears in the adjacency under its implicit
        // dependency (the alias it references in ON other than self).
        var adjacency = new Dictionary<string, List<(JoinDefinition Join, string Target)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var join in schemaJoins)
        {
            var sources = EffectiveSources(join);
            var targets = EffectiveTargetKeys(join).ToList();
            foreach (var src in sources)
            {
                if (!adjacency.TryGetValue(src, out var list))
                    adjacency[src] = list = new List<(JoinDefinition, string)>();
                foreach (var tgt in targets)
                {
                    list.Add((join, tgt));
                }
            }
        }

        // BFS from the primary table, recording the chain of joins used to
        // reach each table/alias. Source labels drive direction (a join's
        // SQL is only valid when its source is already in scope), but the
        // pathfinding itself is direction-only — it doesn't prefer joins
        // whose source happens to match the primary.
        var joinPath = new Dictionary<string, List<JoinDefinition>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryTable };
        joinPath[primaryTable] = new List<JoinDefinition>();
        var bfs = new Queue<string>();
        bfs.Enqueue(primaryTable);
        if (!string.IsNullOrWhiteSpace(primaryAlias))
        {
            visited.Add(primaryAlias);
            joinPath[primaryAlias] = new List<JoinDefinition>();
            bfs.Enqueue(primaryAlias);
        }

        while (bfs.Count > 0)
        {
            var current = bfs.Dequeue();
            if (!adjacency.TryGetValue(current, out var edges)) continue;
            foreach (var (join, target) in edges)
            {
                if (!visited.Add(target)) continue;
                joinPath[target] = new List<JoinDefinition>(joinPath[current]) { join };
                bfs.Enqueue(target);
            }
        }

        // Assemble the result by union-ing the join paths to every required
        // table, preserving relative order and deduplicating. A required
        // alias unreachable via BFS (e.g. legacy join with no Source set
        // AND no parseable ON-clause references) falls through to a soft
        // fallback that picks any join targeting it directly.
        var result = new List<JoinDefinition>();
        var includedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedJoinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvable = new List<string>();

        foreach (var required in requiredTables)
        {
            if (joinPath.TryGetValue(required, out var path) && path.Count > 0)
            {
                foreach (var j in path)
                    if (includedIds.Add(j.Id)) result.Add(j);
                continue;
            }

            // BFS didn't reach this alias. Last-resort: any join targeting
            // it. Lets reports run when a join is authored without source
            // metadata — admins get a warning steering them to fix it.
            if (joinsByTarget.TryGetValue(required, out var fallbackCandidates) && fallbackCandidates.Count > 0)
            {
                var fallback = fallbackCandidates[0];
                if (warnedJoinIds.Add(fallback.Id))
                {
                    var srcDesc = string.Join(", ", EffectiveSources(fallback));
                    if (string.IsNullOrEmpty(srcDesc)) srcDesc = "unset";
                    warnings?.Invoke(
                        $"Join '{fallback.Id}' was selected for target '{required}' but no path from the " +
                        $"report's primary table reaches it (source(s): {srcDesc}). " +
                        "The query will run, but check the join's Source/Target metadata.");
                }
                if (includedIds.Add(fallback.Id)) result.Add(fallback);
                continue;
            }

            unresolvable.Add(required);
        }

        if (unresolvable.Count > 0)
        {
            throw new InvalidOperationException(
                $"No JOIN definition found for required table(s): {string.Join(", ", unresolvable)}. " +
                "Ensure the schema configuration includes at least one join targeting each.");
        }

        return result;
    }

    // The set of keys this join answers to as a "target". Both the alias
    // and the unqualified table name are valid because field definitions
    // sometimes reference the alias ("c") and sometimes the table name
    // ("salesforce.campaign" or just "campaign"). Falls back to regex on
    // the SQL when the explicit fields aren't populated (legacy joins).
    private static IEnumerable<string> EffectiveTargetKeys(JoinDefinition join)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(join.TargetAlias) && seen.Add(join.TargetAlias))
            yield return join.TargetAlias;
        if (!string.IsNullOrWhiteSpace(join.TargetTable) && seen.Add(join.TargetTable))
            yield return join.TargetTable;
        // Always also parse aliases from the SQL — even when TargetAlias /
        // TargetTable are populated. Catches the case where an admin set
        // TargetTable but not TargetAlias (or vice versa) so the alias is
        // still discoverable, and covers nested joins that introduce
        // additional aliases beyond the primary target.
        var extracted = ExtractTargetTable(join.Sql);
        if (extracted is not null && seen.Add(extracted)) yield return extracted;
        foreach (var alias in ExtractAllAliases(join.Sql))
            if (seen.Add(alias)) yield return alias;
    }

    // The single token that identifies this join's parent dependency. Alias
    // wins over table because admins typically reference aliases in ON
    // clauses (and field SourceTables); the table name is fallback when
    // the alias was never assigned. Returns null when neither is set —
    // the resolver treats that as "legacy" and emits a soft warning.
    private static string? EffectiveSource(JoinDefinition join)
    {
        if (!string.IsNullOrWhiteSpace(join.SourceAlias)) return join.SourceAlias;
        if (!string.IsNullOrWhiteSpace(join.SourceTable)) return join.SourceTable;
        return null;
    }

    // Every token this join depends on as a source. Always starts with the
    // explicit EffectiveSource (when set), then adds any aliases referenced
    // in the ON clause that aren't the join's own target. The implicit set
    // lets the recursive resolver discover transitive paths even on legacy
    // joins that pre-date the explicit Source/Target fields — for example,
    // an "account → campaign" join whose ON clause says "c.sfid = a.x" has
    // an implicit source of "a", which the resolver then chases back to a
    // "lead → account" join with target alias "a".
    private static List<string> EffectiveSources(JoinDefinition join)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Include BOTH SourceAlias and SourceTable when set — admins might
        // populate either or both, and downstream lookups against the
        // joinsByTarget index need to try every identifier the parent join
        // might be registered under.
        if (!string.IsNullOrWhiteSpace(join.SourceAlias) && seen.Add(join.SourceAlias))
            result.Add(join.SourceAlias);
        if (!string.IsNullOrWhiteSpace(join.SourceTable) && seen.Add(join.SourceTable))
            result.Add(join.SourceTable);

        var targetKeys = EffectiveTargetKeys(join).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var implicitRef in ExtractOnClauseAliases(join.Sql))
        {
            if (targetKeys.Contains(implicitRef)) continue; // self-reference, not a parent
            if (seen.Add(implicitRef)) result.Add(implicitRef);
        }
        return result;
    }

    // Pulls alias.column references out of the ON clause(s) of a join SQL
    // fragment. Captures the alias prefix only — that's the part that
    // matches a target alias on a parent join. Tolerant of nested joins
    // (multiple ONs in one definition) by scanning the whole SQL.
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\.\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AliasColumnRegex();

    private static IEnumerable<string> ExtractOnClauseAliases(string joinSql)
    {
        // Cheap split: find each ON clause and scan only its body. This
        // avoids matching alias.column references inside the JOIN <table>
        // part (rare but possible when admins use schema-qualified names).
        var onMatches = System.Text.RegularExpressions.Regex.Matches(
            joinSql,
            @"\bON\b\s+(.*?)(?=\bAND\b|\bJOIN\b|\bWHERE\b|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match onMatch in onMatches)
        {
            foreach (System.Text.RegularExpressions.Match m in AliasColumnRegex().Matches(onMatch.Groups[1].Value))
            {
                var alias = m.Groups[1].Value;
                // Skip SQL keywords that look like aliases.
                if (IsSqlKeyword(alias)) continue;
                if (seen.Add(alias)) yield return alias;
            }
        }
    }

    private static bool IsSqlKeyword(string token) => token.ToUpperInvariant() switch
    {
        "AND" or "OR" or "NOT" or "ON" or "AS" or "IS" or "NULL"
            or "TRUE" or "FALSE" or "IN" or "LIKE" or "BETWEEN" => true,
        _ => false
    };

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
