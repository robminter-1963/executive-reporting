using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Scoped state container that holds the current user's preferences in memory
/// for the lifetime of a Blazor circuit. MainLayout and pages read from this;
/// the Preferences page writes to it (and persists via IUserPreferenceService).
/// </summary>
public class UserPreferenceState
{
    public UserPreference Current { get; private set; } = new();

    public event Action? OnChange;

    public void Set(UserPreference preference)
    {
        Current = preference;
        OnChange?.Invoke();
    }

    public void SetDarkMode(bool isDark)
    {
        Current.IsDarkMode = isDark;
        OnChange?.Invoke();
    }
}
