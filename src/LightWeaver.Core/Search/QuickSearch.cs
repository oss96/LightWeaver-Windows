using System.Runtime.ExceptionServices;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.People;

namespace LightWeaver.Search;

/// <summary>A result category of the top-bar search (design 04). All is the chip that shows every
/// group; the rest are groups.</summary>
public enum SearchCategory { All, Movies, Shows, Episodes, People, Collections }

/// <summary>One group of results: the first page of items and the server's total.</summary>
public sealed record SearchGroup(SearchCategory Category, IReadOnlyList<MediaItem> Items, int Total);

/// <summary>Live results for one query, grouped in the design's fixed order with empty groups left out.</summary>
public sealed record QuickSearchResult(string Term, IReadOnlyList<SearchGroup> Groups)
{
    /// <summary>Groups whose answer for <see cref="Term"/> has not arrived yet; they still show the
    /// previous query's.</summary>
    public IReadOnlyList<SearchCategory> Pending { get; init; } = [];

    public bool IsComplete => Pending.Count == 0;

    public int Total => Groups.Sum(g => g.Total);

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Nothing found, and nothing still to come: the empty state.</summary>
    public bool ShowsEmptyState => IsEmpty && IsComplete;

    public int CountOf(SearchCategory category) => category == SearchCategory.All ? Total : Groups.FirstOrDefault(g => g.Category == category)?.Total ?? 0;

    /// <summary>The groups a chip shows: all of them, or the chosen one.</summary>
    public IReadOnlyList<SearchGroup> Shown(SearchCategory category)
        => category == SearchCategory.All ? Groups : [.. Groups.Where(g => g.Category == category)];

    /// <summary>What Enter opens: the first result on screen.</summary>
    public MediaItem? First(SearchCategory category) => Shown(category).SelectMany(g => g.Items).FirstOrDefault();
}

/// <summary>The top-bar search's rules (design 04): debounce, categories, grouping and the hand-off
/// to Advanced Search.</summary>
public static class QuickSearch
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    public const int PerGroup = 24;

    /// <summary>Groups flow in this order: Movies, Shows, People, Collections, Episodes.</summary>
    public static readonly IReadOnlyList<SearchCategory> GroupOrder =
        [SearchCategory.Movies, SearchCategory.Shows, SearchCategory.People, SearchCategory.Collections, SearchCategory.Episodes];

    /// <summary>The chips, All first.</summary>
    public static readonly IReadOnlyList<SearchCategory> Chips =
        [SearchCategory.All, SearchCategory.Movies, SearchCategory.Shows, SearchCategory.Episodes, SearchCategory.People, SearchCategory.Collections];

    public static BaseItemKind? KindOf(SearchCategory category) => category switch
    {
        SearchCategory.Movies => BaseItemKind.Movie,
        SearchCategory.Shows => BaseItemKind.Series,
        SearchCategory.Episodes => BaseItemKind.Episode,
        SearchCategory.Collections => BaseItemKind.BoxSet,
        _ => null,
    };

    /// <summary>Orders the parts and drops the empty ones.</summary>
    public static QuickSearchResult Build(string term, IEnumerable<SearchGroup> parts)
        => parts.Aggregate(Start(null, term), Merge) with { Pending = [] };

    /// <summary>A new query: every group waits for its answer and meanwhile keeps the previous
    /// query's, so the page does not empty while typing.</summary>
    public static QuickSearchResult Start(QuickSearchResult? previous, string term)
        => new(term, previous?.Groups ?? []) { Pending = GroupOrder };

    /// <summary>One group's answer: it replaces that group (or removes it when empty) and the
    /// group stops waiting. The rest are untouched.</summary>
    public static QuickSearchResult Merge(QuickSearchResult current, SearchGroup part)
    {
        var groups = current.Groups.Where(g => g.Category != part.Category).ToList();
        if (part.Items.Count > 0) groups.Add(part with { Total = Math.Max(part.Total, part.Items.Count) });
        return current with
        {
            Groups = [.. GroupOrder.SelectMany(c => groups.Where(g => g.Category == c))],
            Pending = [.. current.Pending.Where(c => c != part.Category)],
        };
    }

    /// <summary>The total the results header shows: the result's once every group has answered,
    /// otherwise the one it showed (none for a first query). A partial sum read as final: Episodes,
    /// the last of five requests on a four-connection client, often answers after the rest.</summary>
    public static int? HeaderTotal(QuickSearchResult result, int? shown) => result.IsComplete ? result.Total : shown;

    /// <summary>The cards of one group's row in <paramref name="width"/>: every card that starts
    /// inside it, the last one clipping at the trailing edge, and how many of them are whole.</summary>
    public static (int Shown, int Whole) RowFit(double width, double cardWidth, double gap)
    {
        var step = cardWidth + gap;
        if (width <= 0 || step <= 0) return (0, 0);
        return (Math.Max(1, (int)Math.Ceiling(width / step)), Math.Max(0, (int)Math.Floor((width + gap) / step)));
    }

    /// <summary>Whether a group's row offers "See all": the group has more than the row's
    /// <paramref name="shown"/> cards, or the last of them is cut off (fewer than
    /// <paramref name="shown"/> are <paramref name="whole"/>). Decided again on every resize.</summary>
    public static bool NeedsSeeAll(int total, int shown, int whole) => total > Math.Min(shown, whole);

    /// <summary>"More filters": Advanced Search with the chip as its Type filter.</summary>
    public static AdvancedSearchFilters MoreFilters(SearchCategory category) => category switch
    {
        SearchCategory.Movies => new AdvancedSearchFilters { Types = [SearchType.Movies] },
        SearchCategory.Shows => new AdvancedSearchFilters { Types = [SearchType.Shows] },
        SearchCategory.Episodes => new AdvancedSearchFilters { Types = [SearchType.Episodes] },
        SearchCategory.Collections => new AdvancedSearchFilters { Types = [SearchType.Collections] },
        _ => AdvancedSearchFilters.Default,
    };

    /// <summary>One group's request: an item kind from the library, or people.</summary>
    public static async Task<SearchGroup> FetchAsync(JellyfinService session, SearchCategory category, string term, CancellationToken ct)
    {
        if (KindOf(category) is { } kind)
        {
            var (found, total) = await session.QuickSearchAsync(term, kind, PerGroup, ct).ConfigureAwait(false);
            return new SearchGroup(category, found, total);
        }
        var people = (await session.SearchPeopleAsync(term, PerGroup, ct).ConfigureAwait(false)).Select(PersonCredits.AsItem).ToList();
        return new SearchGroup(category, people, people.Count);
    }

    /// <summary>Starts every group's request at once and hands each answer to
    /// <paramref name="arrived"/> on the caller's context as soon as it lands, so the slowest
    /// request holds up only its own group. A group that fails arrives empty; the first failure is
    /// rethrown once every group has answered. Nothing arrives after cancellation.</summary>
    public static async Task LoadAsync(Func<SearchCategory, CancellationToken, Task<SearchGroup>> fetch, Action<SearchGroup> arrived, CancellationToken ct)
    {
        async Task<(SearchGroup Group, Exception? Failure)> RunAsync(SearchCategory category)
        {
            try
            {
                return (await fetch(category, ct).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return (new SearchGroup(category, [], 0), ex);
            }
        }

        var running = GroupOrder.Select(RunAsync).ToList();
        Exception? failure = null;
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running);
            running.Remove(done);
            ct.ThrowIfCancellationRequested();
            var (group, error) = await done;
            failure ??= error;
            arrived(group);
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }
}
