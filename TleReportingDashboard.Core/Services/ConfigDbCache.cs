using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace TleReportingDashboard.Web.Services;

// Single-process cache for ConfigDB (RPT_*) reads. All ConfigDB-backed
// services route their Get/List methods through GetOrAddAsync; their write
// methods call Invalidate after the SQL commit so non-editor callers see
// fresh data on the next tick rather than waiting for TTL.
//
// Editor-mode bypass: each call site decides via the `bypass` argument —
// scoped services that own a per-circuit EditorModeState pass
// `bypass: _editorMode.IsActive`. Singleton services pass `false` since
// they're not page-scoped. Bypass evicts the key first so a stale value
// isn't served to a non-editor caller racing the read.
//
// Scoping: registered as Singleton — the cache itself is shared globally.
// EditorModeState is per-circuit (see its own file) and consulted by each
// caller; that's why bypass is a parameter, not a property here.
//
// Key convention: "<ServiceName>:<Method>[:<arg1>[:<arg2>...]]". Invalidate
// accepts either an exact key or a prefix; passing "<ServiceName>:" is the
// nuke for "I changed something in this service, drop everything it owns."
public sealed class ConfigDbCache
{
    private readonly IMemoryCache _cache;
    // Tracks every key currently in the cache so prefix-purge can enumerate
    // them. IMemoryCache doesn't expose its key set, so we shadow it here.
    // Eviction callback removes entries that drop out via TTL.
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    public ConfigDbCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public async Task<T> GetOrAddAsync<T>(
        string key, Func<Task<T>> factory, bool bypass = false, TimeSpan? ttl = null)
    {
        if (bypass)
        {
            // Bypass: always read fresh from the DB, and evict the key so
            // a stale value isn't served to a non-bypass caller racing
            // against this read.
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            return await factory();
        }

        if (_cache.TryGetValue(key, out object? hit) && hit is T typed)
            return typed;

        var value = await factory();
        var opts = new MemoryCacheEntryOptions { SlidingExpiration = ttl ?? DefaultTtl };
        opts.RegisterPostEvictionCallback((k, _, _, _) =>
            _keys.TryRemove((string)k, out _));
        _cache.Set(key, value!, opts);
        _keys[key] = 0;
        return value;
    }

    // Synchronous variant for callers whose factory is already synchronous
    // (e.g. SchemaConfigStore.GetForConnection). Same semantics — bypass
    // evicts and skips, otherwise hit-or-populate.
    public T GetOrAdd<T>(string key, Func<T> factory, bool bypass = false, TimeSpan? ttl = null)
    {
        if (bypass)
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            return factory();
        }

        if (_cache.TryGetValue(key, out object? hit) && hit is T typed)
            return typed;

        var value = factory();
        var opts = new MemoryCacheEntryOptions { SlidingExpiration = ttl ?? DefaultTtl };
        opts.RegisterPostEvictionCallback((k, _, _, _) =>
            _keys.TryRemove((string)k, out _));
        _cache.Set(key, value!, opts);
        _keys[key] = 0;
        return value;
    }

    // Exact-or-prefix purge. Pass a full key for surgical removal, or a
    // prefix ending in ":" to nuke everything a service owns.
    public void Invalidate(string keyOrPrefix)
    {
        if (string.IsNullOrEmpty(keyOrPrefix)) return;
        foreach (var k in _keys.Keys)
        {
            if (k.Equals(keyOrPrefix, StringComparison.Ordinal)
                || k.StartsWith(keyOrPrefix, StringComparison.Ordinal))
            {
                _cache.Remove(k);
                _keys.TryRemove(k, out _);
            }
        }
    }

    // Helper for building a stable key from a method name + argument list.
    // Nulls render as "_" so "GetById:_" is a legal cache key for
    // null-arg callers (e.g. GetForConnection(null) which means "default").
    public static string Key(string service, string method, params object?[] args)
    {
        if (args.Length == 0) return $"{service}:{method}";
        var parts = args.Select(a => a?.ToString() ?? "_");
        return $"{service}:{method}:{string.Join(":", parts)}";
    }
}
