namespace LightWeaver.Jellyfin;

/// <summary>Puts the episodes a server knows from metadata alone into a season's episode list.</summary>
public static class SeasonEpisodes
{
    /// <summary><paramref name="episodes"/> in their order, with each virtual item of
    /// <paramref name="seasonChildren"/> not already in it placed after the child that precedes it
    /// there, so the season's own order decides where it goes.</summary>
    public static List<MediaItem> WithVirtual(IReadOnlyList<MediaItem> episodes, IEnumerable<MediaItem> seasonChildren)
    {
        var merged = episodes.ToList();
        var position = 0;
        foreach (var child in seasonChildren)
        {
            var at = merged.FindIndex(e => e.Id == child.Id);
            if (at >= 0) position = at + 1;
            else if (child.IsVirtual) merged.Insert(position++, child);
        }
        return merged;
    }
}
