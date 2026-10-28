using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;

namespace LightWeaver.Home;

/// <summary>One server list See all reads Recently Added from: titles of <see cref="Kinds"/> in
/// <see cref="SortBy"/> order, and the date (<see cref="Key"/>) that order runs by, newest first.</summary>
public sealed record RecentList(IReadOnlyList<BaseItemKind> Kinds, ItemSortBy SortBy, SortOrder Order, Func<MediaItem, DateTimeOffset> Key)
{
    /// <summary>Movies and other videos by the date they were added.</summary>
    public static readonly RecentList Movies = new([BaseItemKind.Movie, BaseItemKind.Video], ItemSortBy.DateCreated, SortOrder.Descending,
        i => i.DateCreated ?? DateTimeOffset.MinValue);

    /// <summary>Shows by the date their newest episode was added, so new episodes stand under their
    /// show as in the Home row. A show without one sorts last, as the server sorts it.</summary>
    public static readonly RecentList Shows = new([BaseItemKind.Series], ItemSortBy.DateLastContentAdded, SortOrder.Descending,
        i => i.DateLastMediaAdded is { Year: > 1 } last ? last : DateTimeOffset.MinValue);

    /// <summary>The lists for a See all filter and sort: by date, movies and shows read apart and
    /// merged; by name, one list in the server's name order.</summary>
    public static IReadOnlyList<RecentList> For(SectionFilter filter, SectionSort sort)
    {
        RecentList[] lists = filter switch
        {
            SectionFilter.Movies => [Movies],
            SectionFilter.Shows => [Shows],
            _ => [Movies, Shows],
        };
        if (sort != SectionSort.Name) return lists;
        return [new([.. lists.SelectMany(l => l.Kinds)], ItemSortBy.SortName, SortOrder.Ascending, _ => DateTimeOffset.MinValue)];
    }
}

/// <summary>A title the grid shows at <see cref="Index"/>; the titles from there on move down one.</summary>
public readonly record struct FeedInsert(int Index, MediaItem Item);

/// <summary>Recently Added's See all in pages (owner, 2026-10-22). The first page is each list's
/// first titles, merged newest first by each list's <see cref="RecentList.Key"/>, so the grid fills
/// at once. Meanwhile every list is read once as ids and dates only and merged the same way into a
/// snapshot of the order, and the later pages walk it: a title the snapshot puts above the first
/// page's last title that the first page did not have (added meanwhile, or moved up past it) is
/// inserted where it belongs, the rest are appended, and a title already shown is passed over.
/// Paging by offset into a list the server sorts afresh each time would skip a title that moves
/// ahead of the offset (a show with a new episode) or behind it (a title removed or watched), so the
/// order is fixed once read and a refresh takes a new snapshot. The Home row stays on the server's
/// Latest list, which does not page.</summary>
public sealed class RecentlyAddedFeed
{
    /// <summary>The first <c>count</c> titles of a list in its order, with what a card shows, and
    /// how many titles the list has in all.</summary>
    public delegate Task<(List<MediaItem> Items, int Total)> HeadReader(RecentList list, int count, CancellationToken ct);

    /// <summary>Every title of a list in its order, with no more than its id and dates.</summary>
    public delegate Task<List<MediaItem>> OrderReader(RecentList list, CancellationToken ct);

    /// <summary>The titles of <paramref name="ids"/> with what a card shows, in any order; an id the
    /// server no longer knows is left out.</summary>
    public delegate Task<List<MediaItem>> TitleReader(IReadOnlyList<Guid> ids, CancellationToken ct);

    private readonly IReadOnlyList<RecentList> _lists;
    private readonly HeadReader _readHead;
    private readonly OrderReader _readOrder;
    private readonly TitleReader _readTitles;
    private readonly ItemRemovals _removals;
    private readonly List<MediaItem> _shown = [];
    private readonly HashSet<Guid> _shownIds = [];
    private Task<IReadOnlyList<Guid>>? _orderRead;
    private long _orderReadAt;
    private bool _headRead;
    private int? _headTotal;
    private IReadOnlyList<Guid>? _order;
    private int _boundary = -1;
    private int _next;

    public RecentlyAddedFeed(IReadOnlyList<RecentList> lists, HeadReader readHead, OrderReader readOrder, TitleReader readTitles, ItemRemovals? removals = null)
    {
        _lists = lists;
        _readHead = readHead;
        _readOrder = readOrder;
        _readTitles = readTitles;
        _removals = removals ?? new();
    }

    /// <summary>Every title given so far, in the order the grid shows them.</summary>
    public IReadOnlyList<MediaItem> Shown => _shown;

    /// <summary>The titles in all: the lists' own count once the first page is read, the titles
    /// shown and those the order still has once the order is; null before.</summary>
    public int? Total => _order is null ? _headTotal : _shown.Count + _order.Skip(_next).Count(id => !_shownIds.Contains(id) && !_removals.Contains(id));

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        _removals.Remove(ids);
        var removed = _shown.RemoveAll(item => ids.Contains(item.Id));
        _shownIds.ExceptWith(ids);
        if (_headTotal is { } total) _headTotal = Math.Max(_shown.Count, total - removed);
    }

    /// <summary>Whether a later <see cref="NextAsync"/> can still give titles.</summary>
    public bool HasMore => _order is null || _next < _order.Count;

    /// <summary>Up to <paramref name="count"/> more titles and where each goes, to apply in turn:
    /// fewer at the end, and at least one while any are left, since a batch whose titles were all
    /// removed since the order was read is passed over. The first call starts reading the order and
    /// answers from the lists' first titles without waiting for it. A batch is used up only once
    /// its titles have arrived, so after a failed read the same titles come next.</summary>
    public async Task<IReadOnlyList<FeedInsert>> NextAsync(int count, CancellationToken ct)
    {
        count = Math.Max(1, count);
        if (_orderRead is null)
        {
            _orderReadAt = _removals.Mark();
            _orderRead = ReadOrderAsync(ct);
        }
        if (!_headRead)
        {
            var readAt = _removals.Mark();
            var heads = await Task.WhenAll(_lists.Select(l => _readHead(l, count, ct)));
            ct.ThrowIfCancellationRequested();
            var merged = Merge([.. heads.Select(h => h.Items)]).ToList();
            var first = _removals.Filter(merged, readAt).Take(count).ToList();
            _headRead = true;
            _headTotal = heads.Sum(h => h.Total) - merged.Count(item => _removals.Contains(item.Id, readAt));
            if (first.Count > 0) return Place(first.Select(i => (i, int.MaxValue)));
        }
        var order = _order ?? await ReadSnapshotAsync(ct);
        while (true)
        {
            var end = _next;
            var ids = new List<Guid>(count);
            for (; end < order.Count && ids.Count < count; end++)
                if (!_shownIds.Contains(order[end]) && !_removals.Contains(order[end])) ids.Add(order[end]);
            if (ids.Count == 0)
            {
                _next = end;
                return [];
            }
            var readAt = _removals.Mark();
            var byId = _removals.Filter(await _readTitles(ids, ct), readAt).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            ct.ThrowIfCancellationRequested();
            var from = _next;
            _next = end;
            var found = Enumerable.Range(from, end - from).Where(at => ids.Contains(order[at]) && byId.ContainsKey(order[at]) && !_removals.Contains(order[at])).ToList();
            if (found.Count > 0) return Place(found.Select(at => (byId[order[at]], at)));
        }
    }

    /// <summary>Shows each title above the first page's last title where the order puts it there,
    /// else at the end; <c>At</c> is its place in the order.</summary>
    private List<FeedInsert> Place(IEnumerable<(MediaItem Item, int At)> titles)
    {
        var placed = new List<FeedInsert>();
        foreach (var (item, at) in titles)
        {
            var index = at < _boundary ? IndexOfShownAfter(at) : _shown.Count;
            _shown.Insert(index, item);
            _shownIds.Add(item.Id);
            placed.Add(new FeedInsert(index, item));
        }
        return placed;
    }

    /// <summary>Where on screen the first title shown after place <paramref name="at"/> of the order is.</summary>
    private int IndexOfShownAfter(int at)
    {
        for (var i = at + 1; i <= _boundary; i++)
            if (_shownIds.Contains(_order![i])) return _shown.FindIndex(t => t.Id == _order[i]);
        return _shown.Count;
    }

    /// <summary>The order, once read, and the place in it of the last title of the first page still
    /// in it; a failed read is started again on the next call.</summary>
    private async Task<IReadOnlyList<Guid>> ReadSnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> order;
        try
        {
            order = await _orderRead!;
        }
        catch
        {
            _orderRead = null;
            throw;
        }
        ct.ThrowIfCancellationRequested();
        order = order.Where(id => !_removals.Contains(id, _orderReadAt)).ToList();
        var places = order.Select((id, at) => (id, at)).ToDictionary(p => p.id, p => p.at);
        _boundary = _shown.Select(t => places.TryGetValue(t.Id, out var at) ? at : -1).LastOrDefault(at => at >= 0, -1);
        _order = order;
        return order;
    }

    private async Task<IReadOnlyList<Guid>> ReadOrderAsync(CancellationToken ct)
    {
        var lists = await Task.WhenAll(_lists.Select(l => _readOrder(l, ct)));
        ct.ThrowIfCancellationRequested();
        return [.. Merge(lists).Select(i => i.Id)];
    }

    /// <summary>The lists, one per <see cref="RecentList"/> in order, merged newest first, each kept
    /// in the server's order, every title once.</summary>
    private IEnumerable<MediaItem> Merge(IReadOnlyList<List<MediaItem>> lists)
    {
        var heads = new int[lists.Count];
        var seen = new HashSet<Guid>();
        while (true)
        {
            var newest = -1;
            for (var i = 0; i < lists.Count; i++)
                if (heads[i] < lists[i].Count && (newest < 0 || _lists[i].Key(lists[i][heads[i]]) > _lists[newest].Key(lists[newest][heads[newest]])))
                    newest = i;
            if (newest < 0) yield break;
            var item = lists[newest][heads[newest]++];
            if (seen.Add(item.Id)) yield return item;
        }
    }
}
