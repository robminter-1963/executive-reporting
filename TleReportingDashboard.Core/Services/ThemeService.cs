using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class ThemeService : IThemeService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _connStr;
    private readonly ILogger<ThemeService> _logger;
    // Single shared cache — theme is global, no per-user variation. First
    // read populates; SaveAsync invalidates. Threading is fine without a
    // lock because the worst case is two parallel first-reads each doing
    // a DB hit, which is harmless idempotent work.
    private AppTheme? _cached;

    public ThemeService(IConfiguration configuration, ILogger<ThemeService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required for ThemeService.");
        _logger = logger;
    }

    public async Task<AppTheme> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        try
        {
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT json FROM EMPOWER.RPT_app_theme WHERE id = 1;", conn);
            var raw = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = JsonSerializer.Deserialize<AppTheme>(raw!, JsonOpts);
                if (parsed is not null)
                {
                    _cached = parsed;
                    return _cached;
                }
            }
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Table missing — migration hasn't been applied. Log debug
            // and fall through to the seed defaults.
            _logger.LogDebug(ex, "RPT_app_theme missing — using default theme.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Theme load failed — falling back to defaults.");
        }

        _cached = AppTheme.Default();
        return _cached;
    }

    public async Task SaveAsync(AppTheme theme, string? updatedBy, CancellationToken ct = default)
    {
        if (theme is null) throw new ArgumentNullException(nameof(theme));

        var json = JsonSerializer.Serialize(theme, JsonOpts);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // MERGE so first-time saves on a fresh DB still work (the seed
        // INSERT may not have run if the migration was applied AFTER an
        // earlier rollback dropped the table).
        await using var cmd = new SqlCommand(@"
            MERGE INTO EMPOWER.RPT_app_theme AS t
            USING (SELECT 1 AS id, @json AS json, @user AS updated_by) AS s
            ON (t.id = s.id)
            WHEN MATCHED THEN
                UPDATE SET json = s.json, updated_by = s.updated_by, updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (id, json, updated_by) VALUES (s.id, s.json, s.updated_by);", conn);
        cmd.Parameters.Add(new SqlParameter("@json", json));
        cmd.Parameters.Add(new SqlParameter("@user", (object?)updatedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        _cached = theme;
        _logger.LogInformation("Theme saved by {User}.", updatedBy ?? "unknown");
    }
}

// Mock for the in-memory dev mode (no ConfigDb). Always returns the
// default seed; SaveAsync is in-process only — a process restart drops
// any tweaks. Fine for dev; the DB-backed service is what runs in real
// environments.
public sealed class InMemoryThemeService : IThemeService
{
    private AppTheme _current = AppTheme.Default();

    public Task<AppTheme> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(_current);

    public Task SaveAsync(AppTheme theme, string? updatedBy, CancellationToken ct = default)
    {
        _current = theme;
        return Task.CompletedTask;
    }
}
