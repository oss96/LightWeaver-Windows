using System.Collections.Concurrent;
using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Library;

/// <summary>A server query's answer as last loaded: the titles read so far, the server's count,
/// and when the server gave the oldest of them.</summary>
public sealed record QueryCacheEntry(List<MediaItem> Items, int TotalCount, DateTime SavedAtUtc)
{
    public int Schema { get; init; } = QueryCache.CurrentSchema;
}

/// <summary>The keys one profile holds in a <see cref="QueryCache"/>, least recently stored first.</summary>
public sealed record QueryCacheIndex(List<string> Keys);

/// <summary>Persisted answers of the server-side queries no library folder holds (the Home
/// sections' See all grids, Advanced Search), per profile and UI language, so their pages open on
/// the last answer and can show it offline. Each cache keeps the <see cref="Capacity"/> queries a
/// profile stored most recently. A title leaves an answer when it, its show or its season is removed.</summary>
public sealed class QueryCache(string scope, int capacity)
{
    public const int CurrentSchema = 1;

    /// <summary>The most titles one answer keeps.</summary>
    public const int MaxItems = 600;

    public static QueryCache Sections { get; } = new("section", 32);

    public static QueryCache Searches { get; } = new("search", 24);

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _written = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);
    private long _stores;

    public int Capacity => capacity;

    public string Key(string profileKey, string query) => $"{scope}:{profileKey}:{L.Language}:{query}:v{CurrentSchema}";

    private string IndexKey(string profileKey) => $"{scope}:{profileKey}:index:v{CurrentSchema}";

    /// <summary>The stored answer without the titles removed as of <paramref name="readAt"/>
    /// (now by default); null on a miss, for an entry of another schema and without a profile.</summary>
    public async Task<QueryCacheEntry?> ReadAsync(string profileKey, string query, long? readAt = null)
    {
        if (profileKey.Length == 0) return null;
        var removals = ItemRemovals.ForProfile(profileKey);
        var mark = readAt ?? removals.Mark();
        var entry = await ReadEntryAsync(Key(profileKey, query)).ConfigureAwait(false);
        return entry is null ? null : Without(entry, id => removals.Contains(id, mark)) ?? entry;
    }

    /// <summary>Stores an answer the server gave at <paramref name="answeredAtUtc"/>, read at
    /// <paramref name="readAt"/>: at most <see cref="MaxItems"/> of it, without the titles removed
    /// since, written off the caller's thread. Of two stores of one query the later call wins,
    /// whichever finishes first. Past <see cref="Capacity"/> the profile's least recently stored
    /// query is forgotten.</summary>
    public Task StoreAsync(string profileKey, string query, IReadOnlyList<MediaItem> items, int totalCount, DateTime answeredAtUtc, long readAt)
    {
        if (profileKey.Length == 0) return Task.CompletedTask;
        var order = Interlocked.Increment(ref _stores);
        var key = Key(profileKey, query);
        var generation = _generations.GetOrAdd(profileKey, 0);
        var snapshot = items.Take(MaxItems).ToList();
        var entry = new QueryCacheEntry(snapshot, Math.Max(totalCount, items.Count), answeredAtUtc);
        return Task.Run(() => WriteAsync(profileKey, key, generation, order, entry, readAt));
    }

    private async Task WriteAsync(string profileKey, string key, long generation, long order, QueryCacheEntry entry, long readAt)
    {
        var removals = ItemRemovals.ForProfile(profileKey);
        bool Removed(Guid id) => removals.Contains(id, readAt);
        entry = Without(entry, Removed) ?? entry;
        using (await MetadataCache.LockAsync(key).ConfigureAwait(false))
        {
            await _indexGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_generations.GetOrAdd(profileKey, 0) != generation) return;
                if (_written.TryGetValue(key, out var newer) && newer > order) return;
                _written[key] = order;
                await MetadataCache.StoreAsync(key, entry).ConfigureAwait(false);
                while (Without(entry, Removed) is { } late)
                {
                    entry = late;
                    await MetadataCache.StoreAsync(key, entry).ConfigureAwait(false);
                }
                await RecordAsync(profileKey, key).ConfigureAwait(false);
            }
            finally { _indexGate.Release(); }
        }
    }

    /// <summary>Drops titles the server removed, and the titles of removed shows and seasons, from
    /// every answer the profile holds, in every language. With <paramref name="recorded"/>,
    /// the caller recorded the removal already; titles restored since are preserved.</summary>
    public async Task RemoveAsync(string profileKey, IReadOnlySet<Guid> ids, bool recorded = false)
    {
        if (profileKey.Length == 0 || ids.Count == 0) return;
        var removals = ItemRemovals.ForProfile(profileKey);
        if (!recorded) removals.Remove(ids);
        var generation = _generations.GetOrAdd(profileKey, 0);
        foreach (var key in await KeysAsync(profileKey).ConfigureAwait(false))
        {
            using var gate = await MetadataCache.LockAsync(key).ConfigureAwait(false);
            await _indexGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_generations.GetOrAdd(profileKey, 0) != generation) return;
                if (await ReadEntryAsync(key).ConfigureAwait(false) is not { } entry) continue;
                while (Without(entry, removals.Contains) is { } kept)
                {
                    entry = kept;
                    await MetadataCache.StoreAsync(key, entry).ConfigureAwait(false);
                }
            }
            finally { _indexGate.Release(); }
        }
    }

    /// <summary>Drops every answer the profile holds.</summary>
    public async Task InvalidateProfileAsync(string profileKey)
    {
        await _indexGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _generations.AddOrUpdate(profileKey, 1, (_, value) => value + 1);
            foreach (var key in await KeysAsync(profileKey).ConfigureAwait(false)) Forget(key);
            MetadataCache.Remove(IndexKey(profileKey));
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>The entry without the titles <paramref name="removed"/> names directly or by their
    /// show or season, its count lowered by as many; null when none of them goes.</summary>
    private static QueryCacheEntry? Without(QueryCacheEntry entry, Func<Guid, bool> removed)
    {
        var kept = entry.Items.Where(item => !removed(item.Id)
            && !(item.SeriesId is { } series && removed(series))
            && !(item.ParentId is { } parent && removed(parent))).ToList();
        return kept.Count == entry.Items.Count ? null
            : entry with { Items = kept, TotalCount = Math.Max(kept.Count, entry.TotalCount - entry.Items.Count + kept.Count) };
    }

    private void Forget(string key)
    {
        MetadataCache.Remove(key);
        _written.TryRemove(key, out _);
    }

    private static async Task<QueryCacheEntry?> ReadEntryAsync(string key)
        => await MetadataCache.ReadAsync<QueryCacheEntry>(key).ConfigureAwait(false) is { Schema: CurrentSchema, Items: not null } entry ? entry : null;

    private async Task<List<string>> KeysAsync(string profileKey)
        => (await MetadataCache.ReadAsync<QueryCacheIndex>(IndexKey(profileKey)).ConfigureAwait(false))?.Keys ?? [];

    private async Task RecordAsync(string profileKey, string key)
    {
        var keys = await KeysAsync(profileKey).ConfigureAwait(false);
        keys.Remove(key);
        keys.Add(key);
        while (keys.Count > capacity)
        {
            Forget(keys[0]);
            keys.RemoveAt(0);
        }
        await MetadataCache.StoreAsync(IndexKey(profileKey), new QueryCacheIndex(keys)).ConfigureAwait(false);
    }
}
