using System.Collections.Concurrent;
using System.Text.Json;

namespace TleReportingDashboard.Web.Services;

// Dev-mode audit logger used when ConfigDb isn't configured. Keeps an
// in-process bounded ring so the Admin → Audit Log review tab still
// renders something meaningful while exercising the app against mock data.
// Capped at 1,000 rows to keep memory bounded — older rows get dropped.
// Loses everything on app restart — that's fine for dev.
public sealed class InMemoryAuditLogger : IAuditLogger
{
    private readonly ICurrentUserAccessor _currentUser;
    // Process-wide ring so the Admin → Audit Log tab sees actions from
    // every circuit, not just the current one. The InMemoryAuditLogger
    // itself is registered as Scoped (it consumes the Scoped
    // ICurrentUserAccessor) — the static here side-steps the captive-
    // dependency issue that would force the whole thing to Singleton.
    private static readonly ConcurrentQueue<AuditEntry> _entries = new();
    private static long _nextId;
    private const int MaxRows = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public InMemoryAuditLogger(ICurrentUserAccessor currentUser)
    {
        _currentUser = currentUser;
    }

    public Task LogAsync(
        string? actorEmail,
        string action,
        string resourceType,
        string? resourceId,
        string? resourceLabel,
        object? before = null,
        object? after = null,
        string? notes = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        var actor = string.IsNullOrWhiteSpace(actorEmail) ? _currentUser.Email : actorEmail;
        var entry = new AuditEntry(
            Id:             Interlocked.Increment(ref _nextId),
            OccurredAtUtc:  DateTime.UtcNow,
            ActorEmail:     actor,
            ActorUserId:    _currentUser.UserId,
            Action:         action,
            ResourceType:   resourceType,
            ResourceId:     resourceId,
            ResourceLabel:  resourceLabel,
            BeforeJson:     Serialize(before),
            AfterJson:      Serialize(after),
            CorrelationId:  correlationId,
            Notes:          notes);
        _entries.Enqueue(entry);
        // Drop-from-front when over cap. A few extra rows during a race
        // are fine — the cap is approximate, not strict.
        while (_entries.Count > MaxRows && _entries.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public Task<List<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        IEnumerable<AuditEntry> q = _entries.OrderByDescending(e => e.Id);
        if (query.BeforeId is long beforeId) q = q.Where(e => e.Id < beforeId);
        if (query.FromUtc is DateTime from)  q = q.Where(e => e.OccurredAtUtc >= from);
        if (query.ToUtc is DateTime to)      q = q.Where(e => e.OccurredAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.ActorEmail))
            q = q.Where(e => string.Equals(e.ActorEmail, query.ActorEmail, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.ResourceType))
            q = q.Where(e => string.Equals(e.ResourceType, query.ResourceType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.ResourceId))
            q = q.Where(e => string.Equals(e.ResourceId, query.ResourceId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Action))
            q = q.Where(e => string.Equals(e.Action, query.Action, StringComparison.OrdinalIgnoreCase));
        var take = Math.Clamp(query.Take, 1, 1000);
        return Task.FromResult(q.Take(take).ToList());
    }

    public Task<List<string>> GetDistinctActorsAsync(CancellationToken ct = default) =>
        Task.FromResult(_entries
            .Where(e => !string.IsNullOrEmpty(e.ActorEmail))
            .Select(e => e.ActorEmail!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static string? Serialize(object? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Serialize(value, JsonOptions); }
        catch { return value.ToString(); }
    }
}
