using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Grid of a container's children: paged folder contents, a series' seasons,
/// or a season's episodes — decided by the source item's type. Paged grids get
/// sort/filter/genre controls and an A–Z letter filter (Phase 5 M3); state is
/// per-visit (a fresh view starts at the defaults).
/// </summary>
public partial class LibraryView : UserControl
{
    private const int PageSize = 50;

    private static readonly (string Label, ItemSortBy Sort)[] SortChoices =
    [
        ("Name", ItemSortBy.SortName),
        ("Date added", ItemSortBy.DateCreated),
        ("Premiere date", ItemSortBy.PremiereDate),
        ("Community rating", ItemSortBy.CommunityRating),
        ("Random", ItemSortBy.Random),
    ];

    private static readonly (string Label, ItemFilter? Filter)[] FilterChoices =
    [
        ("All", null),
        ("Unwatched", ItemFilter.IsUnplayed),
        ("Watched", ItemFilter.IsPlayed),
    ];

    private readonly AppViewModel _app;
    private readonly MediaItem _source;
    private readonly ObservableCollection<MediaItem> _items = [];

    private int _totalCount;
    private bool _loadingMore;
    private bool _paged;
    private bool _localMode;
    private List<MediaItem> _all = [];
    private List<MediaItem> _view = [];
    private const int LocalPageSize = 100;
    private int _randomSeed = Environment.TickCount;
    private string? _activeFolderCacheKey;
    private bool _initializing = true;
    private int _initialLoadGeneration;
    private string? _letter;   // A–Z filter, exact prefix (Name sort only); null = every letter
    private string? _forcedGenre;   // pre-filter from the genres view, until the combo syncs
    private int _loadGeneration;
    private readonly Dictionary<Guid, UserDataOverride> _userDataOverrides = [];
    private readonly Dictionary<int, int> _activeUserDataRequests = [];
    private int _userDataRevision;
    private int _nextUserDataRequestId;
    private System.Windows.Threading.DispatcherTimer? _persistTimer;

    public event Action<MediaItem>? ItemSelected;

    /// <summary>Play the whole container as a queue (source, shuffle).</summary>
    public event Action<MediaItem, bool>? PlayAllRequested;

    /// <summary>Batch-download the container (M20): season/series per the M15 episode
    /// rules; MainWindow runs the resolution picker + enqueue.</summary>
    public event Action<MediaItem>? DownloadAllRequested;

    /// <summary>Open the genre tile grid for this library.</summary>
    public event Action<MediaItem>? GenresRequested;

    public LibraryView(AppViewModel app, MediaItem source, string? genre = null)
    {
        InitializeComponent();
        // Wired here, not in XAML: the runtime XAML EventConverter used for custom
        // element types (LwSegmentedControl) needs an exact SelectionChangedEventArgs
        // signature, while OnQueryChanged takes RoutedEventArgs (shared with the
        // sort/order handlers) — C# method-group conversion allows the contravariance.
        FilterBox.SelectionChanged += OnQueryChanged;
        _app = app;
        _source = source;
        _forcedGenre = genre;
        QueueActions.Visibility = source.Type is BaseItemDto_Type.Season
            or BaseItemDto_Type.Series or BaseItemDto_Type.Playlist
            or BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.MusicArtist
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Batch download (M20) is an episode-rules affordance: seasons/series only.
        DownloadAllButton.Visibility = source.Type is BaseItemDto_Type.Season or BaseItemDto_Type.Series
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Sort/filter apply to paged folder grids only (not seasons/episodes lists,
        // not playlists — those keep their inherent order).
        var sortable = source.Type is not (BaseItemDto_Type.Season or BaseItemDto_Type.Series
            or BaseItemDto_Type.Playlist or BaseItemDto_Type.MusicAlbum);
        if (sortable)
        {
            foreach (var (label, _) in SortChoices)
                SortBox.Items.Add(label);
            SortBox.SelectedIndex = 0;
            foreach (var (label, _) in FilterChoices)
                FilterBox.Items.Add(label);
            FilterBox.SelectedIndex = 0;
            GenreBox.Items.Add("All genres");
            GenreBox.SelectedIndex = 0;
            SortPanel.Visibility = Visibility.Visible;
            // Genre controls are conditional: server genres exist only for movies/tvshows.
            // Both the Genres button and the genre dropdown are hidden elsewhere so the
            // Collections/Folders/Playlists toolbar carries no dead "All genres" control (M3.3).
            var hasGenres = source.CollectionType is "movies" or "tvshows";
            GenresButton.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
            GenreBox.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
            BuildLetterStrip();
        }
        _initializing = false;

        ItemsGrid.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            if (_items.Count == 0)
                await LoadAsync();
            if (sortable && GenreBox.Items.Count == 1)
                await LoadGenresAsync();
        };
        ItemsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        SizeChanged += OnViewSizeChanged;
        BrowsePrefetcher.Progress += OnPrefetchProgress;
        Unloaded += (_, _) =>
        {
            BrowsePrefetcher.Progress -= OnPrefetchProgress;
            if (_persistTimer is not null && _persistTimer.IsEnabled)
            {
                PersistUserData();
            }
        };
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeaderLayout();
        UpdateRailVisibility();
    }

    /// <summary>M3.1: keep the sort/filter cluster from clipping at narrow widths. When the
    /// header can't fit the left cluster + the controls on one line, drop the cluster to a
    /// full-width second row where its WrapPanel flows onto more lines instead of clipping.</summary>
    private void UpdateHeaderLayout()
    {
        if (SortPanel.Visibility != Visibility.Visible)
            return;
        var avail = HeaderBar.ActualWidth;
        if (avail <= 0)
            return;
        double left = QueueActions.Visibility == Visibility.Visible ? QueueActions.DesiredSize.Width
            : CountText.Visibility == Visibility.Visible ? CountText.DesiredSize.Width
            : 0;
        // Intrinsic single-line width of the controls, independent of the current wrap state
        // (each child's own DesiredSize is constant), so the decision never oscillates.
        double controls = 0;
        foreach (var child in SortPanel.Children)
            if (child is FrameworkElement fe && fe.Visibility != Visibility.Collapsed)
                controls += fe.DesiredSize.Width + fe.Margin.Left + fe.Margin.Right;
        var wrap = left + controls + 24 > avail;
        Grid.SetRow(SortPanel, wrap ? 1 : 0);
        Grid.SetColumn(SortPanel, wrap ? 0 : 1);
        Grid.SetColumnSpan(SortPanel, wrap ? 2 : 1);
        SortPanel.HorizontalAlignment = wrap ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SortPanel.Margin = wrap ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private bool _railWide = true;

    /// <summary>M3.4: the A-Z rail shows only on Name sort AND when the view is wide enough;
    /// at the window minimum it hides so it can't crowd the grid. P10 M11: when it hides for
    /// width, the header's dropdown takes over — the control is relocated, not lost.</summary>
    private void UpdateRailVisibility()
    {
        if (LetterStrip.Children.Count == 0)
        {
            LetterStrip.Visibility = Visibility.Collapsed;
            LetterJumpBox.Visibility = Visibility.Collapsed;
            return;
        }
        _railWide = ActualWidth <= 0 || ActualWidth >= 700;
        var nameSort = SelectedSort == ItemSortBy.SortName;
        LetterStrip.Visibility = nameSort && _railWide ? Visibility.Visible : Visibility.Collapsed;
        // Exactly one of the two is ever on screen: the dropdown is the narrow-width stand-in,
        // not a second permanent control.
        LetterJumpBox.Visibility = nameSort && !_railWide ? Visibility.Visible : Visibility.Collapsed;
        SyncLetterJumpSelection();
    }

    /// <summary>Mirrors the rail's active letter onto the dropdown without re-firing the query —
    /// the two are one piece of state shown two ways.</summary>
    private const string AnyLetter = "Any letter";

    private void SyncLetterJumpSelection()
    {
        if (LetterJumpBox.Items.Count == 0)
            return;
        var want = _letter ?? AnyLetter;
        if (LetterJumpBox.SelectedItem as string == want)
            return;
        _syncingLetterJump = true;
        LetterJumpBox.SelectedItem = want;
        _syncingLetterJump = false;
    }

    private bool _syncingLetterJump;

    private async void OnLetterJumpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLetterJump || _initializing || LetterJumpBox.SelectedItem is not string letter)
            return;
        _letter = letter == AnyLetter ? null : letter;
        MarkActiveLetter(_letter);
        if (_localMode)
        {
            ApplyLocalQuery();
            return;
        }
        await ResetAndReloadAsync();
    }

    private void OnPrefetchProgress(string key, int have, int total, bool complete, IReadOnlyList<MediaItem> items)
    {
        if (key != _activeFolderCacheKey)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (key != _activeFolderCacheKey)
                return;

            if (complete)
            {
                if (StatusText.Text.StartsWith("Caching ", StringComparison.Ordinal))
                    HideStatus();

                if (!_localMode && items.Count > 0)
                {
                    _localMode = true;
                    _all = ReconcileCachedUserData(items);
                    _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
                    _totalCount = _view.Count;
                    if (_items.Count == 0 || (SelectedSort == ItemSortBy.SortName && SelectedOrder == SortOrder.Ascending && SelectedFilter == null && SelectedGenre == null && _letter == null))
                    {
                        ReplaceItems(_view.Take(LocalPageSize));
                    }
                    UpdateCount();
                    SkeletonHost.Visibility = Visibility.Collapsed;
                    UpdateEmptyState();
                }
            }
            else
            {
                SetStatus($"Caching {have:N0} of {total:N0}...", active: true);
                if (_items.Count == 0 && items.Count > 0)
                {
                    var isDefaultQuery = SelectedSort == ItemSortBy.SortName &&
                                         SelectedOrder == SortOrder.Ascending &&
                                         SelectedFilter == null &&
                                         SelectedGenre == null &&
                                         _letter == null;
                    if (isDefaultQuery)
                    {
                        _totalCount = total;
                        ReplaceItems(ReconcileCachedUserData(items).Take(LocalPageSize));
                        UpdateCount();
                        SkeletonHost.Visibility = Visibility.Collapsed;
                        UpdateEmptyState();
                    }
                }
            }
        });
    }

    private void SetStatus(string text, bool active = true)
    {
        StatusText.Text = text;
        if (StatusDots is not null)
        {
            StatusDots.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Margin = active ? new Thickness(10, 0, 0, 0) : new Thickness(0);
        }
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        if (_items.Count == 0)
        {
            if (_letter is { Length: > 0 } active)
                EmptyView.Show($"Nothing under {active}",
                    "No titles here start with that letter. Pick another, or # to show them all.");
            else
                EmptyView.Show("Nothing here", "No items match this view or its filters.");
        }
        else
        {
            EmptyView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Replace a card's item in place with a refreshed copy (e.g. after the detail
    /// view toggled watched/favorite) so its badges update without a refetch. No-op if this
    /// grid doesn't hold the item. MediaItem is immutable, so we swap the instance.</summary>
    public void ApplyUserDataUpdate(MediaItem fresh)
    {
        // A browse request may already be in flight when the action completes. Retain the
        // authoritative update and merge it into that response before it can repaint a stale
        // watched/resume/favorite state.
        _userDataOverrides[fresh.Id] = new UserDataOverride(++_userDataRevision, fresh);
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == fresh.Id)
            {
                _items[i] = fresh;
                break;
            }
        if (_localMode)
        {
            for (var i = 0; i < _all.Count; i++)
                if (_all[i].Id == fresh.Id)
                {
                    _all[i] = fresh;
                    break;
                }
            for (var i = 0; i < _view.Count; i++)
                if (_view[i].Id == fresh.Id)
                {
                    _view[i] = fresh;
                    break;
                }
            SchedulePersistUserData();
        }
    }

    private void SchedulePersistUserData()
    {
        if (!_localMode || string.IsNullOrEmpty(_activeFolderCacheKey))
            return;

        if (_persistTimer is null)
        {
            _persistTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _persistTimer.Tick += (_, _) => PersistUserData();
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void PersistUserData()
    {
        _persistTimer?.Stop();
        if (!_localMode || string.IsNullOrEmpty(_activeFolderCacheKey) || _all.Count == 0)
            return;

        var cacheKey = _activeFolderCacheKey;
        var snapshot = _all.ToList();
        var totalCount = _all.Count;
        Task.Run(async () =>
        {
            var existing = await BrowseFolderCache.ReadAsync(cacheKey).ConfigureAwait(false);
            var complete = existing?.Complete ?? true;
            var truncated = existing?.Truncated ?? false;
            var total = existing?.TotalCount ?? totalCount;
            await BrowseFolderCache.StoreAsync(cacheKey,
                new FolderCacheEntry<MediaItem>(snapshot, total, complete, truncated, DateTime.UtcNow, BrowseFolderCache.CurrentSchema))
                .ConfigureAwait(false);
            Diagnostics.AppLog.Detail("library",
                $"event=persist_userdata outcome=success count={snapshot.Count} total={total} item={_source.Id:N}");
        });
    }

    private ItemSortBy SelectedSort => SortChoices[Math.Max(0, SortBox.SelectedIndex)].Sort;

    private SortOrder SelectedOrder => OrderButton.IsChecked == true
        ? SortOrder.Descending
        : SortOrder.Ascending;

    private ItemFilter? SelectedFilter => FilterChoices[Math.Max(0, FilterBox.SelectedIndex)].Filter;

    private string? SelectedGenre => GenreBox.SelectedIndex > 0
        ? GenreBox.SelectedItem as string
        : _forcedGenre;

    /// <summary>Header row and A-Z rail sit outside the grid's scroller, so the wheel
    /// is dead over them; forward it to the grid (Phase 7 scroll-wheel audit).</summary>
    private void OnDeadZoneWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        => WheelScroll.ForwardTo(_gridScroll ??= WheelScroll.FindScrollViewer(ItemsGrid), e);

    private ScrollViewer? _gridScroll;

    private void BuildLetterStrip()
    {
        var mono = (System.Windows.Media.FontFamily)FindResource("LwFontMono");
        foreach (var letter in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var btn = new Button
            {
                Content = letter.ToString(),
                FontFamily = mono,
                FontSize = 12,
                Padding = new Thickness(0),
                Width = 22,
                Height = 18,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (System.Windows.Media.Brush)FindResource("LwText2Brush"),
                FocusVisualStyle = null,
                Tag = letter.ToString(),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, $"Letter{letter}");
            btn.Click += OnLetterClick;
            LetterStrip.Children.Add(btn);
            // The rail's reset affordance is '#', which is legible in a column of letters and
            // cryptic on its own in a dropdown. The dropdown spells it out instead, matching
            // its neighbour "All genres".
            LetterJumpBox.Items.Add(letter == '#' ? AnyLetter : letter.ToString());
        }
        MarkActiveLetter(null);
        UpdateRailVisibility();   // default sort is Name; visibility also gated by width (M3.4)
    }

    private void UpdateCount()
    {
        if (_paged && _totalCount > 0)
        {
            CountText.Text = _totalCount == 1 ? "1 item" : $"{_totalCount:N0} items";
            CountText.Visibility = Visibility.Visible;
        }
        else
        {
            CountText.Visibility = Visibility.Collapsed;
        }
        UpdateHeaderLayout();   // left-cluster width may have changed the fit (M3.1)
    }

    /// <summary>Light the active letter (stormlight glow), dim the rest — the S3 alphabet rail.</summary>
    private void MarkActiveLetter(string? active)
    {
        var dim = (System.Windows.Media.Brush)FindResource("LwText2Brush");
        var lit = (System.Windows.Media.Brush)FindResource("LwLightCoreBrush");
        var glow = (System.Windows.Media.Effects.Effect)FindResource("LwGlowKnob");
        var key = active ?? "#";
        foreach (var child in LetterStrip.Children)
        {
            if (child is Button { Tag: string tag } b)
            {
                var on = tag == key;
                b.Foreground = on ? lit : dim;
                b.FontSize = on ? 13.5 : 12;
                b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                b.Effect = on ? glow : null;
            }
        }
    }

    private async void OnLetterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string letter })
            return;
        _letter = letter == "#" ? null : letter;
        MarkActiveLetter(_letter);
        SyncLetterJumpSelection();
        if (_localMode)
        {
            ApplyLocalQuery();
            return;
        }
        await ResetAndReloadAsync();
    }

    private async void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || !_paged)
            return;
        var nameSort = SelectedSort == ItemSortBy.SortName;
        if (!nameSort)
            _letter = null;
        UpdateRailVisibility();
        OrderButton.Content = (string)FindResource(SelectedOrder == SortOrder.Descending ? "IconSortDesc" : "IconSortAsc");
        if (_localMode)
        {
            ApplyLocalQuery();
            return;
        }
        await ResetAndReloadAsync();
    }

    private void ApplyLocalQuery()
    {
        if (SelectedSort == ItemSortBy.Random)
            _randomSeed = Environment.TickCount;
        _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
        _totalCount = _view.Count;
        ReplaceItems(_view.Take(LocalPageSize));
        UpdateCount();
        UpdateEmptyState();
        Diagnostics.AppLog.Detail("library",
            $"event=query_change item={_source.Id:N} type={_source.Type} sort={SelectedSort} order={SelectedOrder} filter={SelectedFilter?.ToString() ?? "none"} genre={(SelectedGenre is null ? "all" : "custom")} letter={(_letter is null ? "all" : "set")} mode=local count={_items.Count} total={_totalCount}");
    }

    private async Task ResetAndReloadAsync()
    {
        // Invalidate an in-flight initial/append request before the optional test delay. Without
        // this, an older page can append into the cleared collection while the new query waits.
        ++_loadGeneration;
        Diagnostics.AppLog.Detail("library",
            $"event=query_change item={_source.Id:N} type={_source.Type} sort={SelectedSort} order={SelectedOrder} filter={SelectedFilter?.ToString() ?? "none"} genre={(SelectedGenre is null ? "all" : "custom")} letter={(_letter is null ? "all" : "set")} mode=server");
        _items.Clear();
        _totalCount = 0;
        // M25: structured skeleton instead of the "Loading..." label
        if (SkeletonHost.Content is null)
            SkeletonHost.Content = SkeletonFactory.Grid(this, 12, "GridSkeleton");
        SkeletonHost.Visibility = Visibility.Visible;
        HideStatus();
        // Test hook: LIGHTWEAVER_SLOW_LOAD_MS delays the fetch so the loading state is
        // deterministically observable (the LAN outruns UIA polling otherwise).
        if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
            await Task.Delay(slowMs);
        await LoadAsync();
    }

    private async Task LoadGenresAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("library", $"event=facet_load outcome=start facet=genres item={_source.Id:N}");
        try
        {
            var genres = await _app.Jellyfin.GetGenresAsync(_source.Id);
            foreach (var genre in genres)
                GenreBox.Items.Add(genre);
            // A pre-filter from the genres view becomes the combo selection (without
            // re-querying — the initial load already used it).
            if (_forcedGenre is { } forced && GenreBox.Items.Contains(forced))
            {
                _initializing = true;
                GenreBox.SelectedItem = forced;
                _initializing = false;
                _forcedGenre = null;
            }
            Diagnostics.AppLog.Detail("library",
                $"event=facet_load outcome=success facet=genres elapsed_ms={started.ElapsedMilliseconds} count={genres.Count} item={_source.Id:N}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("library",
                $"event=facet_load outcome=failure facet=genres elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N}", ex);
            // no genre filter then — the dropdown just stays at "All genres"
        }
    }

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        _initialLoadGeneration = generation;
        var profileKey = _app.ActiveSessionKey ?? "";
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("library",
            $"event=load outcome=start mode=initial generation={generation} item={_source.Id:N} type={_source.Type}");

        _paged = _source.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season);

        if (!_paged)
        {
            await LoadSeriesOrSeasonAsync(generation, profileKey, started);
            return;
        }

        await LoadFolderAsync(generation, profileKey, started);
    }

    private async Task LoadSeriesOrSeasonAsync(int generation, string profileKey, System.Diagnostics.Stopwatch started)
    {
        var cacheKey = BrowseCacheKey(profileKey);
        var cached = await Imaging.MetadataCache.ReadAsync<Imaging.BrowseCacheEntry<MediaItem>>(cacheKey);
        if (cached?.Items is null) cached = null;
        if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
        {
            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
            return;
        }
        var hasCachedPage = cached is not null;
        if (cached is not null)
        {
            _totalCount = cached.TotalCount;
            ReplaceItems(ReconcileCachedUserData(cached.Items));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            EmptyView.Visibility = Visibility.Collapsed;
            SetStatus("Showing cached results while refreshing...", active: true);
        }
        else if (_items.Count == 0)
        {
            if (SkeletonHost.Content is null)
                SkeletonHost.Content = SkeletonFactory.Grid(this, 12, "GridSkeleton");
            SkeletonHost.Visibility = Visibility.Visible;
            HideStatus();
        }
        var userDataRequestId = 0;
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                return;
            userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
#if DEBUG
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_LIBRARY_TIMEOUT_TEST") == "1")
                throw new TaskCanceledException("Synthetic library query timeout.");
#endif
            var jf = _app.Jellyfin;
            List<MediaItem> items;
            if (_source.Type == BaseItemDto_Type.Series)
            {
                var seasons = await jf.GetSeasonsAsync(_source.Id);
                items = seasons.Count == 1
                    ? await jf.GetEpisodesAsync(_source.Id, seasons[0].Id)
                    : seasons;
            }
            else
            {
                items = await jf.GetEpisodesAsync(_source.SeriesId!.Value, _source.Id);
            }
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=stale mode=initial generation={generation} item={_source.Id:N} type={_source.Type}");
                return;
            }
            items = ReconcileNetworkUserData(items, requestUserDataRevision);
            await Imaging.MetadataCache.StoreAsync(cacheKey,
                new Imaging.BrowseCacheEntry<MediaItem>(items, items.Count, DateTime.UtcNow));
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=stale mode=initial generation={generation} phase=after_store item={_source.Id:N} type={_source.Type}");
                return;
            }
            items = ReconcileNetworkUserData(items, requestUserDataRevision);
            _totalCount = items.Count;
            ReplaceItems(items);
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            HideStatus();
            UpdateEmptyState();
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=success mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={_totalCount} item={_source.Id:N} type={_source.Type}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=stale mode=initial generation={generation} phase=exception item={_source.Id:N} type={_source.Type}");
                return;
            }
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=failure mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} type={_source.Type}", ex);
            SkeletonHost.Visibility = Visibility.Collapsed;
            EmptyView.Visibility = Visibility.Collapsed;
            if (hasCachedPage)
            {
                SetStatus($"Offline - showing cached results from {cached!.CachedAtUtc.ToLocalTime():g}.", active: false);
            }
            else
            {
                SetStatus($"Failed to load: {ex.Message}", active: false);
            }
        }
        finally
        {
            if (userDataRequestId != 0)
                EndUserDataRequest(userDataRequestId);
            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
        }
    }

    private async Task LoadFolderAsync(int generation, string profileKey, System.Diagnostics.Stopwatch started)
    {
        var folderCacheKey = BrowseFolderCache.Key(profileKey, _source.Id);
        _activeFolderCacheKey = folderCacheKey;

        var folderEntry = await BrowseFolderCache.ReadAsync(folderCacheKey);
        if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
        {
            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
            return;
        }

        if (folderEntry is not null && folderEntry.Complete && !folderEntry.Truncated)
        {
            _localMode = true;
            _all = ReconcileCachedUserData(folderEntry.Items);
            _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
            _totalCount = _view.Count;
            ReplaceItems(_view.Take(LocalPageSize));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
            SetStatus("Showing cached results while refreshing...", active: true);
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=success mode=local generation={generation} count={_items.Count} total={_totalCount} item={_source.Id:N} type={_source.Type}");

            var userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
            _ = Task.Run(() => RefreshFolderAsync(folderCacheKey, folderEntry, generation, profileKey,
                userDataRequestId, requestUserDataRevision, started));

            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
            return;
        }

        // Incomplete / Truncated / Cold
        _localMode = false;
        _all = [];
        _view = [];

        var isDefaultQuery = SelectedSort == ItemSortBy.SortName &&
                             SelectedOrder == SortOrder.Ascending &&
                             SelectedFilter == null &&
                             SelectedGenre == null &&
                             _letter == null;

        if (folderEntry is not null && folderEntry.Items.Count > 0 && isDefaultQuery)
        {
            _totalCount = folderEntry.TotalCount;
            ReplaceItems(ReconcileCachedUserData(folderEntry.Items).Take(LocalPageSize));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
            SetStatus($"Caching {folderEntry.Items.Count:N0} of {folderEntry.TotalCount:N0}...", active: true);
        }
        else if (_items.Count == 0)
        {
            if (SkeletonHost.Content is null)
                SkeletonHost.Content = SkeletonFactory.Grid(this, 12, "GridSkeleton");
            SkeletonHost.Visibility = Visibility.Visible;
            HideStatus();
        }

        if (folderEntry is null || (!folderEntry.Complete && !folderEntry.Truncated))
        {
            BrowsePrefetcher.Start(_app.Jellyfin, profileKey, _source.Id);
        }

        if (!isDefaultQuery || folderEntry is null || folderEntry.Items.Count == 0)
        {
            var userDataRequestId = 0;
            try
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                    await Task.Delay(slowMs);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;
                userDataRequestId = BeginUserDataRequest();
                var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
#if DEBUG
                if (Environment.GetEnvironmentVariable("LIGHTWEAVER_LIBRARY_TIMEOUT_TEST") == "1")
                    throw new TaskCanceledException("Synthetic library query timeout.");
#endif
                var (items, serverTotal) = await _app.Jellyfin.GetItemsAsync(_source.Id, 0, PageSize,
                    SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;
                items = ReconcileNetworkUserData(items, requestUserDataRevision);
                _totalCount = serverTotal;
                ReplaceItems(items);
                UpdateCount();
                SkeletonHost.Visibility = Visibility.Collapsed;
                UpdateEmptyState();
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=success mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={_totalCount} item={_source.Id:N} type={_source.Type}");
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;
                SkeletonHost.Visibility = Visibility.Collapsed;
                EmptyView.Visibility = Visibility.Collapsed;
                SetStatus($"Failed to load: {ex.Message}", active: false);
            }
            finally
            {
                if (userDataRequestId != 0)
                    EndUserDataRequest(userDataRequestId);
                if (_initialLoadGeneration == generation)
                    _initialLoadGeneration = 0;
            }
        }
        else
        {
            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
        }
    }

    private async Task RefreshFolderAsync(string cacheKey, FolderCacheEntry<MediaItem> cachedEntry,
        int generation, string profileKey, int userDataRequestId, int requestUserDataRevision,
        System.Diagnostics.Stopwatch started)
    {
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs).ConfigureAwait(false);

#if DEBUG
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_LIBRARY_TIMEOUT_TEST") == "1")
                throw new TaskCanceledException("Synthetic library query timeout.");
#endif

            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=stale mode=incremental generation={generation} item={_source.Id:N}");
                return;
            }

            var (sweep, serverTotal) = await _app.Jellyfin.SweepFolderAsync(_source.Id, CancellationToken.None).ConfigureAwait(false);

            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=stale mode=incremental generation={generation} phase=after_sweep item={_source.Id:N}");
                return;
            }

            var (provisional, added, removed, changed, toRefetch) = BrowseFolderCache.Diff(cachedEntry.Items, sweep);

            List<MediaItem> refetched = [];
            if (toRefetch.Count > 0)
            {
                refetched = await _app.Jellyfin.GetItemsByIdsAsync(toRefetch, CancellationToken.None).ConfigureAwait(false);
            }

            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=stale mode=incremental generation={generation} phase=after_refetch item={_source.Id:N}");
                return;
            }

            var merged = BrowseFolderCache.Merge(sweep, provisional, refetched);

            List<MediaItem>? reconciled = null;
            Dispatcher.Invoke(() =>
            {
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;

                reconciled = ReconcileNetworkUserData(merged, requestUserDataRevision);
                _all = reconciled;
                _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
                _totalCount = _view.Count;

                var takeCount = Math.Max(_items.Count, LocalPageSize);
                var nextDisplay = _view.Take(takeCount).ToList();
                var sameIds = _items.Count == nextDisplay.Count;
                if (sameIds)
                {
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].Id != nextDisplay[i].Id)
                        {
                            sameIds = false;
                            break;
                        }
                    }
                }

                if (sameIds)
                {
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (!ReferenceEquals(_items[i], nextDisplay[i]))
                            _items[i] = nextDisplay[i];
                    }
                }
                else
                {
                    ReplaceItems(nextDisplay);
                }

                UpdateCount();
                UpdateEmptyState();
                if (StatusText.Text.StartsWith("Showing cached", StringComparison.Ordinal))
                    HideStatus();
            });

            if (reconciled is null)
                return;

            var truncated = serverTotal > BrowseFolderCache.MaxItems;
            var updatedEntry = new FolderCacheEntry<MediaItem>(
                reconciled,
                serverTotal,
                Complete: !truncated,
                Truncated: truncated,
                DateTime.UtcNow,
                BrowseFolderCache.CurrentSchema);

            await BrowseFolderCache.StoreAsync(cacheKey, updatedEntry).ConfigureAwait(false);

            Diagnostics.AppLog.Detail("library",
                $"event=refresh outcome=success mode=incremental swept={sweep.Count} total={serverTotal} added={added} removed={removed} changed={changed} refetched={refetched.Count} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=stale mode=incremental generation={generation} phase=exception item={_source.Id:N}");
                return;
            }

            Diagnostics.AppLog.Detail("library",
                $"event=refresh outcome=failure mode=incremental generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N}", ex);

            Dispatcher.Invoke(() =>
            {
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;

                SetStatus($"Offline - showing cached results from {cachedEntry.CachedAtUtc.ToLocalTime():g}.", active: false);
            });
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() => EndUserDataRequest(userDataRequestId));
        }
    }

    private async void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (!_paged || _loadingMore || _initialLoadGeneration == _loadGeneration
            || _items.Count >= _totalCount)
            return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200)
            return;

        if (_localMode)
        {
            var nextItems = _view.Skip(_items.Count).Take(LocalPageSize).ToList();
            if (nextItems.Count > 0)
            {
                foreach (var item in nextItems)
                    _items.Add(item);
                UpdateCount();
            }
            return;
        }

        _loadingMore = true;
        MoreLoading.Show();
        var generation = _loadGeneration;
        var profileKey = _app.ActiveSessionKey ?? "";
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("library",
            $"event=load outcome=start mode=append generation={generation} offset={_items.Count} item={_source.Id:N} type={_source.Type}");
        var userDataRequestId = 0;
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);

            var isDefaultQuery = SelectedSort == ItemSortBy.SortName &&
                                 SelectedOrder == SortOrder.Ascending &&
                                 SelectedFilter == null &&
                                 SelectedGenre == null &&
                                 _letter == null;

            if (isDefaultQuery && _activeFolderCacheKey is not null)
            {
                var entry = await BrowseFolderCache.ReadAsync(_activeFolderCacheKey);
                if (entry is not null && entry.Items.Count > _items.Count)
                {
                    var moreCached = entry.Items.Skip(_items.Count).Take(LocalPageSize).ToList();
                    foreach (var item in ReconcileCachedUserData(moreCached))
                        _items.Add(item);
                    _totalCount = entry.TotalCount;
                    UpdateCount();
                    if (entry.Complete && !entry.Truncated)
                    {
                        _localMode = true;
                        _all = ReconcileCachedUserData(entry.Items);
                        _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
                    }
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var prefetched = await BrowsePrefetcher.WaitForNextPageAsync(profileKey, _source.Id, cts.Token);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;

                if (prefetched is not null && prefetched.Count > _items.Count)
                {
                    var morePrefetched = prefetched.Skip(_items.Count).Take(LocalPageSize).ToList();
                    foreach (var item in ReconcileCachedUserData(morePrefetched))
                        _items.Add(item);
                    UpdateCount();
                    return;
                }
            }

            userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
            var (more, total) = await _app.Jellyfin.GetItemsAsync(_source.Id, _items.Count, PageSize,
                SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter);
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                return;
            _totalCount = total;
            foreach (var item in ReconcileNetworkUserData(more, requestUserDataRevision))
                _items.Add(item);
            UpdateCount();
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total} item={_source.Id:N} type={_source.Type}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} type={_source.Type}", ex);
        }
        finally
        {
            if (userDataRequestId != 0)
                EndUserDataRequest(userDataRequestId);
            _loadingMore = false;
            MoreLoading.Hide();
        }
    }

    private void OnGenresClick(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("library", $"event=interaction action=open-genres item={_source.Id:N} type={_source.Type}");
        GenresRequested?.Invoke(_source);
    }

    private void OnPlayAll(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("library", $"event=interaction action=play-all item={_source.Id:N} type={_source.Type}");
        PlayAllRequested?.Invoke(_source, false);
    }

    private void OnShuffle(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("library", $"event=interaction action=shuffle item={_source.Id:N} type={_source.Type}");
        PlayAllRequested?.Invoke(_source, true);
    }

    private void OnDownloadAll(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("library", $"event=interaction action=download-all item={_source.Id:N} type={_source.Type}");
        DownloadAllRequested?.Invoke(_source);
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("library", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            ItemsGrid.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }

    private void ReplaceItems(IEnumerable<MediaItem> items)
    {
        _items.Clear();
        foreach (var item in items)
            _items.Add(item);
    }

    private string BrowseCacheKey(string? profileKey)
    {
        var queryIdentity = _source.Type switch
        {
            BaseItemDto_Type.Series => $"seasons:{_source.Id:N}",
            BaseItemDto_Type.Season when _source.SeriesId is { } seriesId => $"episodes:{seriesId:N}:{_source.Id:N}",
            _ => $"library:{_source.Id:N}:{SelectedSort}:{SelectedOrder}:{SelectedFilter}:{SelectedGenre}:{_letter}",
        };
        return $"browse:{profileKey}:{queryIdentity}";
    }

    private List<MediaItem> ReconcileCachedUserData(IEnumerable<MediaItem> items)
        => items.Select(item => _userDataOverrides.TryGetValue(item.Id, out var update)
            ? update.Item : item).ToList();

    private List<MediaItem> ReconcileNetworkUserData(IEnumerable<MediaItem> items, int requestRevision)
        => items.Select(item => _userDataOverrides.TryGetValue(item.Id, out var update)
                && update.Revision > requestRevision ? update.Item : item)
            .ToList();

    private int BeginUserDataRequest()
    {
        var requestId = ++_nextUserDataRequestId;
        _activeUserDataRequests[requestId] = _userDataRevision;
        return requestId;
    }

    private void EndUserDataRequest(int requestId)
    {
        _activeUserDataRequests.Remove(requestId);
        foreach (var id in _userDataOverrides.Where(pair =>
                     _activeUserDataRequests.Values.All(snapshot => snapshot >= pair.Value.Revision))
                     .Select(pair => pair.Key).ToList())
            _userDataOverrides.Remove(id);
    }

    private sealed record UserDataOverride(int Revision, MediaItem Item);
}
