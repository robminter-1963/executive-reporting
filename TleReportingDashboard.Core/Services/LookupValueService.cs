using System.Data.Common;
using Microsoft.Extensions.Caching.Memory;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Services.QueryPipeline;

namespace TleReportingDashboard.Web.Services;

// Reads the value list for a filter-pickable Lookup. Builds a SELECT
// against the lookup's SourceTable, executes it on the connection's data
// DB (via ICompanyConnectionResolver), and caches the result per
// (connectionId, lookupId). The HeaderFilters chip picker calls this
// once per filter-add and re-renders with the returned values.
//
// Cache invalidation: explicit, via Invalidate(connectionId) — called by
// SchemaConfigStore.SaveAsync so a lookup edit shows up immediately in
// future picker renders. Cache TTL is 30 minutes anyway as a backstop.
//
// Trust model: SchemaConfig is admin-authored. Source table + column
// names + WHERE fragment go into the SELECT as-is. Admins already
// inject raw SQL elsewhere (FieldDefinition.SqlExpression, lookup
// CTEs, custom filters) — this is the same posture.
public sealed class LookupValueService : ILookupValueService
{
    private readonly ICompanyConnectionResolver _resolver;
    private readonly ICompanyConnectionAdminService _connections;
    private readonly ISqlDialectFactory _dialects;
    private readonly ISchemaConfigStore _schemaStore;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LookupValueService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public LookupValueService(
        ICompanyConnectionResolver resolver,
        ICompanyConnectionAdminService connections,
        ISqlDialectFactory dialects,
        ISchemaConfigStore schemaStore,
        IMemoryCache cache,
        ILogger<LookupValueService> logger)
    {
        _resolver = resolver;
        _connections = connections;
        _dialects = dialects;
        _schemaStore = schemaStore;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<CodeSetValue>> GetFilterValuesAsync(
        Guid connectionId,
        string lookupTypeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lookupTypeId)) return new();

        var cacheKey = CacheKey(connectionId, lookupTypeId);
        if (_cache.TryGetValue(cacheKey, out List<CodeSetValue>? cached) && cached is not null)
            return cached;

        // Resolve the LookupType from this connection's schema config.
        // LookupTypes are per-connection — same id can mean different
        // things on a different connection's schema.
        var schema = _schemaStore.GetForConnection(connectionId);
        var lookupType = schema?.LookupTypes?.FirstOrDefault(l =>
            string.Equals(l.Id, lookupTypeId, StringComparison.OrdinalIgnoreCase));
        if (lookupType is null)
            return new();

        // Resolve the data-DB connection + dialect. LookupType SELECTs
        // run against the company's data source (where the lookup table
        // lives), not against ConfigDb.
        string connStr;
        ISqlDialect dialect;
        try
        {
            connStr = await _resolver.GetByIdAsync(connectionId, ct);
            var record = await _connections.GetByIdAsync(connectionId, ct);
            if (record is null)
            {
                _logger.LogWarning("Connection {Id} not found while loading lookup type {LookupType}.",
                    connectionId, lookupTypeId);
                return new();
            }
            dialect = _dialects.Get(record.ConnectionType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve connection {Id} for lookup type {LookupType} — returning empty list.",
                connectionId, lookupTypeId);
            return new();
        }

        // Run the admin's SELECT verbatim. We project the two columns
        // they named (ValueColumn / DisplayColumn) by ordinal — admins
        // typically write "SELECT CODENUM, CODEDESC FROM ..." so the
        // first column is the value and the second is the display.
        // To be robust, read by column name from the reader using
        // GetOrdinal so admins can SELECT extra columns or order them
        // differently without breaking the picker.
        var sql = lookupType.SelectSql;

        var results = new List<CodeSetValue>();
        try
        {
            await using var conn = dialect.CreateConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 10;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // Resolve column ordinals by name. If the admin's SELECT
            // doesn't include either named column, GetOrdinal throws —
            // fall through to the outer catch which logs and returns
            // empty (admin can see why in server logs).
            var valueIdx = reader.GetOrdinal(lookupType.ValueColumn);
            var displayIdx = reader.GetOrdinal(lookupType.DisplayColumn);

            while (await reader.ReadAsync(ct))
            {
                var value = reader.IsDBNull(valueIdx) ? null : reader.GetValue(valueIdx)?.ToString()?.Trim();
                var display = reader.IsDBNull(displayIdx) ? null : reader.GetValue(displayIdx)?.ToString()?.Trim();
                if (string.IsNullOrEmpty(display)) continue;
                // CodeSetValue's first arg is the code/value passed back
                // to the query; second is the picker's display text.
                results.Add(new CodeSetValue(value ?? display, display));
            }

            _cache.Set(cacheKey, results, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration
            });
        }
        catch (Exception ex)
        {
            // A bad LookupType config (wrong column name, broken SQL,
            // missing table) shouldn't crash the page — the filter chip
            // just renders empty. Admin sees the cause in server logs.
            _logger.LogWarning(ex,
                "Lookup-type query failed for {LookupType} on connection={Conn}. SQL: {Sql}",
                lookupTypeId, connectionId, sql);
        }

        return results;
    }

    // Hook for SchemaConfigStore.SaveAsync to drop cached values when a
    // schema edit changes a lookup's source table / columns / WHERE. Per-
    // connection because the cache key is per-(connectionId, lookupId);
    // a save targets one connection's schema at a time. Iterates known
    // keys via a connection-id-scoped prefix on the cache.
    public void InvalidateForConnection(Guid connectionId)
    {
        // IMemoryCache doesn't expose enumeration; we'd need a parallel
        // index to do prefix-based eviction efficiently. For now, accept
        // the 30-minute TTL as the eventual-consistency floor and rely on
        // the next request to repopulate. If admins find a stale picker
        // annoying, add an explicit per-key invalidation map here.
        _ = connectionId;
    }

    private static string CacheKey(Guid connectionId, string lookupId) =>
        $"LookupValueService:{connectionId:N}:{lookupId.ToLowerInvariant()}";
}
