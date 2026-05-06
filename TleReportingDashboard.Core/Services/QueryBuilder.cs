using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

/// <summary>
/// SECURITY CRITICAL: Builds parameterized SQL queries from validated field configurations.
/// All user-supplied values MUST become SqlParameters — zero string interpolation of user values.
/// Table/column identifiers from config DB are validated against a safe identifier pattern.
/// </summary>
public static class QueryBuilder
{
    private static readonly Regex SafeIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(\.[A-Za-z_][A-Za-z0-9_]{0,127})?$", RegexOptions.Compiled);

    // Detects whether a SqlExpression is an outer-level aggregate like
    // SUM/COUNT/AVG/MIN/MAX/STDDEV/VAR. The heuristic:
    //   1. Must contain one of those functions followed by "(".
    //   2. Must NOT contain a SELECT keyword anywhere — if it does, the
    //      aggregate sits inside a scalar subquery such as
    //      "(SELECT SUM(x) FROM …)" and evaluates as a row-level value, not
    //      an outer aggregate over the main query.
    // This lets us drive auto-GROUP-BY for hand-authored aggregate measures
    // ("SUM(CASE WHEN …)", "COUNT(*)") without dragging every report that uses
    // a scalar-subquery measure into an aggregated query.
    private static readonly Regex AggregateExpressionRegex = new(
        @"\b(SUM|COUNT|AVG|MIN|MAX|STDDEV|VAR|VARIANCE)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContainsSelectKeywordRegex = new(
        @"\bSELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAggregateField(FieldConfig field)
    {
        if (string.IsNullOrWhiteSpace(field.SqlExpression)) return false;
        if (ContainsSelectKeywordRegex.IsMatch(field.SqlExpression)) return false;
        return AggregateExpressionRegex.IsMatch(field.SqlExpression);
    }
    private static readonly HashSet<string> AllowedJoinTypes = new(StringComparer.OrdinalIgnoreCase)
        { "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "LEFT OUTER JOIN", "RIGHT OUTER JOIN" };

    private static void ValidateIdentifier(string value, string context)
    {
        if (!SafeIdentifier.IsMatch(value))
            throw new ArgumentException($"Invalid SQL identifier in {context}: '{value}'");
    }

    private static void ValidateJoinType(string joinType)
    {
        if (!AllowedJoinTypes.Contains(joinType))
            throw new ArgumentException($"Invalid join type: '{joinType}'");
    }

    // Qualifies the request's FallbackSortColumns (raw PK column names) with
    // the primary table's alias (or bare name when none) so they're safe to
    // drop into an ORDER BY. Each name is identifier-validated — the values
    // come from information_schema, but the trust boundary still lives here.
    // Returns an empty list when no PKs were shipped, so callers can do a
    // simple null/empty check.
    private static List<string> BuildPkOrderExpressions(
        IList<string>? fallbackSortColumns, string? primaryAlias, string primaryName)
    {
        var result = new List<string>();
        if (fallbackSortColumns is null || fallbackSortColumns.Count == 0) return result;
        var aliasOrName = !string.IsNullOrWhiteSpace(primaryAlias) ? primaryAlias : primaryName;
        foreach (var col in fallbackSortColumns)
        {
            if (string.IsNullOrWhiteSpace(col)) continue;
            ValidateIdentifier(col, "FallbackSortColumns");
            result.Add(string.IsNullOrWhiteSpace(aliasOrName) ? col : $"{aliasOrName}.{col}");
        }
        return result;
    }

    // Topologically sorts a list of JOIN SQL fragments. A fragment B is placed
    // after fragment A when B's ON/SQL references an alias defined by A. Self-
    // references and references to unknown aliases (primary table, external
    // schema) are ignored. Cycles (shouldn't happen in practice) fall back to
    // the original insertion order.
    private static readonly Regex DefinedAliasRegex = new(
        @"\bJOIN\s+[\w\.\[\]""]+(?:\s+AS)?\s+([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReferencedAliasRegex = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z_]",
        RegexOptions.Compiled);
    private static readonly HashSet<string> JoinSqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL", "WHERE", "AND", "OR", "JOIN", "AS", "WITH"
    };

    internal static List<string> SortJoinFragmentsByDependency(List<string> fragments)
    {
        if (fragments.Count < 2) return new List<string>(fragments);

        var info = fragments
            .Select((sql, idx) => new
            {
                Idx = idx,
                Sql = sql,
                Defined = ExtractAliases(sql, DefinedAliasRegex),
                Referenced = ExtractAliases(sql, ReferencedAliasRegex)
            })
            .ToList();

        // Pre-compute which aliases are defined by the fragment set so unknown
        // references (primary table, schema-level aliases) don't create edges.
        var allDefined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in info) allDefined.UnionWith(i.Defined);

        var result = new List<string>(info.Count);
        var used = new HashSet<int>();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (result.Count < info.Count)
        {
            var progress = false;
            foreach (var item in info)
            {
                if (used.Contains(item.Idx)) continue;

                var hasUnresolvedDep = item.Referenced
                    .Any(a => allDefined.Contains(a)
                              && !item.Defined.Contains(a)
                              && !resolved.Contains(a));
                if (hasUnresolvedDep) continue;

                result.Add(item.Sql);
                used.Add(item.Idx);
                foreach (var a in item.Defined) resolved.Add(a);
                progress = true;
            }
            if (!progress)
            {
                // Cycle / unresolvable — emit remaining in original order to avoid
                // silently dropping anything.
                foreach (var item in info)
                    if (!used.Contains(item.Idx))
                    {
                        result.Add(item.Sql);
                        used.Add(item.Idx);
                    }
                break;
            }
        }
        return result;
    }

    private static HashSet<string> ExtractAliases(string sql, Regex rx)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in rx.Matches(sql))
        {
            var a = m.Groups[1].Value;
            if (!JoinSqlKeywords.Contains(a)) set.Add(a);
        }
        return set;
    }

    // SQL keywords that follow a JOIN's table name and would otherwise be
    // captured as if they were the alias (e.g. "JOIN tbl ON …" matches "ON"
    // as the alias group). Used to reject false positives in any
    // alias-extraction regex below.
    private static readonly HashSet<string> _joinAliasKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "ON", "WITH", "LEFT", "INNER", "RIGHT", "CROSS", "FULL", "OUTER", "AND", "OR" };

    private static bool ContainsAlias(string rawSql, string alias)
    {
        var matches = Regex.Matches(rawSql, @"\bJOIN\s+[\w.]+\s+(?:AS\s+)?(\w+)\b", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var a = m.Groups[1].Value;
            if (!_joinAliasKeywords.Contains(a) && a.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    /// <summary>
    /// Builds a parameterized SQL query from the request, using only whitelisted field configurations.
    /// </summary>
    /// <param name="request">The query request with field IDs, filters, sort, and pagination.</param>
    /// <param name="fieldConfigs">The whitelisted field configuration from the config database.</param>
    /// <param name="joinConfigs">The join configuration from the config database.</param>
    /// <returns>A tuple of the SQL string and its associated parameters (DbParameter — concrete type matches the passed dialect).</returns>
    /// <exception cref="ArgumentException">Thrown if any field ID is not in the whitelist or field list is empty.</exception>
    public static (string sql, List<System.Data.Common.DbParameter> parameters) BuildQuery(
        QueryRequest request,
        List<FieldConfig> fieldConfigs,
        List<JoinConfig> joinConfigs,
        List<CustomFilterDefinition>? customFilters = null,
        List<LookupDefinition>? lookups = null,
        TleReportingDashboard.Web.Services.QueryPipeline.ISqlDialect? dialect = null,
        string? primaryTableOverride = null)
    {
        // Per-report SQL-mode calc columns ride on the request so every call
        // site (QueryService, ReportBuilder preview, detail viewer, tests)
        // picks them up without a signature change.
        var tableCalcSqlColumns = request.TableCalcSqlColumns;
        // Default to SQL Server dialect when caller didn't supply one —
        // preserves the historical behavior for any call site that hasn't
        // been updated yet. QueryService and SqlEmitter now always pass
        // an explicit dialect based on the request's connection id.
        dialect ??= new TleReportingDashboard.Web.Services.QueryPipeline.SqlServerDialect();
        // Primary table is required per-report — no assumed default. Caller
        // (QueryService / SqlEmitter) must resolve it from request.PrimaryTable
        // and surface a clear error to the user when blank.
        if (string.IsNullOrWhiteSpace(primaryTableOverride))
        {
            throw new ArgumentException(
                "Primary Table is required. Set it on the report before running.",
                nameof(primaryTableOverride));
        }
        var rootTable = primaryTableOverride;
        // Validate: field list must not be empty
        if (request.FieldIds == null || request.FieldIds.Count == 0)
        {
            throw new ArgumentException("At least one field must be specified in the request.");
        }

        // Deduplicate field IDs while preserving order
        var dedupedFieldIds = request.FieldIds.Distinct().ToList();

        // Build a lookup for fast validation. First-wins dedupe matches
        // SchemaService.GetFieldConfigsAsync — defensive against any caller
        // that passes a non-deduped list directly.
        var fieldLookup = fieldConfigs
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Validate every field ID exists in the whitelist
        var unknownFields = dedupedFieldIds.Where(id => !fieldLookup.ContainsKey(id)).ToList();
        if (unknownFields.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown field IDs: {string.Join(", ", unknownFields)}. All field IDs must exist in the field configuration whitelist.");
        }

        // Validate filter field IDs (including date range suffixes _start/_end)
        if (request.Filters != null && request.Filters.Count > 0)
        {
            var unknownFilterFields = request.Filters.Keys
                .Select(k => k.EndsWith("_start") ? k[..^6] : k.EndsWith("_end") ? k[..^4] : k)
                .Where(id => !fieldLookup.ContainsKey(id))
                .Distinct()
                .ToList();
            if (unknownFilterFields.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown filter field IDs: {string.Join(", ", unknownFilterFields)}. All filter field IDs must exist in the field configuration whitelist.");
            }
        }

        // Resolve fields to their table.column references and validate identifiers.
        // Inline-SQL fields manage their own table/column references inside SqlExpression
        // (admin-curated, same trust model as JoinDefinition.Sql) — skip identifier validation.
        var resolvedFields = dedupedFieldIds.Select(id => fieldLookup[id]).ToList();

        // Sort-only fields. The Report Builder lets admins pick a Sort By
        // / Then By field that isn't part of the report's visible columns
        // (useful for "sort by created_at on a Status / Name report" or
        // "tiebreak by an unselected unique field under DISTINCT"). Pull
        // any such field into the SELECT list so it's reachable from the
        // ORDER BY clause — required under SELECT DISTINCT, harmless
        // otherwise. The QueryService column-metadata path keys off
        // request.FieldIds, NOT resolvedFields, so these silent additions
        // don't surface as grid columns.
        foreach (var sortFid in new[] { request.SortField, request.SecondarySortField })
        {
            if (string.IsNullOrWhiteSpace(sortFid)) continue;
            if (resolvedFields.Any(f => string.Equals(f.Id, sortFid, StringComparison.OrdinalIgnoreCase))) continue;
            // Calc keys aren't field ids — they ride on tableCalcSqlColumns
            // and are validated separately.
            if (tableCalcSqlColumns?.Any(c => string.Equals(c.Key, sortFid, StringComparison.OrdinalIgnoreCase)) == true) continue;
            if (!fieldLookup.TryGetValue(sortFid, out var sortField)) continue;
            resolvedFields.Add(sortField);
        }

        foreach (var field in resolvedFields)
        {
            if (!string.IsNullOrWhiteSpace(field.SqlExpression))
                continue;
            ValidateIdentifier(field.SourceTable, $"FieldConfig '{field.Id}' SourceTable");
            ValidateIdentifier(field.SourceColumn, $"FieldConfig '{field.Id}' SourceColumn");
        }

        // Determine required tables from selected fields AND any field
        // referenced by a filter (legacy Filters dictionary + advanced
        // ad-hoc filters + the relative-date DateFieldId). Fields with an
        // inline SqlExpression manage their own table references — skip
        // them so their implicit source-table doesn't trigger a spurious
        // join.
        var requiredTables = new HashSet<string>(
            resolvedFields
                .Where(f => string.IsNullOrWhiteSpace(f.SqlExpression))
                .Select(f => f.SourceTable),
            StringComparer.OrdinalIgnoreCase);

        void AddFilterFieldTable(string? fieldId)
        {
            if (string.IsNullOrEmpty(fieldId)) return;
            if (!fieldLookup.TryGetValue(fieldId, out var ff)) return;
            if (!string.IsNullOrWhiteSpace(ff.SqlExpression)) return;
            if (!string.IsNullOrWhiteSpace(ff.SourceTable))
                requiredTables.Add(ff.SourceTable);
        }

        if (request.Filters is not null)
        {
            foreach (var key in request.Filters.Keys)
            {
                var baseId = key.EndsWith("_start") ? key[..^6]
                           : key.EndsWith("_end")   ? key[..^4]
                           : key;
                AddFilterFieldTable(baseId);
            }
        }

        AddFilterFieldTable(request.DateFieldId);

        if (request.AdvancedFilters is not null)
        {
            // Walk every leaf in the advanced-filter tree so nested groups
            // contribute their filter fields to the join requirements.
            TleReportingDashboard.Web.Services.QueryPipeline.AdvancedFilterSqlEmitter.Walk(
                request.AdvancedFilters,
                c => AddFilterFieldTable(c.FieldId));
        }

        // Row-level scoping predicate may reference a field on a joined
        // table (e.g. the primary is LN_MTGTERMS but the owner column is
        // LOAN.loan_officer_id). Register the owner field's source table
        // so JoinResolver pulls its join(s) into the chain — otherwise the
        // WHERE references an unjoined table and SQL blows up.
        if (request.Scoping?.OwnerFieldId is string ownerFieldIdForJoins)
        {
            AddFilterFieldTable(ownerFieldIdForJoins);
        }

        // Same problem for team scope: each TeamScopeEntry's owner column
        // may be qualified with an alias for a joined table that isn't
        // referenced by any selected/filtered field. Register those aliases
        // here so the JOIN-collection loop pulls them in. Bare column names
        // (no alias prefix) skip this — the emitter falls back to the
        // primary alias and no extra join is needed.
        if (request.Scoping?.TeamScope is { Teams.Count: > 0 } teamScope)
        {
            foreach (var entry in teamScope.Teams)
            {
                var dot = entry.OwnerColumn.IndexOf('.');
                if (dot <= 0) continue;
                var alias = entry.OwnerColumn[..dot].Trim();
                if (string.IsNullOrEmpty(alias)) continue;
                requiredTables.Add(alias);
            }
        }

        // Same problem for direct-column scope (Worker Individual fan-out):
        // a pre-qualified owner column like "BORR.LOAN_NO" needs the BORR
        // table joined in even when no selected field references it.
        if (request.Scoping is { OwnerColumn: string oc } && !string.IsNullOrWhiteSpace(oc))
        {
            var dot = oc.IndexOf('.');
            if (dot > 0)
            {
                var alias = oc[..dot].Trim();
                if (!string.IsNullOrEmpty(alias))
                    requiredTables.Add(alias);
            }
        }

        var sb = new StringBuilder();
        var parameters = new List<System.Data.Common.DbParameter>();

        // Resolve all required lookups + inline preambles (deduplicated)
        var lookupMap = (lookups ?? []).ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        var preambles = new List<string>();
        var lookupJoins = new List<string>();
        var seenLookups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ResolveLookups(List<string>? ids)
        {
            if (ids is null) return;
            foreach (var id in ids)
            {
                if (!seenLookups.Add(id) || !lookupMap.TryGetValue(id, out var lu)) continue;
                preambles.Add(lu.SqlPreamble);
                if (!string.IsNullOrWhiteSpace(lu.SqlJoin))
                    lookupJoins.Add(lu.SqlJoin);
            }
        }

        foreach (var f in resolvedFields)
        {
            ResolveLookups(f.LookupIds);
            if (!string.IsNullOrWhiteSpace(f.SqlPreamble) && !preambles.Contains(f.SqlPreamble!))
                preambles.Add(f.SqlPreamble!);
        }
        if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0 && customFilters is not null)
        {
            foreach (var cf in customFilters.Where(f =>
                request.CustomFilterIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase)))
            {
                ResolveLookups(cf.LookupIds);
                if (!string.IsNullOrWhiteSpace(cf.SqlPreamble) && !preambles.Contains(cf.SqlPreamble!))
                    preambles.Add(cf.SqlPreamble!);
            }
        }
        if (preambles.Count > 0)
            sb.AppendLine($"WITH {string.Join(",\n", preambles)}");

        // Detect whether any selected field is an aggregate expression. When
        // true we'll emit a GROUP BY below and also inject a hidden
        // COUNT(*) AS __row_count column so the dashboard has a real
        // per-group count (the client-side row count would always be 1 when
        // the server already aggregates).
        var willGroupBy = resolvedFields.Any(IsAggregateField);

        // SELECT clause. DISTINCT is opt-in per-report — useful when a
        // one-to-many LEFT JOIN multiplies the parent rows and the admin
        // only cares about the distinct outer rows. Skipped automatically
        // when willGroupBy is true (GROUP BY already deduplicates by the
        // grouped key set, so DISTINCT is redundant noise on those plans
        // — and the planner's de-dupe path is usually slower than the
        // grouping it'd be paired with).
        sb.Append("SELECT ");
        if (request.Distinct && !willGroupBy)
            sb.Append("DISTINCT ");
        var selectColumns = resolvedFields
            .Select(f => $"{f.GetSqlExpression()} AS {dialect.QuoteIdentifier(f.Id)}")
            .ToList();
        if (willGroupBy)
            selectColumns.Add($"COUNT(*) AS {dialect.QuoteIdentifier("__row_count")}");

        // Per-report SQL-mode calc columns. Trust model: admin-authored
        // raw SQL embedded as-is, same as FieldConfig.SqlExpression. Each
        // becomes "<sql> AS <key>" and shows up in the row dict under Key.
        // Skipped silently when the report aggregates — calc columns target
        // the table view (raw rows) and would otherwise need GROUP BY logic
        // we don't currently emit.
        if (tableCalcSqlColumns is { Count: > 0 } && !willGroupBy)
        {
            foreach (var calc in tableCalcSqlColumns)
            {
                if (string.IsNullOrWhiteSpace(calc.Key) || string.IsNullOrWhiteSpace(calc.SqlExpression))
                    continue;
                ValidateIdentifier(calc.Key, $"TableCalcSqlSelector '{calc.Key}' Key");
                selectColumns.Add($"{calc.SqlExpression} AS {dialect.QuoteIdentifier(calc.Key)}");
            }
        }

        // DISTINCT + ORDER BY safety net: Postgres rejects "for SELECT
        // DISTINCT, ORDER BY expressions must appear in select list" when
        // the ORDER BY references something that isn't projected. The
        // sort-only-field add-on above covers the case where the admin
        // sorts by an out-of-report column (the field's GetSqlExpression()
        // gets pulled into SELECT). It does NOT cover the case where the
        // field IS in the report but its ORDER BY uses GetSortExpression()
        // — typical when a status field's lookup auto-set
        // SortExpression="LOOKUP.ORDERBY". SELECT has "c_30.CODEDESC AS
        // loan_status"; ORDER BY emits "LOOKUP.ORDERBY"; Postgres errors.
        // Inject any ORDER BY expression text that doesn't text-match an
        // existing SELECT expression OR alias as a hidden "__sort_N"
        // column. Skipped when not DISTINCT — the bare ORDER BY rule is
        // permissive without it.
        if (request.Distinct && !willGroupBy)
        {
            var existingSelectExprs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in selectColumns)
            {
                var asIdx = col.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
                if (asIdx > 0)
                {
                    existingSelectExprs.Add(col[..asIdx].Trim());
                    existingSelectExprs.Add(col[(asIdx + 4)..].Trim());
                }
                else
                {
                    existingSelectExprs.Add(col.Trim());
                }
            }

            string? ResolveSortExpressionEarly(string fid)
            {
                if (fieldLookup.TryGetValue(fid, out var f)) return f.GetSortExpression();
                if (tableCalcSqlColumns is { Count: > 0 } &&
                    tableCalcSqlColumns.Any(c => string.Equals(c.Key, fid, StringComparison.OrdinalIgnoreCase)))
                {
                    return dialect.QuoteIdentifier(fid);
                }
                return null;
            }

            var sortInjectIdx = 0;
            foreach (var fid in new[] { request.SortField, request.SecondarySortField })
            {
                if (string.IsNullOrWhiteSpace(fid)) continue;
                var sortExpr = ResolveSortExpressionEarly(fid);
                if (string.IsNullOrWhiteSpace(sortExpr)) continue;
                if (existingSelectExprs.Contains(sortExpr)) continue;
                var alias = $"__sort_{sortInjectIdx++}";
                selectColumns.Add($"{sortExpr} AS {dialect.QuoteIdentifier(alias)}");
                existingSelectExprs.Add(sortExpr);
                existingSelectExprs.Add(dialect.QuoteIdentifier(alias));
            }
        }

        sb.AppendLine(string.Join(", ", selectColumns));

        // FROM clause
        sb.AppendLine($"FROM {rootTable}");

        // JOIN clauses — only for tables other than the primary.
        // Collect every JOIN fragment first (deduped), then topo-sort so that any
        // fragment whose ON clause references another fragment's alias is emitted
        // AFTER the fragment defining that alias. Previously we emitted as we went,
        // which could place a dependent JOIN before its dependency.
        //
        // The previous primary-exclusion check compared each requiredTables
        // entry to the raw rootTable string ("schema.table AS alias"), so an
        // alias like "l" never matched and a join targeting the primary would
        // get emitted as a redundant self-join with the same alias = invalid
        // SQL. Build the full identifier set up front and check membership.
        var (primaryName, primaryAlias) = PrimaryTableRef.Parse(rootTable);
        var primaryIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(rootTable))      primaryIdentifiers.Add(rootTable);
        if (!string.IsNullOrWhiteSpace(primaryName))    primaryIdentifiers.Add(primaryName);
        if (!string.IsNullOrWhiteSpace(primaryAlias))   primaryIdentifiers.Add(primaryAlias!);
        if (!string.IsNullOrWhiteSpace(primaryName))
        {
            // Bare table name without schema prefix ("salesforce.lead" → "lead").
            var dot = primaryName.LastIndexOf('.');
            if (dot >= 0)
            {
                var bare = primaryName[(dot + 1)..].Trim('[', ']');
                if (!string.IsNullOrWhiteSpace(bare)) primaryIdentifiers.Add(bare);
            }
        }

        // Primary-scoped joins (PR-2). A join may carry an optional
        // PrimaryTable / PrimaryAlias tag indicating it should ONLY be
        // considered when the report's primary matches. Keep:
        //   * generic joins (PrimaryTable null/empty), and
        //   * tagged joins whose PrimaryTable or PrimaryAlias matches one
        //     of the report's primaryIdentifiers.
        // Drop tagged joins whose primary doesn't match — they belong to a
        // different report and would otherwise pollute the adjacency graph
        // (causing duplicate-alias collisions when two joins target the
        // same alias from different sources).
        // Then sort so tagged-matching come BEFORE generic, so when the
        // BFS picks the first edge to a target, a primary-scoped override
        // wins over the generic fallback.
        bool MatchesPrimaryScope(JoinConfig j)
        {
            if (string.IsNullOrWhiteSpace(j.PrimaryTable) && string.IsNullOrWhiteSpace(j.PrimaryAlias))
                return true; // generic
            if (!string.IsNullOrWhiteSpace(j.PrimaryTable) && primaryIdentifiers.Contains(j.PrimaryTable!))
                return true;
            if (!string.IsNullOrWhiteSpace(j.PrimaryAlias) && primaryIdentifiers.Contains(j.PrimaryAlias!))
                return true;
            return false;
        }
        bool IsTaggedScoped(JoinConfig j) =>
            !string.IsNullOrWhiteSpace(j.PrimaryTable) || !string.IsNullOrWhiteSpace(j.PrimaryAlias);

        var scopedJoinConfigs = joinConfigs
            .Where(MatchesPrimaryScope)
            .OrderByDescending(IsTaggedScoped) // tagged-matching first, generic after
            .ToList();

        var joinedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootTable };
        var emittedJoinIds = new HashSet<int>();
        var emittedJoinSql = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var joinFragments = new List<string>();

        void AddFragment(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            if (emittedJoinSql.Add(sql.Trim())) joinFragments.Add(sql);
        }

        // Skip any join whose target is the primary — it'd self-join the
        // primary to itself with the same alias. Two checks because the
        // target can live in either place:
        //   * Structured joins: j.ToTable holds the schema-qualified name.
        //   * Raw-SQL-only joins: j.ToTable is empty and the target lives
        //     inside RawSql. Peek the first JOIN's table + alias and match
        //     either against the primary's identifiers. The (?:AS\s+)?
        //     branch handles both "JOIN ... AS l" and bare "JOIN ... l".
        bool TargetsPrimary(JoinConfig j)
        {
            if (!string.IsNullOrEmpty(j.ToTable) && primaryIdentifiers.Contains(j.ToTable))
                return true;
            if (string.IsNullOrEmpty(j.RawSql)) return false;

            var m = Regex.Match(j.RawSql,
                @"\bJOIN\s+(?<table>[\w\.\[\]""]+)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][A-Za-z0-9_]*))?\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;

            var rawTable = m.Groups["table"].Value.Trim('[', ']', '"');
            if (primaryIdentifiers.Contains(rawTable)) return true;
            // Strip schema prefix to compare bare names ("salesforce.lead" → "lead").
            var dot = rawTable.LastIndexOf('.');
            if (dot >= 0)
            {
                var bare = rawTable[(dot + 1)..].Trim('[', ']', '"');
                if (primaryIdentifiers.Contains(bare)) return true;
            }
            // Alias capture is optional (no-AS form). Skip SQL-keyword
            // false positives (e.g. "JOIN tbl ON …" captures "ON" as alias).
            if (m.Groups["alias"].Success)
            {
                var a = m.Groups["alias"].Value;
                if (!_joinAliasKeywords.Contains(a) && primaryIdentifiers.Contains(a))
                    return true;
            }
            return false;
        }

        // Build a graph of joins keyed by source alias, then BFS from the
        // primary table to find the chain of joins needed to reach each
        // required table. Replaces the previous "first match by target"
        // approach, which couldn't handle multi-hop paths like
        // lead → account → campaign (only "campaign" is in requiredTables,
        // but you also need the lead → account join to bring "a" into
        // scope before account → campaign can reference it).
        //
        // Sources of a join: aliases referenced in its ON clause that AREN'T
        // its own targets. Targets of a join: aliases it introduces (defined
        // by `JOIN x AS alias`) plus its parsed ToTable when set. Both are
        // computed from the JoinConfig — the parser may have left FromTable
        // empty, so we lean on the RawSql for ground truth.
        IEnumerable<string> JoinDefinedTargets(JoinConfig j)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(j.ToTable) && seen.Add(j.ToTable)) yield return j.ToTable;
            if (!string.IsNullOrEmpty(j.RawSql))
            {
                foreach (Match m in DefinedAliasRegex.Matches(j.RawSql))
                {
                    var a = m.Groups[1].Value;
                    if (JoinSqlKeywords.Contains(a)) continue;
                    if (seen.Add(a)) yield return a;
                }
            }
        }

        IEnumerable<string> JoinSourceAliases(JoinConfig j)
        {
            // Aliases referenced in the join's RawSql that aren't its own
            // targets. These are the "parent" dependencies — what must be
            // in scope before this join can run.
            if (string.IsNullOrEmpty(j.RawSql)) yield break;
            var selfTargets = new HashSet<string>(JoinDefinedTargets(j), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ReferencedAliasRegex.Matches(j.RawSql))
            {
                var a = m.Groups[1].Value;
                if (JoinSqlKeywords.Contains(a)) continue;
                if (selfTargets.Contains(a)) continue;
                if (seen.Add(a)) yield return a;
            }
        }

        var joinAdjacency = new Dictionary<string, List<(JoinConfig Join, List<string> Targets)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var jc in scopedJoinConfigs)
        {
            if (TargetsPrimary(jc)) continue; // never bring the primary in via a JOIN
            var sources = JoinSourceAliases(jc).ToList();
            var targets = JoinDefinedTargets(jc).ToList();
            if (targets.Count == 0) continue; // can't reach anything
            foreach (var src in sources)
            {
                if (!joinAdjacency.TryGetValue(src, out var list))
                    joinAdjacency[src] = list = new List<(JoinConfig, List<string>)>();
                list.Add((jc, targets));
            }
        }

        // BFS from every primary identifier (full name, alias, bare name)
        // so a join authored against any of those forms gets discovered.
        var joinPath = new Dictionary<string, List<JoinConfig>>(StringComparer.OrdinalIgnoreCase);
        var visitedBfs = new HashSet<string>(primaryIdentifiers, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in primaryIdentifiers) joinPath[pid] = new List<JoinConfig>();
        var bfsQueue = new Queue<string>(primaryIdentifiers);
        while (bfsQueue.Count > 0)
        {
            var cur = bfsQueue.Dequeue();
            if (!joinAdjacency.TryGetValue(cur, out var edges)) continue;
            foreach (var (jc, targets) in edges)
            {
                foreach (var tgt in targets)
                {
                    if (!visitedBfs.Add(tgt)) continue;
                    joinPath[tgt] = new List<JoinConfig>(joinPath[cur]) { jc };
                    bfsQueue.Enqueue(tgt);
                }
            }
        }

        // Emit the path joins for every required table. Joins are added to
        // emittedJoinIds so the secondary per-field JoinIds loop later in
        // this method doesn't double-emit them.
        foreach (var table in requiredTables.Where(t => !primaryIdentifiers.Contains(t)))
        {
            if (joinPath.TryGetValue(table, out var path) && path.Count > 0)
            {
                foreach (var jc in path)
                {
                    if (!emittedJoinIds.Add(jc.Id)) continue;
                    AddFragment(jc.RawSql);
                    foreach (var tgt in JoinDefinedTargets(jc)) joinedTables.Add(tgt);
                }
                joinedTables.Add(table);
                continue;
            }

            // BFS didn't reach this table — fall back to the legacy
            // "first match" picker so reports with joins authored before
            // the source-aware resolver still run. The SortJoinFragmentsByDependency
            // pass downstream will reorder fragments topologically.
            var joinConfig = scopedJoinConfigs.FirstOrDefault(j =>
                !TargetsPrimary(j) &&
                (string.Equals(j.ToTable, table, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(j.FromTable, table, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(j.RawSql) && ContainsAlias(j.RawSql, table))));

            if (joinConfig != null && emittedJoinIds.Add(joinConfig.Id))
            {
                if (!string.IsNullOrEmpty(joinConfig.RawSql))
                {
                    AddFragment(joinConfig.RawSql);
                }
                else
                {
                    // Reconstruct from parts (legacy JoinConfig without RawSql)
                    ValidateJoinType(joinConfig.JoinType);
                    ValidateIdentifier(joinConfig.FromTable, "JoinConfig FromTable");
                    ValidateIdentifier(joinConfig.FromColumn, "JoinConfig FromColumn");
                    ValidateIdentifier(joinConfig.ToTable, "JoinConfig ToTable");
                    ValidateIdentifier(joinConfig.ToColumn, "JoinConfig ToColumn");

                    string fromTable, fromColumn, toTable, toColumn;
                    if (string.Equals(joinConfig.FromTable, rootTable, StringComparison.OrdinalIgnoreCase))
                    {
                        fromTable = joinConfig.FromTable;
                        fromColumn = joinConfig.FromColumn;
                        toTable = joinConfig.ToTable;
                        toColumn = joinConfig.ToColumn;
                    }
                    else
                    {
                        fromTable = joinConfig.ToTable;
                        fromColumn = joinConfig.ToColumn;
                        toTable = joinConfig.FromTable;
                        toColumn = joinConfig.FromColumn;
                    }

                    AddFragment($"{joinConfig.JoinType} {toTable} ON {fromTable}.{fromColumn} = {toTable}.{toColumn}");
                }
                joinedTables.Add(table);
            }
        }

        // JOINs from resolved lookups
        foreach (var lj in lookupJoins) AddFragment(lj);

        // Single lookup used by both fields and custom filters to resolve JoinId
        // references (avoids rebuilding the dictionary twice). Skip joins without a
        // JoinId — those can't be referenced and would collide on the empty key.
        // Built off the scoped list so a JoinId tagged to a different primary
        // can't be pulled in via an explicit field/filter reference.
        // First-wins on duplicate JoinIds across scopes — and because the scoped
        // list is sorted tagged-matching-first, a primary-scoped variant beats a
        // generic one with the same JoinId.
        var joinConfigLookup = scopedJoinConfigs
            .Where(j => !string.IsNullOrWhiteSpace(j.JoinId))
            .GroupBy(j => j.JoinId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // True when any of the join's defined targets (ToTable + aliases
        // extracted from RawSql) is already in scope. Used to skip
        // explicit JoinId references whose target is already covered by
        // another join (typically a composite raw-SQL join that nests the
        // target). Without this, a second "JOIN salesforce.user u …" gets
        // emitted alongside the one nested in account's composite join,
        // producing a duplicate-alias error from Postgres.
        bool TargetAlreadyJoined(JoinConfig j)
        {
            foreach (var tgt in JoinDefinedTargets(j))
            {
                if (joinedTables.Contains(tgt)) return true;
            }
            return false;
        }

        // Records every alias a freshly-added fragment introduces so
        // subsequent JoinId lookups can skip duplicates. Mirrors what the
        // BFS path does after AddFragment(jc.RawSql).
        void RegisterTargets(JoinConfig j)
        {
            foreach (var tgt in JoinDefinedTargets(j)) joinedTables.Add(tgt);
        }

        // JOINs from fields — each referenced join id + any inline SqlJoin.
        // The requiredTables-driven loop above already handles joins for
        // every field source-table (selected + filter-referenced); this
        // secondary pass catches per-field JoinIds that point at a
        // specific join by id (for multi-join arrangements where the
        // SourceTable match wouldn't land on the right join).
        foreach (var f in resolvedFields)
        {
            foreach (var jid in f.JoinIds)
            {
                if (!joinConfigLookup.TryGetValue(jid, out var referencedJoin)) continue;
                if (TargetsPrimary(referencedJoin)) continue; // self-join to the primary — skip
                if (!emittedJoinIds.Add(referencedJoin.Id)) continue; // already emitted by id
                if (TargetAlreadyJoined(referencedJoin)) continue;    // alias already in scope
                AddFragment(referencedJoin.RawSql);
                RegisterTargets(referencedJoin);
            }
            if (!string.IsNullOrWhiteSpace(f.SqlJoin)) AddFragment(f.SqlJoin);
        }

        // Pull joins referenced by per-report SQL-mode calc columns. The
        // calc's SqlExpression names aliases (e.g. C, L) that need to be in
        // scope; the JoinIds list is the admin's declaration of which
        // schema joins introduce them.
        // Same dedupe as the per-field loop: skip when the join was already
        // emitted by id, OR when its target alias is already in scope from
        // a composite raw-SQL join that nested it.
        if (tableCalcSqlColumns is { Count: > 0 })
        {
            foreach (var calc in tableCalcSqlColumns)
            {
                if (calc.JoinIds is null) continue;
                foreach (var jid in calc.JoinIds)
                {
                    if (!joinConfigLookup.TryGetValue(jid, out var referencedJoin)) continue;
                    if (TargetsPrimary(referencedJoin)) continue;
                    if (!emittedJoinIds.Add(referencedJoin.Id)) continue;
                    if (TargetAlreadyJoined(referencedJoin)) continue;
                    AddFragment(referencedJoin.RawSql);
                    RegisterTargets(referencedJoin);
                }
            }
        }

        // JOINs from active custom filters. JoinId takes precedence over inline SqlJoin.
        if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0 && customFilters is not null)
        {
            foreach (var cf in customFilters.Where(f =>
                request.CustomFilterIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase)))
            {
                string? joinSql = null;
                if (!string.IsNullOrWhiteSpace(cf.JoinId) && joinConfigLookup.TryGetValue(cf.JoinId, out var referencedJoin))
                {
                    if (TargetsPrimary(referencedJoin)) continue; // skip self-join to primary
                    if (!emittedJoinIds.Add(referencedJoin.Id)) continue;
                    if (TargetAlreadyJoined(referencedJoin)) continue;
                    joinSql = referencedJoin.RawSql;
                    RegisterTargets(referencedJoin);
                }
                else if (!string.IsNullOrWhiteSpace(cf.SqlJoin))
                {
                    joinSql = cf.SqlJoin;
                }

                if (!string.IsNullOrWhiteSpace(joinSql)) AddFragment(joinSql!);
            }
        }

        // Topo-sort the collected fragments so that a JOIN whose ON clause
        // references another JOIN's alias lands AFTER that JOIN.
        foreach (var frag in SortJoinFragmentsByDependency(joinFragments))
            sb.AppendLine(frag);

        // WHERE clause with parameterized filters (supports equality, date range _start/_end, and IN lists)
        {
            var whereClauses = new List<string>();
            var filterIndex = 0;
            foreach (var filter in request.Filters ?? new())
            {
                if (filter.Value is null) continue;

                // Handle date range suffixes
                string baseFieldId;
                string op;
                if (filter.Key.EndsWith("_start"))
                {
                    baseFieldId = filter.Key[..^6];
                    op = ">=";
                }
                else if (filter.Key.EndsWith("_end"))
                {
                    baseFieldId = filter.Key[..^4];
                    op = "<=";
                }
                else
                {
                    baseFieldId = filter.Key;
                    op = "=";
                }

                if (!fieldLookup.TryGetValue(baseFieldId, out var filterField)) continue;

                var column = filterField.GetSqlExpression();

                // Check if value is a list (multi-select) — build IN clause
                var valueList = ExtractStringList(filter.Value);
                if (valueList is not null && valueList.Count > 0)
                {
                    var inParams = new List<string>();
                    for (int i = 0; i < valueList.Count; i++)
                    {
                        var paramName = $"@filter_{filterIndex}_{i}";
                        inParams.Add(paramName);
                        parameters.Add(dialect.CreateParameter(paramName, valueList[i]));
                    }
                    whereClauses.Add($"{column} IN ({string.Join(", ", inParams)})");
                }
                else
                {
                    var paramName = $"@filter_{filter.Key.Replace(".", "_")}";
                    whereClauses.Add($"{column} {op} {paramName}");
                    parameters.Add(dialect.CreateParameter(paramName, filter.Value));
                }
                filterIndex++;
            }

            // Custom filters (admin-curated raw SQL conditions from schema_config.json)
            if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0 && customFilters is not null)
            {
                var cfLookup = customFilters.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var filterId in request.CustomFilterIds)
                {
                    if (cfLookup.TryGetValue(filterId, out var cf))
                        whereClauses.Add($"({cf.SqlCondition})");
                }
            }

            // Ad-hoc Advanced Filters (Report Builder's Advanced panel).
            // Delegates to the shared AdvancedFilterSqlEmitter so the
            // worker path (SqlEmitter → scheduled exports) produces the
            // exact same SQL for the same tree. Additive — null tree is
            // a no-op. Feature-flagged on the UI side; see
            // Features:AdvancedFilters in appsettings.
            if (request.AdvancedFilters is not null)
            {
                var advExpr = TleReportingDashboard.Web.Services.QueryPipeline.AdvancedFilterSqlEmitter
                    .BuildGroupExpression(
                        request.AdvancedFilters,
                        fieldId => fieldLookup.TryGetValue(fieldId, out var fc)
                            ? new TleReportingDashboard.Web.Services.QueryPipeline.AdvancedFilterSqlEmitter.FilterableField
                            {
                                SqlExpression = fc.SqlExpression,
                                SourceTable = fc.SourceTable,
                                SourceColumn = fc.SourceColumn,
                                DataType = fc.DataType,
                                DisplayTimezone = fc.DisplayTimezone,
                                ApplyTimezoneConversion = fc.ApplyTimezoneConversion
                            }
                            : null,
                        dialect,
                        parameters);
                if (!string.IsNullOrWhiteSpace(advExpr))
                    whereClauses.Add(advExpr);
            }

            // Row-level scoping (Permissions Phase 1). When the request
            // carries a Scoping block, we either force zero rows (user is
            // self-scoped but can't be resolved) or inject
            // `<owner_col> = @__scope_user`. Scoping is opted into upstream:
            // admin / 'all' roles leave Scoping null.
            if (request.Scoping is { } scope)
            {
                if (scope.ForceNoMatch)
                {
                    // Scoped user with missing configuration — fail safe to
                    // empty result set. Applies to both self and team scope.
                    whereClauses.Add("1 = 0");
                }
                else if (!string.IsNullOrEmpty(scope.OwnerColumn)
                         && (scope.ExternalUserIds is { Count: > 0 }
                             || !string.IsNullOrEmpty(scope.ExternalUserId)))
                {
                    // Direct-column scope. Bypasses the schema field-id
                    // catalog — emitter takes the OwnerColumn string as-is.
                    // Auto-prefixes with PrimaryAlias when unqualified to
                    // mirror the team-scope shape, so admins configuring a
                    // team-type owner column on Team Builder don't have to
                    // think about alias qualification differently between
                    // the team-scope (live UI) and Individual-schedule
                    // (Worker fan-out) paths.
                    var ownerCol = scope.OwnerColumn!.Contains('.')
                        ? scope.OwnerColumn!
                        : string.IsNullOrWhiteSpace(scope.PrimaryAlias)
                            ? scope.OwnerColumn!
                            : $"{scope.PrimaryAlias}.{scope.OwnerColumn}";

                    if (scope.ExternalUserIds is { Count: > 0 } extIds)
                    {
                        // Multi-value form: WHERE col IN (@p0, @p1, ...).
                        // Used by the Worker's "fetch once, group by
                        // owner" Individual fan-out so a single round-
                        // trip can carry every team member's rows.
                        var paramNames = new List<string>(extIds.Count);
                        for (var i = 0; i < extIds.Count; i++)
                        {
                            var p = $"@__scope_user_{i}";
                            parameters.Add(dialect.CreateParameter(p, extIds[i]));
                            paramNames.Add(p);
                        }
                        whereClauses.Add($"{ownerCol} IN ({string.Join(", ", paramNames)})");
                    }
                    else
                    {
                        // Single-value form: WHERE col = @p (legacy /
                        // per-recipient).
                        var paramName = "@__scope_user";
                        parameters.Add(dialect.CreateParameter(paramName, scope.ExternalUserId!));
                        whereClauses.Add($"{ownerCol} = {paramName}");
                    }
                }
                else if (!string.IsNullOrEmpty(scope.OwnerFieldId)
                         && !string.IsNullOrEmpty(scope.ExternalUserId)
                         && fieldLookup.TryGetValue(scope.OwnerFieldId, out var ownerField))
                {
                    // Self-scope. Resolve the owner field's qualified column
                    // name — use SqlExpression when present (computed columns)
                    // otherwise SourceTable.SourceColumn.
                    var ownerCol = !string.IsNullOrWhiteSpace(ownerField.SqlExpression)
                        ? ownerField.SqlExpression
                        : $"{ownerField.SourceTable}.{ownerField.SourceColumn}";

                    var paramName = "@__scope_user";
                    parameters.Add(dialect.CreateParameter(paramName, scope.ExternalUserId));
                    whereClauses.Add($"{ownerCol} = {paramName}");
                }
                else if (scope.TeamScope is { Teams.Count: > 0 } team
                         && !string.IsNullOrWhiteSpace(team.MembersSql))
                {
                    // Team-scope. One OR'd EXISTS per team assignment,
                    // wrapping the admin's members SQL as a subquery so
                    // nothing about the source schema is baked into the
                    // emitter. The subquery is inlined per-EXISTS — SQL
                    // Server collapses the repeat references under a
                    // single operator in the plan, so this stays cheap.
                    //
                    // Owner-column qualification:
                    //   * Plain column ("PROCESSOR_USERID") — auto-prefixed
                    //     with the primary table's alias (convenience for the
                    //     common case where the column lives on the primary).
                    //   * Already-qualified ("JOINALIAS.PROCESSOR_USERID") —
                    //     used as-is so admins can target a column on a
                    //     joined table instead of the primary.
                    var ors = new List<string>(team.Teams.Count);
                    for (var i = 0; i < team.Teams.Count; i++)
                    {
                        var entry = team.Teams[i];
                        var teamParam = $"@__team_{i}";
                        parameters.Add(dialect.CreateParameter(teamParam, entry.TeamId));
                        var ownerCol = entry.OwnerColumn.Contains('.')
                            ? entry.OwnerColumn
                            : string.IsNullOrWhiteSpace(team.PrimaryAlias)
                                ? entry.OwnerColumn
                                : $"{team.PrimaryAlias}.{entry.OwnerColumn}";
                        ors.Add(
                            $"EXISTS (SELECT 1 FROM ({team.MembersSql}) AS __tm "
                            + $"WHERE __tm.team_id = {teamParam} "
                            + $"AND __tm.member_ext_id = {ownerCol})");
                    }
                    whereClauses.Add("(" + string.Join(" OR ", ors) + ")");
                }
                // Silent fallthrough: Scoping with no ForceNoMatch and no
                // populated branch would be a misconfig; we don't fail open.
                // The resolver is expected to set ForceNoMatch=true in that
                // case so this path is effectively unreachable.
            }

            if (whereClauses.Count > 0)
            {
                sb.AppendLine($"WHERE {string.Join(" AND ", whereClauses)}");
            }
        }

        // GROUP BY — auto-added when any selected field is an aggregate
        // expression. The GROUP BY list is either the caller's explicit
        // GroupByFieldIds (in order) or every selected non-aggregate field.
        // IsAggregateField excludes scalar-subquery fields (SqlExpression
        // containing SELECT) so pre-existing reports with e.g. "(SELECT SUM(x)
        // FROM …) AS total" don't get auto-wrapped in GROUP BY.
        if (willGroupBy)
        {
            List<FieldConfig> groupByFields;
            if (request.GroupByFieldIds is { Count: > 0 })
            {
                groupByFields = new List<FieldConfig>();
                foreach (var gid in request.GroupByFieldIds)
                {
                    var gf = resolvedFields.FirstOrDefault(f => string.Equals(f.Id, gid, StringComparison.OrdinalIgnoreCase));
                    if (gf is not null) groupByFields.Add(gf);
                }
            }
            else
            {
                groupByFields = resolvedFields.Where(f => !IsAggregateField(f)).ToList();
            }

            if (groupByFields.Count > 0)
                sb.AppendLine($"GROUP BY {string.Join(", ", groupByFields.Select(f => f.GetSqlExpression()))}");
        }

        // ORDER BY clause — sort field must be in whitelist. When a
        // SecondarySortField is also present, append it after the primary.
        // Used by the DetailViewer to cluster rows by group first and fall
        // into the default sort within each group.
        // Resolves a sort field id to its ORDER BY expression. Real fields
        // use their authored sort expression; SQL-mode calc Keys order by
        // the quoted SELECT alias — the calc's expression is already in
        // the SELECT under that alias, so the DB can reuse it.
        string? ResolveSortExpression(string fid)
        {
            if (fieldLookup.TryGetValue(fid, out var f)) return f.GetSortExpression();
            if (tableCalcSqlColumns is { Count: > 0 } &&
                tableCalcSqlColumns.Any(c =>
                    string.Equals(c.Key, fid, StringComparison.OrdinalIgnoreCase)))
            {
                return dialect.QuoteIdentifier(fid);
            }
            return null;
        }

        // Resolve the qualified PK column expressions ONCE — they're used in
        // two branches: as silent tiebreakers appended after the user's sort
        // and as the standalone ORDER BY when no sort is configured. Empty
        // when the request didn't ship FallbackSortColumns or the alias
        // can't be determined.
        // Suppress the PK auto-anchor entirely when the query is aggregated
        // OR when DISTINCT is on. Both modes require ORDER BY columns to
        // appear in the SELECT list (Postgres + SQL Server agree on this);
        // injecting a raw `<alias>.id` that isn't an aliased SELECT column
        // produces:
        //   * GROUP BY: "column must appear in the GROUP BY clause"
        //   * DISTINCT: "for SELECT DISTINCT, ORDER BY expressions must
        //                appear in select list"
        // The OFFSET-stability concern PKs solve doesn't apply to either
        // mode anyway — aggregated output already produces unique groups,
        // and DISTINCT collapses duplicates so the row set is deterministic
        // even without the PK tiebreaker. (Tie-order across runs on a non-
        // unique sort under DISTINCT is still possible — admins who care
        // pick a unique Sort By or Then By field, both of which ARE in
        // the SELECT.)
        var pkOrderExpressions = (willGroupBy || request.Distinct)
            ? new List<string>()
            : BuildPkOrderExpressions(request.FallbackSortColumns, primaryAlias, primaryName);

        if (!string.IsNullOrEmpty(request.SortField))
        {
            var sortExpr = ResolveSortExpression(request.SortField)
                ?? throw new ArgumentException($"Unknown sort field ID: '{request.SortField}'");
            var direction = string.Equals(request.SortDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            var orderBy = $"ORDER BY {sortExpr} {direction}";
            // Track expressions already in the ORDER BY so silent PK
            // tiebreakers don't duplicate a column the user already sorted on
            // (e.g. user picked the PK as primary sort — no need to append).
            var seenExprs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sortExpr };

            if (!string.IsNullOrEmpty(request.SecondarySortField)
                && !string.Equals(request.SecondarySortField, request.SortField, StringComparison.OrdinalIgnoreCase))
            {
                var secExpr = ResolveSortExpression(request.SecondarySortField);
                if (secExpr is not null)
                {
                    var secDir = string.Equals(request.SecondarySortDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
                    orderBy += $", {secExpr} {secDir}";
                    seenExprs.Add(secExpr);
                }
            }

            // Silent PK tiebreakers. Even when the admin picked a stable
            // sort field, append the primary table's PKs so OFFSET pagination
            // can never shuffle on duplicate values. Dedupe against the
            // expressions already in the ORDER BY so we don't emit
            // "ORDER BY o.id ASC, o.id ASC" when the user sorted on the PK.
            foreach (var pkExpr in pkOrderExpressions)
            {
                if (seenExprs.Contains(pkExpr)) continue;
                orderBy += $", {pkExpr} ASC";
                seenExprs.Add(pkExpr);
            }
            sb.AppendLine(orderBy);
        }
        else if (pkOrderExpressions.Count > 0)
        {
            // Auto-PK fallback. The Report Builder pre-computed the
            // primary table's PK columns from information_schema and
            // passes them here when no user sort is configured.
            sb.AppendLine($"ORDER BY {string.Join(", ", pkOrderExpressions)} ASC");
        }
        else if (request.DisableDefaultSort
                 && !string.Equals(dialect.Name, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            // Admin explicitly chose "(None)" with no PK fallback available.
            // Postgres allows OFFSET/FETCH without ORDER BY — skip emission.
            // SQL Server's OFFSET requires ORDER BY so the fallback below
            // still applies on that dialect.
        }
        else
        {
            // Default ORDER BY for OFFSET/FETCH to work (required by SQL Server)
            var firstField = resolvedFields.First();
            sb.AppendLine($"ORDER BY {firstField.GetSortExpression()} ASC");
        }

        // Pagination with OFFSET/FETCH — cap PageSize at MaxPageSize, parameterized
        var pageSize = Math.Min(request.PageSize, QueryRequest.MaxPageSize);
        if (pageSize <= 0) pageSize = 50;

        var page = Math.Max(request.Page, 1);
        var offset = (page - 1) * pageSize;

        sb.AppendLine("OFFSET @_offset ROWS");
        sb.AppendLine("FETCH NEXT @_pageSize ROWS ONLY");
        parameters.Add(dialect.CreateParameter("@_offset", offset));
        parameters.Add(dialect.CreateParameter("@_pageSize", pageSize));
    
        return (sb.ToString(), parameters);
    }

    /// <summary>
    /// Wraps a row-level query as a GROUP BY / aggregate outer query for the
    /// Dashboard tab's Show Query preview. Display-only — the app continues to
    /// fetch raw rows and aggregate client-side. This method just produces the
    /// SQL you'd write by hand to get the same summary result server-side.
    ///
    /// <paramref name="baseSql"/> is the output of <see cref="BuildQuery"/>
    /// executed with FieldIds = dimensions ∪ measure fields. The trailing
    /// ORDER BY / OFFSET / FETCH are stripped and the rest is wrapped as a
    /// subquery.
    /// </summary>
    public static string BuildDashboardPreviewSql(
        string baseSql,
        List<string> dimensionFieldIds,
        List<(string FieldId, string Aggregation)> measures,
        List<FieldConfig> fieldConfigs,
        TleReportingDashboard.Web.Services.QueryPipeline.ISqlDialect dialect,
        string? defaultSortColumn,
        string? defaultSortDir)
    {
        var fieldLookup = fieldConfigs.ToDictionary(f => f.Id, f => f, StringComparer.OrdinalIgnoreCase);

        // Drop ORDER BY / OFFSET / FETCH from the base query. These always
        // appear as the last three lines emitted by BuildQuery.
        var lines = baseSql.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0)
        {
            var last = lines[^1].TrimStart();
            if (last.StartsWith("OFFSET ", StringComparison.OrdinalIgnoreCase) ||
                last.StartsWith("FETCH ", StringComparison.OrdinalIgnoreCase) ||
                last.StartsWith("ORDER BY ", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(last))
            {
                lines.RemoveAt(lines.Count - 1);
                if (last.StartsWith("ORDER BY ", StringComparison.OrdinalIgnoreCase)) break;
                continue;
            }
            break;
        }
        var innerSql = string.Join("\n", lines);

        // If the base query already contains a GROUP BY (because the selected
        // fields include an aggregate expression — SUM/COUNT/etc — and
        // QueryBuilder auto-emits GROUP BY for them), the rows are already
        // per-group aggregated server-side. Wrapping them in another outer
        // GROUP BY would double-aggregate (SUM of a SUM, etc.), so skip the
        // wrap and just tack on a dashboard-friendly ORDER BY.
        var innerHasGroupBy = lines.Any(l =>
            l.TrimStart().StartsWith("GROUP BY ", StringComparison.OrdinalIgnoreCase));
        if (innerHasGroupBy)
        {
            string orderBy;
            var dirInner = string.Equals(defaultSortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            if (string.IsNullOrWhiteSpace(defaultSortColumn))
            {
                orderBy = dimensionFieldIds.Count > 0
                    ? $"ORDER BY {dialect.QuoteIdentifier(dimensionFieldIds[0])} {dirInner}"
                    : string.Empty;
            }
            else if (defaultSortColumn == "groupBy" && dimensionFieldIds.Count > 0)
            {
                orderBy = $"ORDER BY {dialect.QuoteIdentifier(dimensionFieldIds[0])} {dirInner}";
            }
            else if (defaultSortColumn.StartsWith("dim:"))
            {
                orderBy = $"ORDER BY {dialect.QuoteIdentifier(defaultSortColumn[4..])} {dirInner}";
            }
            else if (defaultSortColumn == "count")
            {
                // Server-aggregated queries name the per-group count column
                // "__row_count" (see BuildQuery). Map the logical "count" sort
                // key to that actual column name.
                orderBy = $"ORDER BY {dialect.QuoteIdentifier("__row_count")} {dirInner}";
            }
            else
            {
                orderBy = $"ORDER BY {dialect.QuoteIdentifier(defaultSortColumn)} {dirInner}";
            }

            return string.IsNullOrEmpty(orderBy) ? innerSql : innerSql + "\n" + orderBy;
        }

        // Outer SELECT: dimensions by alias, aggregate expressions for measures,
        // plus COUNT(*) as "count" (matches the dashboard's dedicated column).
        var outerColumns = new List<string>();
        var groupByCols = new List<string>();
        foreach (var fid in dimensionFieldIds)
        {
            var alias = dialect.QuoteIdentifier(fid);
            outerColumns.Add(alias);
            groupByCols.Add(alias);
        }
        outerColumns.Add($"COUNT(*) AS {dialect.QuoteIdentifier("count")}");
        foreach (var (fieldId, agg) in measures)
        {
            // NONE = "don't aggregate, don't show on the dashboard." Skip the
            // measure entirely — emitting `NONE(field)` produces invalid SQL
            // that the DB rejects when the user copies it from Show Query.
            if (string.Equals(agg, "NONE", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(agg, "COUNT", StringComparison.OrdinalIgnoreCase))
            {
                // Distinct from the unconditional COUNT(*): counts non-null of the field.
                outerColumns.Add($"COUNT({dialect.QuoteIdentifier(fieldId)}) AS {dialect.QuoteIdentifier($"{fieldId}_count")}");
            }
            else
            {
                outerColumns.Add($"{agg.ToUpperInvariant()}({dialect.QuoteIdentifier(fieldId)}) AS {dialect.QuoteIdentifier($"{fieldId}_{agg.ToLowerInvariant()}")}");
            }
        }

        // Outer ORDER BY: default sort column if set, otherwise descending count.
        string outerOrderBy;
        var dir = string.Equals(defaultSortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        if (string.IsNullOrWhiteSpace(defaultSortColumn))
        {
            outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier("count")} DESC";
        }
        else if (defaultSortColumn == "count")
        {
            outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier("count")} {dir}";
        }
        else if (defaultSortColumn == "groupBy" && dimensionFieldIds.Count > 0)
        {
            outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier(dimensionFieldIds[0])} {dir}";
        }
        else if (defaultSortColumn.StartsWith("dim:"))
        {
            // Dimension column — outer alias is the field id itself.
            outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier(defaultSortColumn[4..])} {dir}";
        }
        else if (defaultSortColumn.StartsWith("extra:"))
        {
            // Extra measure — resolve to the alias we emitted for it
            // ("{fieldId}_{agg}"). Fall back to the raw string if the measure
            // list doesn't contain it.
            var ecKey = defaultSortColumn[6..];
            var sep = ecKey.LastIndexOf('_');
            if (sep > 0)
            {
                var fieldId = ecKey[..sep];
                var agg = ecKey[(sep + 1)..].ToLowerInvariant();
                outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier($"{fieldId}_{agg}")} {dir}";
            }
            else
            {
                outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier(defaultSortColumn)} {dir}";
            }
        }
        else
        {
            // Fallback: the column name already matches an outer alias
            outerOrderBy = $"ORDER BY {dialect.QuoteIdentifier(defaultSortColumn)} {dir}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SELECT {string.Join(", ", outerColumns)}");
        sb.AppendLine("FROM (");
        sb.AppendLine(innerSql);
        sb.AppendLine(") AS base");
        if (groupByCols.Count > 0)
            sb.AppendLine($"GROUP BY {string.Join(", ", groupByCols)}");
        sb.AppendLine(outerOrderBy);
        return sb.ToString();
    }

    private static List<string>? ExtractStringList(object? value)
    {
        if (value is List<string> list)
            return list;
        if (value is IEnumerable<string> enumerable)
            return enumerable.ToList();
        if (value is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            return jsonElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        return null;
    }

}
