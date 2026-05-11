using System.Collections.Concurrent;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public class InMemoryUserPreferenceService : IUserPreferenceService
{
    private static readonly ConcurrentDictionary<string, UserPreference> _store = new();

    public Task<UserPreference> GetPreferencesAsync(string userId)
    {
        var pref = _store.GetOrAdd(userId, _ => new UserPreference
        {
            UserId = userId,
            DefaultPageSize = 100
        });
        return Task.FromResult(pref);
    }

    public Task SavePreferencesAsync(UserPreference preference)
    {
        _store[preference.UserId] = preference;
        return Task.CompletedTask;
    }

    public Task TouchLastMasterDashboardSeenAsync(string userId)
    {
        if (_store.TryGetValue(userId, out var pref))
            pref.LastMasterDashboardSeen = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
