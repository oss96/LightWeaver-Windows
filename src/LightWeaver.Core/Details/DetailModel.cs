using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Playlists;
using LightWeaver.Search;

namespace LightWeaver.Details;

/// <summary>Which right-hand panel a detail page shows: an info panel for a movie, seasons for a
/// series, episodes for a season or an episode (detail sketches).</summary>
public enum DetailLayout { Movie, Series, Season, Episode }

/// <summary>What Play starts with, set in the preview's bottom bar before playback ("preselection
/// mode"). A null <see cref="MaxBitrateMbps"/> is the Settings default and zero plays the original.</summary>
public sealed record PlaybackChoice(string? VersionId, int? AudioIndex, int? SubtitleIndex, TimeSpan Start, int? MaxBitrateMbps, bool ForceTranscode = false)
{
    /// <summary>The same picks playing the original file, so no transcode is forced either.</summary>
    public PlaybackChoice AtOriginalQuality() => this with { MaxBitrateMbps = 0, ForceTranscode = false };
}

/// <summary>One line of the info panel's tracks list: a track, or the subtitles' Off line without an
/// index, and whether Play uses it.</summary>
public sealed record TrackLine(int? Index, string Text, bool IsChosen);

/// <summary>The info panel's tracks list (<see cref="DetailModel.Tracks"/>), one list per track type.</summary>
public sealed record TrackList(IReadOnlyList<TrackLine> Video, IReadOnlyList<TrackLine> Audio, IReadOnlyList<TrackLine> Subtitles);

/// <summary>The detail page's rules, free of UI: layout, the next episode, chips, the default
/// stream picks and Shuffle's queue.</summary>
public static class DetailModel
{
    public static DetailLayout LayoutOf(MediaItem item) => item.Type switch
    {
        BaseItemDto_Type.Series => DetailLayout.Series,
        BaseItemDto_Type.Season => DetailLayout.Season,
        BaseItemDto_Type.Episode => DetailLayout.Episode,
        _ => DetailLayout.Movie,
    };

    /// <summary>The episode Play starts ("Next episode to play in this show"): the first unwatched
    /// playable one after the last watched; with nothing watched, a started one, else the first.</summary>
    public static MediaItem? NextEpisode(IReadOnlyList<MediaItem> episodes)
    {
        var episodesInOrder = episodes.Where(e => e.IsPlayable).ToList();
        if (episodesInOrder.Count == 0) return null;
        var lastPlayed = -1;
        for (var i = 0; i < episodesInOrder.Count; i++)
            if (episodesInOrder[i].Played) lastPlayed = i;
        if (lastPlayed < 0) return episodesInOrder.FirstOrDefault(e => e.ResumePositionTicks > 0) ?? episodesInOrder[0];
        for (var i = lastPlayed + 1; i < episodesInOrder.Count; i++)
            if (!episodesInOrder[i].Played) return episodesInOrder[i];
        return episodesInOrder.FirstOrDefault(e => !e.Played) ?? episodesInOrder[0];
    }

    /// <summary>The episodes a season shows: those with media and the upcoming ones, not the missing.</summary>
    public static IReadOnlyList<MediaItem> SeasonEpisodes(IEnumerable<MediaItem> episodes, DateTimeOffset now)
        => episodes.Where(e => !e.IsVirtual || e.IsUpcoming(now)).ToList();

    /// <summary>The season holding <paramref name="episode"/>, else the first season with an
    /// unwatched episode, else the first.</summary>
    public static MediaItem? SeasonOf(IReadOnlyList<MediaItem> seasons, MediaItem? episode)
        => seasons.FirstOrDefault(s => episode?.SeasonId is { } id && s.Id == id)
            ?? seasons.FirstOrDefault(s => s.UnplayedItemCount is > 0)
            ?? seasons.FirstOrDefault();

    /// <summary>What a virtual item says where a title offers Play: its air date while upcoming,
    /// else that it is missing; empty for an item with media.</summary>
    public static string Availability(MediaItem item, DateTimeOffset now)
        => !item.IsVirtual ? ""
            : !item.IsUpcoming(now) ? L.Text("Detail.Missing")
            : item.PlaybackRelease is { Length: > 0 } airs ? L.Format("Detail.UpcomingOn", airs)
            : L.Text("Detail.Upcoming");

    /// <summary>The info chips: year, age rating, community rating, runtime.</summary>
    public static IReadOnlyList<string> Chips(MediaItem item)
    {
        var chips = new List<string>();
        if (item.ProductionYear is { } year) chips.Add(year.ToString(CultureInfo.InvariantCulture));
        if (item.OfficialRating is { Length: > 0 } rating) chips.Add(rating);
        if (item.CommunityRating is { } stars) chips.Add("★ " + stars.ToString("0.0", CultureInfo.CurrentUICulture));
        if (Runtime(item.RuntimeTicks) is { Length: > 0 } runtime) chips.Add(runtime);
        return chips;
    }

    /// <summary>"1 h 50 min" or "42 min"; empty without a runtime.</summary>
    public static string Runtime(long? ticks)
    {
        if (ticks is not > 0) return "";
        var t = TimeSpan.FromTicks(ticks.Value);
        var minutes = Math.Max(1, (int)Math.Round(t.TotalMinutes));
        return minutes >= 60 ? L.Format("Detail.RuntimeHours", minutes / 60, minutes % 60) : L.Format("Detail.RuntimeMinutes", minutes);
    }

    /// <summary>The format badges of the source Play uses, as the WPF app showed them: the resolution
    /// class (4K, 1080p, 720p, SD), the dynamic range unless SDR, and the default audio track's format.</summary>
    public static IReadOnlyList<string> TechnicalChips(MediaSourceStreams? streams)
    {
        if (streams is null) return [];
        string? Field(string id) => streams.Summary.FirstOrDefault(f => f.Id == id)?.Value;
        var chips = new List<string>();
        if (ResolutionClass(Field("Resolution")) is { } resolution) chips.Add(resolution);
        if (Field("Range") is { Length: > 0 } range && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase)) chips.Add(range);
        if (Field("Audio") is { Length: > 0 } audio) chips.Add(audio);
        return chips;
    }

    /// <summary>"3840x2160" as 4K, 1080p, 720p or SD by its width; null when malformed.</summary>
    private static string? ResolutionClass(string? resolution)
    {
        var x = resolution?.IndexOf('x') ?? -1;
        if (x <= 0 || !int.TryParse(resolution.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var width)) return null;
        return width >= 3400 ? "4K" : width >= 1900 ? "1080p" : width >= 1200 ? "720p" : L.Text("ItemDetailView.Sd.c03901ee");
    }

    /// <summary>Shuffle on a show or a season: its unwatched episodes in random order, or all of
    /// them once every one is watched.</summary>
    public static IReadOnlyList<MediaItem> ShuffleQueue(IEnumerable<MediaItem> episodes, Random random)
    {
        var playable = episodes.Where(e => e.IsPlayable).ToList();
        var unwatched = playable.Where(e => !e.Played).ToList();
        return PlaylistModel.Shuffled(unwatched.Count > 0 ? unwatched : playable, random);
    }

    /// <summary>"S1 · E3"; empty when the numbers are unknown.</summary>
    public static string EpisodeTag(MediaItem episode)
        => episode is { ParentIndexNumber: { } season, IndexNumber: { } number } ? L.Format("Home.Episode", season, number) : "";

    /// <summary>The audio stream Play uses unless the user picks one: the preferred language,
    /// else the source's default, else the first.</summary>
    public static int? DefaultAudio(IReadOnlyList<MediaStreamChoice> audio, string? preferredLanguage)
        => (Preferred(audio, preferredLanguage) ?? audio.FirstOrDefault(a => a.IsDefault) ?? audio.FirstOrDefault())?.Index;

    /// <summary>The subtitle stream Play uses unless the user picks one: the preferred language,
    /// else the source's default, else none.</summary>
    public static int? DefaultSubtitle(IReadOnlyList<MediaStreamChoice> subtitles, string? preferredLanguage)
        => (Preferred(subtitles, preferredLanguage) ?? subtitles.FirstOrDefault(s => s.IsDefault))?.Index;

    /// <summary>The info panel's tracks list: every video, audio and subtitle track of the source Play
    /// uses, in the source's order, marking the ones Play uses: the default video track (else the
    /// first) and the audio and subtitle picks. The subtitles start with Off, as the bar's menu does,
    /// marked when subtitles are off.</summary>
    public static TrackList Tracks(MediaSourceStreams streams, int? audioIndex, int? subtitleIndex)
    {
        var video = (streams.Video.FirstOrDefault(v => v.IsDefault) ?? streams.Video.FirstOrDefault())?.Index;
        var off = new TrackLine(null, L.Text("Detail.Off"), subtitleIndex is null);
        return new TrackList(TrackLines(streams.Video, video), TrackLines(streams.Audio, audioIndex), [off, .. TrackLines(streams.Subtitles, subtitleIndex)]);
    }

    private static IReadOnlyList<TrackLine> TrackLines(IReadOnlyList<MediaStreamChoice> streams, int? chosen)
        => streams.Select(s => new TrackLine(s.Index, s.Display, s.Index == chosen)).ToList();

    /// <summary>Whether the bar shows the version drop-down: by the loaded versions once they are
    /// known (<paramref name="loadedVersions"/>), before that by the count the title's list entry
    /// carried, so a title known to have versions shows it from the start.</summary>
    public static bool ShowsVersions(int? mediaSourceCount, int? loadedVersions)
        => (loadedVersions ?? mediaSourceCount) > 1;

    /// <summary>A version's name on the version drop-down: its label, with its size or bitrate when
    /// another version has the same label.</summary>
    public static string VersionName(MediaVersion version, IReadOnlyList<MediaVersion> versions)
        => versions.Count(v => v.Label == version.Label) > 1 && version.Detail() is { Length: > 0 } detail ? $"{version.Label} · {detail}" : version.Label;

    /// <summary>The start a seek in preselection mode sets, kept inside the runtime.</summary>
    public static TimeSpan ClampStart(TimeSpan start, long? runtimeTicks)
    {
        if (start < TimeSpan.Zero) return TimeSpan.Zero;
        return runtimeTicks is > 0 && start.Ticks > runtimeTicks.Value ? TimeSpan.FromTicks(runtimeTicks.Value) : start;
    }

    /// <summary>The preview's shape when the title's own is unknown.</summary>
    public const double DefaultAspect = 16.0 / 9;

    /// <summary>The preview's shape, fixed when the page opens and kept: the title's own
    /// (<see cref="KnownAspect"/>) for a movie or an episode whose list entry carried it, else
    /// 16:9. A show or a season plays an episode the page has not looked up yet, so it is 16:9.
    /// A video of another shape plays letterboxed inside it.</summary>
    public static double PreviewAspect(DetailLayout layout, MediaItem item)
        => layout is DetailLayout.Series or DetailLayout.Season ? DefaultAspect
            : KnownAspect(item.VideoWidth, item.VideoHeight, item.VideoAspectRatio) ?? DefaultAspect;

    /// <summary>The video's shape on screen from its stored size and Jellyfin's display aspect
    /// ("16:9", "2.40:1"): the exact size ratio when the two agree, else the display aspect
    /// (anamorphic 720x576 16:9 is 16:9). A standard-definition frame without a display aspect is
    /// unknown, since its pixels are rarely square. Null when unknown.</summary>
    public static double? KnownAspect(int? width, int? height, string? displayAspect)
    {
        var stored = width is > 0 && height is > 0 ? (double)width.Value / height.Value : (double?)null;
        if (Ratio(displayAspect) is not { } display)
            return width <= StandardDefinitionWidth && height is 480 or 576 ? null : stored;
        return stored is { } pixels && Math.Abs(display / pixels - 1) <= SquarePixelTolerance ? pixels : display;
    }

    /// <summary>How far Jellyfin's rounded display aspect may be from the size ratio and still
    /// mean square pixels.</summary>
    private const double SquarePixelTolerance = 0.05;

    /// <summary>The widest DVD frame, whose 480 or 576 lines are stretched to 4:3 or 16:9.</summary>
    private const int StandardDefinitionWidth = 720;

    /// <summary>"16:9", "2.40:1" or "1.85" as a number; null when malformed or not positive.</summary>
    private static double? Ratio(string? text)
    {
        if (text is not { Length: > 0 }) return null;
        var parts = text.Split(':');
        if (parts.Length > 2 || !TryPositive(parts[0], out var width)) return null;
        if (parts.Length == 1) return width;
        return TryPositive(parts[1], out var height) ? width / height : null;
    }

    private static bool TryPositive(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value > 0;

    /// <summary>The least height the rows below may leave the preview: room for its title, Play and
    /// a one-line bar. A shorter window scrolls the rows into view instead.</summary>
    public const double PreviewMinHeight = 300;

    /// <summary>The preview's size: the video's shape, as wide as <paramref name="width"/> allows
    /// and no taller than <paramref name="maxHeight"/>, but never under <see cref="PreviewMinHeight"/>
    /// unless the width alone makes it shorter. Never under one DIP.</summary>
    public static (double Width, double Height) PreviewSize(double width, double maxHeight, double aspect)
    {
        var height = Math.Max(1, Math.Min(width / aspect, Math.Max(maxHeight, PreviewMinHeight)));
        return (Math.Min(width, height * aspect), height);
    }

    /// <summary>The page width from which the side panel is <see cref="WideSidePanel"/> wide.</summary>
    public const double WideFrom = 1100;

    /// <summary>The side panel's least width on a page at least <see cref="WideFrom"/> wide.</summary>
    public const double WideSidePanel = 360;

    /// <summary>The side panel's least width on a narrower page.</summary>
    public const double NarrowSidePanel = 300;

    /// <summary>The detail page's frame for a page <paramref name="width"/> x <paramref name="height"/>
    /// DIP: the preview in <paramref name="aspect"/> as large as the room above the rows
    /// (<paramref name="rowsHeight"/> tall) allows (<see cref="PreviewSize"/>), and the side panel
    /// taking the rest of the width. Everything in it is known before the page's lookups answer.</summary>
    public static DetailFrame Frame(double width, double height, double rowsHeight, double aspect, DetailSpacing spacing)
    {
        var side = width >= WideFrom ? WideSidePanel : NarrowSidePanel;
        var cell = Math.Max(1, width - spacing.Horizontal - side - spacing.ColumnGap);
        var room = height - spacing.Vertical - (rowsHeight > 0 ? rowsHeight + spacing.RowGap : 0);
        var (previewWidth, previewHeight) = PreviewSize(cell, room, aspect);
        return new DetailFrame(previewWidth, previewHeight, side + cell - previewWidth);
    }

    /// <summary>The season or episode grid in a panel <paramref name="width"/> DIP wide: as many columns
    /// of <paramref name="card"/>-wide cards as fit <paramref name="gap"/> apart, at least two, with the
    /// cards stretched to share the leftover, like the library grid.</summary>
    public static (int Columns, double CardWidth) ChildGrid(double width, double card, double gap)
    {
        var columns = Math.Max(2, (int)Math.Floor((width + gap) / (card + gap)));
        return (columns, Math.Max(0, (width - (columns - 1) * gap) / columns));
    }

    /// <summary>Where Play starts by default: the resume point of a started title.</summary>
    public static TimeSpan DefaultStart(MediaItem item)
        => item.ResumePositionTicks > 0 && !item.Played ? TimeSpan.FromTicks(item.ResumePositionTicks) : TimeSpan.Zero;

    /// <summary>"More like this →": Advanced Search for the same kind of title in its first genre.</summary>
    public static AdvancedSearchFilters LikeThis(MediaItem item) => new()
    {
        Types = item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.Episode ? [SearchType.Shows] : [SearchType.Movies],
        Genres = [.. item.Genres.Take(1)],
    };

    private static MediaStreamChoice? Preferred(IReadOnlyList<MediaStreamChoice> streams, string? language)
        => language is { Length: > 0 } ? streams.FirstOrDefault(s => Languages.Matches(s.Language, language)) : null;
}
