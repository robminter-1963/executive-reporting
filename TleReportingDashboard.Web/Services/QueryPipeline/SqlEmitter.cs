using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;
using FieldDefinition = TleReportingDashboard.Web.Configuration.FieldDefinition;

namespace TleReportingDashboard.Web.Services.QueryPipeline;

public interface IQueryPipeline
{
    Task<QueryResponse> ExecuteAsync(QueryRequest request, IReadOnlyCollection<string> userRoles);
}

public sealed partial class SqlEmitter : IQueryPipeline
{
    private readonly ISchemaConfigStore _schemaStore;
    private readonly ICompanyConnectionResolver _connectionResolver;
    private readonly ICompanyConnectionAdminService _connectionAdmin;
    private readonly ISqlDialectFactory _dialectFactory;
    private readonly ILogger<SqlEmitter> _logger;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(\.[A-Za-z_][A-Za-z0-9_]{0,127})?$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    public SqlEmitter(
        ISchemaConfigStore schemaStore,
        ICompanyConnectionResolver connectionResolver,
        ICompanyConnectionAdminService connectionAdmin,
        ISqlDialectFactory dialectFactory,
        ILogger<SqlEmitter> logger)
    {
        _schemaStore = schemaStore ?? throw new ArgumentNullException(nameof(schemaStore));
        _connectionResolver = connectionResolver ?? throw new ArgumentNullException(nameof(connectionResolver));
        _connectionAdmin = connectionAdmin ?? throw new ArgumentNullException(nameof(connectionAdmin));
        _dialectFactory = dialectFactory ?? throw new ArgumentNullException(nameof(dialectFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<QueryResponse> ExecuteAsync(QueryRequest request, IReadOnlyCollection<string> userRoles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(userRoles);

        var stopwatch = Stopwatch.StartNew();

        // Primary table is required per-report. Validate up front so JoinResolver
        // can anchor joins to the correct root, and so the error surfaces before
        // any schema lookups happen.
        if (string.IsNullOrWhiteSpace(request.PrimaryTable))
        {
            throw new InvalidOperationException(
                "Primary Table is required. Set it on the report before running.");
        }

        // Pull the connection-specific schema so field/join/lookup ids in
        // the request resolve against the right catalog.
        var schema = request.ConnectionId is Guid cId
            ? _schemaStore.GetForConnection(cId)
            : _schemaStore.Current;

        // Stage 1: Resolve fields
        var resolvedFields = FieldResolver.ResolveFields(request.FieldIds, schema.Fields);

        // Also resolve any fields referenced by filters so JoinResolver can include their tables
        var filterFields = ResolveFilterFields(request.Filters, schema.Fields);

        // Advanced-filter leaf fields — walk the nested tree and collect
        // each clause's field definition so JoinResolver pulls in every
        // required table (same treatment as the flat Filters dictionary).
        var advancedFilterFields = new List<FieldDefinition>();
        if (request.AdvancedFilters is not null)
        {
            // First-wins on duplicate field ids (matches the dedupe in
            // SchemaService.GetFieldConfigsAsync) so a stale duplicate in
            // the persisted schema doesn't crash the emitter mid-build.
            var schemaLookup = schema.Fields
                .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var seenAf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AdvancedFilterSqlEmitter.Walk(request.AdvancedFilters, clause =>
            {
                if (string.IsNullOrEmpty(clause.FieldId)) return;
                if (!seenAf.Add(clause.FieldId)) return;
                if (schemaLookup.TryGetValue(clause.FieldId, out var f))
                    advancedFilterFields.Add(f);
            });
        }

        var allReferencedFields = resolvedFields
            .Concat(filterFields)
            .Concat(advancedFilterFields)
            .DistinctBy(f => f.Id)
            .ToList();

        // Pull the connection's display timezone and paint it on every
        // referenced field. GetSqlExpression() consults this at emission time
        // — any field flagged ApplyTimezoneConversion gets its expression
        // wrapped with AT TIME ZONE. Silently skipped for non-Postgres
        // connections or when the admin hasn't configured a timezone.
        var displayTimezone = await ResolveDisplayTimezoneAsync(request.ConnectionId);
        if (!string.IsNullOrWhiteSpace(displayTimezone))
        {
            foreach (var f in allReferencedFields) f.DisplayTimezone = displayTimezone;
        }

        // Stage 2: Resolve joins — anchored to the report's primary table.
        // request.PrimaryTable may carry an alias ("schema.table AS X"); split
        // into (table, alias) so JoinResolver treats fields referencing the
        // alias as "on the primary" and skips the join lookup for them.
        var (primaryName, primaryAlias) = PrimaryTableRef.Parse(request.PrimaryTable);
        // Warnings feed the regular log stream — soft messages like "join X
        // has no SourceAlias so primary-table precedence couldn't apply"
        // surface here so admins can spot them via standard log search.
        var joins = JoinResolver.ResolveJoins(allReferencedFields, schema.Joins, primaryName, primaryAlias,
            warnings: msg => _logger.LogWarning("JoinResolver: {Message}", msg));

        // Diagnostic — always emitted at Error level so it bypasses the
        // default Serilog filter (which is set to "Error" in appsettings).
        // Lists the joins the resolver picked plus the primary so admins
        // can verify multi-hop paths got assembled correctly without
        // having to enable Warning-level logging globally. Remove or
        // demote to Information once the resolver is trusted.
        _logger.LogError(
            "JoinResolver diagnostic — Primary: {Primary} (alias {Alias}) | Required: {Required} | Selected joins ({Count}): {JoinIds}",
            primaryName,
            primaryAlias ?? "(none)",
            string.Join(", ", allReferencedFields
                .Where(f => string.IsNullOrWhiteSpace(f.SqlExpression))
                .Select(f => f.SourceTable)
                .Distinct(StringComparer.OrdinalIgnoreCase)),
            joins.Count,
            string.Join(", ", joins.Select(j => j.Id)));

        // Stage 3: Enforce redaction
        var projectedColumns = RedactionEnforcer.EnforceRedaction(resolvedFields, userRoles);

        // Stage 4: Build aggregation
        var aggregation = AggregationBuilder.BuildAggregation(
            projectedColumns, resolvedFields, request.Aggregations);

        // Stage 5: Translate date filter
        var dateFilter = DateFilterTranslator.TranslateDateFilter(
            request.DateFieldId,
            request.DateOperatorId,
            request.DateFrom,
            request.DateTo,
            schema.Fields,
            schema.Settings.RelativeDateOperators);

        // Stage 6: Apply guardrails
        var guardrails = QueryGuardrails.Apply(schema.Settings);

        // Resolve dialect for SQL emission + connection dispatch.
        var dialect = await ResolveDialectAsync(request.ConnectionId);

        // Stage 7: Build and execute SQL
        var (sql, parameters) = BuildSql(
            request, resolvedFields, joins, aggregation, dateFilter,
            guardrails, filterFields, advancedFilterFields,
            schema.CustomFilters, schema.Lookups, dialect, request.PrimaryTable);

        var (rows, totalCount, isTruncated) = await ExecuteSqlAsync(
            sql, parameters, guardrails, request.ConnectionId, dialect);

        stopwatch.Stop();

        // Build column metadata
        var columns = resolvedFields.Select(f => new ColumnMeta
        {
            FieldId = f.Id,
            Label = f.Label,
            DataType = f.DataType,
            Format = f.Format
        }).ToList();

        _logger.LogInformation(
            "QueryPipeline executed: Fields=[{Fields}], Rows={RowCount}, Truncated={Truncated}, Duration={Duration}ms",
            string.Join(", ", request.FieldIds),
            rows.Count,
            isTruncated,
            stopwatch.ElapsedMilliseconds);

        return new QueryResponse
        {
            Columns = columns,
            Rows = rows,
            TotalCount = totalCount,
            IsTruncated = isTruncated,
            ExecutionMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static (string Sql, List<System.Data.Common.DbParameter> Parameters) BuildSql(
        QueryRequest request,
        IReadOnlyList<FieldDefinition> resolvedFields,
        List<JoinDefinition> joins,
        AggregationResult aggregation,
        DateFilterResult dateFilter,
        GuardrailConfig guardrails,
        List<FieldDefinition> filterFields,
        List<FieldDefinition> advancedFilterFields,
        List<CustomFilterDefinition> customFilters,
        List<LookupDefinition> lookups,
        ISqlDialect dialect,
        string? primaryTableOverride)
    {
        var sb = new StringBuilder();
        var parameters = new List<System.Data.Common.DbParameter>();

        // Primary table is required per-report. Caller (ExecuteAsync) already
        // validated this, so blank here is a programmer error.
        if (string.IsNullOrWhiteSpace(primaryTableOverride))
        {
            throw new ArgumentException(
                "Primary Table is required. Set it on the report before running.",
                nameof(primaryTableOverride));
        }
        var primaryTable = primaryTableOverride;

        // Resolve all required lookups + inline preambles (deduplicated)
        var lookupMap = lookups.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
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

        // From fields
        foreach (var f in resolvedFields)
        {
            ResolveLookups(f.LookupIds);
            if (!string.IsNullOrWhiteSpace(f.SqlPreamble) && !preambles.Contains(f.SqlPreamble!))
                preambles.Add(f.SqlPreamble!);
        }
        // From active custom filters
        if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0)
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

        // SELECT with row-limit guardrail. Dialect decides whether the
        // limit goes here as `TOP(@MaxRows)` (SQL Server) or at the end
        // of the query as `LIMIT @MaxRows` (Postgres).
        sb.Append("SELECT ").Append(dialect.BuildRowLimitPrefix("@MaxRows"));
        parameters.Add(dialect.CreateParameter("@MaxRows", guardrails.MaxRows));
        sb.AppendLine(string.Join(", ", aggregation.SelectExpressions));

        // FROM
        sb.AppendLine($"FROM {primaryTable}");

        // JOINs
        foreach (var join in joins)
        {
            sb.AppendLine(join.Sql);
        }

        // JOINs from resolved lookups
        var emittedJoins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lj in lookupJoins)
        {
            if (emittedJoins.Add(lj))
                sb.AppendLine(lj);
        }

        // JOINs from fields with SqlJoin (deduplicated)
        foreach (var f in resolvedFields.Where(f => !string.IsNullOrWhiteSpace(f.SqlJoin)))
        {
            if (emittedJoins.Add(f.SqlJoin!))
                sb.AppendLine(f.SqlJoin);
        }

        // JOINs from active custom filters
        if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0)
        {
            foreach (var cf in customFilters.Where(f =>
                request.CustomFilterIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(f.SqlJoin)))
            {
                if (emittedJoins.Add(cf.SqlJoin!))
                    sb.AppendLine(cf.SqlJoin);
            }
        }

        // WHERE clauses
        var whereClauses = new List<string>();

        // Filter-based WHERE clauses
        BuildFilterWhereClauses(request.Filters, resolvedFields, filterFields, whereClauses, parameters, dialect);

        // Date filter WHERE clause
        if (dateFilter.WhereClause is not null)
        {
            whereClauses.Add(dateFilter.WhereClause);
            parameters.AddRange(dateFilter.Parameters);
        }

        // Custom filters (admin-curated raw SQL conditions from schema_config.json)
        if (request.CustomFilterIds is not null && request.CustomFilterIds.Count > 0)
        {
            var filterLookup = customFilters.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var filterId in request.CustomFilterIds)
            {
                if (filterLookup.TryGetValue(filterId, out var cf))
                    whereClauses.Add($"({cf.SqlCondition})");
            }
        }

        // Ad-hoc Advanced Filters tree. Uses the shared emitter so the
        // worker path produces the exact same SQL as the Web path for
        // the same tree. Null / empty tree is a no-op.
        if (request.AdvancedFilters is not null)
        {
            // Build a lookup across every field we might reference —
            // SELECT fields, legacy-filter fields, and advanced-filter
            // fields. DistinctBy tolerates overlap (same field in SELECT
            // + a filter) without duplicate-key exceptions.
            var schemaLookupForAdv = resolvedFields
                .Concat(filterFields)
                .Concat(advancedFilterFields)
                .DistinctBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
            var advExpr = AdvancedFilterSqlEmitter.BuildGroupExpression(
                request.AdvancedFilters,
                fieldId => schemaLookupForAdv.TryGetValue(fieldId, out var fd)
                    ? new AdvancedFilterSqlEmitter.FilterableField
                    {
                        SqlExpression = fd.SqlExpression,
                        SourceTable = fd.SourceTable,
                        SourceColumn = fd.SourceColumn,
                        DataType = fd.DataType,
                        DisplayTimezone = fd.DisplayTimezone
                    }
                    : null,
                dialect,
                parameters);
            if (!string.IsNullOrWhiteSpace(advExpr))
                whereClauses.Add(advExpr);
        }

        if (whereClauses.Count > 0)
        {
            sb.AppendLine($"WHERE {string.Join(" AND ", whereClauses)}");
        }

        // GROUP BY
        if (aggregation.GroupByExpressions.Count > 0)
        {
            sb.AppendLine($"GROUP BY {string.Join(", ", aggregation.GroupByExpressions)}");
        }

        // ORDER BY
        AppendOrderBy(sb, request, resolvedFields, lookups);

        // Pagination: OFFSET/FETCH
        var pageSize = Math.Clamp(request.PageSize, 1, QueryRequest.MaxPageSize);
        pageSize = Math.Min(pageSize, guardrails.PageSize > 0 ? guardrails.PageSize : pageSize);
        var page = Math.Max(request.Page, 1);
        var offset = (page - 1) * pageSize;

        // OFFSET/FETCH NEXT is ANSI SQL — both SQL Server and Postgres
        // support it. (Postgres also has LIMIT/OFFSET but OFFSET/FETCH
        // works in both.)
        sb.AppendLine("OFFSET @_offset ROWS");
        sb.AppendLine("FETCH NEXT @_pageSize ROWS ONLY");
        parameters.Add(dialect.CreateParameter("@_offset", offset));
        parameters.Add(dialect.CreateParameter("@_pageSize", pageSize));

        // Dialect-specific row-limit suffix. No-op for SQL Server (the
        // cap goes in TOP); Postgres appends LIMIT @MaxRows.
        var suffix = dialect.BuildRowLimitSuffix("@MaxRows");
        if (!string.IsNullOrEmpty(suffix))
            sb.AppendLine(suffix.TrimStart());

        return (sb.ToString(), parameters);
    }

    private static void BuildFilterWhereClauses(
        Dictionary<string, object?> filters,
        IReadOnlyList<FieldDefinition> resolvedFields,
        List<FieldDefinition> filterFields,
        List<string> whereClauses,
        List<System.Data.Common.DbParameter> parameters,
        ISqlDialect dialect)
    {
        if (filters.Count == 0)
            return;

        // Build a combined lookup of all known fields
        var fieldLookup = resolvedFields
            .Concat(filterFields)
            .DistinctBy(f => f.Id)
            .ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            if (filter.Value is null)
                continue;

            // Handle date range suffixes
            string baseFieldId;
            string op;
            if (filter.Key.EndsWith("_start", StringComparison.OrdinalIgnoreCase))
            {
                baseFieldId = filter.Key[..^6];
                op = ">=";
            }
            else if (filter.Key.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
            {
                baseFieldId = filter.Key[..^4];
                op = "<=";
            }
            else
            {
                baseFieldId = filter.Key;
                op = "=";
            }

            if (!fieldLookup.TryGetValue(baseFieldId, out var filterField))
                continue;

            // SqlExpression is admin-curated config (same trust level as JoinDefinition.Sql);
            // only validate identifiers on the default SourceTable.SourceColumn path.
            if (string.IsNullOrWhiteSpace(filterField.SqlExpression))
            {
                ValidateIdentifier(filterField.SourceTable, $"Filter field '{baseFieldId}' SourceTable");
                ValidateIdentifier(filterField.SourceColumn, $"Filter field '{baseFieldId}' SourceColumn");
            }

            var paramName = $"@filter_{filter.Key.Replace(".", "_").Replace(" ", "_")}";
            whereClauses.Add($"{filterField.GetSqlExpression()} {op} {paramName}");
            parameters.Add(dialect.CreateParameter(paramName, filter.Value));
        }
    }

    private static void AppendOrderBy(
        StringBuilder sb,
        QueryRequest request,
        IReadOnlyList<FieldDefinition> resolvedFields,
        List<LookupDefinition> lookups)
    {
        if (!string.IsNullOrWhiteSpace(request.SortField))
        {
            var sortField = resolvedFields.FirstOrDefault(f =>
                f.Id.Equals(request.SortField, StringComparison.OrdinalIgnoreCase));

            if (sortField is null)
                throw new ArgumentException($"Sort field '{request.SortField}' is not among the selected fields.");

            if (string.IsNullOrWhiteSpace(sortField.SqlExpression))
            {
                ValidateIdentifier(sortField.SourceTable, "Sort field SourceTable");
                ValidateIdentifier(sortField.SourceColumn, "Sort field SourceColumn");
            }

            var direction = string.Equals(request.SortDirection, "desc", StringComparison.OrdinalIgnoreCase)
                ? "DESC"
                : "ASC";
            var sortExpr = ResolveSortExpression(sortField, lookups);
            var orderBy = $"ORDER BY {sortExpr} {direction}";

            // Secondary sort — appended after the primary. Silently skipped
            // when it isn't among selected fields or matches the primary id
            // (no point sorting by the same field twice).
            if (!string.IsNullOrWhiteSpace(request.SecondarySortField)
                && !string.Equals(request.SecondarySortField, request.SortField, StringComparison.OrdinalIgnoreCase))
            {
                var secondary = resolvedFields.FirstOrDefault(f =>
                    f.Id.Equals(request.SecondarySortField, StringComparison.OrdinalIgnoreCase));
                if (secondary is not null)
                {
                    if (string.IsNullOrWhiteSpace(secondary.SqlExpression))
                    {
                        ValidateIdentifier(secondary.SourceTable, "Secondary sort field SourceTable");
                        ValidateIdentifier(secondary.SourceColumn, "Secondary sort field SourceColumn");
                    }
                    var secDir = string.Equals(request.SecondarySortDirection, "desc", StringComparison.OrdinalIgnoreCase)
                        ? "DESC"
                        : "ASC";
                    orderBy += $", {ResolveSortExpression(secondary, lookups)} {secDir}";
                }
            }
            sb.AppendLine(orderBy);
        }
        else
        {
            // Default ORDER BY first field (required by SQL Server for OFFSET/FETCH)
            var first = resolvedFields[0];
            var sortExpr = ResolveSortExpression(first, lookups);
            sb.AppendLine($"ORDER BY {sortExpr} ASC");
        }
    }

    /// <summary>
    /// For fields with a LookupId that has an ORDERBY column, returns "LOOKUP.ORDERBY".
    /// Otherwise falls back to the field's default SQL expression.
    /// </summary>
    private static string ResolveSortExpression(FieldDefinition field, List<LookupDefinition> lookups)
    {
        if (field.LookupIds is not null && field.LookupIds.Count > 0)
        {
            var lookupMap = lookups.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var id in field.LookupIds)
            {
                if (!lookupMap.TryGetValue(id, out var lu)) continue;
                // Parse CTE name and 3rd column: "LOOKUP(STATUS, EVENTNUM, ORDERBY)" → "LOOKUP.ORDERBY"
                var match = System.Text.RegularExpressions.Regex.Match(lu.SqlPreamble.Trim(),
                    @"^(\w+)\s*\(\s*\w+\s*,\s*\w+\s*,\s*(\w+)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
            }
        }
        return field.GetSqlExpression();
    }

    private async Task<(List<Dictionary<string, object?>> Rows, int TotalCount, bool IsTruncated)> ExecuteSqlAsync(
        string sql,
        List<System.Data.Common.DbParameter> parameters,
        GuardrailConfig guardrails,
        Guid? connectionId,
        ISqlDialect dialect)
    {
        var rows = new List<Dictionary<string, object?>>();

        // Explicit connection wins; fall back to the registry-wide default
        // (any active is_default row) for legacy paths that haven't yet been
        // re-saved with a connection id.
        var connectionString = connectionId is Guid cid
            ? await _connectionResolver.GetByIdAsync(cid)
            : await _connectionResolver.GetDefaultConnectionStringAsync();
        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = guardrails.CommandTimeoutSeconds;

        foreach (var param in parameters)
        {
            command.Parameters.Add(param);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    row[columnName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }
        catch (SqlException ex) when (ex.Number == -2) // Timeout
        {
            _logger.LogError(ex,
                "Query timed out after {Timeout}s. Consider narrowing filters or reducing page size.",
                guardrails.CommandTimeoutSeconds);
            throw new TimeoutException(
                $"Query exceeded the {guardrails.CommandTimeoutSeconds}-second timeout. " +
                "Try narrowing your filters or reducing the page size.", ex);
        }

        var isTruncated = rows.Count >= guardrails.MaxRows;
        var totalCount = rows.Count;

        return (rows, totalCount, isTruncated);
    }

    // Resolve the dialect for the request's connection. Defaults to SQL
    // Server when no connection id is provided (legacy path).
    private async Task<ISqlDialect> ResolveDialectAsync(Guid? connectionId)
    {
        if (connectionId is not Guid id)
            return _dialectFactory.Get("sqlserver");
        var record = await _connectionAdmin.GetByIdAsync(id);
        return _dialectFactory.Get(record?.ConnectionType ?? "sqlserver");
    }

    // Pulls the IANA timezone to use for AT TIME ZONE wrapping. Null when
    // no connection id, non-Postgres connection, or the admin hasn't set
    // one. Any of those cases mean "don't wrap" — the flag on the field
    // becomes a no-op.
    private async Task<string?> ResolveDisplayTimezoneAsync(Guid? connectionId)
    {
        if (connectionId is not Guid id) return null;
        var record = await _connectionAdmin.GetByIdAsync(id);
        if (record is null) return null;
        if (!string.Equals(record.ConnectionType, "postgres", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.IsNullOrWhiteSpace(record.PgDisplayTimezone) ? null : record.PgDisplayTimezone;
    }

    private static List<FieldDefinition> ResolveFilterFields(
        Dictionary<string, object?> filters,
        IReadOnlyList<FieldDefinition> schemaFields)
    {
        if (filters.Count == 0)
            return [];

        // First-wins dedupe — see SchemaService.GetFieldConfigsAsync for why.
        var lookup = schemaFields
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FieldDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in filters.Keys)
        {
            // Strip _start / _end suffixes
            var baseFieldId = key;
            if (key.EndsWith("_start", StringComparison.OrdinalIgnoreCase))
                baseFieldId = key[..^6];
            else if (key.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
                baseFieldId = key[..^4];

            if (seen.Add(baseFieldId) && lookup.TryGetValue(baseFieldId, out var field))
                result.Add(field);
        }

        return result;
    }

    private static void ValidateIdentifier(string value, string context)
    {
        if (!SafeIdentifierRegex().IsMatch(value))
            throw new ArgumentException($"Invalid SQL identifier in {context}: '{value}'");
    }
}
