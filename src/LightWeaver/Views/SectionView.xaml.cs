using LightWeaver.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Full-screen listing behind a Home rail's "see all" header (Phase 7): Continue
/// Watching / Next Up / Recently Added / Favorites, each just a different paged
/// query delegate over the same grid. Infinite scroll + generation-guarded loads
/// follow <see cref="AdvancedSearchView"/>; card badges stay fresh via
/// <see cref="ApplyUserDataUpdate"/> (fanned out by MainWindow like the other
/// cached list views).
/// </summary>
public partial class SectionView : UserControl
{
    private const int PageSize = 50;

    private readonly AppViewModel _app;
    private readonly Func<int, int, Task<(List<MediaItem> Items, int TotalCount)>> _query;
    private readonly ObservableCollection<MediaItem> _items = [];

    private int _totalCount;
    private bool _loadingMore;
    private int _loadGeneration;
    /// <summary>Bumped when <see cref="RemoveItems"/> takes cards off the grid. An append asks for
    /// the page at the grid's count, so one in flight across a removal would land one card off per
    /// removed card: a skipped item, or a boundary card twice. It carries the epoch and the count it
    /// started at, is dropped when either moved, and is asked again at the new count.</summary>
    private int _appendEpoch;
    /// <summary>A first load is in flight; the grid's state is its to set when it lands.</summary>
    private bool _reloading;
    /// <summary>Asks again when an append ran out of retries on an emptied grid (see
    /// <see cref="RemovalRefill"/>).</summary>
    private readonly RefillBackoff _refillBackoff = new();

    public event Action<MediaItem>? ItemSelected;

    public SectionView(AppViewModel app, Func<int, int, Task<(List<MediaItem> Items, int TotalCount)>> query)
    {
        InitializeComponent();
        LiveText.Watch(this);
        _app = app;
        _query = query;
        SkeletonHost.Children.Add(SkeletonFactory.Grid(this, 12, "SectionSkeleton"));
        ItemsGrid.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            if (_items.Count == 0 && _loadGeneration == 0)
                await LoadFirstAsync();
            else
                RefillPending();   // shown again: an emptied grid with items left asks now
        };
        // Playback collapses the browse layer without unloading this view, so Loaded alone misses
        // the return from the player; a retry the backoff skipped while hidden asks here.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && IsLoaded && _loadGeneration > 0)
                RefillPending();
        };
        ItemsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
    }

    private async Task LoadFirstAsync()
    {
        var generation = ++_loadGeneration;
        ReleaseStaleAppend();
        _refillBackoff.Cancel();
        _reloading = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("section", $"event=load outcome=start mode=initial generation={generation}");
        SkeletonScroll.Visibility = Visibility.Visible;
        try
        {
            // Test hook (see LibraryView): makes the skeleton observable on fast LANs.
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var profileKey = _app.ActiveSessionKey;
            var requestedAtUtc = DateTime.UtcNow;
            var (items, total) = await _query(0, PageSize);
            if (generation != _loadGeneration)
            {
                Diagnostics.AppLog.Detail("section", $"event=load outcome=stale mode=initial generation={generation}");
                return;
            }
            var listedCount = items.Count;
            (items, total) = DropRemoved(items, total, requestedAtUtc, profileKey);
            _reloading = false;
            _totalCount = RemovalRefill.RemainingTotal(items.Count, listedCount, total);
            _items.Clear();
            foreach (var item in items)
                _items.Add(item);
            UpdateCount();
            // Every card it listed was removed while it ran, with items left: the loading state and
            // the append asks again, the same hand-off as Advanced Search's first load.
            Settle();
            RefillPending();
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=success mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={total}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                Diagnostics.AppLog.Detail("section",
                    $"event=load outcome=stale mode=initial generation={generation} phase=exception");
                return;
            }
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=failure mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            _reloading = false;
            SkeletonScroll.Visibility = Visibility.Collapsed;
            LiveText.Bind(StatusText, "Text", () => L.Format("SectionView.FailedToLoad.8dd51815", (ex.Message)));
            StatusText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A server-side library change landed: reload from the first page. This listing is
    /// server-paged and carries no folder of its own, so there is nothing to compare the changed
    /// folder ids against — every batch reloads it, which is the same rule LibraryView applies to
    /// a change it cannot attribute.
    /// <para>Straight to <see cref="LoadFirstAsync"/>: it bumps the load generation, so an append
    /// already in flight lands as <c>outcome=stale</c> instead of pasting a stale page onto a
    /// fresh one. The appended pages are lost with it and that is the intended trade — they were
    /// read against the library as it was before the change.</para></summary>
    public void NotifyLibraryChanged() => _ = LoadFirstAsync();

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200)
            return;
        TryAppend();
    }

    /// <summary>Starts the next append unless one is running or the listing is complete.</summary>
    private void TryAppend()
    {
        if (_loadingMore || _items.Count >= _totalCount)
            return;
        _ = AppendPageAsync();
    }

    /// <summary>One append at the grid's count. It lands only if the load generation, the append
    /// epoch and the count it started at are all unchanged. One dropped for a moved epoch or count
    /// (a removal took cards off the grid meanwhile) is asked again at the new count; one dropped
    /// for a reload is not, and the reload released its flag.</summary>
    private async Task AppendPageAsync()
    {
        _loadingMore = true;
        // Append-owned: hidden in the finally by the append of the current load. See LibraryView.
        MoreLoading.Show();
        var generation = _loadGeneration;
        var appendEpoch = _appendEpoch;
        var offset = _items.Count;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var requeue = false;
        Diagnostics.AppLog.Detail("section", $"event=load outcome=start mode=append generation={generation} offset={offset}");
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var profileKey = _app.ActiveSessionKey;
            List<MediaItem> more;
            int total;
            int listedCount;
            for (var attempt = 0; ; attempt++)
            {
                var requestedAtUtc = DateTime.UtcNow;
                var (listed, serverTotal) = await _query(offset, PageSize);
                if (generation != _loadGeneration)
                {
                    Diagnostics.AppLog.Detail("section", $"event=load outcome=stale mode=append generation={generation}");
                    return;
                }
                if (appendEpoch != _appendEpoch || offset != _items.Count)
                {
                    Diagnostics.AppLog.Detail("section",
                        $"event=load outcome=stale mode=append generation={generation} reason=count_moved offset={offset} count={_items.Count}");
                    requeue = true;
                    return;
                }
                listedCount = listed.Count;
                (more, total) = DropRemoved(listed, serverTotal, requestedAtUtc, profileKey);
                // Every card it listed was removed while it ran, and the server still counts more
                // past this offset: ask again. A removal of cards not on the grid moves no epoch,
                // so a refill of an emptied grid otherwise took this page for the end.
                if (!RemovalRefill.AskAgain(more.Count, listedCount, total, offset, attempt))
                    break;
                Diagnostics.AppLog.Detail("section",
                    $"event=load outcome=retry mode=append generation={generation} reason=all_removed offset={offset} total={total} attempt={attempt + 1}");
            }
            foreach (var item in more)
                _items.Add(item);
            // Out of retries with every card removed is not the end: the count stays, and an
            // emptied grid shows the loading state and asks again after a backoff. Only a page
            // that listed nothing, or a count that drops to what is shown, ends the listing.
            _totalCount = RemovalRefill.RemainingTotal(_items.Count, listedCount, total);
            if (RemovalRefill.Classify(_items.Count, _totalCount) == RemovalRefill.State.Pending)
            {
                Diagnostics.AppLog.Detail("section",
                    $"event=load outcome=exhausted mode=append generation={generation} offset={offset} total={_totalCount}");
                ArmRefillRetry(generation);
            }
            else
                _refillBackoff.Succeeded();
            Settle();
            UpdateCount();
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            // next scroll retries; an emptied grid has no scroll, so it asks again after a backoff
            if (generation == _loadGeneration
                && RemovalRefill.Classify(_items.Count, _totalCount) == RemovalRefill.State.Pending)
                ArmRefillRetry(generation);
        }
        finally
        {
            // A reload released the flag when it bumped the generation, and the flag may belong to
            // its own append by now.
            if (generation == _loadGeneration)
            {
                _loadingMore = false;
                MoreLoading.Hide();
                if (requeue)
                    TryAppend();
            }
        }
    }

    /// <summary>A reload supersedes any append in flight; its page is dropped when it lands, so it
    /// must not hold <c>_loadingMore</c> until then and block the fresh listing's first append.</summary>
    private void ReleaseStaleAppend()
    {
        _loadingMore = false;
        MoreLoading.Hide();
    }

    /// <summary>A page answered for a request that started before a removal still lists the item,
    /// which <see cref="RemoveItems"/> has already taken off the grid. The removal ledger drops
    /// it, and the server's count, which still has it, shrinks with it. A watched or favorite
    /// toggle made while the request was out is applied the same way, as HomeView's rows do, so
    /// a reload that started before the toggle cannot put the old badge back over the fan-out.</summary>
    private (List<MediaItem> Items, int Total) DropRemoved(List<MediaItem> items, int total,
        DateTime requestedAtUtc, string? profileKey)
    {
        var kept = _app.DropRemovedItems(items, requestedAtUtc, profileKey);
        var count = total - (items.Count - kept.Count);
        return (_app.ApplyUserDataLedger(kept, requestedAtUtc, profileKey), count);
    }

    private void UpdateCount() =>
        LiveText.Bind(CountText, "Text", () => _totalCount == 1 ? L.Text("SectionView.1Item.43dc7d9b") : L.Format("SectionView.Items.d8880750", (_totalCount)));

    /// <summary>Swap a refreshed item in place so watched/favorite badges follow changes
    /// made elsewhere (mirrors LibraryView/HomeView; MediaItem is immutable).</summary>
    public void ApplyUserDataUpdate(MediaItem fresh)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == fresh.Id)
            {
                _items[i] = fresh;
                break;
            }
    }

    /// <summary>Drops items the server no longer has. Server-paged, so there is no cache entry
    /// to edit. The total shrinks with them, as the server's own listing has, so the end-of-list
    /// test in <see cref="TryAppend"/> still agrees with it.</summary>
    public void RemoveItems(IReadOnlySet<Guid> itemIds)
    {
        var removed = 0;
        for (var i = _items.Count - 1; i >= 0; i--)
            if (itemIds.Contains(_items[i].Id))
            {
                _items.RemoveAt(i);
                removed++;
            }
        if (removed == 0)
            return;
        // The grid pages by count: an append in flight was asked at the old one.
        _appendEpoch++;
        _totalCount = Math.Max(_items.Count, _totalCount - removed);
        UpdateCount();
        // Emptied with items left on the server: the loading state and their first page again,
        // rather than "Nothing here" under a count that still has them.
        Settle();
        RefillPending();
    }

    /// <summary>Shows the grid's state under <see cref="RemovalRefill"/>: cards, the loading
    /// skeleton while an emptied grid still counts items, or "Nothing here" at a confirmed zero.
    /// A first load in flight sets it when it lands.</summary>
    private void Settle()
    {
        if (_reloading)
            return;
        switch (RemovalRefill.Classify(_items.Count, _totalCount))
        {
            case RemovalRefill.State.Showing:
                SkeletonScroll.Visibility = Visibility.Collapsed;
                EmptyView.Visibility = Visibility.Collapsed;
                break;
            case RemovalRefill.State.Pending:
                EmptyView.Visibility = Visibility.Collapsed;
                SkeletonScroll.Visibility = Visibility.Visible;
                break;
            default:
                SkeletonScroll.Visibility = Visibility.Collapsed;
                ShowEmpty();
                break;
        }
    }

    /// <summary>Starts the refill of an emptied grid that still counts items. A no-op while an
    /// append or a first load runs; each of those settles the grid when it lands.</summary>
    private void RefillPending()
    {
        if (!_reloading && RemovalRefill.Classify(_items.Count, _totalCount) == RemovalRefill.State.Pending)
            TryAppend();
    }

    /// <summary>One more ask after the backoff, for the load that ran out of retries, on this
    /// profile, while the view is shown. Hidden (navigated away, or collapsed under the player,
    /// which leaves it loaded), it waits for the next reveal instead.</summary>
    private void ArmRefillRetry(int generation)
    {
        var profileKey = _app.ActiveSessionKey;
        _refillBackoff.Arm(() =>
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey || !IsVisible)
                return;
            Diagnostics.AppLog.Detail("section", $"event=load outcome=backoff_retry generation={generation}");
            RefillPending();
        });
    }

    private void ShowEmpty()
        => EmptyView.Show(() => L.Text("SectionView.NothingHere.a7f57eb9"), () => L.Text("SectionView.ThisSectionHasNoItemsRightNow.740c9867"));

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("section", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            ItemsGrid.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }
}
