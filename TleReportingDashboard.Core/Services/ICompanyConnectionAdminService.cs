namespace TleReportingDashboard.Web.Services;

public interface ICompanyConnectionAdminService
{
    Task<List<CompanyConnectionRecord>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default);
    Task<CompanyConnectionRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CompanyConnectionRecord> CreateAsync(CompanyConnectionRecord record, CancellationToken ct = default);
    Task UpdateAsync(CompanyConnectionRecord record, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task SetDefaultAsync(Guid companyId, Guid connectionId, CancellationToken ct = default);

    // Opens the connection, runs a trivial "SELECT 1" equivalent, closes.
    // Safe on unsaved form values — caller passes the in-memory record.
    Task<ConnectionTestResult> TestAsync(CompanyConnectionRecord record, CancellationToken ct = default);

    // Copies a hand-picked set of connections (by id) into targetCompanyId.
    // Each copy gets a new id and prefixed name to dodge the (company_id,
    // name) unique index. is_default is cleared on copies so the
    // (company_id) WHERE is_default=1 filtered unique index doesn't collide
    // with the target's existing default. Source rows must all belong to a
    // single source company; mixing across companies isn't supported (the
    // UI's picker doesn't allow it either). Returns the count copied.
    Task<int> CopyConnectionsAsync(
        IReadOnlyList<Guid> sourceConnectionIds, Guid targetCompanyId, string namePrefix,
        CancellationToken ct = default);
}

// Test-button outcome. Success=true means an open + ping succeeded;
// Error carries the root message when it didn't.
public sealed record ConnectionTestResult(bool Success, string? Error, long LatencyMs);

// Full-shape row for the admin connection editor. Null fields are intentional
// — connection_type='sqlserver' rows leave the Pg* fields null and vice versa.
public sealed class CompanyConnectionRecord
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "sqlserver";    // 'sqlserver' | 'postgres'
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    // SQL Server
    public string? SsDataSource { get; set; }
    public string? SsInitialCatalog { get; set; }
    public bool? SsIntegratedSecurity { get; set; }
    public string? SsUserId { get; set; }
    public string? SsPassword { get; set; }
    public string? SsApplicationIntent { get; set; }
    public bool? SsEncrypt { get; set; }
    public bool? SsTrustServerCertificate { get; set; }
    public bool? SsMultipleActiveResultSets { get; set; }

    // Postgres
    public string? PgHost { get; set; }
    public int? PgPort { get; set; }
    public string? PgDatabase { get; set; }
    public string? PgUsername { get; set; }
    public string? PgPassword { get; set; }
    public string? PgSslMode { get; set; }
    public int? PgCommandTimeout { get; set; }
    public int? PgTimeout { get; set; }
    public byte[]? PgRootCertificate { get; set; }
    public byte[]? PgSslCertificate { get; set; }
    public byte[]? PgSslKey { get; set; }
    // IANA timezone name (e.g. "America/Los_Angeles"). When set, Postgres
    // date/datetime field expressions flagged with ApplyTimezoneConversion
    // get wrapped as "(expr AT TIME ZONE '<tz>')" at emission time. Null /
    // blank = no wrapping regardless of field flags.
    public string? PgDisplayTimezone { get; set; }

    // Optional WHERE-fragments used by every table-listing path (Schema
    // Builder browser, Admin → Table Aliases picker). Both are appended after
    // `WHERE TABLE_TYPE = 'BASE TABLE' AND` with AND semantics, so admins can
    // narrow on TABLE_SCHEMA and TABLE_NAME independently. Either blank means
    // "no filter on that axis". Dialect-specific.
    public string? SchemaFilterSql { get; set; }
    public string? TableFilterSql { get; set; }
}
