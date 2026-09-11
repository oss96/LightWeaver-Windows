namespace LightWeaver.Imaging;

/// <summary>
/// Disk-backed JSON cache for third-party metadata — OMDB critic scores today:
/// %LOCALAPPDATA%\LightWeaver\externalcache\&lt;sha1(key)&gt;.json with a per-call TTL.
/// What lands here is <b>global</b>, keyed by a public id (IMDb) rather than by server,
/// user or profile, so an entry stays valid across accounts and re-logins.
///
/// <para>Deliberately a separate directory from <c>metacache</c> even though the
/// mechanics are identical (<see cref="DiskJsonCache"/>): Settings carries "Media info"
/// and "External metadata (OMDB)" as two rows with their own size readout and their own
/// Clear button, and one directory cannot be measured or emptied as two.</para>
/// </summary>
public static class ExternalMetadataCache
{
    private static readonly DiskJsonCache Cache = new("externalcache", "ExternalMetadataCache");

    /// <inheritdoc cref="DiskJsonCache.GetOrFetchAsync{T}"/>
    public static Task<T?> GetOrFetchAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> fetch)
        where T : class
        => Cache.GetOrFetchAsync(key, ttl, fetch);

    /// <inheritdoc cref="DiskJsonCache.EvictOlderThan"/>
    public static void EvictOlderThan(TimeSpan maxAge) => Cache.EvictOlderThan(maxAge);

    /// <inheritdoc cref="DiskJsonCache.ScheduleEviction"/>
    public static void ScheduleEviction(TimeSpan maxAge) => Cache.ScheduleEviction(maxAge);

    /// <inheritdoc cref="DiskJsonCache.CurrentSizeBytes"/>
    public static long CurrentSizeBytes() => Cache.CurrentSizeBytes();

    /// <inheritdoc cref="DiskJsonCache.Clear"/>
    public static void Clear() => Cache.Clear();
}
