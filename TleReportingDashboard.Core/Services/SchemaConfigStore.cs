using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Configuration;

namespace TleReportingDashboard.Web.Services;

// DB-backed SchemaConfig store, one entry per RPT_company_connections row.
// Reads cache per-connection; writes invalidate the relevant entry and
// raise OnChanged with the connection id so listeners refresh narrowly.
//
// Thread-safety: a single SemaphoreSlim gates the load path for a given
// connection so concurrent first-access requests don't both hit the DB.
public class SchemaConfigStore : ISchemaConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _connStr;
    private readonly ILogger<SchemaConfigStore> _logger;
    private readonly ConfigDbCache _configCache;
    private readonly ConcurrentDictionary<Guid, SchemaConfig> _cache = new();
    private readonly SemaphoreSlim _loadMutex = new(1, 1);

    public SchemaConfigStore(
        IConfiguration configuration,
        ConfigDbCache configCache,
        ILogger<SchemaConfigStore> logger)
    {
        _connStr = configuration.GetConnectionString("ConfigDb")
            ?? throw new InvalidOperationException(
                "ConfigDb connection string is required for SchemaConfigStore.");
        _configCache = configCache;
        _logger = logger;
    }

    public event Action<Guid>? OnChanged;

    // Back-compat: resolve the registry-wide default connection's schema.
    // Callers that know which connection they want should use GetForConnection
    // directly — this shim exists only for legacy call sites that predate
    // multi-connection awareness.
    public SchemaConfig Current
    {
        get
        {
            var fallbackId = ResolveDefaultConnectionId();
            return fallbackId is Guid id ? GetForConnection(id) : new SchemaConfig();
        }
    }

    public SchemaConfig GetForConnection(Guid connectionId)
    {
        if (_cache.TryGetValue(connectionId, out var cached)) return cached;

        _loadMutex.Wait();
        try
        {
            if (_cache.TryGetValue(connectionId, out cached)) return cached;
            var loaded = LoadFromDb(connectionId) ?? new SchemaConfig();
            _cache[connectionId] = loaded;
            return loaded;
        }
        finally
        {
            _loadMutex.Release();
        }
    }

    public async Task SaveAsync(SchemaConfig config, Guid connectionId, string? updatedBy = null)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);

        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        // Upsert keyed by connection_id. The UPDATE path preserves the
        // existing company_id column (vestigial but still populated).
        await using (var merge = new SqlCommand(@"
            MERGE INTO EMPOWER.RPT_schema_config AS t
            USING (SELECT @connectionId AS connection_id, @json AS json, @user AS updated_by) AS s
            ON (t.connection_id = s.connection_id)
            WHEN MATCHED THEN UPDATE SET json = s.json, updated_by = s.updated_by, updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (connection_id, company_id, json, updated_by)
                VALUES (s.connection_id,
                        (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = s.connection_id),
                        s.json, s.updated_by);
        ", conn, tx))
        {
            merge.Parameters.Add(new SqlParameter("@connectionId", connectionId));
            merge.Parameters.Add(new SqlParameter("@json", json));
            merge.Parameters.Add(new SqlParameter("@user", (object?)updatedBy ?? DBNull.Value));
            await merge.ExecuteNonQueryAsync();
        }

        // Audit history row.
        await using (var hist = new SqlCommand(@"
            INSERT INTO EMPOWER.RPT_schema_config_history (connection_id, company_id, json, updated_by)
            VALUES (@connectionId,
                    (SELECT company_id FROM EMPOWER.RPT_company_connections WHERE id = @connectionId),
                    @json, @user);
        ", conn, tx))
        {
            hist.Parameters.Add(new SqlParameter("@connectionId", connectionId));
            hist.Parameters.Add(new SqlParameter("@json", json));
            hist.Parameters.Add(new SqlParameter("@user", (object?)updatedBy ?? DBNull.Value));
            await hist.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        _cache[connectionId] = config;
        // Drop every SchemaService-derived entry so the next read from
        // every other circuit gets the saved version, not the pre-edit
        // copy. Editor-mode bypass would have evicted the editing user's
        // own keys already; this catches everyone else.
        _configCache.Invalidate("SchemaService:");
        _logger.LogInformation("Schema config saved for connection {ConnectionId} by {User} ({Fields} fields, {Joins} joins)",
            connectionId, updatedBy ?? "unknown", config.Fields.Count, config.Joins.Count);
        OnChanged?.Invoke(connectionId);
    }

    public async Task CloneAsync(Guid sourceConnectionId, Guid targetConnectionId, bool overwrite, string? updatedBy = null)
    {
        if (sourceConnectionId == targetConnectionId)
            throw new ArgumentException("Source and target connections must differ.");

        if (!overwrite && await HasSchemaAsync(targetConnectionId))
            throw new InvalidOperationException(
                "Target connection already has a schema. Pass overwrite=true to replace it.");

        var source = GetForConnection(sourceConnectionId);
        // Don't reference the same SchemaConfig instance on both connections
        // — a later edit to the target would silently mutate the source's cache.
        // Round-trip through JSON to make a deep copy.
        var copyJson = JsonSerializer.Serialize(source, JsonOpts);
        var copy = JsonSerializer.Deserialize<SchemaConfig>(copyJson, JsonOpts) ?? new SchemaConfig();

        await SaveAsync(copy, targetConnectionId, updatedBy ?? $"clone-from:{sourceConnectionId:N}");
    }

    public async Task<bool> HasSchemaAsync(Guid connectionId)
    {
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM EMPOWER.RPT_schema_config WHERE connection_id = @c", conn);
        cmd.Parameters.Add(new SqlParameter("@c", connectionId));
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }

    public void Invalidate(Guid connectionId)
    {
        _cache.TryRemove(connectionId, out _);
        _configCache.Invalidate("SchemaService:");
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private SchemaConfig? LoadFromDb(Guid connectionId)
    {
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT json FROM EMPOWER.RPT_schema_config WHERE connection_id = @c", conn);
            cmd.Parameters.Add(new SqlParameter("@c", connectionId));
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var parsed = JsonSerializer.Deserialize<SchemaConfig>(raw, JsonOpts);
            if (parsed is null) return null;

            SeedDomainsIfEmpty(parsed);
            _logger.LogInformation(
                "Schema config loaded from DB for connection {ConnectionId} ({Fields} fields, {Joins} joins, {Domains} domains)",
                connectionId, parsed.Fields.Count, parsed.Joins.Count, parsed.Domains.Count);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load schema config from DB for connection {ConnectionId}.", connectionId);
            return null;
        }
    }

    // Resolve the registry-wide default connection id. Used only to back
    // the deprecated Current shim; real call sites pass a connection id
    // explicitly. Deterministic tiebreak (ORDER BY company_id, id) so the
    // same row wins every time when multiple companies each have a default.
    private Guid? ResolveDefaultConnectionId()
    {
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1 id FROM EMPOWER.RPT_company_connections
                WHERE is_active = 1 AND is_default = 1
                ORDER BY company_id, id", conn);
            var result = cmd.ExecuteScalar();
            return result is Guid g ? g : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve registry-wide default connection.");
            return null;
        }
    }

    private static void SeedDomainsIfEmpty(SchemaConfig config)
    {
        if (config.Domains is { Count: > 0 }) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in config.Fields)
        {
            if (!string.IsNullOrWhiteSpace(f.Domain) && seen.Add(f.Domain))
                config.Domains.Add(f.Domain);
        }
        config.Domains.Sort(StringComparer.OrdinalIgnoreCase);
    }
}
