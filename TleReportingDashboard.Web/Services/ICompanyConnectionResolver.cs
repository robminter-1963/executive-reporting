namespace TleReportingDashboard.Web.Services;

// Resolves the ADO.NET connection string for a company's data source at
// runtime. The source of truth is the RPT_company_connections table; the
// bootstrap connection to that table (ConfigDb) stays in appsettings.
public interface ICompanyConnectionResolver
{
    // Returns the built ADO.NET connection string for the named connection
    // on the given company. Defaults to the company's 'primary' connection
    // when name is null. Throws when no connection can be resolved.
    Task<string> GetConnectionStringAsync(Guid companyId, string? name = null, CancellationToken ct = default);

    // Returns the built ADO.NET connection string for a specific
    // RPT_company_connections row. Used by the query path where each report
    // explicitly names its connection by id. Throws when the id is unknown
    // or the row is inactive.
    Task<string> GetByIdAsync(Guid connectionId, CancellationToken ct = default);

    // Resolves "the" default connection across the whole registry for call
    // sites that don't (yet) know which connection they want — legacy paths,
    // code-set lookups, schema-config fallbacks. Picks an active row flagged
    // is_default = 1 deterministically; throws when none exist. Prefer
    // GetByIdAsync when the caller can supply a connection id.
    Task<string> GetDefaultConnectionStringAsync(CancellationToken ct = default);

    // The id behind GetDefaultConnectionStringAsync — exposed so callers that
    // need to re-use it (e.g., to look up per-connection config from a
    // different service) don't have to round-trip.
    Task<Guid?> GetDefaultConnectionIdAsync(CancellationToken ct = default);

    // Drops the in-memory cache entry so the next call re-queries the DB.
    // Call this from the admin save path once the connection editor UI
    // exists. No-op when the entry isn't cached.
    void Invalidate(Guid companyId);
}
