using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public class AppSettingsService : IAppSettingsService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IConfiguration configuration,
        ConfigDbCache cache,
        ILogger<AppSettingsService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _logger = logger;
    }

    public Task<string?> GetAsync(string key) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("AppSettingsService", "Key", key),
            async () =>
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT [value] FROM EMPOWER.RPT_app_settings WHERE [key] = @Key", conn);
                cmd.Parameters.Add(new SqlParameter("@Key", key));
                try
                {
                    var result = await cmd.ExecuteScalarAsync();
                    return result is DBNull or null ? null : (string)result;
                }
                catch (SqlException ex) when (ex.Number == 208) // Invalid object name
                {
                    // Migration not applied on this DB yet — return null so
                    // callers fall through to "unset" behavior.
                    _logger.LogDebug(ex, "RPT_app_settings not present yet — returning null for {Key}.", key);
                    return null;
                }
            });

    public async Task SetAsync(string key, string? value, string updatedByEmail)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // MERGE-style upsert in a single statement so concurrent writes
        // can't race a separate INSERT/UPDATE pair into a unique-key
        // collision. Empty/whitespace values store NULL so the read path
        // can short-circuit on null instead of treating "" as configured.
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        await using var cmd = new SqlCommand(@"
            MERGE EMPOWER.RPT_app_settings AS target
            USING (SELECT @Key AS [key]) AS source
            ON target.[key] = source.[key]
            WHEN MATCHED THEN
                UPDATE SET [value] = @Value, updated_at = GETDATE(), updated_by = @UpdatedBy
            WHEN NOT MATCHED THEN
                INSERT ([key], [value], updated_at, updated_by)
                VALUES (@Key, @Value, GETDATE(), @UpdatedBy);", conn);
        cmd.Parameters.Add(new SqlParameter("@Key", key));
        cmd.Parameters.Add(new SqlParameter("@Value", (object?)normalized ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@UpdatedBy", (object?)updatedByEmail ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate("AppSettingsService:");
        _logger.LogInformation("App setting updated: {Key} by {User}", key, updatedByEmail);
    }
}
