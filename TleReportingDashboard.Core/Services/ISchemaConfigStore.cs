using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services;

// Authoritative accessor for SchemaConfig, keyed per connection. Each row
// in RPT_company_connections gets its own schema — admin-curated fields,
// joins, lookups, custom filters. The store caches per-connection.
public interface ISchemaConfigStore
{
    // Back-compat shim: returns the schema for the caller's "current"
    // connection. In TLE-only mode that's the primary connection; post-
    // Phase 2 (ICompanyContext), it's the connection the request targets.
    // New code should prefer GetForConnection explicitly.
    SchemaConfig Current { get; }

    // Returns (and caches) the schema config for a specific connection.
    // If the connection has no schema row yet, returns an empty SchemaConfig
    // — the admin authors it via Schema Builder.
    SchemaConfig GetForConnection(Guid connectionId);

    // Persists a new config for the given connection. Writes to the row,
    // appends a history entry, swaps the in-memory cache, fires OnChanged.
    Task SaveAsync(SchemaConfig config, Guid connectionId, string? updatedBy = null);

    // Copies the source connection's schema into the target. Caller must
    // verify the two connections share a connection_type before calling.
    // Throws InvalidOperationException if target already has a schema and
    // overwrite = false.
    Task CloneAsync(Guid sourceConnectionId, Guid targetConnectionId, bool overwrite, string? updatedBy = null);

    // Returns true when the target connection already has a schema row.
    // Used by the Clone confirmation dialog to decide whether to prompt.
    Task<bool> HasSchemaAsync(Guid connectionId);

    // Drops a connection's cached entry so the next GetForConnection re-reads
    // the DB. Called automatically after SaveAsync/CloneAsync.
    void Invalidate(Guid connectionId);

    // Raised after a successful Save/Clone. Subscribers re-read their
    // relevant connection's schema.
    event Action<Guid>? OnChanged;
}
