using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Required output-column contracts for the admin-supplied Teams and Members
// SQL. No source-schema literals here — the body of either SELECT is whatever
// the customer's LOS/CRM uses; the only thing the app cares about is that
// the result set carries these column aliases.
public static class TeamSourceDefaults
{
    public static readonly string[] RequiredTeamColumns =
        { "team_id", "team_name", "manager_ext_id", "manager_name", "team_type" };

    public static readonly string[] RequiredMemberColumns =
        { "team_id", "member_ext_id" };
}

public sealed class TeamSourceService : ITeamSourceService
{
    private readonly string _configConnStr;
    private readonly ICompanyConnectionAdminService _connections;
    private readonly ICompanyConnectionResolver _resolver;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<TeamSourceService> _logger;

    public TeamSourceService(
        IConfiguration configuration,
        ICompanyConnectionAdminService connections,
        ICompanyConnectionResolver resolver,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<TeamSourceService> logger)
    {
        _configConnStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required for TeamSourceService.");
        _connections = connections;
        _resolver = resolver;
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

    public Task<TeamSourceConfig?> GetConfigAsync(Guid connectionId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("TeamSourceService", "Config", connectionId),
            async () =>
            {
                await using var conn = new SqlConnection(_configConnStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT connection_id, teams_sql, members_sql, updated_at, updated_by
                      FROM EMPOWER.RPT_team_sources
                     WHERE connection_id = @c;", conn);
                cmd.Parameters.Add(new SqlParameter("@c", connectionId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                return new TeamSourceConfig(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            },
            bypass: _editorMode.IsActive);

    public async Task SaveConfigAsync(Guid connectionId, string teamsSql, string? membersSql,
                                      string? updatedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamsSql))
            throw new ArgumentException("teams_sql is required.", nameof(teamsSql));

        var trimmedMembers = string.IsNullOrWhiteSpace(membersSql) ? null : membersSql;

        await using var conn = new SqlConnection(_configConnStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_team_sources AS t
            USING (SELECT @c AS connection_id) AS s
               ON t.connection_id = s.connection_id
            WHEN MATCHED THEN
                UPDATE SET teams_sql   = @sql,
                           members_sql = @members,
                           updated_at  = SYSUTCDATETIME(),
                           updated_by  = @by
            WHEN NOT MATCHED THEN
                INSERT (connection_id, teams_sql, members_sql, updated_by)
                VALUES (@c, @sql, @members, @by);", conn);
        cmd.Parameters.Add(new SqlParameter("@c", connectionId));
        cmd.Parameters.Add(new SqlParameter("@sql", teamsSql));
        cmd.Parameters.Add(new SqlParameter("@members", (object?)trimmedMembers ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)updatedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("TeamSourceService:");

        _logger.LogInformation("Team source SQL saved for connection {ConnectionId} by {By} (members_sql={MembersSet})",
            connectionId, updatedBy ?? "unknown", trimmedMembers is null ? "unset" : "set");
    }

    public Task<Dictionary<string, string>> GetTypeColumnsAsync(Guid connectionId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("TeamSourceService", "TypeColumns", connectionId),
            async () =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                await using var conn = new SqlConnection(_configConnStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
                    SELECT team_type, owner_column
                      FROM EMPOWER.RPT_team_type_columns
                     WHERE connection_id = @c;", conn);
                cmd.Parameters.Add(new SqlParameter("@c", connectionId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    map[reader.GetString(0)] = reader.GetString(1);
                }
                return map;
            },
            bypass: _editorMode.IsActive);

    public async Task SaveTypeColumnsAsync(Guid connectionId,
                                           IReadOnlyDictionary<string, string> typeColumns,
                                           CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_configConnStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Full replace: the Team Builder editor owns the full mapping
            // for a connection, so a save with an empty dict wipes all
            // existing rows (which is the right thing — if the admin
            // removed every row in the UI they intend zero mappings).
            await using (var del = new SqlCommand(
                "DELETE FROM EMPOWER.RPT_team_type_columns WHERE connection_id = @c;", conn, tx))
            {
                del.Parameters.Add(new SqlParameter("@c", connectionId));
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var (type, col) in typeColumns)
            {
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(col)) continue;
                await using var ins = new SqlCommand(@"
                    INSERT INTO EMPOWER.RPT_team_type_columns
                        (connection_id, team_type, owner_column)
                    VALUES (@c, @t, @col);", conn, tx);
                ins.Parameters.Add(new SqlParameter("@c", connectionId));
                ins.Parameters.Add(new SqlParameter("@t", type.Trim()));
                ins.Parameters.Add(new SqlParameter("@col", col.Trim()));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            _cache.Invalidate("TeamSourceService:TypeColumns:");
            _logger.LogInformation("Team-type columns saved for connection {ConnectionId}: {Count} mapping(s).",
                connectionId, typeColumns.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<List<TeamRecord>> QueryTeamsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(connectionId, ct);
        if (config is null) return new List<TeamRecord>();
        return await RunTeamsSqlAsync(connectionId, config.TeamsSql, ct);
    }

    public Task<List<TeamRecord>> PreviewTeamsAsync(Guid connectionId, string teamsSql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamsSql))
            throw new ArgumentException("teams_sql is required.", nameof(teamsSql));
        return RunTeamsSqlAsync(connectionId, teamsSql, ct);
    }

    public async Task<List<string>> GetSourceTableColumnsAsync(Guid connectionId, string tableName,
                                                                CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("tableName is required.", nameof(tableName));

        var record = await _connections.GetByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
        if (!string.Equals(record.ConnectionType, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Source-table column lookup currently supports SQL Server only. '{record.Name}' is '{record.ConnectionType}'.");
        }

        // Split "schema.table" into parts. Default schema is dbo to match
        // SQL Server's resolution rules; bracketed identifiers ("[schema].
        // [table]") are stripped so they don't end up inside the parameter
        // value and miss the metadata join.
        string schemaName = "dbo", bareName;
        var dot = tableName.IndexOf('.');
        if (dot >= 0)
        {
            schemaName = Strip(tableName[..dot]);
            bareName = Strip(tableName[(dot + 1)..]);
        }
        else
        {
            bareName = Strip(tableName);
        }

        var sourceConnStr = await _resolver.GetByIdAsync(connectionId, ct);
        var rows = new List<string>();

        await using var conn = new SqlConnection(sourceConnStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT COLUMN_NAME
              FROM INFORMATION_SCHEMA.COLUMNS
             WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
             ORDER BY COLUMN_NAME;", conn);
        cmd.Parameters.Add(new SqlParameter("@schema", schemaName));
        cmd.Parameters.Add(new SqlParameter("@table", bareName));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;

        static string Strip(string s) => s.Trim().Trim('[', ']', '"').Trim();
    }

    private async Task<List<TeamRecord>> RunTeamsSqlAsync(Guid connectionId, string teamsSql, CancellationToken ct)
    {
        var record = await _connections.GetByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
        if (!string.Equals(record.ConnectionType, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Team Builder currently supports SQL Server source connections only. '{record.Name}' is '{record.ConnectionType}'.");
        }

        var sourceConnStr = await _resolver.GetByIdAsync(connectionId, ct);
        var rows = new List<TeamRecord>();

        await using var conn = new SqlConnection(sourceConnStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(teamsSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Map column name → ordinal so admin SELECTs with columns in any order
        // still bind correctly. Required names are validated up-front so a
        // typo surfaces as a clear error rather than a silent NULL.
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        var missing = TeamSourceDefaults.RequiredTeamColumns
            .Where(c => !ordinals.ContainsKey(c))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Teams SQL is missing required column(s): {string.Join(", ", missing)}. "
                + $"Expected: {string.Join(", ", TeamSourceDefaults.RequiredTeamColumns)}.");
        }

        int idI = ordinals["team_id"], nameI = ordinals["team_name"],
            mgrExtI = ordinals["manager_ext_id"], mgrNameI = ordinals["manager_name"],
            typeI = ordinals["team_type"];

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TeamRecord(
                Convert.ToInt32(reader.GetValue(idI)),     // tolerate TINYINT/SMALLINT/INT/BIGINT
                reader.IsDBNull(nameI) ? null : reader.GetValue(nameI)?.ToString(),
                reader.IsDBNull(mgrExtI) ? null : reader.GetValue(mgrExtI)?.ToString(),
                reader.IsDBNull(mgrNameI) ? null : reader.GetValue(mgrNameI)?.ToString(),
                reader.IsDBNull(typeI) ? null : reader.GetValue(typeI)?.ToString()));
        }

        return rows;
    }
}
