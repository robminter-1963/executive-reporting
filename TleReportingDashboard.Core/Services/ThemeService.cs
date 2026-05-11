using System.Collections.Concurrent;
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
    // Per-scope cache. Key = company id (Guid.Empty == global). First read
    // populates; SaveAsync invalidates only the affected scope. Concurrent
    // dictionary so the singleton service stays thread-safe across
    // simultaneous Master Dashboard mounts in different circuits.
    private static readonly Guid GlobalKey = Guid.Empty;
    private readonly ConcurrentDictionary<Guid, AppTheme> _cache = new();

    public ThemeService(IConfiguration configuration, ILogger<ThemeService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required for ThemeService.");
        _logger = logger;
    }

    public async Task<AppTheme> GetAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        var cacheKey = companyId ?? GlobalKey;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        AppTheme? loaded = null;
        try
        {
            // Per-company first; fall back to the global (id = 1, company_id IS NULL)
            // row; fall back to AppTheme.Default. The global row is the existing
            // seeded one — env-without-companies setups keep behaving as before.
            if (companyId is Guid cid)
            {
                loaded = await LoadByCompanyAsync(cid, ct);
            }
            loaded ??= await LoadGlobalAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            _logger.LogDebug(ex, "RPT_app_theme missing — using default theme.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Theme load failed — falling back to defaults.");
        }

        loaded ??= AppTheme.Default();
        _cache[cacheKey] = loaded;
        return loaded;
    }

    private async Task<AppTheme?> LoadByCompanyAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // CASE WHEN COL_LENGTH guard so pre-migration DBs (no company_id
        // column) don't error — we just return null and the caller
        // falls back to the global row.
        await using var cmd = new SqlCommand(@"
            IF COL_LENGTH('EMPOWER.RPT_app_theme', 'company_id') IS NULL
                SELECT NULL;
            ELSE
                SELECT json FROM EMPOWER.RPT_app_theme WHERE company_id = @CompanyId;", conn);
        cmd.Parameters.Add(new SqlParameter("@CompanyId", companyId));
        var raw = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonSerializer.Deserialize<AppTheme>(raw, JsonOpts);
    }

    private async Task<AppTheme?> LoadGlobalAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT json FROM EMPOWER.RPT_app_theme WHERE id = 1;", conn);
        var raw = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonSerializer.Deserialize<AppTheme>(raw, JsonOpts);
    }

    public async Task SaveAsync(AppTheme theme, Guid? companyId, string? updatedBy, CancellationToken ct = default)
    {
        if (theme is null) throw new ArgumentNullException(nameof(theme));

        var json = JsonSerializer.Serialize(theme, JsonOpts);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        if (companyId is Guid cid)
        {
            // MERGE on company_id. The unique filtered index in the
            // migration enforces "at most one per-company row," so an
            // existing row gets updated and a brand-new company gets a
            // freshly-allocated id (MAX+1). Doing the id allocation inside
            // the same statement keeps it atomic w.r.t. concurrent writes.
            await using var cmd = new SqlCommand(@"
                MERGE INTO EMPOWER.RPT_app_theme AS t
                USING (SELECT @CompanyId AS company_id, @Json AS json, @User AS updated_by) AS s
                ON (t.company_id = s.company_id)
                WHEN MATCHED THEN
                    UPDATE SET json = s.json, updated_by = s.updated_by, updated_at = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (id, company_id, json, updated_by)
                    VALUES (
                        ISNULL((SELECT MAX(id) + 1 FROM EMPOWER.RPT_app_theme), 1),
                        s.company_id, s.json, s.updated_by);", conn);
            cmd.Parameters.Add(new SqlParameter("@CompanyId", cid));
            cmd.Parameters.Add(new SqlParameter("@Json", json));
            cmd.Parameters.Add(new SqlParameter("@User", (object?)updatedBy ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
            _cache[cid] = theme;
        }
        else
        {
            // Global update — preserves the legacy id=1 row that pre-migration
            // environments seeded.
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
            _cache[GlobalKey] = theme;
        }

        _logger.LogInformation("Theme saved by {User} (scope: {Scope}).",
            updatedBy ?? "unknown", companyId?.ToString() ?? "global");
    }
}

// Mock for the in-memory dev mode (no ConfigDb). Always returns the
// default seed; SaveAsync is in-process only — a process restart drops
// any tweaks. Fine for dev; the DB-backed service is what runs in real
// environments.
public sealed class InMemoryThemeService : IThemeService
{
    private static readonly Guid GlobalKey = Guid.Empty;
    private readonly ConcurrentDictionary<Guid, AppTheme> _store = new();

    public Task<AppTheme> GetAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        var key = companyId ?? GlobalKey;
        // Per-company first; fall back to global; fall back to default.
        if (_store.TryGetValue(key, out var t)) return Task.FromResult(t);
        if (key != GlobalKey && _store.TryGetValue(GlobalKey, out var g)) return Task.FromResult(g);
        return Task.FromResult(AppTheme.Default());
    }

    public Task SaveAsync(AppTheme theme, Guid? companyId, string? updatedBy, CancellationToken ct = default)
    {
        _store[companyId ?? GlobalKey] = theme;
        return Task.CompletedTask;
    }
}
