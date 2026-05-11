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
                    await using var conn = new SqlConnection(_connectionString);
                    await conn.OpenAsync();
                    await using var cmd = new SqlCommand(@"
                        SELECT report_id
                          FROM EMPOWER.RPT_user_favorites
                         WHERE user_id = @UserId
                         ORDER BY sort_order, created_at DESC;", conn);
                    cmd.Parameters.Add(new SqlParameter("@UserId", userId));
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        ids.Add(reader.GetGuid(0));
                }
                catch (SqlException ex) when (ex.Number == 208) // Invalid object name
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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // INSERT IGNORE pattern via NOT EXISTS — composite PK would
        // throw on a re-add otherwise, and the click-to-toggle UX
        // means accidental re-adds aren't unusual (double-click, race).
        await using var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_user_favorites
                           WHERE user_id = @UserId AND report_id = @ReportId)
            BEGIN
                INSERT INTO EMPOWER.RPT_user_favorites
                    (user_id, report_id, sort_order, created_at)
                VALUES (@UserId, @ReportId, 0, GETDATE());
            END", conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        await cmd.ExecuteNonQueryAsync();
        _cache.Invalidate(ConfigDbCache.Key("FavoriteService", "ByUser", userId));
    }

    public async Task RemoveAsync(string userId, Guid reportId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM EMPOWER.RPT_user_favorites WHERE user_id = @UserId AND report_id = @ReportId;",
            conn);
        cmd.Parameters.Add(new SqlParameter("@UserId", userId));
        cmd.Parameters.Add(new SqlParameter("@ReportId", reportId));
        await cmd.ExecuteNonQueryAsync();
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
