using System.Text.RegularExpressions;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;
using ConfigFieldDefinition = TleReportingDashboard.Web.Configuration.FieldDefinition;
using ModelFieldDefinition = TleReportingDashboard.Web.Models.FieldDefinition;

namespace TleReportingDashboard.Web.Services;

public partial class SchemaService : ISchemaService
{
    private readonly ISchemaConfigStore _schemaConfigStore;
    private readonly ICodeSetService _codeSetService;
    private readonly ICompanyConnectionAdminService _connectionAdmin;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<SchemaService> _logger;

    private const string CachePrefix = "SchemaService:";

    public SchemaService(
        ISchemaConfigStore schemaConfigStore,
        ICodeSetService codeSetService,
        ICompanyConnectionAdminService connectionAdmin,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<SchemaService> logger)
    {
        _schemaConfigStore = schemaConfigStore;
        _codeSetService = codeSetService;
        _connectionAdmin = connectionAdmin;
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

    public Task<List<FieldConfig>> GetFieldConfigsAsync(Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("SchemaService", "Fields", connectionId),
            () => GetFieldConfigsImplAsync(connectionId),
            bypass: _editorMode.IsActive);

    private async Task<List<FieldConfig>> GetFieldConfigsImplAsync(Guid? connectionId)
    {
        var config = connectionId is Guid id ? _schemaConfigStore.GetForConnection(id) : _schemaConfigStore.Current;

        // Resolve the connection's display timezone once. Every FieldConfig
        // built below gets it painted on so the wrap (AT TIME ZONE '<tz>')
        // is applied at expression time whenever a field opted in. Only
        // Postgres connections with a configured timezone actually carry a
        // value — other cases leave it null, which makes the field's
        // ApplyTimezoneConversion flag a no-op downstream.
        var displayTimezone = await ResolveDisplayTimezoneAsync(connectionId);

        var fieldConfigs = config.Fields
            .Select((field, index) => new FieldConfig
            {
                Id = field.Id,
                Label = field.Label,
                Description = field.Description,
                Domain = field.Domain,
                DataType = field.DataType,
                FieldType = field.FieldType,
                SourceTable = field.SourceTable,
                SourceColumn = field.SourceColumn,
                SqlExpression = field.SqlExpression,
                SqlPreamble = field.SqlPreamble,
                // Normalize legacy single JoinId into JoinIds list; union if both set.
                JoinIds = MergeJoinIds(field.JoinId, field.JoinIds),
                SqlJoin = field.SqlJoin,
                LookupIds = field.LookupIds,
                SortOrder = index,
                MaxLength = field.MaxLength,
                CodeSetId = field.CodeSetId,
                ValueSortOrder = field.ValueSortOrder is not null ? new Dictionary<string, int>(field.ValueSortOrder) : null,
                RolesRequired = field.RolesRequired,
                AllowedAggregations = field.AllowedAggregations,
                DefaultRedactionValue = field.DefaultRedactionValue,
                Format = field.Format,
                ApplyTimezoneConversion = field.ApplyTimezoneConversion,
                IsUnique = field.IsUnique,
                DisplayTimezone = displayTimezone
            })
            .OrderBy(f => f.Domain)
            .ThenBy(f => f.SortOrder)
            .ToList();

        // Defensive dedupe: every downstream consumer (QueryBuilder,
        // ReportBuilder, GridTemplateEditor, etc.) keys a dictionary on
        // f.Id and crashes on a duplicate. The Schema Builder enforces
        // uniqueness on save, but pre-existing schemas may still hold
        // duplicates from before that validation existed (or from a
        // clone-from-another-connection that overlapped ids). First-wins
        // by Id (case-insensitive) — the warning logs the dropped ids
        // so the issue surfaces in server logs even if no admin opens
        // Schema Builder. Schema Builder's load-time warning is the
        // primary place for an admin to see + fix the data.
        var beforeDedupe = fieldConfigs.Count;
        fieldConfigs = fieldConfigs
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (fieldConfigs.Count < beforeDedupe)
        {
            var dropped = beforeDedupe - fieldConfigs.Count;
            _logger.LogWarning(
                "SchemaService.GetFieldConfigsAsync: dropped {Count} duplicate field id(s) for connection {ConnectionId}. Open Schema Builder for that connection — the load-time warning lists the duplicate ids; rename one of each pair to unblock cleanly.",
                dropped, connectionId);
        }

        // Auto-populate SortExpression and ValueSortOrder from referenced
        // lookups. The two have different prerequisites and used to share a
        // gate that incorrectly required CodeSetId for both:
        //   * SortExpression — needs only LookupIds + a parseable CTE shape
        //     (e.g. "LOOKUP(STATUS, EVENTNUM, ORDERBY)" → "LOOKUP.ORDERBY").
        //     Used by QueryBuilder.ResolveSortExpression so ORDER BY on a
        //     status-like column emits the lookup's workflow order column
        //     instead of alphabetic on the description. Calc fields with a
        //     lookup but no CodeSetId (days_in_status, days_in_process) need
        //     this too.
        //   * ValueSortOrder — needs LookupIds + CodeSetId + parseable rows.
        //     Maps the lookup's STATUS column to code descriptions for
        //     client-side group/value sort in DetailViewer/ReportGrid.
        var lookupMap = config.Lookups.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var fc in fieldConfigs)
        {
            if (fc.LookupIds is null || fc.LookupIds.Count == 0) continue;

            foreach (var lookupId in fc.LookupIds)
            {
                if (!lookupMap.TryGetValue(lookupId, out var lookup)) continue;

                // SortExpression first — independent of CodeSetId.
                if (string.IsNullOrWhiteSpace(fc.SortExpression))
                {
                    var sortExpr = BuildLookupSortExpression(lookup.SqlPreamble);
                    if (sortExpr is not null)
                    {
                        fc.SortExpression = sortExpr;
                        _logger.LogInformation(
                            "Field '{FieldId}' SortExpression set to '{SortExpr}' from lookup '{LookupId}'",
                            fc.Id, sortExpr, lookupId);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Field '{FieldId}' references lookup '{LookupId}' but its CTE preamble didn't match the LOOKUP(c1, c2, c3) pattern — SortExpression unset",
                            fc.Id, lookupId);
                    }
                }

                // ValueSortOrder — text-keyed lookups (Salesforce-style:
                // first column is a string literal that matches the field's
                // stored value) map directly to ValueSortOrder; no codeset
                // translation needed. Numeric-keyed lookups (EMPOWER-style:
                // first column is an integer code) require a CodeSetId on
                // the field so we can translate code → description before
                // building ValueSortOrder.
                if (fc.ValueSortOrder is null)
                {
                    var rowMap = ParseLookupOrderBy(lookup.SqlPreamble);

                    if (rowMap.TextKey.Count > 0)
                    {
                        fc.ValueSortOrder = new Dictionary<string, int>(rowMap.TextKey, StringComparer.OrdinalIgnoreCase);
                        _logger.LogInformation(
                            "Field '{FieldId}' ValueSortOrder set from text-keyed lookup '{LookupId}' ({Count} entries)",
                            fc.Id, lookupId, rowMap.TextKey.Count);
                    }
                    else if (rowMap.NumericKey.Count > 0 && fc.CodeSetId.HasValue)
                    {
                        try
                        {
                            var codeSetValues = await _codeSetService.GetCodeSetValuesAsync(fc.CodeSetId.Value);
                            var sortOrder = new Dictionary<string, int>();
                            foreach (var csv in codeSetValues)
                            {
                                if (int.TryParse(csv.Code, out var codeInt) && rowMap.NumericKey.TryGetValue(codeInt, out var order))
                                    sortOrder[csv.Description] = order;
                            }
                            if (sortOrder.Count > 0)
                            {
                                fc.ValueSortOrder = sortOrder;
                                _logger.LogInformation(
                                    "Field '{FieldId}' ValueSortOrder auto-generated with {Count} entries from lookup '{LookupId}' + codeset {CodeSetId}",
                                    fc.Id, sortOrder.Count, lookupId, fc.CodeSetId.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to auto-generate ValueSortOrder for field '{FieldId}'", fc.Id);
                        }
                    }
                }

                break;
            }
        }

        return fieldConfigs;
    }

    /// <summary>
    /// Parses a LOOKUP CTE preamble to extract STATUS → ORDERBY mapping.
    /// Expected format: "LOOKUP( STATUS, EVENTNUM, ORDERBY ) AS (SELECT 1, 346, 1 UNION ALL SELECT 2, 347, 2 ...)"
    /// Extracts column 1 (STATUS) → column 3 (ORDERBY) from each SELECT row.
    /// If the CTE has only 2 columns, uses column 1 → column 2.
    /// </summary>
    // Combines the legacy singular JoinId and the new plural JoinIds into one
    // ordered, deduped list. Keeps schema_config.json files that predate the
    // multi-join change working without migration.
    private static List<string> MergeJoinIds(string? legacyJoinId, List<string>? joinIds)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (joinIds is not null)
        {
            foreach (var id in joinIds)
            {
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    merged.Add(id);
            }
        }
        if (!string.IsNullOrWhiteSpace(legacyJoinId) && seen.Add(legacyJoinId))
            merged.Add(legacyJoinId);
        return merged;
    }

    private sealed record LookupRowMap(
        Dictionary<int, int> NumericKey,
        Dictionary<string, int> TextKey);

    private static LookupRowMap ParseLookupOrderBy(string sqlPreamble)
    {
        // Convention (re-stated): every lookup CTE row starts with the join
        // key and ends with the workflow order integer, with any number of
        // columns in between. The first column may be either:
        //   * a numeric code (EMPOWER pattern, paired with a CodeSetId on
        //     the field — caller translates code → description via codeset
        //     before populating ValueSortOrder), or
        //   * a quoted string literal (Salesforce pattern, where the field's
        //     stored value matches the lookup STATUS text directly — caller
        //     can use this map as ValueSortOrder without codeset translation).
        // Both flavors live in the same return shape so the caller can
        // pick whichever applies.
        var numericKey = new Dictionary<int, int>();
        var textKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rowMatches = Regex.Matches(
            sqlPreamble,
            @"SELECT\s+(.+?)(?=\s+UNION\s+ALL|\s*\)|\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match m in rowMatches)
        {
            var values = SplitSqlRowValues(m.Groups[1].Value);
            if (values.Count < 2) continue;

            var firstRaw = values[0].Trim();
            var lastRaw  = values[^1].Trim();
            if (!int.TryParse(lastRaw, out var orderBy)) continue;

            if (int.TryParse(firstRaw, out var firstInt))
            {
                numericKey[firstInt] = orderBy;
            }
            else
            {
                // Strip the SQL string-literal quotes. Postgres + SQL Server
                // both use single-quote literals; double-quotes only appear
                // on identifiers (which shouldn't show up as a row value).
                var stripped = firstRaw.Trim('\'', '"');
                if (stripped.Length > 0) textKey[stripped] = orderBy;
            }
        }

        return new LookupRowMap(numericKey, textKey);
    }

    // Splits a CTE row's value list on commas, respecting SQL string
    // literals so a comma inside `'New, Lead'` doesn't break the split.
    // Caller passes only the column-value portion (e.g. "1, 346, 1" or
    // "'New', 0"), already stripped of the leading "SELECT ".
    private static List<string> SplitSqlRowValues(string row)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        foreach (var c in row)
        {
            if (inQuote)
            {
                current.Append(c);
                if (c == quoteChar) inQuote = false;
            }
            else if (c == '\'' || c == '"')
            {
                current.Append(c);
                inQuote = true;
                quoteChar = c;
            }
            else if (c == ',')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Extracts a sort expression like "LOOKUP.ORDERBY" from a CTE preamble.
    /// Parses the CTE name and picks the last column from the header — works
    /// for the common 3-column shape "LOOKUP(STATUS, EVENTNUM, ORDERBY)" as
    /// well as 2-column "LOOKUP(STATUS, ORDERBY)" and 4+-column variants
    /// (the convention is "last column = workflow order"). Mirrors the
    /// 2/3-column fallback already in ParseLookupOrderBy so admins don't
    /// hit a "JOIN works but ORDER BY doesn't" mismatch on lookups whose
    /// shape isn't exactly 3 columns.
    /// </summary>
    private static string? BuildLookupSortExpression(string sqlPreamble)
    {
        // CTENAME( COL1, COL2, ..., COL_N ). Group 1 = CTE name, group 2 =
        // comma-separated column list (optionally quoted). Whitespace is
        // permissive everywhere.
        var match = Regex.Match(sqlPreamble.Trim(),
            @"^(\w+)\s*\(\s*([^)]+?)\s*\)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var cteName = match.Groups[1].Value;
        var cols = match.Groups[2].Value
            .Split(',')
            .Select(c => c.Trim().Trim('"', '[', ']', '`'))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        if (cols.Count < 2) return null;
        return $"{cteName}.{cols[^1]}"; // last column = ORDERBY by convention
    }

    private SchemaConfig ResolveConfig(Guid? connectionId) =>
        connectionId is Guid id ? _schemaConfigStore.GetForConnection(id) : _schemaConfigStore.Current;

    // Pulls the IANA display timezone off the connection so FieldConfigs can
    // be painted with it. Null when there's no connection id, the connection
    // isn't Postgres, or the admin hasn't configured one. Any of those mean
    // "no AT TIME ZONE wrap" — downstream GetSqlExpression() returns the
    // raw expression untouched.
    private async Task<string?> ResolveDisplayTimezoneAsync(Guid? connectionId)
    {
        if (connectionId is not Guid id) return null;
        try
        {
            var record = await _connectionAdmin.GetByIdAsync(id);
            if (record is null) return null;
            if (!string.Equals(record.ConnectionType, "postgres", StringComparison.OrdinalIgnoreCase))
                return null;
            return string.IsNullOrWhiteSpace(record.PgDisplayTimezone) ? null : record.PgDisplayTimezone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve display timezone for connection {ConnectionId}", id);
            return null;
        }
    }

    public Task<List<LookupDefinition>> GetLookupsAsync(Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("SchemaService", "Lookups", connectionId),
            () => Task.FromResult(new List<LookupDefinition>(ResolveConfig(connectionId).Lookups)),
            bypass: _editorMode.IsActive);

    public Task<List<CustomFilterDefinition>> GetCustomFiltersAsync(Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("SchemaService", "CustomFilters", connectionId),
            () => Task.FromResult(new List<CustomFilterDefinition>(ResolveConfig(connectionId).CustomFilters)),
            bypass: _editorMode.IsActive);

    public Task<List<JoinConfig>> GetJoinConfigsAsync(Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("SchemaService", "Joins", connectionId),
            () =>
            {
                var config = ResolveConfig(connectionId);
                var joinConfigs = config.Joins
                    .Select((join, index) => ParseJoinDefinition(join, index + 1))
                    .ToList();
                return Task.FromResult(joinConfigs);
            },
            bypass: _editorMode.IsActive);

    public Task<List<DomainGroup>> GetDomainGroupsAsync(string? userRole = null, Guid? connectionId = null) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("SchemaService", "Domains", userRole, connectionId),
            () => GetDomainGroupsImplAsync(connectionId),
            bypass: _editorMode.IsActive);

    private Task<List<DomainGroup>> GetDomainGroupsImplAsync(Guid? connectionId)
    {
        var config = ResolveConfig(connectionId);

        var domainGroups = config.Fields
            .GroupBy(f => f.Domain)
            .Select(g => new DomainGroup
            {
                Name = g.Key,
                Fields = g.Select(f => new ModelFieldDefinition
                    {
                        Id = f.Id,
                        Label = f.Label,
                        DataType = f.DataType,
                        Description = f.Description,
                        FieldType = f.FieldType,
                        CodeSetId = f.CodeSetId,
                        RolesRequired = f.RolesRequired,
                        DefaultRedactionValue = f.DefaultRedactionValue,
                        SqlExpression = f.SqlExpression,
                        SourceTable = f.SourceTable,
                        SourceColumn = f.SourceColumn,
                        IsUnique = f.IsUnique
                    })
                    .ToList()
            })
            .OrderBy(g => g.Name)
            .ToList();

        return Task.FromResult(domainGroups);
    }

    // Legacy best-effort parser. The query pipeline uses RawSql directly; the
    // structured FromTable/ToTable/etc are only consulted by a fallback branch
    // that reconstructs JOIN SQL when RawSql is empty. Any parse failure here
    // should NOT prevent the schema from loading — downgrade to empty fields.
    private static JoinConfig ParseJoinDefinition(JoinDefinition join, int id)
    {
        var sql = join.Sql ?? string.Empty;
        var fallback = new JoinConfig
        {
            Id = id,
            JoinId = join.Id,
            FromTable = string.Empty,
            FromColumn = string.Empty,
            ToTable = string.Empty,
            ToColumn = string.Empty,
            JoinType = "LEFT JOIN",
            RawSql = sql.Trim(),
            PrimaryTable = join.PrimaryTable,
            PrimaryAlias = join.PrimaryAlias
        };

        try
        {
            var parts = sql.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string joinType;
            int tableIndex;

            if (parts.Length >= 7 &&
                string.Equals(parts[0], "INNER", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "JOIN", StringComparison.OrdinalIgnoreCase))
            {
                joinType = "INNER JOIN";
                tableIndex = 2;
            }
            else if (parts.Length >= 7 &&
                     string.Equals(parts[0], "LEFT", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(parts[1], "JOIN", StringComparison.OrdinalIgnoreCase))
            {
                joinType = "LEFT JOIN";
                tableIndex = 2;
            }
            else
            {
                return fallback;
            }

            var toTable = parts[tableIndex];
            var onIndex = tableIndex + 1;

            if (onIndex < parts.Length &&
                string.Equals(parts[onIndex], "AS", StringComparison.OrdinalIgnoreCase) &&
                onIndex + 1 < parts.Length)
            {
                toTable = parts[onIndex + 1];
                onIndex += 2;
            }

            // Structured parse needs "<col> = <col>" as three separate tokens at
            // onIndex+1, onIndex+2, onIndex+3. Missing tokens → fall back to
            // RawSql-only mode; the query pipeline doesn't actually need these.
            if (onIndex + 3 >= parts.Length)
            {
                fallback.JoinType = joinType;
                fallback.ToTable = toTable;
                return fallback;
            }

            var leftFull = parts[onIndex + 1];
            var lastDot = leftFull.LastIndexOf('.');
            var fromTable = lastDot > 0 ? leftFull[..lastDot] : leftFull;
            var fromColumn = lastDot > 0 ? leftFull[(lastDot + 1)..] : leftFull;

            var rightFull = parts[onIndex + 3];
            var rightLastDot = rightFull.LastIndexOf('.');
            var toColumn = rightLastDot > 0 ? rightFull[(rightLastDot + 1)..] : rightFull;

            return new JoinConfig
            {
                Id = id,
                JoinId = join.Id,
                FromTable = fromTable,
                FromColumn = fromColumn,
                ToTable = toTable,
                ToColumn = toColumn,
                JoinType = joinType,
                RawSql = sql.Trim(),
                PrimaryTable = join.PrimaryTable,
                PrimaryAlias = join.PrimaryAlias
            };
        }
        catch
        {
            return fallback;
        }
    }
}
