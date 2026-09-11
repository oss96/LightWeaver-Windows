using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Immutable filter contract for the Advanced Search Screen (Phase 7 M4). This is the single
/// source of the query shape: M5 (person) and M6 (genre) construct one and push the view —
/// they never re-specify the filter model. Defaults reproduce a broad, name-sorted
/// movie + series + episode search across the whole server.
/// </summary>
public sealed record AdvancedSearchQuery
{
    /// <summary>Broad default type set (movies, series, episodes) — used when the caller
    /// doesn't scope the search to specific types.</summary>
    public static readonly IReadOnlyList<BaseItemKind> DefaultTypes =
        [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];

    /// <summary>Free-text term (server SearchTerm). Null/empty = no text constraint.</summary>
    public string? Term { get; init; }

    /// <summary>Item kinds to include. Empty is treated as "all default types".</summary>
    public IReadOnlyList<BaseItemKind> ItemTypes { get; init; } = DefaultTypes;

    /// <summary>Genre names AND'd together (server Genres). Empty = any genre.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>People the item must feature (server PersonIds). Empty = any. Seeded by M5.</summary>
    public IReadOnlyList<Guid> PersonIds { get; init; } = [];

    /// <summary>Display name for the seeded person — purely presentational (the ASS
    /// redesign's removable context chip needs a label). Never sent to the server.</summary>
    public string? PersonName { get; init; }

    /// <summary>Inclusive premiere-year lower bound; null = unbounded.</summary>
    public int? MinYear { get; init; }

    /// <summary>Inclusive premiere-year upper bound; null = unbounded.</summary>
    public int? MaxYear { get; init; }

    /// <summary>Watched state filter (IsPlayed / IsUnplayed); null = all.</summary>
    public ItemFilter? Watched { get; init; }

    public ItemSortBy Sort { get; init; } = ItemSortBy.SortName;

    public SortOrder Order { get; init; } = SortOrder.Ascending;
}
