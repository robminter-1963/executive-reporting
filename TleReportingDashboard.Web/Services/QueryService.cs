using System.Data;
using System.Diagnostics;
using TleReportingDashboard.Web.Models;
using TleReportingDashboard.Web.Services.QueryPipeline;

namespace TleReportingDashboard.Web.Services;

public class QueryService : IQueryService
{
    private readonly ISchemaService _schemaService;
    private readonly ICompanyConnectionResolver _connectionResolver;
    private readonly ICompanyConnectionAdminService _connectionAdmin;
    private readonly ISqlDialectFactory _dialectFactory;
    private readonly ILogger<QueryService> _logger;

    private const int CommandTimeoutSeconds = 30;

    public QueryService(
        ISchemaService schemaService,
        ICompanyConnectionResolver connectionResolver,
        ICompanyConnectionAdminService connectionAdmin,
        ISqlDialectFactory dialectFactory,
        ILogger<QueryService> logger)
    {
        _schemaService = schemaService;
        _connectionResolver = connectionResolver;
        _connectionAdmin = connectionAdmin;
        _dialectFactory = dialectFactory;
        _logger = logger;
    }

    public async Task<QueryResponse> ExecuteQueryAsync(QueryRequest request)
    {
        var stopwatch = Stopwatch.StartNew();

        // Resolve the dialect based on the request's connection id. SQL
        // emission, parameter typing, and DbConnection instantiation all
        // branch on this — SQL Server uses TOP + bracket identifiers +
        // SqlParameter + SqlConnection; Postgres uses LIMIT + double-quoted
        // identifiers + NpgsqlParameter + NpgsqlConnection.
        var dialect = await ResolveDialectAsync(request.ConnectionId);

        // Schema is keyed on connection_id — pull the config for the
        // connection this request targets so query emission matches the
        // table shapes of that data source.
        // GetFieldConfigsAsync already paints each FieldConfig with the
        // connection's display timezone (when Postgres + configured), so
        // downstream GetSqlExpression() applies the AT TIME ZONE wrap for
        // any field that opted in.
        var fieldConfigs = await _schemaService.GetFieldConfigsAsync(request.ConnectionId);
        var joinConfigs = await _schemaService.GetJoinConfigsAsync(request.ConnectionId);
        var customFilters = await _schemaService.GetCustomFiltersAsync(request.ConnectionId);
        var lookups = await _schemaService.GetLookupsAsync(request.ConnectionId);

        // Primary table is required per-report. No schema-default fallback —
        // each report must explicitly set the root table its joins hang off.
        if (string.IsNullOrWhiteSpace(request.PrimaryTable))
        {
            throw new InvalidOperationException(
                "Primary Table is required. Set it on the report before running.");
        }

        var (sql, parameters) = QueryBuilder.BuildQuery(
            request, fieldConfigs, joinConfigs, customFilters, lookups, dialect, request.PrimaryTable);

        // Resolve columns metadata. First-wins dedupe matches the source
        // (SchemaService.GetFieldConfigsAsync) — guards against any path
        // that bypasses it.
        var fieldLookup = fieldConfigs
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dedupedFieldIds = request.FieldIds.Distinct().ToList();
        var columns = dedupedFieldIds
            .Where(id => fieldLookup.ContainsKey(id))
            .Select(id => new ColumnMeta
            {
                FieldId = id,
                Label = fieldLookup[id].Label,
                DataType = fieldLookup[id].DataType,
                MaxLength = fieldLookup[id].MaxLength,
                ValueSortOrder = fieldLookup[id].ValueSortOrder,
                Format = fieldLookup[id].Format
            })
            .ToList();

        var rows = new List<Dictionary<string, object?>>();
        int totalCount = 0;

        _logger.LogInformation("Executing query ({Dialect}) {sql} with parameters: {Parameters}",
            dialect.Name, sql, string.Join(", ", parameters.Select(p => $"{p.ParameterName}={p.Value}")));

        try
        {
            // Explicit connection wins; fall back to the registry-wide default
            // only for legacy requests that haven't been re-saved through the
            // editor. The default is resolved dynamically from is_default = 1
            // rather than pinned to a hardcoded company id.
            var connectionString = request.ConnectionId is Guid cid
                ? await _connectionResolver.GetByIdAsync(cid)
                : await _connectionResolver.GetDefaultConnectionStringAsync();
            await using var connection = dialect.CreateConnection(connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            foreach (var param in parameters)
            {
                command.Parameters.Add(param);
            }

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    row[columnName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            totalCount = rows.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing query with fields: {Fields}", string.Join(", ", request.FieldIds));
            throw;
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Query executed: Fields=[{Fields}], RowCount={RowCount}, Duration={Duration}ms",
            string.Join(", ", request.FieldIds),
            rows.Count,
            stopwatch.ElapsedMilliseconds);

        return new QueryResponse
        {
            Columns = columns,
            Rows = rows,
            TotalCount = totalCount,
            IsTruncated = rows.Count >= request.PageSize,
            ExecutionMs = stopwatch.ElapsedMilliseconds,
            // Surfaced for the "Show query" debug dialog. Parameter values
            // are ADO.NET DbParameter.Value objects — DBNull → null here
            // so the dialog's simple-table rendering shows "<null>" rather
            // than the DBNull sentinel.
            Sql = sql,
            Parameters = parameters.ToDictionary(
                p => p.ParameterName,
                p => p.Value is DBNull ? null : p.Value),
            ScopingNote = request.Scoping?.Reason,
            ScopingForceNoMatch = request.Scoping?.ForceNoMatch == true
        };
    }

    public async Task<(string Sql, Dictionary<string, object?> Parameters)> BuildSqlAsync(QueryRequest request)
    {
        // Mirrors the pre-execute portion of ExecuteQueryAsync. Stops short
        // of opening a DB connection — used by the debug dialog to recover
        // the SQL when ExecuteQueryAsync threw (typically a runtime error
        // from the source DB) and we still want to show the admin what got
        // sent. Build-phase exceptions (no primary table, bad sort field,
        // etc.) propagate to the caller.
        var dialect = await ResolveDialectAsync(request.ConnectionId);
        var fieldConfigs = await _schemaService.GetFieldConfigsAsync(request.ConnectionId);
        var joinConfigs = await _schemaService.GetJoinConfigsAsync(request.ConnectionId);
        var customFilters = await _schemaService.GetCustomFiltersAsync(request.ConnectionId);
        var lookups = await _schemaService.GetLookupsAsync(request.ConnectionId);

        if (string.IsNullOrWhiteSpace(request.PrimaryTable))
        {
            throw new InvalidOperationException(
                "Primary Table is required. Set it on the report before running.");
        }

        var (sql, parameters) = QueryBuilder.BuildQuery(
            request, fieldConfigs, joinConfigs, customFilters, lookups, dialect, request.PrimaryTable);

        return (sql, parameters.ToDictionary(
            p => p.ParameterName,
            p => p.Value is DBNull ? null : p.Value));
    }

    // Resolve the dialect for the request's connection. Defaults to SQL
    // Server when no connection id is provided (legacy reports pre-Phase 2).
    private async Task<ISqlDialect> ResolveDialectAsync(Guid? connectionId)
    {
        if (connectionId is not Guid id)
            return _dialectFactory.Get("sqlserver");
        var record = await _connectionAdmin.GetByIdAsync(id);
        return _dialectFactory.Get(record?.ConnectionType ?? "sqlserver");
    }

}
