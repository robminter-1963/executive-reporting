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
    private readonly ILogger<SchemaService> _logger;

    public SchemaService(
        ISchemaConfigStore schemaConfigStore,
        ICodeSetService codeSetService,
        ICompanyConnectionAdminService connectionAdmin,
        ILogger<SchemaService> logger)
    {
        _schemaConfigStore = schemaConfigStore;
        _codeSetService = codeSetService;
        _connectionAdmin = connectionAdmin;
        _logger = logger;
    }

    public async Task<List<FieldConfig>> GetFieldConfigsAsync(Guid? connectionId = null)
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

        // Auto-generate ValueSortOrder from referenced lookups for fields that have
        // a CodeSetId + LookupIds but no explicit ValueSortOrder in the JSON.
        var lookupMap = config.Lookups.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var fc in fieldConfigs)
        {
            if (fc.ValueSortOrder is not null || fc.LookupIds is null || !fc.CodeSetId.HasValue)
                continue;

            // Find the first referenced lookup that has an ORDERBY column
            foreach (var lookupId in fc.LookupIds)
            {
                if (!lookupMap.TryGetValue(lookupId, out var lookup))
                    continue;

                var orderMap = ParseLookupOrderBy(lookup.SqlPreamble);
                _logger.LogInformation("Lookup '{LookupId}' parsed {Count} order entries from CTE", lookupId, orderMap.Count);
                if (orderMap.Count == 0) continue;

                try
                {
                    var codeSetValues = await _codeSetService.GetCodeSetValuesAsync(fc.CodeSetId.Value);
                    _logger.LogInformation("CodeSet {CodeSetId} returned {Count} values. Sample: {Sample}",
                        fc.CodeSetId.Value, codeSetValues.Count,
                        string.Join(", ", codeSetValues.Take(3).Select(v => $"Code='{v.Code}' Desc='{v.Description}'")));

                    var sortOrder = new Dictionary<string, int>();
                    foreach (var csv in codeSetValues)
                    {
                        if (int.TryParse(csv.Code, out var codeInt) && orderMap.TryGetValue(codeInt, out var order))
                            sortOrder[csv.Description] = order;
                    }

                    _logger.LogInformation("Auto-generated ValueSortOrder for '{FieldId}': {Count} entries. Sample: {Sample}",
                        fc.Id, sortOrder.Count,
                        string.Join(", ", sortOrder.Take(3).Select(kv => $"'{kv.Key}'={kv.Value}")));

                    if (sortOrder.Count > 0)
                        fc.ValueSortOrder = sortOrder;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-generate ValueSortOrder for field '{FieldId}'", fc.Id);
                }

                // Build SQL sort expression from lookup (e.g., "LOOKUP.ORDERBY")
                var sortExpr = BuildLookupSortExpression(lookup.SqlPreamble);
                if (sortExpr is not null)
                    fc.SortExpression = sortExpr;

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

    private static Dictionary<int, int> ParseLookupOrderBy(string sqlPreamble)
    {
        var result = new Dictionary<int, int>();
        // Match each "SELECT <num>, <num>, <num>" row
        var matches = Regex.Matches(sqlPreamble, @"SELECT\s+(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out var status) && int.TryParse(m.Groups[3].Value, out var orderBy))
                result[status] = orderBy;
        }

        // Fallback: 2-column CTEs
        if (result.Count == 0)
        {
            var matches2 = Regex.Matches(sqlPreamble, @"SELECT\s+(\d+)\s*,\s*(\d+)", RegexOptions.IgnoreCase);
            foreach (Match m in matches2)
            {
                if (int.TryParse(m.Groups[1].Value, out var s) && int.TryParse(m.Groups[2].Value, out var o))
                    result[s] = o;
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts a sort expression like "LOOKUP.ORDERBY" from a CTE preamble.
    /// Parses the CTE name and the 3rd column name from: "LOOKUP(STATUS, EVENTNUM, ORDERBY) AS (...)"
    /// </summary>
    private static string? BuildLookupSortExpression(string sqlPreamble)
    {
        // Match: CTENAME( COL1, COL2, COL3 )
        var match = Regex.Match(sqlPreamble.Trim(), @"^(\w+)\s*\(\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\)", RegexOptions.IgnoreCase);
        if (match.Success)
            return $"{match.Groups[1].Value}.{match.Groups[4].Value}"; // LOOKUP.ORDERBY
        return null;
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

    public Task<List<LookupDefinition>> GetLookupsAsync(Guid? connectionId = null)
    {
        return Task.FromResult(new List<LookupDefinition>(ResolveConfig(connectionId).Lookups));
    }

    public Task<List<CustomFilterDefinition>> GetCustomFiltersAsync(Guid? connectionId = null)
    {
        return Task.FromResult(new List<CustomFilterDefinition>(ResolveConfig(connectionId).CustomFilters));
    }

    public Task<List<JoinConfig>> GetJoinConfigsAsync(Guid? connectionId = null)
    {
        var config = ResolveConfig(connectionId);

        var joinConfigs = config.Joins
            .Select((join, index) => ParseJoinDefinition(join, index + 1))
            .ToList();

        return Task.FromResult(joinConfigs);
    }

    public Task<List<DomainGroup>> GetDomainGroupsAsync(string? userRole = null, Guid? connectionId = null)
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
                        SourceColumn = f.SourceColumn
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
            RawSql = sql.Trim()
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
                RawSql = sql.Trim()
            };
        }
        catch
        {
            return fallback;
        }
    }
}
