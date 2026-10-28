using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Search;

/// <summary>Item kinds offered by the Type filter.</summary>
public enum SearchType { Movies, Shows, Episodes, Collections }

/// <summary>The Watched filter's segments.</summary>
public enum WatchedFilter { All, Unplayed, Played }

/// <summary>The Sort pop-up's choices.</summary>
public enum SearchSort { ReleaseDate, Name, DateAdded, Rating, Runtime }

/// <summary>The filters that count as active and show as removable tokens. Sort is not one.</summary>
public enum FilterKind { Types, Genres, People, Years, Watched, Rating }

public sealed record SearchPerson(Guid Id, string Name);

/// <summary>An active filter as shown above the results.</summary>
public sealed record FilterToken(FilterKind Kind, string Label);

/// <summary>The Advanced Search filter panel's state (design 05): Type, Genre, Person, Year
/// range, Watched, Minimum rating and Sort. Immutable; every edit produces a new value, which the
/// model turns into one server query combined with the text typed in the search field.</summary>
public sealed record AdvancedSearchFilters
{
    public static readonly IReadOnlyList<SearchType> DefaultTypes = [SearchType.Movies, SearchType.Shows, SearchType.Episodes];

    /// <summary>The year slider's range. A bound at the edge of the range means "unbounded".</summary>
    public const int YearFloor = 1900;
    public static int YearCeiling => DateTime.Now.Year;

    public const double MaxRating = 10;

    public static AdvancedSearchFilters Default { get; } = new();

    public IReadOnlyList<SearchType> Types { get; init; } = DefaultTypes;
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<SearchPerson> People { get; init; } = [];
    public int? MinYear { get; init; }
    public int? MaxYear { get; init; }
    public WatchedFilter Watched { get; init; }
    /// <summary>0 = any rating.</summary>
    public double MinRating { get; init; }
    /// <summary>Name A–Z by default, as in the WPF app: newest-release-first opens on unaired
    /// "TBA" episodes.</summary>
    public SearchSort Sort { get; init; } = SearchSort.Name;
    public bool Descending { get; init; }

    /// <summary>Opening Advanced Search from a person's page fills in that person and nothing
    /// else (owner, 2026-10-17).</summary>
    public static AdvancedSearchFilters ForPerson(Guid id, string name) => new() { People = [new SearchPerson(id, name)] };

    public bool IsActive(FilterKind kind) => kind switch
    {
        // No type ticked searches the default kinds (see ToQuery), so it is not a filter either.
        FilterKind.Types => Types.Count > 0 && !SameTypes(Types, DefaultTypes),
        FilterKind.Genres => Genres.Count > 0,
        FilterKind.People => People.Count > 0,
        FilterKind.Years => MinYear is not null || MaxYear is not null,
        FilterKind.Watched => Watched != WatchedFilter.All,
        FilterKind.Rating => MinRating > 0,
        _ => false,
    };

    public int ActiveCount => Enum.GetValues<FilterKind>().Count(IsActive);

    /// <summary>True when Reset has nothing to do: every filter and the sort are at their defaults.</summary>
    public bool IsDefault => ActiveCount == 0 && Sort == Default.Sort && Descending == Default.Descending;

    public AdvancedSearchFilters Without(FilterKind kind) => kind switch
    {
        FilterKind.Types => this with { Types = DefaultTypes },
        FilterKind.Genres => this with { Genres = [] },
        FilterKind.People => this with { People = [] },
        FilterKind.Years => this with { MinYear = null, MaxYear = null },
        FilterKind.Watched => this with { Watched = WatchedFilter.All },
        FilterKind.Rating => this with { MinRating = 0 },
        _ => this,
    };

    /// <summary>Sets the year range, treating the slider's ends as unbounded.</summary>
    public AdvancedSearchFilters WithYears(int min, int max)
    {
        (min, max) = (Math.Min(min, max), Math.Max(min, max));
        return this with
        {
            MinYear = min <= YearFloor ? null : min,
            MaxYear = max >= YearCeiling ? null : max,
        };
    }

    public IEnumerable<FilterToken> Tokens()
    {
        foreach (var kind in Enum.GetValues<FilterKind>())
            if (IsActive(kind)) yield return new FilterToken(kind, Label(kind));
    }

    /// <summary>How a filter reads in the empty state's hint ("Removing “Rating ★ 9.0+” …").</summary>
    public string HintLabel(FilterKind kind)
        => kind == FilterKind.Rating ? L.Format("AdvancedSearch.HintRating", Label(kind)) : Label(kind);

    public string Label(FilterKind kind) => kind switch
    {
        FilterKind.Types => string.Join(L.Text("AdvancedSearch.ListSeparator"), Types.Select(TypeName)),
        FilterKind.Genres => string.Join(L.Text("AdvancedSearch.ListSeparator"), Genres),
        FilterKind.People => string.Join(L.Text("AdvancedSearch.ListSeparator"), People.Select(p => p.Name)),
        FilterKind.Years => (MinYear, MaxYear) switch
        {
            ({ } a, { } b) => a == b ? a.ToString(CultureInfo.InvariantCulture) : L.Format("AdvancedSearch.YearsRange", a, b),
            ({ } a, null) => L.Format("AdvancedSearch.YearsFrom", a),
            (null, { } b) => L.Format("AdvancedSearch.YearsUntil", b),
            _ => "",
        },
        FilterKind.Watched => WatchedName(Watched),
        FilterKind.Rating => L.Format("AdvancedSearch.RatingAtLeast", MinRating),
        _ => "",
    };

    public static string TypeName(SearchType type) => type switch
    {
        SearchType.Movies => L.Text("AdvancedSearch.TypeMovies"),
        SearchType.Shows => L.Text("AdvancedSearch.TypeShows"),
        SearchType.Episodes => L.Text("AdvancedSearch.TypeEpisodes"),
        _ => L.Text("AdvancedSearch.TypeCollections"),
    };

    public static string WatchedName(WatchedFilter watched) => watched switch
    {
        WatchedFilter.Unplayed => L.Text("AdvancedSearch.WatchedUnplayed"),
        WatchedFilter.Played => L.Text("AdvancedSearch.WatchedPlayed"),
        _ => L.Text("AdvancedSearch.WatchedAll"),
    };

    public static string SortName(SearchSort sort) => sort switch
    {
        SearchSort.ReleaseDate => L.Text("AdvancedSearch.SortReleaseDate"),
        SearchSort.Name => L.Text("AdvancedSearch.SortName"),
        SearchSort.DateAdded => L.Text("AdvancedSearch.SortDateAdded"),
        SearchSort.Rating => L.Text("AdvancedSearch.SortRating"),
        _ => L.Text("AdvancedSearch.SortRuntime"),
    };

    /// <summary>The server query: these filters plus the typed text.</summary>
    public AdvancedSearchQuery ToQuery(string? term) => new()
    {
        Term = string.IsNullOrWhiteSpace(term) ? null : term.Trim(),
        // An empty type selection searches the default kinds rather than nothing.
        ItemTypes = (Types.Count == 0 ? DefaultTypes : Types).Select(Kind).ToArray(),
        Genres = Genres,
        PersonIds = People.Select(p => p.Id).ToArray(),
        PersonName = People.Count == 1 ? People[0].Name : null,
        MinYear = MinYear,
        MaxYear = MaxYear,
        Watched = Watched switch
        {
            WatchedFilter.Unplayed => ItemFilter.IsUnplayed,
            WatchedFilter.Played => ItemFilter.IsPlayed,
            _ => null,
        },
        MinRating = MinRating > 0 ? MinRating : null,
        Sort = Sort switch
        {
            SearchSort.ReleaseDate => ItemSortBy.PremiereDate,
            SearchSort.Name => ItemSortBy.SortName,
            SearchSort.DateAdded => ItemSortBy.DateCreated,
            SearchSort.Rating => ItemSortBy.CommunityRating,
            _ => ItemSortBy.Runtime,
        },
        Order = Descending ? SortOrder.Descending : SortOrder.Ascending,
    };

    private static BaseItemKind Kind(SearchType type) => type switch
    {
        SearchType.Movies => BaseItemKind.Movie,
        SearchType.Shows => BaseItemKind.Series,
        SearchType.Episodes => BaseItemKind.Episode,
        _ => BaseItemKind.BoxSet,
    };

    private static bool SameTypes(IReadOnlyList<SearchType> a, IReadOnlyList<SearchType> b)
        => a.Count == b.Count && a.All(b.Contains);
}
