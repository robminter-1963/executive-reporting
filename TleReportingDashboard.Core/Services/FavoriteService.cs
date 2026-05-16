using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public class FavoriteService : IFavoriteService
{
    private readonly string _connectionString;
    private readonly ConfigDbCache _cache;
    private readonly ILogger<FavoriteService> _logger;

    public FavoriteService(
        IConfiguration configuration,
        ConfigDbCache cache,
        ILogger<FavoriteService> logger)
    {
        _connectionString = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException("ConfigDb connection string is required.");
        _cache = cache;
        _logger = logger;
    }

    public Task<List<Guid>> GetReportIdsAsync(string userId) =>
        _cache.GetOrAddAsync(
            ConfigDbCache.Key("FavoriteService", "ByUser", userId),
            async () =>
            {
                var ids = new List<Guid>();
                if (string.IsNullOrWhiteSpace(userId)) return ids;
                try
                {
                    await using var conn = await ConfigDb.OpenAsync(_connectionString);
                    await using var cmd = conn.Cmd(@"
                        SELECT report_id
                          FROM EMPOWER.RPT_user_favorites
                         WHERE user_id = @UserId
                         ORDER BY sort_order, created_at DESC;");
                    cmd.AddParam("@UserId", userId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        ids.Add(reader.GetGuid(0));
                }
                catch (SqlException ex) when (ex.IsObjectMissing())
                {
                    // Migration not applied — return empty so the UI
                    // degrades to "no favorites" without erroring.
                    _logger.LogDebug(ex, "RPT_user_favorites missing — returning empty list.");
                }
                return ids;
            });

    public async Task AddAsync(string userId, Guid reportId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        await using var conn = await ConfigDb.OpenAsync(_connectionString);
        // INSERT IGNORE pattern via NOT EXISTS — composite PK would
        // throw on a re-add otherwise, and the click-to-toggle UX
        // means accidental re-adds aren't unusual (double-click, race).
        await using var cmd = conn.Cmd(@"
            IF NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_user_favorites
                           WHERE user_id = @UserId AND report_id = @ReportId)
            BEGIN
                INSERT INTO EMPOWER.RPT_user_favorites
                    (user_id, report_id, sort_order, created_at)
                VALUES (@UserId, @ReportId, 0, GETDATE());
            END");
        cmd.AddParam("@UserId", userId);
        cmd.AddParam("@ReportId", reportId);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.IsObjectMissing())
        {
            // Migration not applied — silently no-op. The read path
            // returns an empty favorites list in the same situation,
            // so the UI stays consistent (the star click visually flips
            // via optimistic update but the persisted state stays empty
            // until the admin runs 2026-05-09_16-00_user_favorites.sql).
            _logger.LogDebug(ex, "RPT_user_favorites missing — AddAsync no-op until migration runs.");
        }
        _cache.Invalidate(ConfigDbCache.Key("FavoriteService", "ByUser", userId));
    }

    public async Task RemoveAsync(string userId, Guid reportId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        await using var conn = await ConfigDb.OpenAsync(_connectionString);
        await using var cmd = conn.Cmd(
            "DELETE FROM EMPOWER.RPT_user_favorites WHERE user_id = @UserId AND report_id = @ReportId;");
        cmd.AddParam("@UserId", userId);
        cmd.AddParam("@ReportId", reportId);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.IsObjectMissing())
        {
            // Same defensive fallback as AddAsync — no-op on a
            // pre-migration env.
            _logger.LogDebug(ex, "RPT_user_favorites missing — RemoveAsync no-op until migration runs.");
        }
        _cache.Invalidate(ConfigDbCache.Key("FavoriteService", "ByUser", userId));
    }
}

// Dev-without-DB fallback. Per-process, per-user list. Loses state on
// restart — fine for mock mode since the rest of the app does too.
public class InMemoryFavoriteService : IFavoriteService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Guid>> _byUser = new();

    public Task<List<Guid>> GetReportIdsAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Task.FromResult(new List<Guid>());
        var list = _byUser.GetOrAdd(userId, _ => new List<Guid>());
        lock (list) return Task.FromResult(list.ToList());
    }

    public Task AddAsync(string userId, Guid reportId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Task.CompletedTask;
        var list = _byUser.GetOrAdd(userId, _ => new List<Guid>());
        lock (list) { if (!list.Contains(reportId)) list.Add(reportId); }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string userId, Guid reportId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Task.CompletedTask;
        if (_byUser.TryGetValue(userId, out var list))
            lock (list) list.Remove(reportId);
        return Task.CompletedTask;
    }
}
