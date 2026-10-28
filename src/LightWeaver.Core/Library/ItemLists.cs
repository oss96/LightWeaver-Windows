using LightWeaver.Jellyfin;

namespace LightWeaver.Library;

/// <summary>Compares two answers for the same list, so a refresh can keep the cards it already
/// shows and touch only the ones that changed.</summary>
public static class ItemLists
{
    /// <summary>The same items in the same order.</summary>
    public static bool SameIds(IReadOnlyList<MediaItem> a, IReadOnlyList<MediaItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id) return false;
        return true;
    }

    /// <summary>The same items in the same order, each with the same card content.</summary>
    public static bool SameShown(IReadOnlyList<MediaItem> a, IReadOnlyList<MediaItem> b)
    {
        if (!SameIds(a, b)) return false;
        for (var i = 0; i < a.Count; i++)
            if (!SameShown(a[i], b[i])) return false;
        return true;
    }

    /// <summary>Whether two copies of an item show the same on a card or a row: titles, lines,
    /// artwork, progress, watched and favourite state, badges.</summary>
    public static bool SameShown(MediaItem a, MediaItem b)
        => ReferenceEquals(a, b) || (a.Id == b.Id
            && a.Name == b.Name
            && a.Type == b.Type
            && a.SeriesName == b.SeriesName
            && a.IndexNumber == b.IndexNumber
            && a.ParentIndexNumber == b.ParentIndexNumber
            && a.ProductionYear == b.ProductionYear
            && a.RuntimeTicks == b.RuntimeTicks
            && a.ResumePositionTicks == b.ResumePositionTicks
            && a.PlayedPercentage == b.PlayedPercentage
            && a.Played == b.Played
            && a.IsFavorite == b.IsFavorite
            && a.UnplayedItemCount == b.UnplayedItemCount
            && a.ChildCount == b.ChildCount
            && a.Overview == b.Overview
            && a.PrimaryImageTag == b.PrimaryImageTag
            && a.BackdropImageTag == b.BackdropImageTag
            && a.SeriesPrimaryImageTag == b.SeriesPrimaryImageTag
            && a.PosterUrl == b.PosterUrl
            && a.ThumbUrl == b.ThumbUrl
            && a.PlaylistItemId == b.PlaylistItemId);
}
