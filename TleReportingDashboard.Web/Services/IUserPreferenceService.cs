using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IUserPreferenceService
{
    Task<UserPreference> GetPreferencesAsync(string userId);
    Task SavePreferencesAsync(UserPreference preference);
}
