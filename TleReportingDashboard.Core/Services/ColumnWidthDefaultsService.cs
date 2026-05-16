using System.Text.Json;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public sealed class ColumnWidthDefaultsService : IColumnWidthDefaultsService
{
    private readonly IAppSettingsService _appSettings;
    private ColumnWidthDefaults? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ColumnWidthDefaultsService(IAppSettingsService appSettings)
    {
        _appSettings = appSettings;
    }

    public async Task<ColumnWidthDefaults> GetAsync(CancellationToken ct = default)
    {
        // Double-checked pattern so a quiescent steady state hits the
        // fast path with zero locking. The lock serializes the cold-start
        // load so a burst of concurrent renders only deserializes once.
        if (_cached is not null) return _cached;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null) return _cached;
            _cached = await LoadFreshAsync();
            return _cached;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(ColumnWidthDefaults defaults, string updatedByEmail, CancellationToken ct = default)
    {
        // Sync alias pairs so resolve()'s single-key lookup works even
        // when a schema uses the alias. Mutates the supplied instance;
        // callers don't keep a separate canonical copy.
        foreach (var (canonical, alias) in ColumnWidthDefaults.Aliases)
        {
            if (defaults.Types.TryGetValue(canonical, out var entry))
                defaults.Types[alias] = entry;
        }

        var json = JsonSerializer.Serialize(defaults);
        await _appSettings.SetAsync(AppSettingKeys.ColumnWidthDefaults, json, updatedByEmail);
        _cached = defaults;
    }

    public async Task ResetToSeedAsync(string updatedByEmail, CancellationToken ct = default)
    {
        // Clear the persisted value so future reads hit the in-code seed.
        // We don't write the seed JSON back because doing so would freeze
        // the values at this moment — a future code change to Seed()
        // would silently NOT take effect. NULL means "use code defaults."
        await _appSettings.SetAsync(AppSettingKeys.ColumnWidthDefaults, null, updatedByEmail);
        _cached = ColumnWidthDefaults.Seed();
    }

    private async Task<ColumnWidthDefaults> LoadFreshAsync()
    {
        var json = await _appSettings.GetAsync(AppSettingKeys.ColumnWidthDefaults);
        if (string.IsNullOrWhiteSpace(json)) return ColumnWidthDefaults.Seed();

        try
        {
            var parsed = JsonSerializer.Deserialize<ColumnWidthDefaults>(json);
            if (parsed is null || parsed.Types is null || parsed.Types.Count == 0)
                return ColumnWidthDefaults.Seed();
            // Re-key the dictionary as case-insensitive — System.Text.Json
            // restores it as ordinal-sensitive by default, which would
            // fight the case-insensitive resolve path. Cheap rebuild;
            // dictionary is bounded by EditableTypes.Count.
            parsed.Types = new Dictionary<string, ColumnWidthEntry>(parsed.Types, StringComparer.OrdinalIgnoreCase);
            return parsed;
        }
        catch (JsonException)
        {
            // Bad / partial JSON in storage shouldn't crash the renderers —
            // fall back to seed and let the admin re-save from the UI.
            return ColumnWidthDefaults.Seed();
        }
    }
}
