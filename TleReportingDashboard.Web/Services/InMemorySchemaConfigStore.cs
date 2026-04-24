using System.Collections.Concurrent;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services;

// Per-connection in-memory schema for mock/dev mode (no ConfigDb). Save
// updates the cached copy but nothing persists — mock mode is for quick
// local work only.
public class InMemorySchemaConfigStore : ISchemaConfigStore
{
    private readonly ILogger<InMemorySchemaConfigStore> _logger;
    private readonly ConcurrentDictionary<Guid, SchemaConfig> _byConnection = new();

    public InMemorySchemaConfigStore(ILogger<InMemorySchemaConfigStore> logger)
    {
        _logger = logger;
    }

    public event Action<Guid>? OnChanged;

    // No real "current" in mock mode — return the first cached connection
    // or an empty config if nothing's been saved.
    public SchemaConfig Current =>
        _byConnection.Values.FirstOrDefault() ?? new SchemaConfig();

    public SchemaConfig GetForConnection(Guid connectionId) =>
        _byConnection.GetOrAdd(connectionId, _ => new SchemaConfig());

    public Task SaveAsync(SchemaConfig config, Guid connectionId, string? updatedBy = null)
    {
        _byConnection[connectionId] = config;
        _logger.LogInformation("Schema config updated in-memory for connection {ConnectionId} by {User} ({Fields} fields) — not persisted in mock mode",
            connectionId, updatedBy ?? "unknown", config.Fields.Count);
        OnChanged?.Invoke(connectionId);
        return Task.CompletedTask;
    }

    public async Task CloneAsync(Guid sourceConnectionId, Guid targetConnectionId, bool overwrite, string? updatedBy = null)
    {
        if (sourceConnectionId == targetConnectionId)
            throw new ArgumentException("Source and target connections must differ.");
        if (!overwrite && await HasSchemaAsync(targetConnectionId))
            throw new InvalidOperationException(
                "Target connection already has a schema. Pass overwrite=true to replace it.");

        var source = GetForConnection(sourceConnectionId);
        // Shallow copy is fine in mock mode since we won't persist.
        await SaveAsync(source, targetConnectionId, updatedBy ?? $"clone-from:{sourceConnectionId:N}");
    }

    public Task<bool> HasSchemaAsync(Guid connectionId) =>
        Task.FromResult(_byConnection.ContainsKey(connectionId));

    public void Invalidate(Guid connectionId) => _byConnection.TryRemove(connectionId, out _);
}
