using LightWeaver.Jellyfin;

namespace LightWeaver.Library;

/// <summary>Shows a profile's cached folder, then refreshes it in server order.</summary>
public static class LibraryLoader
{
    public static Task<FolderCacheEntry<MediaItem>?> ReadCachedAsync(string profileKey, Guid folderId)
        => profileKey.Length == 0 ? Task.FromResult<FolderCacheEntry<MediaItem>?>(null)
            : BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(profileKey, folderId));

    public static async Task LoadAsync(JellyfinService jellyfin, string profileKey, Guid folderId,
        Action<IReadOnlyList<MediaItem>, int, bool, DateTime> progress, CancellationToken ct, bool cachedOnly = false)
    {
        var removals = ItemRemovals.ForProfile(profileKey);
        var key = BrowseFolderCache.Key(profileKey, folderId);
        var cached = await ReadCachedAsync(profileKey, folderId);
        ct.ThrowIfCancellationRequested();
        if (cached is not null)
        {
            var filtered = removals.FilterPage(cached.Items, cached.TotalCount);
            progress(filtered.Items, filtered.TotalCount, cachedOnly || !jellyfin.IsConnected, cached.CachedAtUtc);
        }
        if (cachedOnly || !jellyfin.IsConnected) return;

        var items = new List<MediaItem>();
        var total = int.MaxValue;
        while (items.Count < Math.Min(total, BrowseFolderCache.MaxItems))
        {
            var (page, count) = await jellyfin.GetFolderPageAsync(folderId, items.Count, BrowseFolderCache.PrefetchPageSize, ct);
            ct.ThrowIfCancellationRequested();
            total = count;
            items.AddRange(page);
            if (page.Count == 0) break;
            if (cached is not { Items.Count: > 0 } || items.Count >= cached.Items.Count)
            {
                var filtered = removals.FilterPage(items, total);
                progress(filtered.Items, filtered.TotalCount, false, DateTime.UtcNow);
            }
        }
        var complete = items.Count >= total;
        var current = removals.FilterPage(items, total);
        var at = DateTime.UtcNow;
        progress(current.Items, current.TotalCount, true, at);
        if (profileKey.Length == 0) return;
        var entry = new FolderCacheEntry<MediaItem>(items, total, complete,
            !complete && items.Count >= BrowseFolderCache.MaxItems, at, BrowseFolderCache.CurrentSchema);
        await Task.Run(() => BrowseFolderCache.StoreAsync(key, entry,
            values => values.Where(i => removals.Contains(i.Id)).Select(i => i.Id).ToHashSet()), CancellationToken.None);
    }
}
