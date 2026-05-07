using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

// Resolves a company's data-source connection string from the
// RPT_company_connections table. Caches in-memory until Invalidate() is
// called; connections are expected to change rarely (admin edits only).
//
// Bootstrap: the resolver itself needs a DB connection to read the table.
// It uses the ConfigDb connection from appsettings for that. ConfigDb is
// required for the app to run in live mode.
public sealed class DbCompanyConnectionResolver : ICompanyConnectionResolver
{
    private readonly string _configConnectionString;
    private readonly ILogger<DbCompanyConnectionResolver> _logger;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DbCompanyConnectionResolver(
        IConfiguration configuration,
        ILogger<DbCompanyConnectionResolver> logger)
    {
        _logger = logger;
        _configConnectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required — CompanyConnectionResolver has no way to bootstrap without it.");
    }

    public async Task<string> GetConnectionStringAsync(Guid companyId, string? name = null, CancellationToken ct = default)
    {
        var cacheKey = $"{companyId:N}:{name ?? "<default>"}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var record = await LoadRecordAsync(companyId, name, ct);
        if (record is null)
        {
            throw new InvalidOperationException(
                $"No active connection found for company {companyId} (name='{name ?? "<default>"}') in RPT_company_connections.");
        }

        var built = CompanyConnectionStringBuilder.Build(record);
        _cache[cacheKey] = built;
        return built;
    }

    public async Task<string> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        var cacheKey = $"id:{connectionId:N}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var record = await LoadRecordByIdAsync(connectionId, ct)
            ?? throw new InvalidOperationException(
                $"No active connection found with id {connectionId} in RPT_company_connections.");

        var built = CompanyConnectionStringBuilder.Build(record);
        _cache[cacheKey] = built;
        return built;
    }

    public async Task<string> GetDefaultConnectionStringAsync(CancellationToken ct = default)
    {
        const string cacheKey = "default:<registry>";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var record = await LoadDefaultRecordAsync(ct)
            ?? throw new InvalidOperationException(
                "No active default connection found in RPT_company_connections. " +
                "Flag at least one row with is_default = 1 AND is_active = 1.");

        var built = CompanyConnectionStringBuilder.Build(record);
        _cache[cacheKey] = built;
        return built;
    }

    public async Task<Guid?> GetDefaultConnectionIdAsync(CancellationToken ct = default)
    {
        var record = await LoadDefaultRecordAsync(ct);
        return record?.Id;
    }

    public void Invalidate(Guid companyId)
    {
        // Clear every entry tied to this company. Because GetByIdAsync keys
        // on connection id (not company id), we can't narrow cleanly here —
        // safest is to clear the whole cache on any company change.
        _cache.Clear();
    }

    // Pulls an active is_default row without a company filter. Deterministic
    // pick: order by company_id then id so if multiple companies each have a
    // default, the same one wins every time. Callers that actually know which
    // connection they need should use GetByIdAsync / GetConnectionStringAsync;
    // this exists for legacy "no id supplied" paths.
    private async Task<CompanyConnectionRecord?> LoadDefaultRecordAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT TOP 1
               id, company_id, name, connection_type, is_default, is_active,
               ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
               ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
               pg_host, pg_port, pg_database, pg_username, pg_password,
               pg_ssl_mode, pg_command_timeout, pg_timeout,
               pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
               pg_display_timezone,
               -- Dataverse columns are read defensively so pre-migration
               -- databases (no dv_* columns yet) still load SQL Server /
               -- Postgres rows. CASE WHEN COL_LENGTH ... IS NULL returns
               -- a typed NULL the reader maps to record.Dv* = null.
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_environment_url') IS NULL
                    THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_environment_url END AS dv_environment_url,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_tenant_id') IS NULL
                    THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_tenant_id END AS dv_tenant_id,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_id') IS NULL
                    THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_client_id END AS dv_client_id,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_secret') IS NULL
                    THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_client_secret END AS dv_client_secret
            FROM EMPOWER.RPT_company_connections
            WHERE is_active = 1 AND is_default = 1
            ORDER BY company_id, id";

        try
        {
            await using var conn = new SqlConnection(_configConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return ReadRecord(reader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load registry-wide default RPT_company_connections row.");
            return null;
        }
    }

    private async Task<CompanyConnectionRecord?> LoadRecordByIdAsync(Guid connectionId, CancellationToken ct)
    {
        const string sql = @"
            SELECT TOP 1
               id, company_id, name, connection_type, is_default, is_active,
               ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
               ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
               pg_host, pg_port, pg_database, pg_username, pg_password,
               pg_ssl_mode, pg_command_timeout, pg_timeout,
               pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
               pg_display_timezone,
               -- Dataverse columns are read defensively so pre-migration
               -- databases (no dv_* columns yet) still load SQL Server /
               -- Postgres rows. CASE WHEN COL_LENGTH ... IS NULL returns
               -- a typed NULL the reader maps to record.Dv* = null.
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_environment_url') IS NULL
                    THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_environment_url END AS dv_environment_url,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_tenant_id') IS NULL
                    THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_tenant_id END AS dv_tenant_id,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_id') IS NULL
                    THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_client_id END AS dv_client_id,
               CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_secret') IS NULL
                    THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_client_secret END AS dv_client_secret
            FROM EMPOWER.RPT_company_connections
            WHERE id = @id AND is_active = 1";

        try
        {
            await using var conn = new SqlConnection(_configConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@id", connectionId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return ReadRecord(reader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load RPT_company_connections row by id {ConnectionId}", connectionId);
            return null;
        }
    }

    private async Task<CompanyConnectionRecord?> LoadRecordAsync(Guid companyId, string? name, CancellationToken ct)
    {
        // Prefer the named row if specified; otherwise pull is_default = 1.
        // Only active rows are considered — is_active = 0 hides a row
        // without requiring a delete.
        var sql = name is null
            ? @"SELECT TOP 1
                   id, company_id, name, connection_type, is_default, is_active,
                   ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
                   ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
                   pg_host, pg_port, pg_database, pg_username, pg_password,
                   pg_ssl_mode, pg_command_timeout, pg_timeout,
                   pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
                   pg_display_timezone,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_environment_url') IS NULL
                        THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_environment_url END AS dv_environment_url,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_tenant_id') IS NULL
                        THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_tenant_id END AS dv_tenant_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_id') IS NULL
                        THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_client_id END AS dv_client_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_secret') IS NULL
                        THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_client_secret END AS dv_client_secret
               FROM EMPOWER.RPT_company_connections
               WHERE company_id = @c AND is_active = 1 AND is_default = 1"
            : @"SELECT TOP 1
                   id, company_id, name, connection_type, is_default, is_active,
                   ss_data_source, ss_initial_catalog, ss_integrated_security, ss_user_id, ss_password,
                   ss_application_intent, ss_encrypt, ss_trust_server_certificate, ss_mars,
                   pg_host, pg_port, pg_database, pg_username, pg_password,
                   pg_ssl_mode, pg_command_timeout, pg_timeout,
                   pg_root_certificate, pg_ssl_certificate, pg_ssl_key,
                   pg_display_timezone,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_environment_url') IS NULL
                        THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_environment_url END AS dv_environment_url,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_tenant_id') IS NULL
                        THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_tenant_id END AS dv_tenant_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_id') IS NULL
                        THEN CAST(NULL AS NVARCHAR(100)) ELSE dv_client_id END AS dv_client_id,
                   CASE WHEN COL_LENGTH('EMPOWER.RPT_company_connections','dv_client_secret') IS NULL
                        THEN CAST(NULL AS NVARCHAR(500)) ELSE dv_client_secret END AS dv_client_secret
               FROM EMPOWER.RPT_company_connections
               WHERE company_id = @c AND is_active = 1 AND name = @name";

        try
        {
            await using var conn = new SqlConnection(_configConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@c", companyId));
            if (name is not null) cmd.Parameters.Add(new SqlParameter("@name", name));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return ReadRecord(reader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load RPT_company_connections row for {CompanyId}/{Name}",
                companyId, name ?? "<default>");
            return null;
        }
    }

    // Shared mapping for both lookup paths. The SELECT column order must
    // match across both queries above.
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
        PgDisplayTimezone = reader.IsDBNull(26) ? null : reader.GetString(26),
        // Dataverse credentials. The CASE WHEN COL_LENGTH guard in the
        // SELECT means these are typed NULL on databases that haven't run
        // the 2026-05-05 dv_* migration yet; the IsDBNull checks here
        // work either way.
        DvEnvironmentUrl = reader.IsDBNull(27) ? null : reader.GetString(27),
        DvTenantId       = reader.IsDBNull(28) ? null : reader.GetString(28),
        DvClientId       = reader.IsDBNull(29) ? null : reader.GetString(29),
        DvClientSecret   = reader.IsDBNull(30) ? null : reader.GetString(30),
    };
}
