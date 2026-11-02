namespace LightWeaver.Jellyfin;

public static class PlayableItems
{
    /// <summary>Filters a queue while retaining the selected occurrence, or the next surviving entry.</summary>
    public static (IReadOnlyList<MediaItem> Items, int Index) Select(IReadOnlyList<MediaItem> items, int index, Predicate<MediaItem>? include = null)
    {
        index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        var kept = new List<MediaItem>();
        var before = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].IsPlayable || include?.Invoke(items[i]) == false) continue;
            if (i < index) before++;
            kept.Add(items[i]);
        }
        return (kept, kept.Count == 0 ? -1 : Math.Min(before, kept.Count - 1));
    }
}
