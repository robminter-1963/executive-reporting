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

    public static readonly string[] RequiredUserEmailColumns =
        { "member_ext_id", "email" };
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
                // user_emails_sql is read defensively because envs that
                // haven't run the 2026-05-05_15-00 migration yet won't
                // have the column. Fall back to null and the runtime
                // resolver returns an empty list, dropping the Worker
                // back to the older GetByExternalUserIdAsync chain.
                await using var cmd = new SqlCommand(@"
                    SELECT connection_id, teams_sql, members_sql,
                           CASE WHEN COL_LENGTH('EMPOWER.RPT_team_sources','user_emails_sql') IS NULL
                                THEN CAST(NULL AS NVARCHAR(MAX))
                                ELSE user_emails_sql END AS user_emails_sql,
                           updated_at, updated_by
                      FROM EMPOWER.RPT_team_sources
                     WHERE connection_id = @c;", conn);
                cmd.Parameters.Add(new SqlParameter("@c", connectionId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                return new TeamSourceConfig(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            },
            bypass: _editorMode.IsActive);

    public async Task SaveConfigAsync(Guid connectionId, string teamsSql, string? membersSql,
                                      string? userEmailsSql, string? updatedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamsSql))
            throw new ArgumentException("teams_sql is required.", nameof(teamsSql));

        var trimmedMembers = string.IsNullOrWhiteSpace(membersSql) ? null : membersSql;
        var trimmedEmails = string.IsNullOrWhiteSpace(userEmailsSql) ? null : userEmailsSql;

        await using var conn = new SqlConnection(_configConnStr);
        await conn.OpenAsync(ct);

        // Detect whether the user_emails_sql column has been added yet
        // — the migration is additive, but envs that haven't applied it
        // would error on the column reference. Fall back to a 3-column
        // MERGE in that case so saves still work and the new SQL just
        // doesn't persist until the migration lands.
        var hasEmailsColumn = await ColumnExistsAsync(conn, "RPT_team_sources", "user_emails_sql", ct);

        var sql = hasEmailsColumn
            ? @"MERGE EMPOWER.RPT_team_sources AS t
                USING (SELECT @c AS connection_id) AS s
                   ON t.connection_id = s.connection_id
                WHEN MATCHED THEN
                    UPDATE SET teams_sql       = @sql,
                               members_sql     = @members,
                               user_emails_sql = @emails,
                               updated_at      = SYSUTCDATETIME(),
                               updated_by      = @by
                WHEN NOT MATCHED THEN
                    INSERT (connection_id, teams_sql, members_sql, user_emails_sql, updated_by)
                    VALUES (@c, @sql, @members, @emails, @by);"
            : @"MERGE EMPOWER.RPT_team_sources AS t
                USING (SELECT @c AS connection_id) AS s
                   ON t.connection_id = s.connection_id
                WHEN MATCHED THEN
                    UPDATE SET teams_sql   = @sql,
                               members_sql = @members,
                               updated_at  = SYSUTCDATETIME(),
                               updated_by  = @by
                WHEN NOT MATCHED THEN
                    INSERT (connection_id, teams_sql, members_sql, updated_by)
                    VALUES (@c, @sql, @members, @by);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@c", connectionId));
        cmd.Parameters.Add(new SqlParameter("@sql", teamsSql));
        cmd.Parameters.Add(new SqlParameter("@members", (object?)trimmedMembers ?? DBNull.Value));
        if (hasEmailsColumn)
            cmd.Parameters.Add(new SqlParameter("@emails", (object?)trimmedEmails ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)updatedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Invalidate("TeamSourceService:");

        _logger.LogInformation(
            "Team source SQL saved for connection {ConnectionId} by {By} (members_sql={MembersSet}, user_emails_sql={EmailsSet})",
            connectionId, updatedBy ?? "unknown",
            trimmedMembers is null ? "unset" : "set",
            !hasEmailsColumn ? "n/a (pre-migration)" : trimmedEmails is null ? "unset" : "set");
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN COL_LENGTH('EMPOWER.' + @t, @col) IS NULL THEN 0 ELSE 1 END;", conn);
        cmd.Parameters.Add(new SqlParameter("@t", table));
        cmd.Parameters.Add(new SqlParameter("@col", column));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i && i == 1;
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

    public Task<List<TeamMemberRecord>> PreviewMembersAsync(Guid connectionId, string membersSql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(membersSql))
            throw new ArgumentException("members_sql is required.", nameof(membersSql));
        return RunMembersSqlAsync(connectionId, membersSql, ct);
    }

    public async Task<List<TeamMemberRecord>> QueryMembersAsync(Guid connectionId, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(connectionId, ct);
        if (config is null || string.IsNullOrWhiteSpace(config.MembersSql))
            return new List<TeamMemberRecord>();
        return await RunMembersSqlAsync(connectionId, config.MembersSql!, ct);
    }

    public Task<List<TeamMemberEmailRecord>> PreviewUserEmailsAsync(Guid connectionId, string userEmailsSql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmailsSql))
            throw new ArgumentException("user_emails_sql is required.", nameof(userEmailsSql));
        return RunUserEmailsSqlAsync(connectionId, userEmailsSql, ct);
    }

    public async Task<List<TeamMemberEmailRecord>> QueryUserEmailsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(connectionId, ct);
        if (config is null || string.IsNullOrWhiteSpace(config.UserEmailsSql))
            return new List<TeamMemberEmailRecord>();
        return await RunUserEmailsSqlAsync(connectionId, config.UserEmailsSql!, ct);
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

    private async Task<List<TeamMemberEmailRecord>> RunUserEmailsSqlAsync(Guid connectionId, string userEmailsSql, CancellationToken ct)
    {
        var record = await _connections.GetByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
        if (!string.Equals(record.ConnectionType, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Team Builder currently supports SQL Server source connections only. '{record.Name}' is '{record.ConnectionType}'.");
        }

        var sourceConnStr = await _resolver.GetByIdAsync(connectionId, ct);
        var rows = new List<TeamMemberEmailRecord>();

        await using var conn = new SqlConnection(sourceConnStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(userEmailsSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        var missing = TeamSourceDefaults.RequiredUserEmailColumns
            .Where(c => !ordinals.ContainsKey(c))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"User Emails SQL is missing required column(s): {string.Join(", ", missing)}. "
                + $"Expected: {string.Join(", ", TeamSourceDefaults.RequiredUserEmailColumns)}.");
        }

        int extI = ordinals["member_ext_id"], emailI = ordinals["email"];

        while (await reader.ReadAsync(ct))
        {
            var ext = reader.GetValue(extI)?.ToString() ?? string.Empty;
            var email = reader.IsDBNull(emailI) ? string.Empty : reader.GetValue(emailI)?.ToString() ?? string.Empty;
            // Drop rows where either side is blank — they'd just cause
            // skip-with-log noise downstream and aren't useful for any
            // consumer.
            if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(email)) continue;
            rows.Add(new TeamMemberEmailRecord(ext, email));
        }

        return rows;
    }

    private async Task<List<TeamMemberRecord>> RunMembersSqlAsync(Guid connectionId, string membersSql, CancellationToken ct)
    {
        var record = await _connections.GetByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
        if (!string.Equals(record.ConnectionType, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Team Builder currently supports SQL Server source connections only. '{record.Name}' is '{record.ConnectionType}'.");
        }

        var sourceConnStr = await _resolver.GetByIdAsync(connectionId, ct);
        var rows = new List<TeamMemberRecord>();

        await using var conn = new SqlConnection(sourceConnStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(membersSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        var missing = TeamSourceDefaults.RequiredMemberColumns
            .Where(c => !ordinals.ContainsKey(c))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Members SQL is missing required column(s): {string.Join(", ", missing)}. "
                + $"Expected: {string.Join(", ", TeamSourceDefaults.RequiredMemberColumns)}.");
        }

        int idI = ordinals["team_id"], extI = ordinals["member_ext_id"];
        // member_name is opportunistic — surfaced when present so the
        // preview can render "Jane Doe (jdoe)" instead of just "jdoe".
        int? nameI = ordinals.TryGetValue("member_name", out var n) ? n : null;

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TeamMemberRecord(
                Convert.ToInt32(reader.GetValue(idI)),
                reader.GetValue(extI)?.ToString() ?? string.Empty,
                nameI is int ni && !reader.IsDBNull(ni)
                    ? reader.GetValue(ni)?.ToString()
                    : null));
        }

        return rows;
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
