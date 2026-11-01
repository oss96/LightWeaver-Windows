using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Pages;

public sealed partial class SectionPage : UserControl, INavPage
{
    private const int PageSize = 100;
    private const int MaxItems = 600;
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);
    private readonly IAppShell _shell;
    private readonly string _profileKey;
    private readonly ItemRemovals _removals;
    private readonly HomeSection _section;
    private readonly UserDataOverrides _overrides = new();
    private readonly List<MediaItem> _all = [];
    private readonly KeyedList<MediaItem, Guid> _shown = new(item => item.Id);
    private RecentlyAddedFeed? _feed;
    private int? _cachedTotal;
    private DateTime? _shownAtUtc;
    private CancellationTokenSource? _feedCts;
    private bool _loadingMore;
    private SectionFilter _filter = SectionFilter.All;
    private SectionSort _sort;
    private bool _small;
    private bool _loaded;
    private int _load;

    public SectionPage(IAppShell shell, HomeSection section)
    {
        _shell = shell;
        _profileKey = shell.CacheProfileKey;
        _removals = ItemRemovals.ForProfile(_profileKey);
        _section = section;
        _sort = HomeSections.DefaultSort(section);
        InitializeComponent();
        Skeleton.ItemsSource = Enumerable.Range(0, 40).ToList();
        foreach (var filter in HomeSections.Filters(section))
        {
            var segment = new RadioButton { Style = Ui.Resource<Style>("LwSegment"), GroupName = "SectionFilter" + section, Padding = new Thickness(12, 0, 12, 0), Tag = filter, IsChecked = filter == _filter };
            AutomationProperties.SetAutomationId(segment, "SectionFilter" + filter);
            segment.Checked += (_, _) => { _filter = filter; Requery(); };
            FilterSegments.Children.Add(segment);
        }
        ApplyLayout();
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyTexts(); Show(); }));
        Loaded += (_, _) => { if (!_loaded) _ = LoadAsync(); };
        Unloaded += (_, _) => _feedCts?.Cancel();
        if (!Paged) return;
        Scroller.ViewChanged += (_, _) => LoadMoreIfNear();
        Scroller.SizeChanged += (_, _) => LoadMoreIfNear();
        Items.SizeChanged += (_, _) => LoadMoreIfNear();
    }

    /// <summary>A new filter or sort: Recently Added asks the server again from the top; the other
    /// sections filter and sort what they hold.</summary>
    private void Requery()
    {
        if (!Paged)
        {
            Show();
            return;
        }
        Scroller.ChangeView(null, 0, null, true);
        _ = LoadFeedAsync(fresh: true);
    }

    /// <summary>Recently Added pages from the server as the grid scrolls; the other sections load whole.</summary>
    private bool Paged => HomeSections.IsPaged(_section);

    private string CacheQuery => HomeSections.CacheQuery(_section, _filter, _sort);

    /// <summary>The page belongs to the profile it was built for; an answer for another is dropped.</summary>
    private bool SameProfile => _profileKey == _shell.CacheProfileKey;

    public void OnShown() { if (_loaded) _ = LoadAsync(); }

    public void Reload() => _ = LoadAsync();

    public DateTime? CachedAtUtc => _shownAtUtc;

    public void LoadCached() { if (!_loaded) _ = LoadAsync(); }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        _all.RemoveAll(item => ids.Contains(item.Id));
        _feed?.RemoveItems(ids);
        if (Paged)
        {
            var before = _shown.Count;
            for (var i = _shown.Count - 1; i >= 0; i--)
                if (ids.Contains(_shown[i].Id)) _shown.RemoveAt(i);
            if (_cachedTotal is { } total) _cachedTotal = Math.Max(_shown.Count, total - (before - _shown.Count));
        }
        Show();
    }

    private async Task LoadAsync()
    {
        if (Paged)
        {
            await LoadFeedAsync(fresh: false);
            return;
        }
        var load = ++_load;
        var jellyfin = _shell.Model.Jellyfin;
        var items = new List<MediaItem>();
        var readAt = _removals.Mark();
        var skeleton = RevealSkeletonSoon();
        try
        {
            if (!_loaded && await QueryCache.Sections.ReadAsync(_profileKey, CacheQuery, readAt) is { } cached)
            {
                if (load != _load || !SameProfile) return;
                ShowLoaded(cached.Items, cached.SavedAtUtc);
                _shell.CachedDataShown(this);
            }
            if (_shell.Connectivity.IsOffline) return;
            var askedAt = DateTime.UtcNow;
            var total = int.MaxValue;
            while (items.Count < Math.Min(total, MaxItems))
            {
                var (page, count) = await PageAsync(jellyfin, items.Count);
                if (load != _load || !SameProfile) return;
                total = count;
                items.AddRange(page);
                if (page.Count == 0) break;
            }
            ShowLoaded(_removals.Filter(items, readAt), askedAt);
            Remember(items, total, askedAt, readAt);
        }
        catch (Exception ex)
        {
            if (load == _load) _shell.ReportFailure(ex);
        }
        finally
        {
            skeleton.Cancel();
            if (load == _load) SkeletonScroller.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowLoaded(IEnumerable<MediaItem> items, DateTime atUtc)
    {
        _loaded = true;
        _shownAtUtc = atUtc;
        _all.Clear();
        _all.AddRange(items);
        SkeletonScroller.Visibility = Visibility.Collapsed;
        Show();
    }

    /// <summary>Keeps the server's answer for the next visit and for offline.</summary>
    private void Remember(IReadOnlyList<MediaItem> items, int total, DateTime answeredAtUtc, long readAt)
        => _ = QueryCache.Sections.StoreAsync(_profileKey, CacheQuery, items, total, answeredAtUtc, readAt);

    /// <summary>Shows the library grid's skeleton after <see cref="SkeletonDelay"/> while nothing
    /// has loaded yet, unless the request cancels the returned source first, as it does once it ends.</summary>
    private CancellationTokenSource RevealSkeletonSoon()
    {
        var reveal = new CancellationTokenSource();
        if (!_loaded)
            _ = Task.Delay(SkeletonDelay).ContinueWith(_ =>
            {
                if (!reveal.IsCancellationRequested && !_loaded) SkeletonScroller.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        return reveal;
    }

    /// <summary>Recently Added from its first page again, for the current filter and sort (newest
    /// first, movies and shows merged; or by name), as far as the grid had read, while a new snapshot
    /// of the order is read for the pages after it. Played titles stay
    /// out when the user's server preference keeps them out of the Home row. The same titles in the
    /// same order update the cards in place and keep the scroll position; <paramref name="fresh"/>
    /// (a new filter or sort) reads one page, or as far as its cached answer went. Offline, a new
    /// filter or sort with no cached answer leaves the page to the window's unreachable view.</summary>
    private async Task LoadFeedAsync(bool fresh)
    {
        _feedCts?.Cancel();
        var cts = _feedCts = new CancellationTokenSource();
        var jellyfin = _shell.Model.Jellyfin;
        var wanted = fresh ? PageSize : Math.Max(PageSize, _shown.Count);
        var cachedShown = false;
        var readAt = _removals.Mark();
        var skeleton = RevealSkeletonSoon();
        try
        {
            var cached = fresh || Items.ItemsSource is null ? await QueryCache.Sections.ReadAsync(_profileKey, CacheQuery, readAt) : null;
            if (cts.IsCancellationRequested || !SameProfile) return;
            if (cached is not null)
            {
                cachedShown = true;
                _feed = null;
                _cachedTotal = cached.TotalCount;
                _shownAtUtc = cached.SavedAtUtc;
                _loaded = true;
                SkeletonScroller.Visibility = Visibility.Collapsed;
                ShowItems(cached.Items);
                ShowFeedState();
                _shell.CachedDataShown(this);
                wanted = Math.Max(wanted, cached.Items.Count);
            }
            if (_shell.Connectivity.IsOffline)
            {
                if (fresh && cached is null)
                {
                    _shownAtUtc = null;
                    _shell.CachedDataShown(this);
                }
                return;
            }
            var askedAt = DateTime.UtcNow;
            var unplayedOnly = await jellyfin.HidesPlayedInLatestAsync(cts.Token);
            var feed = new RecentlyAddedFeed(RecentList.For(_filter, _sort),
                (list, count, ct) => jellyfin.GetRecentlyAddedHeadAsync(list.Kinds, list.SortBy, list.Order, unplayedOnly, count, ct),
                (list, ct) => jellyfin.GetRecentlyAddedOrderAsync(list.Kinds, list.SortBy, list.Order, unplayedOnly, ct),
                jellyfin.GetRecentlyAddedTitlesAsync, _removals);
            await feed.NextAsync(wanted, cts.Token);
            if (cts.IsCancellationRequested || !SameProfile) return;
            var items = feed.Shown.ToList();
            _feed = feed;
            _cachedTotal = null;
            _shownAtUtc = askedAt;
            _loaded = true;
            ShowItems(items);
            ShowFeedState();
            Remember(items, feed.Total ?? items.Count, askedAt, readAt);
            LoadMoreIfNear();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                if (fresh && !cachedShown) _shownAtUtc = null;
                _shell.ReportFailure(ex);
            }
        }
        finally
        {
            skeleton.Cancel();
            if (ReferenceEquals(cts, _feedCts)) SkeletonScroller.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Follows the newer list by key, so titles the grid already shows keep their cards
    /// and take their newer copies.</summary>
    private void ShowItems(IReadOnlyList<MediaItem> items)
    {
        Items.ItemsSource ??= _shown;
        _shown.Sync(items);
        MediaCard.RefreshRealized(Items, _shown);
    }

    /// <summary>The next page once the grid is scrolled to within two screens of its end, or does not
    /// fill the window yet.</summary>
    private async void LoadMoreIfNear()
    {
        if (_feed is not { HasMore: true } feed || _loadingMore || _feedCts is not { IsCancellationRequested: false } cts || Scroller.ViewportHeight <= 0
            || Scroller.ExtentHeight - Scroller.VerticalOffset - Scroller.ViewportHeight > 2 * Scroller.ViewportHeight) return;
        _loadingMore = true;
        var readAt = _removals.Mark();
        try
        {
            var inserts = await feed.NextAsync(PageSize, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(feed, _feed) || !SameProfile) return;
            foreach (var insert in inserts) _shown.Insert(insert.Index, insert.Item);
            ShowFeedState();
            Remember([.. _shown], feed.Total ?? _shown.Count, _shownAtUtc ?? DateTime.UtcNow, readAt);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _shell.ReportFailure(ex);
            return;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void ShowFeedState()
    {
        var total = _feed?.Total ?? _cachedTotal ?? _shown.Count;
        CountText.Text = L.Format("Section.ItemCount", total);
        EmptyState.Visibility = _loaded && total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task<(List<MediaItem> Items, int TotalCount)> PageAsync(JellyfinService jellyfin, int start) => _section switch
    {
        HomeSection.ContinueWatching => jellyfin.GetResumePagedAsync(start, PageSize),
        HomeSection.NextUp => jellyfin.GetNextUpPagedAsync(start, PageSize),
        _ => jellyfin.GetFavoritesPagedAsync(start, PageSize),
    };

    /// <summary>Lists the items for the filter and sort; titles that stay keep their cards.</summary>
    private void Show()
    {
        if (Paged)
        {
            ShowFeedState();
            return;
        }
        ShowItems(HomeSections.Apply(_all, _filter, _sort));
        CountText.Text = _section == HomeSection.NextUp ? L.Format("Section.ShowCount", _shown.Count) : L.Format("Section.ItemCount", _shown.Count);
        EmptyState.Visibility = _loaded && _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not MediaCard card) return;
        card.Shell = _shell;
        card.Session = _shell.Model.Jellyfin;
        card.Kind = HomeSections.IsWide(_section) ? CardKind.Wide : CardKind.Poster;
        card.Overrides = _overrides;
        card.Title = HomeSections.Title;
        card.Subtitle = i => HomeSections.Subtitle(_section, i);
        card.Poster = i => HomeSections.SeriesPoster(_section, i);
        card.MenuOptions = _section == HomeSection.ContinueWatching ? new ItemMenuOptions(RemoveFromContinueWatching) : null;
        card.Item = _shown[e.Index];
    }

    private async void RemoveFromContinueWatching(MediaItem item)
    {
        var jellyfin = _shell.Model.Jellyfin;
        var index = _all.FindIndex(i => i.Id == item.Id);
        if (index < 0) return;
        _all.RemoveAt(index);
        Show();
        if (!await jellyfin.SetResumePositionAsync(item.Id, 0))
        {
            _all.Insert(index, item);
            Show();
            ToastHost.Show(ToastKind.Error, L.Text("Home.RemoveFailed"));
            return;
        }
        Remember([.. _all], _all.Count, _shownAtUtc ?? DateTime.UtcNow, _removals.Mark());
        ToastHost.Show(ToastKind.Success, L.Text("Home.RemovedFromContinueWatching"), L.Text("Toast.Undo"), async () =>
        {
            if (!await jellyfin.SetResumePositionAsync(item.Id, item.ResumePositionTicks)) return;
            _all.Insert(Math.Min(index, _all.Count), item);
            Show();
            Remember([.. _all], _all.Count, _shownAtUtc ?? DateTime.UtcNow, _removals.Mark());
        });
    }

    private void OnHome(object sender, RoutedEventArgs e) => _shell.ReturnHome();

    private void OnSort(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout());
        foreach (var sort in HomeSections.Sorts(_section))
        {
            var item = new RadioMenuFlyoutItem { Text = SortName(sort), GroupName = "SectionSort", IsChecked = sort == _sort };
            AutomationProperties.SetAutomationId(item, "SectionSort" + sort);
            item.Click += (_, _) => { _sort = sort; ApplyTexts(); Requery(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(SortButton);
    }

    private void OnSize(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout());
        foreach (var small in new[] { false, true })
        {
            var item = new RadioMenuFlyoutItem { Text = small ? L.Text("Section.SmallCards") : L.Text("Section.LargeCards"), GroupName = "SectionSize", IsChecked = small == _small };
            AutomationProperties.SetAutomationId(item, small ? "SectionSmallCards" : "SectionLargeCards");
            item.Click += (_, _) => { _small = small; ApplyLayout(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(SizeButton);
    }

    /// <summary>minmax(270px, 1fr) for wide cards (design 03), 150 px for posters; small cards
    /// fit more per row. The height is the artwork plus the two caption lines.</summary>
    private void ApplyLayout()
    {
        var wide = HomeSections.IsWide(_section);
        var width = wide ? (_small ? 200 : 270) : (_small ? 110 : 150);
        GridLayout.MinItemWidth = SkeletonLayout.MinItemWidth = width;
        GridLayout.MinItemHeight = SkeletonLayout.MinItemHeight = (wide ? width * 9.0 / 16 : width * 1.5) + 44;
    }

    private void ApplyTexts()
    {
        TitleText.Text = _section switch
        {
            HomeSection.ContinueWatching => L.Text("Home.ContinueWatching"),
            HomeSection.NextUp => L.Text("Home.NextUp"),
            HomeSection.RecentlyAdded => L.Text("Home.RecentlyAdded"),
            _ => L.Text("Home.Favorites"),
        };
        foreach (var segment in FilterSegments.Children.OfType<RadioButton>()) segment.Content = FilterName((SectionFilter)segment.Tag);
        SortText.Text = SortName(_sort);
        var size = L.Text("Section.CardSize");
        ToolTipService.SetToolTip(SizeButton, size);
        AutomationProperties.SetName(SizeButton, size);
        AutomationProperties.SetName(SortButton, L.Format("Section.SortedBy", SortText.Text));
    }

    private static string FilterName(SectionFilter filter) => filter switch
    {
        SectionFilter.Movies => L.Text("Section.Movies"),
        SectionFilter.Episodes => L.Text("Section.Episodes"),
        SectionFilter.Shows => L.Text("Section.Shows"),
        SectionFilter.InProgress => L.Text("Section.InProgress"),
        SectionFilter.NotStarted => L.Text("Section.NotStarted"),
        _ => L.Text("Section.All"),
    };

    private string SortName(SectionSort sort) => sort switch
    {
        SectionSort.Name => L.Text("Section.SortName"),
        SectionSort.DateAdded => L.Text("Section.SortDateAdded"),
        _ when _section == HomeSection.RecentlyAdded => L.Text("Section.SortDateAdded"),
        _ => L.Text("Section.SortLastPlayed"),
    };
}
