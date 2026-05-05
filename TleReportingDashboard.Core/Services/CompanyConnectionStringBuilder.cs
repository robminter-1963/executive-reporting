using Microsoft.Data.SqlClient;
using Npgsql;

namespace TleReportingDashboard.Web.Services;

// Shared connection-string assembly. Used by the resolver at query time and
// by the admin service's Test button to validate unsaved form values. Kept
// pure-functional — no DB access, no caching — so both callers can use it
// without side effects.
public static class CompanyConnectionStringBuilder
{
    public static string Build(CompanyConnectionRecord r) =>
        r.ConnectionType?.ToLowerInvariant() switch
        {
            "sqlserver" => BuildSqlServer(r),
            "postgres"  => BuildPostgres(r),
            var other   => throw new InvalidOperationException($"Unknown connection_type '{other}'.")
        };

    private static string BuildSqlServer(CompanyConnectionRecord r)
    {
        var b = new SqlConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(r.SsDataSource))     b.DataSource     = r.SsDataSource;
        if (!string.IsNullOrWhiteSpace(r.SsInitialCatalog)) b.InitialCatalog = r.SsInitialCatalog;
        if (r.SsIntegratedSecurity == true)                 b.IntegratedSecurity = true;
        if (!string.IsNullOrWhiteSpace(r.SsUserId))         b.UserID         = r.SsUserId;
        if (!string.IsNullOrWhiteSpace(r.SsPassword))       b.Password       = r.SsPassword;
        if (r.SsEncrypt.HasValue)                           b.Encrypt        = r.SsEncrypt.Value;
        if (r.SsTrustServerCertificate == true)             b.TrustServerCertificate = true;
        if (!string.IsNullOrWhiteSpace(r.SsApplicationIntent)
            && Enum.TryParse<ApplicationIntent>(r.SsApplicationIntent, ignoreCase: true, out var intent))
        {
            b.ApplicationIntent = intent;
        }
        // Admin-toggle: enables MultipleActiveResultSets on the connection
        // string so the driver permits multiple active readers per connection.
        // Off by default — the query pipeline uses single-reader-per-connection
        // throughout, so most admins should leave this unset.
        if (r.SsMultipleActiveResultSets == true)
        {
            b.MultipleActiveResultSets = true;
        }
        return b.ConnectionString;
    }

    private static string BuildPostgres(CompanyConnectionRecord r)
    {
        var b = new NpgsqlConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(r.PgHost))     b.Host     = r.PgHost;
        if (r.PgPort.HasValue)                        b.Port     = r.PgPort.Value;
        if (!string.IsNullOrWhiteSpace(r.PgDatabase)) b.Database = r.PgDatabase;
        if (!string.IsNullOrWhiteSpace(r.PgUsername)) b.Username = r.PgUsername;
        if (!string.IsNullOrWhiteSpace(r.PgPassword)) b.Password = r.PgPassword;
        if (r.PgCommandTimeout.HasValue)              b.CommandTimeout = r.PgCommandTimeout.Value;
        if (r.PgTimeout.HasValue)                     b.Timeout        = r.PgTimeout.Value;
        if (!string.IsNullOrWhiteSpace(r.PgSslMode)
            && Enum.TryParse<SslMode>(r.PgSslMode, ignoreCase: true, out var mode))
        {
            b.SslMode = mode;
        }

        // Npgsql takes file paths for cert/key, not bytes. We materialize
        // PEM blobs to %TEMP%\tle-reporting-certs\ under hash-named files
        // so repeat calls hit the same files (no re-write each call).
        if (r.PgRootCertificate is { Length: > 0 })
            b.RootCertificate = MaterializeCert("root", r.PgRootCertificate);
        if (r.PgSslCertificate is { Length: > 0 })
            b.SslCertificate  = MaterializeCert("cert", r.PgSslCertificate);
        if (r.PgSslKey is { Length: > 0 })
            b.SslKey          = MaterializeCert("key",  r.PgSslKey);

        return b.ConnectionString;
    }

    private static string MaterializeCert(string purpose, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        var dir = Path.Combine(Path.GetTempPath(), "tle-reporting-certs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{purpose}-{hash}.pem");
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return path;
    }
}
