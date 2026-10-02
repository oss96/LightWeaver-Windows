using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Imaging;

namespace LightWeaver.Jellyfin;

/// <summary>One whole folder held in the server's canonical order (SortName ascending, unfiltered)
/// so sort, filter and letter can be applied locally. <paramref name="Complete"/> says the
/// prefetch reached the end of the folder; <paramref name="Truncated"/> says it stopped at
/// <see cref="BrowseFolderCache.MaxItems"/> instead, in which case <paramref name="TotalCount"/>
/// is still the server's count and is larger than <paramref name="Items"/>.
/// <para><paramref name="Schema"/> is checked on read: an entry written by an older shape is a
/// miss, not a crash.</para></summary>
public sealed record FolderCacheEntry<T>(List<T> Items, int TotalCount, bool Complete,
    bool Truncated, DateTime CachedAtUtc, int Schema);

/// <summary>The folder ids one profile has a cached entry for. The entries themselves are stored
/// under SHA-1 of their key, so the directory cannot be searched by profile — and a LibraryChanged
/// that names no folder at all has to be able to drop every one of them.</summary>
public sealed record FolderCacheIndex(List<Guid> Folders);

/// <summary>
/// The one-entry-per-folder browse cache: disk shape, local query evaluation, and the diff/merge
/// the incremental refresh runs. It replaces the per-query <see cref="BrowseCacheEntry{T}"/>,
/// whose key carried the sort, filter, genre and letter — so every control change was a cache
/// miss and a round trip.
/// <para>Everything here is pure or a thin call onto <see cref="MetadataCache"/>. Nothing logs:
/// the view owns the decision ("served from cache", "refetched 12"), and it is the only layer
/// that knows which decision was taken.</para>
/// </summary>
public static class BrowseFolderCache
{
    /// <summary>Bumped whenever <see cref="FolderCacheEntry{T}"/> or the meaning of a stored
    /// field changes. Read rejects anything else.</summary>
    public const int CurrentSchema = 2;

    /// <summary>Ceiling on a cached folder. Past this the entry is marked <c>Truncated</c> and
    /// the view falls back to server-side paging. Seeded from <see cref="Settings.AppSettings.FolderCacheMaxItems"/>.</summary>
    public static int MaxItems { get; set; } = 5000;

    /// <summary>Page size of the background prefetch that fills a folder entry.</summary>
    public const int PrefetchPageSize = 500;

    /// <summary>Page size of the identity sweep that opens an incremental refresh.</summary>
    public const int SweepPageSize = 500;

    /// <summary>How many ids one refetch request carries.</summary>
    public const int RefetchBatchSize = 100;

    /// <summary>Cache key for a folder under a profile. The folder id and the schema are in the
    /// key, the query is not — that is the whole point of this cache.</summary>
    public static string Key(string profileKey, Guid folderId)
        => $"browse:{profileKey}:folder:{folderId:N}:v2";

    /// <summary>Reads a folder entry, or null on a miss. A null entry, an entry with no item list
    /// and an entry from another schema are all misses — a foreign file on disk must not throw on
    /// a path that runs during the first render of a browse view.
    /// <para>A HIT is recorded in the profile index too. Two reasons, and the second is the one
    /// that makes it necessary: every entry written before the index existed is invisible to
    /// <see cref="InvalidateProfileAsync"/> until its folder is next STORED, which on an upgraded
    /// install is exactly what does not happen to the folders somebody opens every day; and a
    /// folder that is only ever read keeps the index's own age moving with it.</para></summary>
    public static async Task<FolderCacheEntry<MediaItem>?> ReadAsync(string key)
    {
        var entry = await MetadataCache.ReadAsync<FolderCacheEntry<MediaItem>>(key).ConfigureAwait(false);
        if (entry is null || entry.Items is null || entry.Schema != CurrentSchema)
            return null;
        if (TryParseKey(key, out var profileKey, out var folderId))
            await RecordAsync(profileKey, folderId).ConfigureAwait(false);
        return entry;
    }

    /// <summary>Writes a folder entry best-effort (the underlying cache swallows write errors) and
    /// records the folder in its profile's index, which is what <see cref="InvalidateProfileAsync"/>
    /// later enumerates.</summary>
    public static async Task StoreAsync(string key, FolderCacheEntry<MediaItem> entry)
    {
        await MetadataCache.StoreAsync(key, entry).ConfigureAwait(false);
        if (TryParseKey(key, out var profileKey, out var folderId))
            await RecordAsync(profileKey, folderId).ConfigureAwait(false);
    }

    /// <summary>Drops one folder's entry, so the next visit refetches instead of rendering items
    /// the server no longer has. The profile index is left alone: an id in it whose file is gone
    /// costs one no-op delete on the next blanket invalidation, while pruning it would need the
    /// index rewritten on every single-folder change.</summary>
    public static void Invalidate(string profileKey, Guid folderId)
        => MetadataCache.Remove(Key(profileKey, folderId));

    /// <summary>Drops every folder entry this profile has. For the LibraryChanged batches that
    /// report items added or removed but name no folder — correct beats cheap there, because the
    /// deleted item is in an entry nothing else will ever invalidate.</summary>
    public static async Task InvalidateProfileAsync(string profileKey)
    {
        var index = await MetadataCache.ReadAsync<FolderCacheIndex>(IndexKey(profileKey))
            .ConfigureAwait(false);
        foreach (var folderId in index?.Folders ?? [])
            MetadataCache.Remove(Key(profileKey, folderId));
        MetadataCache.Remove(IndexKey(profileKey));
    }

    /// <summary>Cache key for a profile's folder index. Its own <c>v2</c> because it is keyed off
    /// the same <see cref="CurrentSchema"/> the entries are.</summary>
    private static string IndexKey(string profileKey) => $"browse:{profileKey}:folder-index:v2";

    /// <summary>One writer at a time across the process. The index is a read-modify-write over a
    /// single file and two folders finishing their prefetch together would otherwise each write
    /// back a list missing the other's id — leaving an entry no invalidation can find.</summary>
    private static readonly SemaphoreSlim IndexGate = new(1, 1);

    private static async Task RecordAsync(string profileKey, Guid folderId)
    {
        await IndexGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var key = IndexKey(profileKey);
            var index = await MetadataCache.ReadAsync<FolderCacheIndex>(key).ConfigureAwait(false);
            var folders = index?.Folders ?? [];
            if (folders.Contains(folderId))
            {
                // The list is already right, but its file's AGE still has to move. The index sits
                // in the same directory as the entries and the 30-day sweep judges every file by
                // its own mtime — so an index only ever rewritten for a NEW folder id outlives
                // nothing and expires while every folder it lists is still being visited. See
                // DiskJsonCache.Touch.
                MetadataCache.Touch(key);
                return;
            }
            folders.Add(folderId);
            await MetadataCache.StoreAsync(key, new FolderCacheIndex(folders)).ConfigureAwait(false);
        }
        finally
        {
            IndexGate.Release();
        }
    }

    /// <summary>Splits a key made by <see cref="Key"/> back into its parts. Parsed rather than
    /// threaded through <see cref="StoreAsync"/> as extra parameters: every caller already holds
    /// the key and nothing else, and the index must not depend on each of them remembering to
    /// pass the pieces. Read from the right, because a profile key carries the server URL and so
    /// contains colons of its own.</summary>
    private static bool TryParseKey(string key, out string profileKey, out Guid folderId)
    {
        profileKey = "";
        folderId = Guid.Empty;
        const string prefix = "browse:";
        const string marker = ":folder:";
        const string suffix = ":v2";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !key.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var body = key[prefix.Length..^suffix.Length];
        var split = body.LastIndexOf(marker, StringComparison.Ordinal);
        if (split <= 0 || !Guid.TryParseExact(body[(split + marker.Length)..], "N", out folderId))
            return false;
        profileKey = body[..split];
        return true;
    }

    /// <summary>Applies the view's sort/filter/genre/letter to a cached folder, in place of the
    /// server round trip that used to answer every control change. Filters first, then sorts.
    /// <para>The letter test is an exact prefix on the sort key, matching the letter rail's
    /// semantics; a null letter and "#" both mean every letter. Sorts are stable through an
    /// <see cref="MediaItem.Id"/> tiebreak, and nulls sort first ascending, which is what the
    /// server's own SQLite ordering gives. <paramref name="randomSeed"/> keeps the Random sort
    /// stable across re-evaluations of the same visit.</para>
    /// Pure: no I/O, no logging, no mutation of <paramref name="all"/>.</summary>
    public static List<MediaItem> ApplyQuery(IReadOnlyList<MediaItem> all, ItemSortBy sortBy,
        SortOrder order, ItemFilter? filter, string? genre, string? letter, int randomSeed)
    {
        IEnumerable<MediaItem> query = all;

        // Only the two watched-state filters the library view offers are honoured. Every other
        // ItemFilter the SDK defines (IsResumable, Likes, Dislikes, IsFolder, ...) needs state a
        // cached item does not carry, so an unsupported filter filters nothing rather than
        // filtering wrongly — a short list that quietly omits matches is the worse failure.
        if (filter == ItemFilter.IsPlayed)
            query = query.Where(i => i.Played);
        else if (filter == ItemFilter.IsUnplayed)
            query = query.Where(i => !i.Played);

        if (genre is { Length: > 0 })
            query = query.Where(i => i.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));

        if (letter is { Length: > 0 } && letter != "#")
            query = query.Where(i =>
                (i.SortName ?? i.Name).StartsWith(letter, StringComparison.OrdinalIgnoreCase));

        if (sortBy == ItemSortBy.Random)
        {
            // Fisher-Yates off the seed. Order is ignored: a reversed shuffle is still a shuffle.
            var shuffled = query.ToList();
            var rng = new Random(randomSeed);
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            return shuffled;
        }

        var sorted = sortBy switch
        {
            ItemSortBy.DateCreated => query.OrderBy(i => i.DateCreated)
                                           .ThenBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(i => i.Id).ToList(),
            ItemSortBy.PremiereDate => query.OrderBy(i => i.PremiereDate)
                                            .ThenBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                                            .ThenBy(i => i.Id).ToList(),
            ItemSortBy.CommunityRating => query.OrderBy(i => i.CommunityRating)
                                               .ThenBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                                               .ThenBy(i => i.Id).ToList(),
            // SortName, and any value the library view does not offer.
            _ => query.OrderBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(i => i.Id).ToList(),
        };
        if (order == SortOrder.Descending)
            sorted.Reverse();
        return sorted;
    }

    /// <summary>Compares a cached folder against a fresh identity sweep.
    /// <para><c>Removed</c> counts cached ids the sweep no longer lists; <c>Added</c> counts swept
    /// ids the cache has never seen; <c>Changed</c> counts ids whose change token differs, plus
    /// ids whose cached token is null — an entry written before Etag was requested cannot be
    /// compared, so it is treated as stale rather than as current.</para>
    /// <para><c>Items</c> is the PROVISIONAL list: sweep order, cached metadata, fresh user state
    /// from the sweep row. Added ids have nothing cached to show and are left out; they arrive
    /// with the refetch. <c>ToRefetch</c> is added + changed, in sweep order.</para>
    /// Pure: no I/O, no logging.</summary>
    public static (List<MediaItem> Items, int Added, int Removed, int Changed, List<Guid> ToRefetch)
        Diff(IReadOnlyList<MediaItem> cached, IReadOnlyList<SweepRow> sweep)
    {
        var byId = new Dictionary<Guid, MediaItem>(cached.Count);
        foreach (var item in cached)
            byId[item.Id] = item;
        var sweptIds = new HashSet<Guid>(sweep.Count);
        foreach (var row in sweep)
            sweptIds.Add(row.Id);

        var provisional = new List<MediaItem>(sweep.Count);
        var toRefetch = new List<Guid>();
        var seenSwept = new HashSet<Guid>(sweep.Count);
        var added = 0;
        var changed = 0;
        foreach (var row in sweep)
        {
            if (!seenSwept.Add(row.Id))
                continue;

            if (!byId.TryGetValue(row.Id, out var item))
            {
                added++;
                toRefetch.Add(row.Id);
                continue;
            }
            if (item.Etag is null || !string.Equals(item.Etag, row.Etag, StringComparison.Ordinal))
            {
                changed++;
                toRefetch.Add(row.Id);
            }
            provisional.Add(item.WithUserData(row.Played, row.IsFavorite,
                row.ResumePositionTicks, row.PlayedPercentage));
        }

        var removed = 0;
        var seenCached = new HashSet<Guid>(cached.Count);
        foreach (var item in cached)
        {
            if (seenCached.Add(item.Id) && !sweptIds.Contains(item.Id))
                removed++;
        }

        return (provisional, added, removed, changed, toRefetch);
    }

    /// <summary>Folds the refetched items into the provisional list: sweep order, refetched item
    /// where one came back, provisional item otherwise. An id in neither list is dropped.
    /// <para>A refetched item keeps its OWN user state rather than having the sweep row's applied
    /// over it. The two observations can straddle a toggle either way and neither carries a
    /// timestamp that would settle it, so the rule is the simple one: the fuller response wins.
    /// A toggle made inside the app is not at risk either way — the view's own override path
    /// outlives this merge.</para>
    /// Pure: no I/O, no logging.</summary>
    public static List<MediaItem> Merge(IReadOnlyList<SweepRow> sweep,
        IReadOnlyList<MediaItem> provisional, IReadOnlyList<MediaItem> refetched)
    {
        var fresh = new Dictionary<Guid, MediaItem>(refetched.Count);
        foreach (var item in refetched)
            fresh[item.Id] = item;
        var kept = new Dictionary<Guid, MediaItem>(provisional.Count);
        foreach (var item in provisional)
            kept[item.Id] = item;

        var merged = new List<MediaItem>(sweep.Count);
        var seenSwept = new HashSet<Guid>(sweep.Count);
        foreach (var row in sweep)
        {
            if (!seenSwept.Add(row.Id))
                continue;

            if (fresh.TryGetValue(row.Id, out var item) || kept.TryGetValue(row.Id, out item))
                merged.Add(item);
        }
        return merged;
    }
}
