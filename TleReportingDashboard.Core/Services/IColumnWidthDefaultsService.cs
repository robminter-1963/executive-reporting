using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Cache + persistence boundary for ColumnWidthDefaults. Sits on top of
// IAppSettingsService (single JSON blob under AppSettingKeys.ColumnWidthDefaults)
// and adds in-memory caching so the renderers can call GetAsync on every
// component init without re-hitting the DB.
//
// Registered as Singleton so the cache is shared across circuits.
// Invalidated on Save by replacing the cached instance with the saved one.
public interface IColumnWidthDefaultsService
{
    // Returns the current defaults. Falls back to ColumnWidthDefaults.Seed()
    // when nothing has been persisted yet. Cached after first read.
    Task<ColumnWidthDefaults> GetAsync(CancellationToken ct = default);

    // Serializes + persists the supplied defaults and updates the cache.
    // Saves both halves of any alias pair (currency↔money, integer↔int)
    // so the dictionary keys stay in sync.
    Task SaveAsync(ColumnWidthDefaults defaults, string updatedByEmail, CancellationToken ct = default);

    // Drops any persisted JSON so the next GetAsync returns the seed.
    // Used by the Admin tab's "Reset to defaults" button.
    Task ResetToSeedAsync(string updatedByEmail, CancellationToken ct = default);
}
