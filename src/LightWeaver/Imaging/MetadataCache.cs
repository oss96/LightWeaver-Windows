namespace LightWeaver.Imaging;

/// <summary>Persisted browse page. The timestamp belongs to the entry rather than the file so
/// callers can render a valid stale page after an interrupted write or a restored backup.</summary>
public sealed record BrowseCacheEntry<T>(List<T> Items, int TotalCount, DateTime CachedAtUtc)
    where T : class;

/// <summary>
/// Disk-backed JSON cache for server metadata (Phase 7 M19):
/// %LOCALAPPDATA%\LightWeaver\metacache\&lt;sha1(key)&gt;.json with a per-call TTL.
/// The mechanics live in <see cref="DiskJsonCache"/>; this is the process-wide instance
/// for Jellyfin projections. Browse projections may carry UserData, but their callers must
/// render cached values only as stale-while-revalidate data and reconcile local updates with
/// the current server response.
/// </summary>
public static class MetadataCache
{
    private static readonly DiskJsonCache Cache = new("metacache", "MetadataCache");

    /// <inheritdoc cref="DiskJsonCache.GetOrFetchAsync{T}"/>
    public static Task<T?> GetOrFetchAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> fetch)
        where T : class
        => Cache.GetOrFetchAsync(key, ttl, fetch);

    /// <inheritdoc cref="DiskJsonCache.ReadAsync{T}"/>
    public static Task<T?> ReadAsync<T>(string key) where T : class
        => Cache.ReadAsync<T>(key);

    /// <inheritdoc cref="DiskJsonCache.StoreAsync{T}"/>
    public static Task StoreAsync<T>(string key, T data) where T : class
        => Cache.StoreAsync(key, data);

    /// <inheritdoc cref="DiskJsonCache.EvictOlderThan"/>
    public static void EvictOlderThan(TimeSpan maxAge) => Cache.EvictOlderThan(maxAge);

    /// <inheritdoc cref="DiskJsonCache.ScheduleEviction"/>
    public static void ScheduleEviction(TimeSpan maxAge) => Cache.ScheduleEviction(maxAge);

    /// <inheritdoc cref="DiskJsonCache.CurrentSizeBytes"/>
    public static long CurrentSizeBytes() => Cache.CurrentSizeBytes();

    /// <inheritdoc cref="DiskJsonCache.Clear"/>
    public static void Clear() => Cache.Clear();
}
