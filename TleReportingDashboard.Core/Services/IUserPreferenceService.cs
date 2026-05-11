using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IUserPreferenceService
{
    Task<UserPreference> GetPreferencesAsync(string userId);
    Task SavePreferencesAsync(UserPreference preference);
    // Bumps the user's last_master_dashboard_seen timestamp to UtcNow.
    // Dedicated UPDATE rather than going through SavePreferencesAsync so
    // a Master-Dashboard mount can't accidentally write back stale values
    // for preferences another tab edited concurrently.
    Task TouchLastMasterDashboardSeenAsync(string userId);
}
