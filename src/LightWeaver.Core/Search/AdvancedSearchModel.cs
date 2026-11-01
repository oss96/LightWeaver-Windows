using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.ViewModels;

namespace LightWeaver.Search;

/// <summary>Runs Advanced Search (design 05): every change to the filters or the typed text
/// re-queries the server after <see cref="Debounce"/>. While a request is in flight the old
/// results stay in place (the view dims them; no skeleton swap); a newer change cancels it, and
/// only the newest answer is applied. When a combination matches nothing, the model asks the
/// server which single filter would help most and how many titles removing it would show, and
/// applies the empty result together with that hint. Answers land as a keyed diff on
/// <see cref="Items"/>, so titles that stay keep their cards.
///
/// Call from the UI thread: <see cref="Items"/> is bound to the view, and the model never leaves
/// the caller's synchronization context.</summary>
public sealed class AdvancedSearchModel : ObservableObject
{
    public delegate Task<(List<MediaItem> Items, int TotalCount)> SearchFunc(
        AdvancedSearchQuery query, int startIndex, int limit, CancellationToken cancellationToken);

    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a new query's answer has to stay before it is cached, so the prefixes of
    /// a term typed on do not push earlier searches out.</summary>
    public static readonly TimeSpan RememberAfter = TimeSpan.FromSeconds(2);
    public const int PageSize = 60;

    private readonly SearchFunc _search;
    private readonly TimeProvider _time;
    private readonly UserDataOverrides? _overrides;
    private readonly ItemRemovals _removals;
    private readonly QueryCache? _cache;
    private readonly string _profileKey;
    private AdvancedSearchFilters _filters = AdvancedSearchFilters.Default;
    private string _term = "";
    private int _totalCount;
    private bool _isUpdating;
    private bool _isReloading;
    private bool _hasResults;
    private bool _failed;
    private EmptyHint? _hint;
    private int _generation;
    private int _pages;
    private bool _resyncOwed;
    private bool _resyncing;
    private bool _loadingMore;
    private bool _settled;
    private CancellationTokenSource? _pending;

    /// <summary><paramref name="overrides"/> are the page's watched and favourite changes; each
    /// answer replaces the ones it already carries. With a <paramref name="cache"/>, a query opens
    /// on the profile's last answer to it while the server is asked, and keeps every answer.</summary>
    public AdvancedSearchModel(SearchFunc search, TimeProvider? time = null, UserDataOverrides? overrides = null, ItemRemovals? removals = null,
        QueryCache? cache = null, string profileKey = "")
    {
        _search = search;
        _time = time ?? TimeProvider.System;
        _overrides = overrides;
        _removals = removals ?? new();
        _cache = cache;
        _profileKey = profileKey;
    }

    /// <summary>Raised after an answer to a query landed, whether or not it changed
    /// <see cref="Items"/>: the view re-reads its cards and checks whether to load more. A cached
    /// answer raises it too, before the server's.</summary>
    public event EventHandler? Answered;

    /// <summary>When the server gave the titles shown: at the request for a live answer, earlier for a
    /// cached one; null when a query with no cached answer failed, so what is shown is not its answer.</summary>
    public DateTime? ShownAtUtc { get; private set; }

    /// <summary>A query failed (server unreachable, timeout); <see cref="ShownAtUtc"/> says whether
    /// the titles shown still answer it.</summary>
    public event EventHandler? Failed;

    /// <summary>A quiet resync replaced the loaded titles; paging may continue from them.</summary>
    public event EventHandler? Resynced;

    /// <summary>The filter that would help most when nothing matches, and the result count without it.</summary>
    public sealed record EmptyHint(FilterKind Kind, string Label, int Count);

    public KeyedList<MediaItem, Guid> Items { get; } = new(item => item.Id);

    public AdvancedSearchFilters Filters
    {
        get => _filters;
        set
        {
            if (Equals(_filters, value)) return;
            _filters = value;
            OnPropertyChanged();
            Schedule();
        }
    }

    public string Term
    {
        get => _term;
        set
        {
            value ??= "";
            if (_term == value) return;
            _term = value;
            OnPropertyChanged();
            Schedule();
        }
    }

    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

    /// <summary>A request for the current filters is pending or in flight.</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        private set { if (SetProperty(ref _isUpdating, value)) OnPropertyChanged(nameof(IsEmpty)); }
    }

    /// <summary>The pending request is a <see cref="ReloadAsync"/>: the view shows that it is
    /// updating but keeps the grid as it is.</summary>
    public bool IsReloading { get => _isReloading; private set => SetProperty(ref _isReloading, value); }

    /// <summary>At least one answer has landed (before that, the view shows neither results nor the empty state).</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set { if (SetProperty(ref _hasResults, value)) OnPropertyChanged(nameof(IsEmpty)); }
    }

    /// <summary>The last request failed (server unreachable, timeout). The previous results stay.</summary>
    public bool HasFailed { get => _failed; private set => SetProperty(ref _failed, value); }

    public EmptyHint? Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    public bool IsEmpty => HasResults && TotalCount == 0 && !IsUpdating;

    /// <summary>Queries now, without the debounce (opening the page).</summary>
    public Task RefreshNowAsync()
    {
        var cts = Restart();
        return RunAsync(_generation, PageSize, cts.Token);
    }

    /// <summary>Re-runs the current query now for every title loaded so far (Refresh, F5), so the
    /// grid keeps its length and scroll position and only what changed on the server moves.</summary>
    public Task ReloadAsync()
    {
        var limit = Math.Max(PageSize, (Items.Count + PageSize - 1) / PageSize * PageSize);
        var cts = Restart(reloading: true);
        return RunAsync(_generation, limit, cts.Token);
    }

    /// <summary>Drops deleted titles at once. With more than one page loaded, a page fetched after
    /// the server deleted a title, but before that was reported here, started too late; the loaded
    /// range is then fetched again quietly.</summary>
    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        _removals.Remove(ids);
        var kept = _removals.Filter(Items);
        var removed = Items.Count - kept.Count;
        if (removed == 0) return;
        Items.Sync(kept);
        TotalCount = Math.Max(Items.Count, TotalCount - removed);
        Hint = null;
        if (_pages > 1) Resync();
    }

    /// <summary>Whether titles are left to load: more pages, or a resync that failed.</summary>
    public bool HasMore => Items.Count < TotalCount || _resyncOwed;

    /// <summary>Appends the next page when the view scrolls near the end; a page that a deletion
    /// overlapped is replaced by a resync of the whole loaded range.</summary>
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || _resyncing || IsUpdating || !HasMore) return;
        if (_resyncOwed)
        {
            Resync();
            return;
        }
        _loadingMore = true;
        var generation = _generation;
        var token = _pending?.Token ?? CancellationToken.None;
        try
        {
            var sentAt = _overrides?.Mark() ?? 0;
            var readAt = _removals.Mark();
            var (items, total) = await _search(_filters.ToQuery(_term), Items.Count, PageSize, token);
            if (generation != _generation) return;
            if (_removals.Mark() != readAt)
            {
                Resync();
                return;
            }
            var (kept, remaining) = _removals.FilterPage(items, total, readAt);
            _overrides?.Reconcile(sentAt, kept);
            var shown = Items.Select(item => item.Id).ToHashSet();
            foreach (var item in kept.Where(item => shown.Add(item.Id))) Items.Add(item);
            _pages++;
            TotalCount = items.Count == 0 ? Items.Count : Math.Max(Items.Count, remaining);
            if (_settled) Remember(_filters, _term, ShownAtUtc ?? _time.GetUtcNow().UtcDateTime, readAt);
        }
        catch (Exception)
        {
            // Cancelled by a newer query, or a failed page: the list stays as it is and the next
            // scroll retries.
        }
        finally { _loadingMore = false; }
    }

    /// <summary>Fetches the loaded range again in one request and syncs the grid in place: no
    /// spinner, the scroll position stays. Deletions during a resync run it once more when it ends;
    /// during a user query it stays owed until that query succeeds.</summary>
    private void Resync()
    {
        if (IsUpdating)
        {
            _resyncOwed = true;
            return;
        }
        if (_resyncing) return;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        var limit = Math.Max(PageSize, (Items.Count + PageSize - 1) / PageSize * PageSize);
        _resyncOwed = false;
        _resyncing = true;
        _ = ResyncAsync(++_generation, limit, _pending.Token);
    }

    private async Task ResyncAsync(int generation, int limit, CancellationToken token)
    {
        try
        {
            var sentAt = _overrides?.Mark() ?? 0;
            var readAt = _removals.Mark();
            var askedAt = _time.GetUtcNow().UtcDateTime;
            var (items, total) = await _search(_filters.ToQuery(_term), 0, limit, token);
            if (generation != _generation) return;
            _resyncing = false;
            if (_removals.Mark() != readAt)
            {
                Resync();
                return;
            }
            (items, total) = _removals.FilterPage(items, total, readAt);
            _overrides?.Reconcile(sentAt, items);
            Items.Sync(items);
            TotalCount = Math.Max(Items.Count, total);
            _pages = 1;
            ShownAtUtc = askedAt;
            if (_settled) Remember(_filters, _term, askedAt, readAt);
            else _ = RememberSettledAsync(generation, _filters, _term, askedAt, readAt, token);
            Resynced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception) when (generation != _generation)
        {
            // Superseded: the newer request owns the state.
        }
        catch (Exception)
        {
            _resyncOwed = true;
            _resyncing = false;
        }
    }

    private CancellationTokenSource Restart(bool reloading = false)
    {
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _generation++;
        if (_resyncing) _resyncOwed = true;
        _resyncing = false;
        IsReloading = reloading;
        if (!reloading) _settled = false;
        IsUpdating = true;
        return _pending;
    }

    private void Schedule()
    {
        var cts = Restart();
        _ = DelayThenRunAsync(_generation, cts.Token);
    }

    private async Task DelayThenRunAsync(int generation, CancellationToken token)
    {
        try { await Task.Delay(Debounce, _time, token); }
        catch (OperationCanceledException) { return; }
        await RunAsync(generation, PageSize, token);
    }

    private async Task RunAsync(int generation, int limit, CancellationToken token)
    {
        var filters = _filters;
        var term = _term;
        var reloading = IsReloading;
        var cachedShown = false;
        try
        {
            var sentAt = _overrides?.Mark() ?? 0;
            var readAt = _removals.Mark();
            if (!reloading && _cache is not null && await _cache.ReadAsync(_profileKey, filters.CacheQuery(term), readAt) is { } cached)
            {
                if (generation != _generation) return;
                ShowCached(cached);
                cachedShown = true;
                limit = Math.Max(limit, (cached.Items.Count + PageSize - 1) / PageSize * PageSize);
            }
            var askedAt = _time.GetUtcNow().UtcDateTime;
            var (items, total) = await _search(filters.ToQuery(term), 0, limit, token);
            var hint = total == 0 ? await FindHintAsync(filters, term, token) : null;
            if (generation != _generation) return;
            (items, total) = _removals.FilterPage(items, total, readAt);
            _pages = 1;
            _resyncOwed = false;
            _overrides?.Reconcile(sentAt, items);
            // Items, count and hint land together: the old grid stays until the whole answer is in.
            Items.Sync(items);
            TotalCount = Math.Max(Items.Count, total);
            Hint = hint;
            ShownAtUtc = askedAt;
            HasFailed = false;
            HasResults = true;
            IsUpdating = false;
            IsReloading = false;
            if (reloading)
            {
                _settled = true;
                Remember(filters, term, askedAt, readAt);
            }
            else _ = RememberSettledAsync(generation, filters, term, askedAt, readAt, token);
            Answered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception) when (generation != _generation)
        {
            // Superseded (cancelled or not): the newer request owns the state.
        }
        catch (Exception)
        {
            if (!reloading && !cachedShown && _cache is not null) ShownAtUtc = null;
            HasFailed = true;
            IsUpdating = false;
            IsReloading = false;
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The query's last answer while the server is asked: the grid shows it, still marked
    /// as updating, and its count; the empty state and its hint wait for the server.</summary>
    private void ShowCached(QueryCacheEntry cached)
    {
        Items.Sync(cached.Items);
        TotalCount = Math.Max(Items.Count, cached.TotalCount);
        Hint = null;
        ShownAtUtc = cached.SavedAtUtc;
        HasResults = true;
        Answered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps the loaded titles as the answer to these filters and text.</summary>
    private void Remember(AdvancedSearchFilters filters, string term, DateTime answeredAtUtc, long readAt)
        => _ = _cache?.StoreAsync(_profileKey, filters.CacheQuery(term), Items, TotalCount, answeredAtUtc, readAt);

    /// <summary>Remembers a new query's answer once it has stood for <see cref="RememberAfter"/>
    /// without a newer query.</summary>
    private async Task RememberSettledAsync(int generation, AdvancedSearchFilters filters, string term, DateTime answeredAtUtc, long readAt,
        CancellationToken token)
    {
        if (_cache is null) return;
        try { await Task.Delay(RememberAfter, _time, token); }
        catch (OperationCanceledException) { return; }
        if (generation != _generation) return;
        _settled = true;
        Remember(filters, term, answeredAtUtc, readAt);
    }

    /// <summary>Asks, in parallel, how many titles each active filter's removal would show, and
    /// returns the filter that frees the most.</summary>
    private async Task<EmptyHint?> FindHintAsync(AdvancedSearchFilters filters, string term, CancellationToken token)
    {
        var kinds = Enum.GetValues<FilterKind>().Where(filters.IsActive).ToArray();
        if (kinds.Length == 0) return null;
        var counts = await Task.WhenAll(kinds.Select(async kind =>
        {
            try { return (Kind: kind, Count: (await _search(filters.Without(kind).ToQuery(term), 0, 1, token)).TotalCount); }
            catch (Exception) when (!token.IsCancellationRequested) { return (Kind: kind, Count: 0); }
        }));
        var best = counts.Where(c => c.Count > 0).OrderByDescending(c => c.Count).ThenBy(c => c.Kind).FirstOrDefault();
        return best.Count > 0 ? new EmptyHint(best.Kind, filters.HintLabel(best.Kind), best.Count) : null;
    }
}
