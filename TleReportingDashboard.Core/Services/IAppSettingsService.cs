namespace TleReportingDashboard.Web.Services;

// Generic key/value store for admin-configurable runtime settings. First
// consumer: WorkerDashboardUrl. Use AppSettingKeys for canonical names so
// callers don't typo strings.
public interface IAppSettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value, string updatedByEmail);
}

// Canonical setting keys. Add a constant here when introducing a new
// admin-configurable setting so reads/writes go through the same string.
public static class AppSettingKeys
{
    public const string WorkerDashboardUrl = "worker_dashboard_url";
}
