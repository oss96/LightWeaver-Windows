using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Jellyfin;

/// <summary>Chapter marker from server metadata (ticks = 100 ns).</summary>
public sealed record MediaChapter(string? Name, long StartTicks)
{
    public double StartSeconds => StartTicks / 10_000_000.0;
}

/// <summary>Cast/crew entry of an item (detail view row). <see cref="Id"/> is the person's
/// Jellyfin id — clicking the tile opens the Advanced Search Screen pre-filtered on it (M5).</summary>
public sealed record PersonEntry(Guid Id, string Name, string? Role, string? ImageUrl)
{
    public string RoleDisplay => Role ?? "";
    public override string ToString() => Name;
}

/// <summary>
/// Lightweight view model of a Jellyfin item — views bind to this, never to SDK DTOs.
/// </summary>
public sealed class MediaItem
{
    public required Guid Id { get; init; }
    /// <summary>Server change token for the item; the server changes it whenever its copy of the
    /// metadata changes. The folder cache's incremental refresh compares it to decide which items
    /// need refetching. Null unless the query asked for <c>ItemFields.Etag</c>.</summary>
    public string? Etag { get; init; }
    public required string Name { get; init; }
    /// <summary>Server-side sort key ("Matrix, The"); null unless the query asked for
    /// <c>ItemFields.SortName</c>, and null on the server for items that never got one — in both
    /// cases <see cref="Name"/> is the sort key.</summary>
    public string? SortName { get; init; }
    public BaseItemDto_Type Type { get; init; }
    public Guid? ParentId { get; init; }
    public Guid? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public Guid? SeasonId { get; init; }
    public string? SeasonName { get; init; }
    public int? IndexNumber { get; init; }         // episode number
    public int? ParentIndexNumber { get; init; }   // season number
    public int? ProductionYear { get; init; }
    /// <summary>Release date for a film, first-air date for an episode (2026-08-05). Distinct from
    /// <see cref="ProductionYear"/>, which is all the app carried before and is useless for an
    /// episode — every episode of a series shares one production year.</summary>
    public DateTimeOffset? PremiereDate { get; init; }
    /// <summary>When the item was added to the library — the "Date added" sort key. Null unless
    /// the query asked for <c>ItemFields.DateCreated</c>.</summary>
    public DateTimeOffset? DateCreated { get; init; }
    public long? RuntimeTicks { get; init; }
    public string? Overview { get; init; }
    public float? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public long ResumePositionTicks { get; init; }
    public double? PlayedPercentage { get; init; }
    public bool Played { get; init; }
    public bool IsFavorite { get; init; }
    /// <summary>Cast & crew; populated on single-item fetches (list queries omit People).</summary>
    public IReadOnlyList<PersonEntry> People { get; init; } = [];
    /// <summary>Library collection type ("movies", "tvshows", "boxsets", …); null for
    /// non-library items. Decides which libraries get a Genres entry point.</summary>
    public string? CollectionType { get; init; }
    /// <summary>External provider ids ("Imdb"→"tt1232829", "Tmdb"→"55420", …); populated
    /// on single-item fetches. Empty for list queries. See <see cref="ImdbId"/>/<see cref="TmdbId"/>.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } =
        new Dictionary<string, string>();
    /// <summary>Trailer URL from the server's own remote trailers (YouTube, preferred),
    /// populated on single-item fetches; null when the item has none. Opened in the browser.</summary>
    public string? TrailerUrl { get; init; }
    public string? PrimaryImageTag { get; init; }
    public string? BackdropImageTag { get; init; }
    /// <summary>Populated on single-item fetches (list queries omit chapters).</summary>
    public IReadOnlyList<MediaChapter> Chapters { get; init; } = [];

    /// <summary>Poster/backdrop URLs resolved against the current server (set by JellyfinService).</summary>
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }

    /// <summary>Landscape image for episode cards (the episode still), null otherwise.</summary>
    public string? ThumbUrl { get; init; }

    /// <summary>IMDb id ("tt…") from <see cref="ProviderIds"/>, or null. Case-insensitive key.</summary>
    public string? ImdbId => ProviderId("Imdb");
    /// <summary>TMDB id from <see cref="ProviderIds"/>, or null. Case-insensitive key.</summary>
    public string? TmdbId => ProviderId("Tmdb");

    private string? ProviderId(string key) => ProviderIds
        .FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
        .Value is { Length: > 0 } v ? v : null;

    /// <summary>Copy of this item with ONLY the four user-state fields replaced. The folder
    /// cache's incremental refresh learns fresh user state from a lightweight sweep that carries
    /// no metadata, and this is how that state gets onto the cached item.
    /// <para>The copy is written out by hand because <see cref="MediaItem"/> is a class rather
    /// than a record, and it has to stay one — reference equality is load-bearing elsewhere, and
    /// a <c>with</c> expression would need a record. <b>A new property must be added to this copy
    /// too</b>, or it is silently dropped from every refreshed item.</para></summary>
    public MediaItem WithUserData(bool played, bool isFavorite, long resumePositionTicks,
        double? playedPercentage)
    {
        if (Played == played && IsFavorite == isFavorite &&
            ResumePositionTicks == resumePositionTicks &&
            PlayedPercentage == playedPercentage)
        {
            return this;
        }

        return new()
        {
            Id = Id,
            Etag = Etag,
            Name = Name,
            SortName = SortName,
            Type = Type,
            ParentId = ParentId,
            SeriesId = SeriesId,
            SeriesName = SeriesName,
            SeasonId = SeasonId,
            SeasonName = SeasonName,
            IndexNumber = IndexNumber,
            ParentIndexNumber = ParentIndexNumber,
            ProductionYear = ProductionYear,
            PremiereDate = PremiereDate,
            DateCreated = DateCreated,
            RuntimeTicks = RuntimeTicks,
            Overview = Overview,
            CommunityRating = CommunityRating,
            OfficialRating = OfficialRating,
            Genres = Genres,
            People = People,
            CollectionType = CollectionType,
            ProviderIds = ProviderIds,
            TrailerUrl = TrailerUrl,
            PrimaryImageTag = PrimaryImageTag,
            BackdropImageTag = BackdropImageTag,
            Chapters = Chapters,
            PosterUrl = PosterUrl,
            BackdropUrl = BackdropUrl,
            ThumbUrl = ThumbUrl,
            // The four this copy exists to replace.
            Played = played,
            IsFavorite = isFavorite,
            ResumePositionTicks = resumePositionTicks,
            PlayedPercentage = playedPercentage,
        };
    }

    public bool IsPlayable => Type is BaseItemDto_Type.Movie or BaseItemDto_Type.Episode
        or BaseItemDto_Type.Audio or BaseItemDto_Type.MusicVideo or BaseItemDto_Type.Video;

    /// <summary>Top-level library view (Home "Libraries" rail / nav rail). These render as a
    /// collection-type icon tile rather than a poster. Individual box sets/folders are not
    /// libraries — they carry no <see cref="CollectionType"/>.</summary>
    public bool IsLibrary => Type is BaseItemDto_Type.CollectionFolder or BaseItemDto_Type.UserView
        || CollectionType is not null;

    public bool IsBrowsable => Type is BaseItemDto_Type.CollectionFolder or BaseItemDto_Type.Folder
        or BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.MusicAlbum
        or BaseItemDto_Type.MusicArtist or BaseItemDto_Type.BoxSet or BaseItemDto_Type.UserView
        or BaseItemDto_Type.Playlist;

    /// <summary>"S1E5 · Name" for episodes, plain name otherwise (queue rows).</summary>
    public string QueueDisplay => Type == BaseItemDto_Type.Episode && EpisodeTag.Length > 0
        ? $"{EpisodeTag} · {Name}"
        : Name;

    /// <summary>"S2E5" style tag for episodes, empty otherwise.</summary>
    public string EpisodeTag =>
        Type == BaseItemDto_Type.Episode && ParentIndexNumber is { } s && IndexNumber is { } e
            ? $"S{s}E{e}"
            : "";

    /// <summary>Overlay OSD title: series name for episodes, item name otherwise.</summary>
    public string PlaybackTitle =>
        Type == BaseItemDto_Type.Episode && SeriesName is { Length: > 0 } ? SeriesName : Name;

    /// <summary>Overlay OSD subtitle: "S2E5 · Episode Name" for episodes, year otherwise.</summary>
    public string PlaybackSubtitle => Type == BaseItemDto_Type.Episode
        ? string.Join("  ·  ", new[] { EpisodeTag, Name }.Where(s => s.Length > 0))
        : ProductionYear?.ToString() ?? "";

    /// <summary>Overlay OSD release badge: the release date for a film, the first-air date for an
    /// episode (2026-08-05). Empty when the server has no premiere date — the badge then stays
    /// hidden rather than falling back to <see cref="ProductionYear"/>, which for a film is
    /// already the OSD subtitle and for an episode is the whole series' year.
    /// <para>Long date, not short: this is read once at a glance from across a room, and
    /// "12 March 2024" cannot be misread the way 12/03/2024 can.</para>
    /// <para>Rendered in LOCAL time, and that choice is visible for episodes: the server stores
    /// air dates as an instant, commonly the broadcaster's local midnight in UTC, so values like
    /// <c>2022-09-07T22:00Z</c> are normal and show as 8 September in UTC+2. Displaying the date
    /// of an instant is only meaningful in some timezone and the viewer's is the only one we know,
    /// so local wins — but it does mean the badge can differ by a day from a listing site quoting
    /// the broadcaster's timezone. Films are unaffected: they carry midnight UTC.</para></summary>
    public string PlaybackRelease =>
        PremiereDate?.ToLocalTime().ToString("d MMMM yyyy",
            System.Globalization.CultureInfo.CurrentCulture) ?? "";

    /// <summary>Card sub-line: "Series · S1E5" for episodes, the year otherwise.</summary>
    public string CardSubtitle => Type == BaseItemDto_Type.Episode
        ? string.Join("  ·  ", new[] { SeriesName, EpisodeTag }.Where(s => !string.IsNullOrEmpty(s)))
        : ProductionYear?.ToString() ?? "";

    /// <summary>"31min left" / "1h 12min left" for a resumable item, empty otherwise
    /// (the Continue Watching overlay).</summary>
    public string RemainingDisplay
    {
        get
        {
            if (ResumePositionTicks <= 0 || RuntimeTicks is not { } total || total <= ResumePositionTicks)
                return "";
            var t = TimeSpan.FromTicks(total - ResumePositionTicks);
            // Uppercase eyebrow to match the S2 overlay (WPF has no letter-spacing).
            return (t.TotalHours >= 1 ? $"{(int)t.TotalHours}H {t.Minutes}MIN" : $"{t.Minutes}MIN") + " LEFT";
        }
    }

    /// <summary>List items surface this via UI Automation; also nicer in a debugger.</summary>
    public override string ToString() => Name;

    public string RuntimeDisplay
    {
        get
        {
            if (RuntimeTicks is not { } ticks || ticks <= 0)
                return "";
            var t = TimeSpan.FromTicks(ticks);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}min" : $"{t.Minutes}min";
        }
    }
}
