using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace TleReportingDashboard.Web.Services;

public sealed class CompanyConnectionAdminService : ICompanyConnectionAdminService
{
    private readonly string _connStr;
    private readonly ICompanyConnectionResolver _resolver;
    private readonly ConfigDbCache _cache;
    private readonly EditorModeState _editorMode;
    private readonly ILogger<CompanyConnectionAdminService> _logger;

    public CompanyConnectionAdminService(
        IConfiguration configuration,
        ICompanyConnectionResolver resolver,
        ConfigDbCache cache,
        EditorModeState editorMode,
        ILogger<CompanyConnectionAdminService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _resolver = resolver;
        _cache = cache;
        _editorMode = editorMode;
        _logger = logger;
    }

    public Task<List<CompanyConnectionRecord>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CompanyConnectionAdminService", "ByCompany", companyId),
            async () =>
            {
                var rows = new List<CompanyConnectionRecord>();
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(SelectAllColumns +
                    "FROM EMPOWER.RPT_company_connections WHERE company_id = @c ORDER BY is_default DESC, name;", conn);
                cmd.Parameters.Add(new SqlParameter("@c", companyId));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(ReadRecord(reader));
                }
                return rows;
            },
            bypass: _editorMode.IsActive);

    public Task<CompanyConnectionRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("CompanyConnectionAdminService", "ById", id),
            async () =>
            {
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(SelectAllColumns +
                    "FROM EMPOWER.RPT_company_connections WHERE id = @id;", conn);
                cmd.Parameters.Add(new SqlParameter("@id", id));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
            },
            bypass: _editorMode.IsActive);

    // Single source of truth for the column order, used by both GET paths
    // and the ReadRecord mapper below.
    private const string SelectAllColumns = @"
        SELECT id, company_id, name, connection_type, is_default, is_active,
               ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
               ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
               pg_host, pg_port, pg_database, pg_username, pg_password,
               pg_ssl_mode, pg_command_timeout, pg_timeout,
               pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
               table_filter_sql, schema_filter_sql, pg_display_timezone ";

    private static CompanyConnectionRecord ReadRecord(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        CompanyId = reader.GetGuid(1),
        Name = reader.GetString(2),
        ConnectionType = reader.GetString(3),
        IsDefault = reader.GetBoolean(4),
        IsActive = reader.GetBoolean(5),

        SsDataSource = reader.IsDBNull(6) ? null : reader.GetString(6),
        SsInitialCatalog = reader.IsDBNull(7) ? null : reader.GetString(7),
        SsIntegratedSecurity = reader.IsDBNull(8) ? null : reader.GetBoolean(8),
        SsUserId = reader.IsDBNull(9) ? null : reader.GetString(9),
        SsPassword = reader.IsDBNull(10) ? null : reader.GetString(10),
        SsApplicationIntent = reader.IsDBNull(11) ? null : reader.GetString(11),
        SsEncrypt = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
        SsTrustServerCertificate = reader.IsDBNull(13) ? null : reader.GetBoolean(13),
        SsMultipleActiveResultSets = reader.IsDBNull(14) ? null : reader.GetBoolean(14),

        PgHost = reader.IsDBNull(15) ? null : reader.GetString(15),
        PgPort = reader.IsDBNull(16) ? null : reader.GetInt32(16),
        PgDatabase = reader.IsDBNull(17) ? null : reader.GetString(17),
        PgUsername = reader.IsDBNull(18) ? null : reader.GetString(18),
        PgPassword = reader.IsDBNull(19) ? null : reader.GetString(19),
        PgSslMode = reader.IsDBNull(20) ? null : reader.GetString(20),
        PgCommandTimeout = reader.IsDBNull(21) ? null : reader.GetInt32(21),
        PgTimeout = reader.IsDBNull(22) ? null : reader.GetInt32(22),
        PgRootCertificate = reader.IsDBNull(23) ? null : (byte[])reader.GetValue(23),
        PgSslCertificate = reader.IsDBNull(24) ? null : (byte[])reader.GetValue(24),
        PgSslKey = reader.IsDBNull(25) ? null : (byte[])reader.GetValue(25),
        TableFilterSql = reader.IsDBNull(26) ? null : reader.GetString(26),
        SchemaFilterSql = TryReadString(reader, 27),
        PgDisplayTimezone = TryReadString(reader, 28),
    };

    // Tolerates schema_filter_sql not yet being present (migration unrun)
    // so the service keeps working with older DBs. Once the column is
    // there, the reader falls through to GetString.
    private static string? TryReadString(SqlDataReader reader, int ordinal)
    {
        try
        {
            if (ordinal >= reader.FieldCount) return null;
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public async Task<CompanyConnectionRecord> CreateAsync(CompanyConnectionRecord r, CancellationToken ct = default)
    {
        Validate(r);
        r.Id = Guid.NewGuid();

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (r.IsDefault)
            await ClearDefaultAsync(conn, tx, r.CompanyId, excludeId: null, ct);

        await using (var cmd = BuildInsertCommand(r, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _resolver.Invalidate(r.CompanyId);
        _cache.Invalidate("CompanyConnectionAdminService:");
        _cache.Invalidate("SchemaService:");
        _logger.LogInformation("Connection created: company={CompanyId} name={Name} type={Type}", r.CompanyId, r.Name, r.ConnectionType);
        return r;
    }

    public async Task UpdateAsync(CompanyConnectionRecord r, CancellationToken ct = default)
    {
        Validate(r);
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (r.IsDefault)
            await ClearDefaultAsync(conn, tx, r.CompanyId, excludeId: r.Id, ct);

        await using (var cmd = BuildUpdateCommand(r, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _resolver.Invalidate(r.CompanyId);
        _cache.Invalidate("CompanyConnectionAdminService:");
        _cache.Invalidate("SchemaService:");
        _logger.LogInformation("Connection updated: {Id} ({Name})", r.Id, r.Name);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Capture companyId before deleting so we can invalidate the resolver's cache.
        Guid? companyId = null;
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        await using (var lookup = new SqlCommand(
            "SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @id", conn))
        {
            lookup.Parameters.Add(new SqlParameter("@id", id));
            var val = await lookup.ExecuteScalarAsync(ct);
            if (val is Guid g) companyId = g;
        }

        await using (var del = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_company_connections WHERE id = @id", conn))
        {
            del.Parameters.Add(new SqlParameter("@id", id));
            await del.ExecuteNonQueryAsync(ct);
        }

        if (companyId is Guid c) _resolver.Invalidate(c);
        _cache.Invalidate("CompanyConnectionAdminService:");
        _cache.Invalidate("SchemaService:");
        _logger.LogInformation("Connection deleted: {Id}", id);
    }

    public async Task<ConnectionTestResult> TestAsync(CompanyConnectionRecord r, CancellationToken ct = default)
    {
        // Validate shape first so we don't fail deep in the provider with a
        // cryptic error for an obvious form-level mistake.
        try { Validate(r); }
        catch (ArgumentException ex) { return new ConnectionTestResult(false, ex.Message, 0); }

        string connStr;
        try { connStr = CompanyConnectionStringBuilder.Build(r); }
        catch (Exception ex) { return new ConnectionTestResult(false, $"Could not build connection string: {ex.Message}", 0); }

        var sw = Stopwatch.StartNew();
        try
        {
            // Short timeout so the Test button doesn't hang for 30s when the
            // server is unreachable. We override the timeout in the builder
            // rather than at the SqlConnection level because the builder owns
            // the canonical connection string.
            if (r.ConnectionType == "sqlserver")
            {
                var b = new SqlConnectionStringBuilder(connStr) { ConnectTimeout = 5 };
                await using var conn = new SqlConnection(b.ConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
            }
            else
            {
                var b = new NpgsqlConnectionStringBuilder(connStr) { Timeout = 5 };
                await using var conn = new NpgsqlConnection(b.ConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
            }
            sw.Stop();
            _logger.LogInformation("Connection test OK for {Company}/{Name} ({Ms}ms)",
                r.CompanyId, r.Name, sw.ElapsedMilliseconds);
            return new ConnectionTestResult(true, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Connection test failed for {Company}/{Name}", r.CompanyId, r.Name);
            return new ConnectionTestResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<int> CopyConnectionsAsync(
        IReadOnlyList<Guid> sourceConnectionIds, Guid targetCompanyId, string namePrefix,
        CancellationToken ct = default)
    {
        if (sourceConnectionIds is null || sourceConnectionIds.Count == 0)
            throw new ArgumentException("At least one source connection id is required.");
        if (namePrefix is null) namePrefix = string.Empty;
        if (namePrefix.Length > 50)
            throw new ArgumentException("Name prefix must be 50 characters or fewer.");

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Discover the live column list from INFORMATION_SCHEMA so the copy
        // doesn't depend on a hand-maintained allowlist that would drift
        // every time a migration adds a column. Excludes columns the
        // INSERT shouldn't carry forward:
        //   id          → identity / default, fresh per row
        //   company_id  → overridden with @target
        //   name        → overridden with @prefix + name
        //   is_default  → forced to 0 so the (company_id) WHERE is_default=1
        //                 filtered unique index can't conflict with the
        //                 target's existing default; admin promotes one
        //                 explicitly afterwards
        //   created_at  → reset to SYSUTCDATETIME()
        //   updated_at  → reset to SYSUTCDATETIME()
        var columns = new List<string>();
        await using (var colCmd = new SqlCommand(@"
            SELECT COLUMN_NAME
              FROM INFORMATION_SCHEMA.COLUMNS
             WHERE TABLE_SCHEMA = 'EMPOWER'
               AND TABLE_NAME   = 'RPT_company_connections'
             ORDER BY ORDINAL_POSITION;", conn, tx))
        await using (var reader = await colCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
        }

        // Carry-over columns: everything NOT in the override list above.
        var overrideCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "company_id", "name", "is_default", "created_at", "updated_at"
        };
        var carryCols = columns.Where(c => !overrideCols.Contains(c)).ToList();

        // INSERT column list = overrides (in fixed order) + carry-over.
        // Same order on both sides so the projection lines up by position.
        var insertCols = new List<string> { "company_id", "name", "is_default", "created_at", "updated_at" };
        insertCols.AddRange(carryCols);
        var selectExprs = new List<string>
        {
            "@target",
            "@prefix + c.name",
            "0",
            "SYSUTCDATETIME()",
            "SYSUTCDATETIME()"
        };
        selectExprs.AddRange(carryCols.Select(c => $"c.[{c}]"));

        // STRING_SPLIT on a CSV of the ids — works on every supported SQL
        // Server version without requiring a TVP type. TRY_CAST + the
        // company_id <> @target guard are belt-and-suspenders against
        // stray ids; the call is fully parameterized.
        var idsCsv = string.Join(",", sourceConnectionIds);

        var sql = $@"
            INSERT INTO EMPOWER.RPT_company_connections (
                {string.Join(", ", insertCols.Select(c => $"[{c}]"))}
            )
            SELECT
                {string.Join(", ", selectExprs)}
              FROM EMPOWER.RPT_company_connections c
              JOIN STRING_SPLIT(@ids, ',') s
                ON c.id = TRY_CAST(s.value AS UNIQUEIDENTIFIER)
             WHERE c.company_id <> @target;";

        int rows;
        await using (var cmd = new SqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@target", targetCompanyId));
            cmd.Parameters.Add(new SqlParameter("@prefix", namePrefix));
            cmd.Parameters.Add(new SqlParameter("@ids", idsCsv));
            rows = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _resolver.Invalidate(targetCompanyId);
        _cache.Invalidate("CompanyConnectionAdminService:");
        _cache.Invalidate("SchemaService:");
        _logger.LogInformation(
            "Connections copied: {Rows} rows ({SourceCount} requested) → {Target} (prefix='{Prefix}')",
            rows, sourceConnectionIds.Count, targetCompanyId, namePrefix);
        return rows;
    }

    public async Task SetDefaultAsync(Guid companyId, Guid connectionId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await ClearDefaultAsync(conn, tx, companyId, excludeId: connectionId, ct);
        await using (var setCmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_company_connections SET is_default = 1, updated_at = SYSUTCDATETIME()
            WHERE id = @id AND company_id = @c;", conn, tx))
        {
            setCmd.Parameters.Add(new SqlParameter("@id", connectionId));
            setCmd.Parameters.Add(new SqlParameter("@c", companyId));
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _resolver.Invalidate(companyId);
        _cache.Invalidate("CompanyConnectionAdminService:");
        _logger.LogInformation("Connection default set: company={CompanyId} connection={ConnectionId}", companyId, connectionId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task ClearDefaultAsync(SqlConnection conn, SqlTransaction tx, Guid companyId, Guid? excludeId, CancellationToken ct)
    {
        var sql = excludeId is null
            ? "UPDATE EMPOWER.RPT_company_connections SET is_default = 0, updated_at = SYSUTCDATETIME() WHERE company_id = @c AND is_default = 1;"
            : "UPDATE EMPOWER.RPT_company_connections SET is_default = 0, updated_at = SYSUTCDATETIME() WHERE company_id = @c AND is_default = 1 AND id <> @x;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@c", companyId));
        if (excludeId is not null) cmd.Parameters.Add(new SqlParameter("@x", excludeId.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand BuildInsertCommand(CompanyConnectionRecord r, SqlConnection conn, SqlTransaction tx)
    {
        var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_company_connections (
                id, company_id, name, connection_type, is_default, is_active,
                ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
                ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
                pg_host, pg_port, pg_database, pg_username, pg_password,
                pg_ssl_mode, pg_command_timeout, pg_timeout,
                pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
                table_filter_sql, schema_filter_sql, pg_display_timezone)
            VALUES (
                @id, @companyId, @name, @type, @isDefault, @isActive,
                @ssDataSource, @ssInitialCatalog, @ssIntegrated, @ssUserId, @ssPassword,
                @ssAppIntent, @ssEncrypt, @ssTrust, @ssMars,
                @pgHost, @pgPort, @pgDatabase, @pgUsername, @pgPassword,
                @pgSslMode, @pgCommandTimeout, @pgTimeout,
                @pgRoot, @pgCert, @pgKey,
                @tableFilterSql, @schemaFilterSql, @pgDisplayTimezone);", conn, tx);
        AddAllParams(cmd, r);
        return cmd;
    }

    private static SqlCommand BuildUpdateCommand(CompanyConnectionRecord r, SqlConnection conn, SqlTransaction tx)
    {
        var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_company_connections SET
                name = @name, connection_type = @type, is_default = @isDefault, is_active = @isActive,
                ss_data_source = @ssDataSource, ss_initial_catalog = @ssInitialCatalog,
                ss_integrated_security = @ssIntegrated, ss_user_id = @ssUserId, ss_password = @ssPassword,
                ss_application_intent = @ssAppIntent, ss_encrypt = @ssEncrypt, ss_trust_server_certificate = @ssTrust,
                ss_mars = @ssMars,
                pg_host = @pgHost, pg_port = @pgPort, pg_database = @pgDatabase,
                pg_username = @pgUsername, pg_password = @pgPassword,
                pg_ssl_mode = @pgSslMode, pg_command_timeout = @pgCommandTimeout, pg_timeout = @pgTimeout,
                pg_root_certificate = @pgRoot, pg_ssl_certificate = @pgCert, pg_ssl_key = @pgKey,
                table_filter_sql = @tableFilterSql,
                schema_filter_sql = @schemaFilterSql,
                pg_display_timezone = @pgDisplayTimezone,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id;", conn, tx);
        AddAllParams(cmd, r);
        return cmd;
    }

    private static void AddAllParams(SqlCommand cmd, CompanyConnectionRecord r)
    {
        cmd.Parameters.Add(new SqlParameter("@id", r.Id));
        cmd.Parameters.Add(new SqlParameter("@companyId", r.CompanyId));
        cmd.Parameters.Add(new SqlParameter("@name", r.Name));
        cmd.Parameters.Add(new SqlParameter("@type", r.ConnectionType));
        cmd.Parameters.Add(new SqlParameter("@isDefault", r.IsDefault));
        cmd.Parameters.Add(new SqlParameter("@isActive", r.IsActive));

        cmd.Parameters.Add(Optional("@ssDataSource", r.SsDataSource));
        cmd.Parameters.Add(Optional("@ssInitialCatalog", r.SsInitialCatalog));
        cmd.Parameters.Add(OptionalBool("@ssIntegrated", r.SsIntegratedSecurity));
        cmd.Parameters.Add(Optional("@ssUserId", r.SsUserId));
        cmd.Parameters.Add(Optional("@ssPassword", r.SsPassword));
        cmd.Parameters.Add(Optional("@ssAppIntent", r.SsApplicationIntent));
        cmd.Parameters.Add(OptionalBool("@ssEncrypt", r.SsEncrypt));
        cmd.Parameters.Add(OptionalBool("@ssTrust", r.SsTrustServerCertificate));
        cmd.Parameters.Add(OptionalBool("@ssMars", r.SsMultipleActiveResultSets));

        cmd.Parameters.Add(Optional("@pgHost", r.PgHost));
        cmd.Parameters.Add(OptionalInt("@pgPort", r.PgPort));
        cmd.Parameters.Add(Optional("@pgDatabase", r.PgDatabase));
        cmd.Parameters.Add(Optional("@pgUsername", r.PgUsername));
        cmd.Parameters.Add(Optional("@pgPassword", r.PgPassword));
        cmd.Parameters.Add(Optional("@pgSslMode", r.PgSslMode));
        cmd.Parameters.Add(OptionalInt("@pgCommandTimeout", r.PgCommandTimeout));
        cmd.Parameters.Add(OptionalInt("@pgTimeout", r.PgTimeout));

        cmd.Parameters.Add(OptionalBytes("@pgRoot", r.PgRootCertificate));
        cmd.Parameters.Add(OptionalBytes("@pgCert", r.PgSslCertificate));
        cmd.Parameters.Add(OptionalBytes("@pgKey", r.PgSslKey));

        cmd.Parameters.Add(Optional("@tableFilterSql", r.TableFilterSql));
        cmd.Parameters.Add(Optional("@schemaFilterSql", r.SchemaFilterSql));
        cmd.Parameters.Add(Optional("@pgDisplayTimezone", r.PgDisplayTimezone));
    }

    private static SqlParameter Optional(string name, string? value) => new(name, (object?)value ?? DBNull.Value);
    private static SqlParameter OptionalBool(string name, bool? value) => new(name, (object?)value ?? DBNull.Value);
    private static SqlParameter OptionalInt(string name, int? value) => new(name, (object?)value ?? DBNull.Value);
    private static SqlParameter OptionalBytes(string name, byte[]? value) =>
        new(name, SqlDbType.VarBinary) { Value = (object?)value ?? DBNull.Value };

    private static void Validate(CompanyConnectionRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Name is required.");
        if (r.ConnectionType is not ("sqlserver" or "postgres"))
            throw new ArgumentException("connection_type must be 'sqlserver' or 'postgres'.");
        if (r.ConnectionType == "sqlserver")
        {
            if (string.IsNullOrWhiteSpace(r.SsDataSource) || string.IsNullOrWhiteSpace(r.SsInitialCatalog))
                throw new ArgumentException("SQL Server connections require DataSource and InitialCatalog.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.PgHost) || string.IsNullOrWhiteSpace(r.PgDatabase) || string.IsNullOrWhiteSpace(r.PgUsername))
                throw new ArgumentException("Postgres connections require Host, Database, and Username.");
        }
    }
}
