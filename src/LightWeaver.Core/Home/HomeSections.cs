using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Home;

/// <summary>The Home rows that open as full grids (design 03, "See more").</summary>
public enum HomeSection { ContinueWatching, NextUp, RecentlyAdded, Favorites }

/// <summary>The segmented filter in a section's header.</summary>
public enum SectionFilter { All, Movies, Episodes, Shows, InProgress, NotStarted }

/// <summary>The section's sort menu. <see cref="ServerOrder"/> keeps the order the server lists
/// (last played for Continue Watching and Next Up, newest first for Recently Added).</summary>
public enum SectionSort { ServerOrder, Name, DateAdded }

/// <summary>What each Home section shows and how its header filters and sorts it.</summary>
public static class HomeSections
{
    public static bool IsWide(HomeSection section) => section is HomeSection.ContinueWatching or HomeSection.NextUp;

    public static IReadOnlyList<SectionFilter> Filters(HomeSection section) => section switch
    {
        HomeSection.ContinueWatching => [SectionFilter.All, SectionFilter.Movies, SectionFilter.Episodes],
        HomeSection.NextUp => [SectionFilter.All, SectionFilter.InProgress, SectionFilter.NotStarted],
        HomeSection.RecentlyAdded => [SectionFilter.All, SectionFilter.Movies, SectionFilter.Shows],
        _ => [SectionFilter.All, SectionFilter.Movies, SectionFilter.Shows, SectionFilter.Episodes],
    };

    public static IReadOnlyList<SectionSort> Sorts(HomeSection section) => section switch
    {
        HomeSection.RecentlyAdded => [SectionSort.ServerOrder, SectionSort.Name],
        HomeSection.Favorites => [SectionSort.Name, SectionSort.DateAdded],
        _ => [SectionSort.ServerOrder, SectionSort.Name],
    };

    public static SectionSort DefaultSort(HomeSection section) => Sorts(section)[0];

    public static bool Matches(MediaItem item, SectionFilter filter) => filter switch
    {
        SectionFilter.Movies => item.Type == BaseItemDto_Type.Movie,
        SectionFilter.Episodes => item.Type == BaseItemDto_Type.Episode,
        SectionFilter.Shows => item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.Episode,
        SectionFilter.NotStarted => IsFirstEpisode(item),
        SectionFilter.InProgress => !IsFirstEpisode(item),
        _ => true,
    };

    /// <summary>The section's items for the chosen filter and sort.</summary>
    public static IReadOnlyList<MediaItem> Apply(IReadOnlyList<MediaItem> items, SectionFilter filter, SectionSort sort)
    {
        var shown = items.Where(i => Matches(i, filter));
        return sort switch
        {
            SectionSort.Name => [.. shown.OrderBy(Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.ParentIndexNumber).ThenBy(i => i.IndexNumber)],
            SectionSort.DateAdded => [.. shown.OrderByDescending(i => i.DateCreated ?? DateTimeOffset.MinValue)],
            _ => [.. shown],
        };
    }

    /// <summary>The libraries Recently Added lists from: the video libraries (movies, shows, home
    /// videos, mixed or untyped), not music, books, collections, folders or playlists.</summary>
    public static IEnumerable<Guid> LatestLibraries(IEnumerable<MediaItem> libraries)
        => libraries.Where(l => l.CollectionType is null or "movies" or "tvshows" or "homevideos" or "mixed").Select(l => l.Id);

    /// <summary>When a Recently Added entry last got a title: a show grouping new episodes takes
    /// its newest episode's date, anything else the date it was added.</summary>
    public static DateTimeOffset AddedDate(MediaItem item)
        => item.DateLastMediaAdded is { Year: > 1 } last ? last : item.DateCreated ?? DateTimeOffset.MinValue;

    /// <summary>The Home row's Recently Added from each library's newest titles: newest first across
    /// the libraries by <see cref="AddedDate"/>, at most <paramref name="count"/>. See all reads the
    /// paged lists of <see cref="RecentList"/> instead.</summary>
    public static IReadOnlyList<MediaItem> Newest(IEnumerable<MediaItem> latest, int count)
        => [.. latest.OrderByDescending(AddedDate).Take(count)];

    /// <summary>A card's title: the show for an episode or a season, the item otherwise.</summary>
    public static string Title(MediaItem item)
        => item.Type is BaseItemDto_Type.Episode or BaseItemDto_Type.Season && !string.IsNullOrWhiteSpace(item.SeriesName) ? item.SeriesName : item.Name;

    /// <summary>A card's second line in the section (design 03 and the Home sketch).</summary>
    public static string Subtitle(HomeSection section, MediaItem item)
    {
        var episode = item.Type == BaseItemDto_Type.Episode && item.ParentIndexNumber is { } s && item.IndexNumber is { } e ? (s, e) : ((int, int)?)null;
        switch (section)
        {
            case HomeSection.ContinueWatching:
                var left = TimeLeft(item);
                return episode is var (season, number)
                    ? (left.Length > 0 ? L.Format("Home.EpisodeTimeLeft", season, number, left) : L.Format("Home.Episode", season, number))
                    : left.Length > 0 ? left : Year(item);
            case HomeSection.NextUp:
                return episode is var (s2, n2) ? L.Format("Home.NextEpisode", s2, n2, item.Name) : item.Name;
            default:
                return episode is var (s3, n3) ? L.Format("Home.Episode", s3, n3)
                    : item.Type == BaseItemDto_Type.Season && !string.IsNullOrWhiteSpace(item.SeriesName) ? item.Name
                    : Year(item);
        }
    }

    /// <summary>"1 h 5 min left" or "13 min left" for a started item; empty otherwise.</summary>
    public static string TimeLeft(MediaItem item)
    {
        if (item.ResumePositionTicks <= 0 || item.RuntimeTicks is not { } total || total <= item.ResumePositionTicks) return "";
        var t = TimeSpan.FromTicks(total - item.ResumePositionTicks);
        var minutes = Math.Max(1, t.Minutes + (t.Seconds >= 30 ? 1 : 0));
        return t.TotalHours >= 1 ? L.Format("Home.HoursMinutesLeft", (int)t.TotalHours, t.Minutes) : L.Format("Home.MinutesLeft", minutes);
    }

    /// <summary>Episodes left after Next Up's episode, from its series' unplayed count.</summary>
    public static int? EpisodesAfter(MediaItem nextEpisode, IReadOnlyDictionary<Guid, int> unplayedBySeries)
        => nextEpisode.SeriesId is { } id && unplayedBySeries.TryGetValue(id, out var unplayed) && unplayed > 1 ? unplayed - 1 : null;

    private static bool IsFirstEpisode(MediaItem item) => item.ParentIndexNumber is 1 or 0 && item.IndexNumber == 1 && item.ResumePositionTicks == 0;

    private static string Year(MediaItem item) => item.ProductionYear?.ToString(System.Globalization.CultureInfo.CurrentUICulture) ?? "";
}
