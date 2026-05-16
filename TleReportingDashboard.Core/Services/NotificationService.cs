using Microsoft.Data.SqlClient;

namespace TleReportingDashboard.Web.Services;

public sealed class NotificationService : INotificationService
{
    private readonly string _connStr;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IConfiguration configuration, ILogger<NotificationService> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required for NotificationService.");
        _logger = logger;
    }

    public async Task CreateAsync(
        string userEmail,
        string kind,
        string title,
        string? body = null,
        string? linkUrl = null,
        string? relatedEntityType = null,
        string? relatedEntityId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
            throw new ArgumentException("userEmail is required.", nameof(userEmail));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required.", nameof(title));

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_user_notifications
                (user_email, kind, title, body, link_url, related_entity_type, related_entity_id)
            VALUES (@email, @kind, @title, @body, @link, @relType, @relId);", conn);
        cmd.Parameters.Add(new SqlParameter("@email", userEmail));
        cmd.Parameters.Add(new SqlParameter("@kind", kind));
        cmd.Parameters.Add(new SqlParameter("@title", title));
        cmd.Parameters.Add(new SqlParameter("@body", (object?)body ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@link", (object?)linkUrl ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@relType", (object?)relatedEntityType ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@relId", (object?)relatedEntityId ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Notification created: {Kind} for {Email}", kind, userEmail);
    }

    public async Task<List<NotificationRecord>> GetForUserAsync(
        string userEmail,
        int max = 50,
        bool unreadOnly = false,
        CancellationToken ct = default)
    {
        var rows = new List<NotificationRecord>();
        if (string.IsNullOrWhiteSpace(userEmail)) return rows;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        // Hard cap: 200 even if caller passes higher. Bell dropdown only
        // ever wants ~10; the inbox page uses paging in front of this.
        var clamped = Math.Clamp(max, 1, 200);
        var sql = unreadOnly
            ? @"SELECT TOP (@n) id, user_email, kind, title, body, link_url, related_entity_type, related_entity_id, is_read, created_at
                  FROM EMPOWER.RPT_user_notifications
                 WHERE user_email = @email AND is_read = 0
                 ORDER BY created_at DESC;"
            : @"SELECT TOP (@n) id, user_email, kind, title, body, link_url, related_entity_type, related_entity_id, is_read, created_at
                  FROM EMPOWER.RPT_user_notifications
                 WHERE user_email = @email
                 ORDER BY created_at DESC;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@n", clamped));
        cmd.Parameters.Add(new SqlParameter("@email", userEmail));

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new NotificationRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetBoolean(8),
                    reader.GetDateTime(9)));
            }
        }
        catch (SqlException ex) when (ex.IsObjectMissing()) // table missing
        {
            // Migration hasn't run on this env. Logged at debug; UI degrades
            // to "no notifications" without breaking.
            _logger.LogDebug(ex, "RPT_user_notifications missing — returning empty list.");
        }
        return rows;
    }

    public async Task<int> GetUnreadCountAsync(string userEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return 0;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            SELECT COUNT(*) FROM EMPOWER.RPT_user_notifications
             WHERE user_email = @email AND is_read = 0;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", userEmail));
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is int i ? i : 0;
        }
        catch (SqlException ex) when (ex.IsObjectMissing()) // table missing
        {
            _logger.LogDebug(ex, "RPT_user_notifications missing — returning 0 unread.");
            return 0;
        }
    }

    public async Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_user_notifications
               SET is_read = 1
             WHERE id = @id AND is_read = 0;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkAllReadAsync(string userEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return;

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
            UPDATE EMPOWER.RPT_user_notifications
               SET is_read = 1
             WHERE user_email = @email AND is_read = 0;", conn);
        cmd.Parameters.Add(new SqlParameter("@email", userEmail));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// Mock for in-memory dev mode (when ConfigDb isn't configured).
public sealed class InMemoryNotificationService : INotificationService
{
    private readonly object _gate = new();
    private readonly List<NotificationRecord> _store = new();

    public Task CreateAsync(string userEmail, string kind, string title, string? body = null,
        string? linkUrl = null, string? relatedEntityType = null, string? relatedEntityId = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _store.Add(new NotificationRecord(
                Guid.NewGuid(), userEmail, kind, title, body, linkUrl,
                relatedEntityType, relatedEntityId, false, DateTime.UtcNow));
        }
        return Task.CompletedTask;
    }

    public Task<List<NotificationRecord>> GetForUserAsync(string userEmail, int max = 50,
        bool unreadOnly = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = _store.Where(n => string.Equals(n.UserEmail, userEmail, StringComparison.OrdinalIgnoreCase));
            if (unreadOnly) q = q.Where(n => !n.IsRead);
            return Task.FromResult(q.OrderByDescending(n => n.CreatedAt).Take(max).ToList());
        }
    }

    public Task<int> GetUnreadCountAsync(string userEmail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_store.Count(n =>
                string.Equals(n.UserEmail, userEmail, StringComparison.OrdinalIgnoreCase) && !n.IsRead));
        }
    }

    public Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var idx = _store.FindIndex(n => n.Id == id);
            if (idx >= 0 && !_store[idx].IsRead)
                _store[idx] = _store[idx] with { IsRead = true };
        }
        return Task.CompletedTask;
    }

    public Task MarkAllReadAsync(string userEmail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            for (var i = 0; i < _store.Count; i++)
            {
                if (string.Equals(_store[i].UserEmail, userEmail, StringComparison.OrdinalIgnoreCase) && !_store[i].IsRead)
                    _store[i] = _store[i] with { IsRead = true };
            }
        }
        return Task.CompletedTask;
    }
}
