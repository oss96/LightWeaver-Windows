using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;

namespace LightWeaver.Home;

/// <summary>The Home rows and badge counts saved for one profile.</summary>
public sealed record HomeSnapshot(List<MediaItem> Resume, List<MediaItem> NextUp, List<MediaItem> Latest,
    List<MediaItem> Favorites, Dictionary<Guid, int> Unplayed)
{
    public const int CurrentSchema = 1;
    public int Schema { get; init; } = CurrentSchema;
    public DateTime? SavedAtUtc { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public long? ReadAt { get; init; }

    public static string Key(string profileKey) => $"home:{profileKey}:{L.Language}:v{CurrentSchema}";

    private static Remembered? s_last;

    public static HomeSnapshot? Last(string profileKey)
        => s_last is { } last && profileKey.Length > 0 && last.Key == Key(profileKey)
            ? last.Snapshot.Filter(ItemRemovals.ForProfile(profileKey), last.ReadAt) : null;

    public static async Task<HomeSnapshot?> ReadAsync(string profileKey, long? readAt = null)
    {
        if (profileKey.Length == 0) return null;
        var removals = ItemRemovals.ForProfile(profileKey);
        var mark = readAt ?? removals.Mark();
        var key = Key(profileKey);
        using var gate = await MetadataCache.LockAsync(key).ConfigureAwait(false);
        var snapshot = await ReadEntryAsync(key).ConfigureAwait(false);
        if (snapshot is null) return null;
        snapshot = (snapshot with { ReadAt = mark }).Filter(removals, mark);
        s_last = new Remembered(key, snapshot, mark);
        return snapshot;
    }

    private static async Task<HomeSnapshot?> ReadEntryAsync(string key)
    {
        var snapshot = await MetadataCache.ReadAsync<HomeSnapshot>(key).ConfigureAwait(false);
        if (snapshot is not { Schema: CurrentSchema, Resume: not null, NextUp: not null, Latest: not null, Favorites: not null, Unplayed: not null }) return null;
        return snapshot.SavedAtUtc is not null ? snapshot : snapshot with { SavedAtUtc = MetadataCache.WrittenAtUtc(key) };
    }

    public static async Task StoreAsync(string profileKey, HomeSnapshot snapshot, HomeSnapshot? previous, long? readAt = null)
    {
        if (profileKey.Length == 0) return;
        var key = Key(profileKey);
        var removals = ItemRemovals.ForProfile(profileKey);
        var mark = readAt ?? snapshot.ReadAt ?? removals.Mark();
        using var gate = await MetadataCache.LockAsync(key).ConfigureAwait(false);
        snapshot = snapshot.SavedAtUtc is not null ? snapshot : snapshot with { SavedAtUtc = DateTime.UtcNow };
        while (true)
        {
            snapshot = snapshot.Filter(removals, mark);
            await MetadataCache.StoreAsync(key, snapshot).ConfigureAwait(false);
            var filtered = snapshot.Filter(removals, mark);
            if (!ReferenceEquals(filtered, snapshot)) { snapshot = filtered; continue; }
            s_last = new Remembered(key, snapshot, mark);
            return;
        }
    }

    /// <summary>Drops removed titles from every language's cached Home. With
    /// <paramref name="recorded"/> the caller has already recorded the removal, and a title
    /// re-added since stays in the cache.</summary>
    public static async Task RemoveAsync(string profileKey, IReadOnlySet<Guid> ids, bool recorded = false)
    {
        if (profileKey.Length == 0 || ids.Count == 0) return;
        var removals = ItemRemovals.ForProfile(profileKey);
        if (!recorded) removals.Remove(ids);
        foreach (var language in L.Languages)
        {
            var key = $"home:{profileKey}:{language.Code}:v{CurrentSchema}";
            using var gate = await MetadataCache.LockAsync(key).ConfigureAwait(false);
            if (await ReadEntryAsync(key).ConfigureAwait(false) is not { } snapshot) continue;
            var mark = removals.Mark();
            snapshot = snapshot.Filter(removals, mark);
            await MetadataCache.StoreAsync(key, snapshot).ConfigureAwait(false);
            if (s_last?.Key == key) s_last = new Remembered(key, snapshot, mark);
        }
    }

    public HomeSnapshot Without(IReadOnlySet<Guid> ids)
    {
        bool Removed(MediaItem item) => ids.Contains(item.Id) || item.SeriesId is { } series && ids.Contains(series)
            || item.ParentId is { } parent && ids.Contains(parent);
        if (!Resume.Concat(NextUp).Concat(Latest).Concat(Favorites).Any(Removed) && !Unplayed.Keys.Any(ids.Contains)) return this;
        return this with
        {
            Resume = Resume.Where(i => !Removed(i)).ToList(),
            NextUp = NextUp.Where(i => !Removed(i)).ToList(),
            Latest = Latest.Where(i => !Removed(i)).ToList(),
            Favorites = Favorites.Where(i => !Removed(i)).ToList(),
            Unplayed = Unplayed.Where(p => !ids.Contains(p.Key)).ToDictionary(),
        };
    }

    public HomeSnapshot Filter(ItemRemovals removals, long? readAt = null)
    {
        var mark = readAt ?? removals.Mark();
        var rows = Resume.Concat(NextUp).Concat(Latest).Concat(Favorites).ToList();
        var ids = rows.SelectMany(i => new Guid?[] { i.Id, i.SeriesId, i.ParentId }).OfType<Guid>()
            .Concat(Unplayed.Keys).Where(id => removals.Contains(id, mark)).ToHashSet();
        return Without(ids);
    }

    public bool SameAs(HomeSnapshot? other)
        => other is not null
            && ItemLists.SameShown(Resume, other.Resume)
            && ItemLists.SameShown(NextUp, other.NextUp)
            && ItemLists.SameShown(Latest, other.Latest)
            && ItemLists.SameShown(Favorites, other.Favorites)
            && Unplayed.Count == other.Unplayed.Count
            && Unplayed.All(p => other.Unplayed.TryGetValue(p.Key, out var count) && count == p.Value);

    private sealed record Remembered(string Key, HomeSnapshot Snapshot, long ReadAt);
}
