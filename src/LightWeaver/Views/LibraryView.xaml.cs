using LightWeaver.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;
using LightWeaver.Settings;

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

    private static (string Label, ItemSortBy Sort)[] SortChoices =>
    [
        (L.Text("LibraryView.Name.25dc8233"), ItemSortBy.SortName),
        (L.Text("LibraryView.DateAdded.b6b2b943"), ItemSortBy.DateCreated),
        (L.Text("LibraryView.PremiereDate.73434a41"), ItemSortBy.PremiereDate),
        (L.Text("LibraryView.CommunityRating.25d902ea"), ItemSortBy.CommunityRating),
        (L.Text("LibraryView.Random.79ca58ae"), ItemSortBy.Random),
    ];

    private static (string Label, ItemFilter? Filter)[] FilterChoices =>
    [
        (L.Text("LibraryView.All.e343cc96"), null),
        (L.Text("LibraryView.Unwatched.cf8a01a1"), ItemFilter.IsUnplayed),
        (L.Text("LibraryView.Watched.3eedb227"), ItemFilter.IsPlayed),
    ];

    private readonly AppViewModel _app;
    private readonly MediaItem _source;
    /// <summary>Whether this page offers the show's Up Next lead: a series or season the session
    /// can form a show key for.</summary>
    private readonly bool _hasShowLead;
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
    /// <summary>The profile <see cref="_activeFolderCacheKey"/> belongs to: the ledger is asked
    /// with it, not with whichever profile is active when a prefetch page lands.</summary>
    private string? _activeFolderProfileKey;
    /// <summary>The <c>CachedAtUtc</c> of the data <see cref="_all"/> came from: the time of its
    /// oldest observation. <see cref="PersistUserData"/> stamps the entry with it, since a write
    /// time would hide a toggle made between that observation and the write from the ledger.</summary>
    private DateTime _allCachedAtUtc;
    /// <summary>The profile's invalidation epoch taken before the data <see cref="_all"/> came from
    /// was read (see <see cref="BrowsePrefetcher.InvalidationEpoch"/>). <see cref="PersistUserData"/>
    /// deletes its write again when a library change's invalidation overlapped since, so a list
    /// from before that delete does not bring the entry back.</summary>
    private int _allInvalidationEpoch;
    /// <summary>Ids <see cref="RemoveItems"/> dropped. A prefetch page already queued for the UI
    /// thread, or an entry read before the edit landed, still lists them; every cache-derived list
    /// goes through <see cref="ReconcileCachedEntry"/>, which filters them out. Kept for the view's
    /// life: an item re-added under the same id shows again from the next server-backed load.</summary>
    private readonly HashSet<Guid> _removedIds = [];
    private bool _initializing = true;
    private int _initialLoadGeneration;
    private string? _letter;   // A–Z filter, exact prefix (Name sort only); null = every letter
    private string? _forcedGenre;   // pre-filter from the genres view, until the combo syncs
    private int _loadGeneration;
    /// <summary>Bumped when a capped refresh's prefix becomes what appends continue (see
    /// <see cref="_refreshedPrefix"/>), whether or not it restarts pagination (see
    /// <see cref="_restore"/>). Every append carries the epoch and the card count it started at,
    /// and is dropped when either has moved: a server page is asked for by count, so one that
    /// lands after the grid changed under it would repeat or skip cards at the boundary.</summary>
    private int _appendEpoch;
    /// <summary>The swept prefix of a capped refresh, for the rest of this load: the default
    /// query's first cards in canonical order, newer than the entry on disk until the refresh's
    /// store lands. Appends continue it before they go to the server, since the old entry still
    /// lists an item the refresh moved or found pushed past the cap at its old place.</summary>
    private (List<MediaItem> Items, DateTime CachedAtUtc)? _refreshedPrefix;
    /// <summary>A pagination restart in progress: a capped refresh found the grid holding cards
    /// past its prefix, showed the prefix alone and is fetching server pages from there, chained
    /// through the normal append, until <see cref="PaginationRestore.Target"/> cards are back. Tied
    /// to the append epoch it bumped.</summary>
    private PaginationRestore? _restore;
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

    /// <summary>The server answered 404 for this page's own container (a series, season or
    /// playlist deleted since the card that opened it was cached). MainWindow evicts it and
    /// leaves the page instead of showing a load failure.</summary>
    public event Action<Guid>? ItemNotFound;

    /// <summary>The container this page lists.</summary>
    public Guid SourceId => _source.Id;

    public LibraryView(AppViewModel app, MediaItem source, string? genre = null, AppSettings? settings = null)
    {
        InitializeComponent();
        LiveText.Watch(this);
        // Wired here, not in XAML: the runtime XAML EventConverter used for custom
        // element types (LwSegmentedControl) needs an exact SelectionChangedEventArgs
        // signature, while OnQueryChanged takes RoutedEventArgs (shared with the
        // sort/order handlers) — C# method-group conversion allows the contravariance.
        FilterBox.SelectionChanged += OnQueryChanged;
        _app = app;
        _source = source;
        _forcedGenre = genre;
        var seriesId = source.Type switch
        {
            BaseItemDto_Type.Series => (Guid?)source.Id,
            BaseItemDto_Type.Season => source.SeriesId,
            _ => null,
        };
        if (settings is not null
            && ShowLeadFlyout.Attach(settings, app.Jellyfin.ServerUrl, app.Jellyfin.UserId, seriesId, UpNextButton))
        {
            _hasShowLead = true;
            UpNextButton.Visibility = Visibility.Visible;
            LiveText.Bind(UpNextButtonText, "Text", UpNextPillText);
            // The content is an icon plus text, so UI Automation no longer derives a name from it.
            LiveText.Bind(UpNextButton, System.Windows.Automation.AutomationProperties.NameProperty, UpNextPillText);
            ShowLeadFlyout.LeadChanged += RefreshUpNext;
            // Series, season and episode pages share the value; whichever was changed last, the
            // others show it when they come back into view.
            IsVisibleChanged += (_, _) => { if (IsVisible) RefreshUpNext(); };
            // Labels change width with the value and the language; re-check the wrap after each.
            foreach (var child in QueueActions.Children.OfType<FrameworkElement>())
                child.SizeChanged += (_, _) => UpdateQueueActionsWrap();
        }
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
            GenreBox.Items.Add(L.Text("LibraryView.AllGenres.470d4399"));
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
        LiveText.Observe(this, OnLanguageChanged);
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
        UpdateQueueActionsWrap();
        UpdateHeaderLayout();
        UpdateRailVisibility();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var initializing = _initializing;
        _initializing = true;
        try
        {
            if (LetterJumpBox.Items.Count > 0)
            {
                var selected = LetterJumpBox.SelectedIndex;
                LetterJumpBox.Items[0] = AnyLetter;
                LetterJumpBox.SelectedIndex = selected;
            }
            if (SortBox.Items.Count > 0) LiveText.ReplaceChoices(SortBox, SortChoices.Select(choice => choice.Label));
            if (FilterBox.Items.Count > 0) LiveText.ReplaceChoices(FilterBox, FilterChoices.Select(choice => choice.Label));
            if (GenreBox.Items.Count > 0)
            {
                var selected = GenreBox.SelectedIndex;
                GenreBox.Items[0] = L.Text("LibraryView.AllGenres.470d4399");
                GenreBox.SelectedIndex = selected;
            }
        }
        finally { _initializing = initializing; }
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
        double left = QueueActions.Visibility == Visibility.Visible ? QueueActionsLineWidth()
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

    /// <summary>Single-line width of the queue actions, from each child's own DesiredSize (which
    /// includes its margin), so it does not depend on whether the WrapPanel has wrapped.</summary>
    private double QueueActionsLineWidth()
    {
        double width = 0;
        foreach (var child in QueueActions.Children)
            if (child is FrameworkElement fe && fe.Visibility != Visibility.Collapsed)
                width += fe.DesiredSize.Width;
        return width;
    }

    /// <summary>When the Up Next pill does not fit after Download all (a long translation at the
    /// 700 px minimum), the WrapPanel moves it to a second line; there it starts flush left with a
    /// gap above instead of the inline gap. Both margins add the same 10 px of width, so the
    /// panel's own wrap and this test agree and neither can flip the other.</summary>
    private void UpdateQueueActionsWrap()
    {
        if (UpNextButton.Visibility != Visibility.Visible || HeaderBar.ActualWidth <= 0)
            return;
        var wrapped = QueueActionsLineWidth() > HeaderBar.ActualWidth;
        UpNextButton.Margin = wrapped ? new Thickness(0, 10, 10, 0) : new Thickness(10, 0, 0, 0);
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
    private static string AnyLetter => L.Text("LibraryView.AnyLetter.fd19bab2");

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

    private void OnPrefetchProgress(string key, int have, int total, bool complete, IReadOnlyList<MediaItem> items,
        DateTime dataAtUtc)
    {
        if (key != _activeFolderCacheKey)
            return;
        // Here, on the run's thread right after its store: a library change that began since then
        // has moved the epoch by the time the list below becomes _all.
        var invalidationEpoch = BrowsePrefetcher.InvalidationEpoch(_activeFolderProfileKey ?? "");

        Dispatcher.BeginInvoke(() =>
        {
            if (key != _activeFolderCacheKey)
                return;

            // The prefetcher's list is cache data like an entry read from disk: a toggle made
            // since its oldest page was fetched is in the ledger, not in the list, and this view's
            // own override for it is gone once no request of its own is in flight.
            if (complete)
            {
                if (_statusKind == StatusKind.Caching)
                    HideStatus();

                if (!_localMode && items.Count > 0)
                {
                    _localMode = true;
                    _all = ReconcileCachedEntry(items, dataAtUtc, _activeFolderProfileKey);
                    _allCachedAtUtc = dataAtUtc;
                    _allInvalidationEpoch = invalidationEpoch;
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
                SetStatus(() => L.Format("LibraryView.CachingOf.7657db40", (have), (total)), active: true, StatusKind.Caching);
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
                        ReplaceItems(ReconcileCachedEntry(items, dataAtUtc, _activeFolderProfileKey).Take(LocalPageSize));
                        UpdateCount();
                        SkeletonHost.Visibility = Visibility.Collapsed;
                        UpdateEmptyState();
                    }
                }
            }
        });
    }

    private enum StatusKind { Other, Caching, Refreshing }
    private StatusKind _statusKind;

    private void SetStatus(Func<string> text, bool active = true, StatusKind kind = StatusKind.Other)
    {
        _statusKind = kind;
        LiveText.Bind(StatusText, "Text", () => text());
        if (StatusDots is not null)
        {
            StatusDots.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Margin = active ? new Thickness(10, 0, 0, 0) : new Thickness(0);
        }
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        _statusKind = StatusKind.Other;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        if (_items.Count == 0)
        {
            if (_letter is { Length: > 0 } active)
                EmptyView.Show(() => L.Format("LibraryView.NothingUnder.ced1a7f3", (active)), () => L.Text("LibraryView.NoTitlesHereStartWithThatLetter.2a76e639"));
            else
                EmptyView.Show(() => L.Text("LibraryView.NothingHere.6a3852d2"), () => L.Text("LibraryView.NoItemsMatchThisViewOrIts.f4014918"));
        }
        else
        {
            EmptyView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>A server-side library change landed and this profile's browse caches have already
    /// been dropped for it: reload from the server if the change touched this view's folder. An
    /// empty <paramref name="folderIds"/> is the blanket case — the batch named items but no
    /// folder, so there is nothing to compare against and every view reloads.
    /// <para>Straight to <see cref="LoadAsync"/>: it bumps the load generation, so a reload that
    /// lands mid-load supersedes rather than races, and the cold path it now takes resets
    /// <c>_localMode</c> for us.</para></summary>
    public void NotifyLibraryChanged(IReadOnlyList<Guid> folderIds)
    {
        if (folderIds.Count > 0 && !folderIds.Contains(_source.Id))
            return;
        Diagnostics.AppLog.Detail("library",
            $"event=library_changed outcome=reload item={_source.Id:N} type={_source.Type}");
        _ = LoadAsync();
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

    /// <summary>Drops items the server no longer has from this page and from the cache entry
    /// behind it, so they are neither on screen now nor painted from the cache on the next visit.
    /// <para>Local mode persists the whole list through the same debounced write a watched
    /// toggle uses — it owns <c>_all</c>. Otherwise the page shows a prefix or a server page and
    /// the entry is edited through <see cref="BrowsePrefetcher.RemoveItemsAsync"/>, which hands the
    /// ids to a run filling the folder; an ordered container's entry is rewritten the same way. A
    /// server load in flight can have been answered before the delete: its response goes through
    /// the shell's removal ledger (<see cref="AppViewModel.DropRemovedItems"/>) by its request's
    /// start before it is stored or shown. Cache-derived lists (an entry, a prefetch page) can
    /// still carry them for a moment, so the ids are remembered and filtered out of those.</para></summary>
    public void RemoveItems(IReadOnlySet<Guid> itemIds)
    {
        _removedIds.UnionWith(itemIds);
        var removed = 0;
        for (var i = _items.Count - 1; i >= 0; i--)
            if (itemIds.Contains(_items[i].Id))
            {
                _items.RemoveAt(i);
                removed++;
            }
        if (_localMode)
        {
            _view.RemoveAll(item => itemIds.Contains(item.Id));
            if (_all.RemoveAll(item => itemIds.Contains(item.Id)) == 0)
                return;
            _totalCount = _view.Count;
            SchedulePersistUserData();
        }
        else
        {
            // Through the prefetcher: a run filling this folder would write its own list back
            // over a direct edit on its next page.
            if (_paged && _activeFolderCacheKey is { } folderKey)
            {
                var profileKey = _app.ActiveSessionKey ?? "";
                _ = Task.Run(() => BrowsePrefetcher.RemoveItemsAsync(profileKey, folderKey, itemIds));
            }
            else if (!_paged)
            {
                var orderedKey = BrowseCacheKey(_app.ActiveSessionKey ?? "");
                _ = Task.Run(() => AppViewModel.RemoveFromBrowseListAsync(orderedKey, itemIds));
            }
            if (removed == 0)
                return;
            _totalCount = Math.Max(_items.Count, _totalCount - removed);
        }
        Diagnostics.AppLog.Detail("library",
            $"event=items_removed count={removed} item={_source.Id:N} type={_source.Type}");
        UpdateCount();
        UpdateEmptyState();
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
        // Not the write time: the list is as old as the data it was built from, plus the toggles
        // applied since. A ledger update newer than that is re-applied on the next render, which
        // is a no-op for the ones already in the list.
        var cachedAtUtc = _allCachedAtUtc;
        var profileKey = _activeFolderProfileKey ?? "";
        var invalidationEpoch = _allInvalidationEpoch;
        Task.Run(async () =>
        {
            // One read-modify-write under the entry's write gate, filtered there against the
            // removals recorded since the list's data was observed.
            var stored = await BrowseFolderCache.UpdateAsync(cacheKey, existing =>
            {
                var complete = existing?.Complete ?? true;
                var truncated = existing?.Truncated ?? false;
                // A whole folder's count is its list's: RemoveItems shrinks the list through here.
                var total = complete && !truncated ? totalCount : existing?.TotalCount ?? totalCount;
                return new FolderCacheEntry<MediaItem>(snapshot, total, complete, truncated, cachedAtUtc,
                    BrowseFolderCache.CurrentSchema);
            }, items => _app.RemovedSince(items, cachedAtUtc, profileKey)).ConfigureAwait(false);
            var total = stored?.TotalCount ?? totalCount;
            // The list predates a library change that deleted this entry since it was read.
            if (BrowsePrefetcher.DropIfInvalidatedSince(profileKey, cacheKey, invalidationEpoch))
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=persist_userdata outcome=dropped reason=invalidated count={snapshot.Count} item={_source.Id:N}");
                return;
            }
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
            LiveText.Bind(CountText, "Text", () => _totalCount == 1 ? L.Text("LibraryView.1Item.2a0ae87a") : L.Format("LibraryView.Items.14975942", (_totalCount)));
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
        ReleaseStaleAppend();
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
                $"event=facet_load outcome=failure facet=genres elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} error={ex.GetType().Name}");
            // no genre filter then — the dropdown just stays at "All genres"
        }
    }

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        _initialLoadGeneration = generation;
        ReleaseStaleAppend();
        _refreshedPrefix = null;
        _restore = null;
        var profileKey = _app.ActiveSessionKey ?? "";
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("library",
            $"event=load outcome=start mode=initial generation={generation} item={_source.Id:N} type={_source.Type}");

        // An album is an ordered container too: the constructor keeps it unsortable because its
        // order is disc/track, and the paged folder path can only show the canonical SortName
        // order — which put an album's tracks alphabetical while Play all queued them by number.
        _paged = _source.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season
            or BaseItemDto_Type.Playlist or BaseItemDto_Type.MusicAlbum);

        if (!_paged)
        {
            await LoadOrderedContainerAsync(generation, profileKey, started);
            return;
        }

        await LoadFolderAsync(generation, profileKey, started);
    }

    private async Task LoadOrderedContainerAsync(int generation, string profileKey, System.Diagnostics.Stopwatch started)
    {
        var cacheKey = BrowseCacheKey(profileKey);
        var cached = await Imaging.MetadataCache.ReadAsync<Imaging.BrowseCacheEntry<MediaItem>>(cacheKey);
        if (cached is not null && cached.Items is null)
        {
            // An entry that deserialised without its list (a foreign or truncated file) is a miss,
            // but a logged one: a cache that keeps missing should be visible in app.log.
            Diagnostics.AppLog.Detail("library",
                $"event=cache_read outcome=corrupt mode=ordered item={_source.Id:N} type={_source.Type}");
            cached = null;
        }
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
            ReplaceItems(ReconcileCachedEntry(cached.Items, cached.CachedAtUtc, profileKey));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            EmptyView.Visibility = Visibility.Collapsed;
            SetStatus(() => L.Text("LibraryView.ShowingCachedResultsWhileRefreshing.a2cf5caa"), active: true, StatusKind.Refreshing);
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
            // The entry's stamp: the oldest data it will hold, so the ledger still re-applies a
            // toggle made while the request was in flight.
            var requestedAtUtc = DateTime.UtcNow;
            List<MediaItem> items;
            if (_source.Type == BaseItemDto_Type.Playlist)
            {
                // Generic folder queries and their id-keyed cache collapse repeated entries
                // and sort by name. A playlist is an ordered list of occurrences instead.
                items = await jf.GetPlaylistItemsAsync(_source.Id);
            }
            else if (_source.Type == BaseItemDto_Type.MusicAlbum)
            {
                // Disc/track order, the same list Play all queues.
                items = await jf.GetAlbumTracksAsync(_source.Id);
            }
            else if (_source.Type == BaseItemDto_Type.Series)
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
            // A removal since the request started is not in the response's past: the item goes
            // before it is stored or shown (see AppViewModel.DropRemovedItems).
            items = ReconcileNetworkUserData(_app.DropRemovedItems(items, requestedAtUtc, profileKey),
                requestUserDataRevision);
            await _app.StoreBrowseListAsync(cacheKey, items, requestedAtUtc, profileKey);
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=stale mode=initial generation={generation} phase=after_store item={_source.Id:N} type={_source.Type}");
                return;
            }
            items = ReconcileNetworkUserData(_app.DropRemovedItems(items, requestedAtUtc, profileKey),
                requestUserDataRevision);
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
                $"event=load outcome=failure mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} type={_source.Type} error={ex.GetType().Name}");
            SkeletonHost.Visibility = Visibility.Collapsed;
            EmptyView.Visibility = Visibility.Collapsed;
            if (JellyfinService.IsNotFound(ex))
            {
                // The container itself is gone from the server. Not "offline": the cached page
                // is a list of things nobody can open any more. The "showing cached results"
                // status goes too: with nothing behind it the page can stay up until MainWindow
                // has somewhere to take the user.
                _ = Task.Run(() => Imaging.MetadataCache.Remove(cacheKey));
                HideStatus();
                ItemNotFound?.Invoke(_source.Id);
            }
            else if (hasCachedPage)
            {
                SetStatus(() => L.Format("LibraryView.OfflineShowingCachedResultsFrom.fc87ef69", (cached!.CachedAtUtc.ToLocalTime())), active: false);
            }
            else
            {
                SetStatus(() => L.Format("LibraryView.FailedToLoad.f9500460", (ex.Message)), active: false);
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
        _activeFolderProfileKey = profileKey;

        var invalidationEpoch = BrowsePrefetcher.InvalidationEpoch(profileKey);
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
            _all = ReconcileCachedEntry(folderEntry.Items, folderEntry.CachedAtUtc, profileKey);
            _allCachedAtUtc = folderEntry.CachedAtUtc;
            _allInvalidationEpoch = invalidationEpoch;
            _view =BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
            _totalCount = _view.Count;
            ReplaceItems(_view.Take(LocalPageSize));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
            SetStatus(() => L.Text("LibraryView.ShowingCachedResultsWhileRefreshing.a2cf5caa"), active: true, StatusKind.Refreshing);
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
            ReplaceItems(ReconcileCachedEntry(folderEntry.Items, folderEntry.CachedAtUtc, profileKey).Take(LocalPageSize));
            UpdateCount();
            SkeletonHost.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
            if (folderEntry.Truncated)
                // Past the cache cap nothing more is being cached: the prefix is all there is,
                // and the sweep started below re-checks it against the server.
                SetStatus(() => L.Text("LibraryView.ShowingCachedResultsWhileRefreshing.a2cf5caa"), active: true, StatusKind.Refreshing);
            else
                SetStatus(() => L.Format("LibraryView.CachingOf.7657db40", (folderEntry.Items.Count), (folderEntry.TotalCount)), active: true, StatusKind.Caching);
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
                var requestedAtUtc = DateTime.UtcNow;
                var (items, serverTotal) = await _app.Jellyfin.GetItemsAsync(_source.Id, 0, PageSize,
                    SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;
                var listed = items.Count;
                items = ReconcileNetworkUserData(_app.DropRemovedItems(items, requestedAtUtc, profileKey),
                    requestUserDataRevision);
                // The server's count still has what was dropped; it no longer lists them.
                _totalCount = serverTotal - (listed - items.Count);
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
                SetStatus(() => L.Format("LibraryView.FailedToLoad.f9500460", (ex.Message)), active: false);
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
            if (folderEntry.Truncated)
            {
                // A truncated entry has no prefetch to complete it and no server page behind it,
                // so without this its prefix was never checked again: items deleted on the server
                // stayed on screen (and in every later visit) for as long as the entry lived.
                var userDataRequestId = BeginUserDataRequest();
                var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
                _ = Task.Run(() => RefreshFolderAsync(folderCacheKey, folderEntry, generation, profileKey,
                    userDataRequestId, requestUserDataRevision, started, local: false));
            }
            if (_initialLoadGeneration == generation)
                _initialLoadGeneration = 0;
        }
    }

    /// <summary>The incremental refresh: identity sweep, diff, refetch, merge, store. With
    /// <paramref name="local"/> the result becomes the local-mode folder; without it the entry is
    /// a truncated prefix (the sweep is capped at the same <see cref="BrowseFolderCache.MaxItems"/>,
    /// so both cover the same first items in canonical order) and only the default-query display
    /// that shows the prefix is corrected. When that display had loaded cards past the prefix,
    /// pagination restarts from it (see <see cref="_restore"/>): which of those cards are still
    /// there, and where, only the server can say once anything in the prefix moved.</summary>
    private async Task RefreshFolderAsync(string cacheKey, FolderCacheEntry<MediaItem> cachedEntry,
        int generation, string profileKey, int userDataRequestId, int requestUserDataRevision,
        System.Diagnostics.Stopwatch started, bool local = true)
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

            // The refreshed entry's stamp: every item in the merge was observed at or after this.
            var sweptAtUtc = DateTime.UtcNow;
            // Before the sweep: a library change that deletes this entry while the sweep or the
            // store is under way must not have the sweep's list written back over the delete.
            var invalidationEpoch = BrowsePrefetcher.InvalidationEpoch(profileKey);
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

            // What gets stored, copied on the UI thread: in local mode the reconciled list becomes
            // _all, which a watched toggle or a removal edits on the UI thread while the store
            // below serialises on this one. A change mid-serialise threw, and the write was lost.
            List<MediaItem>? toStore = null;
            var storedTotal = serverTotal;
            var restoreTarget = 0;
            Dispatcher.Invoke(() =>
            {
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;

                // A removal since the sweep started is newer than anything it listed.
                var current = _app.DropRemovedItems(merged, sweptAtUtc, profileKey);
                storedTotal = serverTotal - (merged.Count - current.Count);
                var reconciled = ReconcileNetworkUserData(current, requestUserDataRevision);
                toStore = reconciled.ToList();
                if (local)
                {
                    _all = reconciled;
                    _allCachedAtUtc = sweptAtUtc;
                    _allInvalidationEpoch = invalidationEpoch;
                    _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
                    _totalCount = _view.Count;
                    UpdateDisplay(_view.Take(Math.Max(_items.Count, LocalPageSize)).ToList());
                }
                else if (!_localMode && IsDefaultQuery)
                {
                    _totalCount = storedTotal;
                    // Appends continue this list from here on, not the entry the store below
                    // replaces.
                    _refreshedPrefix = (reconciled, sweptAtUtc);
                    // An append in flight read the old order, from the entry or a server page, and
                    // is dropped even when the card count comes out the same: a refresh that pushed
                    // one item past the cap and pulled the next one in keeps the count, and that
                    // append would put the pushed item back and shift every later offset by one.
                    var appendDropped = _loadingMore;
                    _appendEpoch++;
                    ReleaseStaleAppend();
                    var loaded = _items.Count;
                    if (loaded > reconciled.Count)
                    {
                        // The cards past the prefix came from server pages read against the old
                        // order. Once anything in the prefix moved, the grid's count is no longer
                        // the server offset of what follows them, so they go and the server is
                        // asked again from the prefix's end. The restart's appends carry the new
                        // epoch.
                        var scroll = _gridScroll ??= WheelScroll.FindScrollViewer(ItemsGrid);
                        UpdateDisplay(reconciled);
                        _restore = new PaginationRestore(_appendEpoch, loaded, scroll?.VerticalOffset ?? 0);
                        restoreTarget = loaded;
                        Diagnostics.AppLog.Detail("library",
                            $"event=pagination_restart offset={reconciled.Count} target={loaded} total={storedTotal} item={_source.Id:N}");
                        TryLoadMore(nearEnd: false);
                    }
                    else
                    {
                        // Within the prefix the display is its first cards, whatever moved.
                        UpdateDisplay(reconciled.Take(Math.Max(loaded, LocalPageSize)).ToList());
                        // The dropped append's scroll asks again, now against the prefix.
                        if (appendDropped)
                            TryLoadMore(NearEnd());
                    }
                }

                UpdateCount();
                UpdateEmptyState();
                if (_statusKind == StatusKind.Refreshing)
                    HideStatus();
            });

            if (toStore is null)
                return;

            var truncated = serverTotal > BrowseFolderCache.MaxItems;
            var updatedEntry = new FolderCacheEntry<MediaItem>(
                toStore,
                storedTotal,
                Complete: !truncated,
                Truncated: truncated,
                sweptAtUtc,
                BrowseFolderCache.CurrentSchema);

            // Filtered against the removal ledger inside the entry's write gate: a removal recorded
            // since the sweep began either edited the entry before this write, which then leaves
            // its items out too, or waits for the gate and edits this write.
            var stored = await BrowseFolderCache.StoreAsync(cacheKey, updatedEntry,
                items => _app.RemovedSince(items, sweptAtUtc, profileKey)).ConfigureAwait(false);
            if (stored.Items.Count != toStore.Count)
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=refiltered mode=incremental count={toStore.Count - stored.Items.Count} item={_source.Id:N}");
            if (BrowsePrefetcher.DropIfInvalidatedSince(profileKey, cacheKey, invalidationEpoch))
            {
                Diagnostics.AppLog.Detail("library",
                    $"event=refresh outcome=dropped mode=incremental reason=invalidated item={_source.Id:N}");
                return;
            }

            Diagnostics.AppLog.Detail("library",
                $"event=refresh outcome=success mode=incremental swept={sweep.Count} total={serverTotal} added={added} removed={removed} changed={changed} refetched={refetched.Count} restore_target={restoreTarget} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N}");
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
                $"event=refresh outcome=failure mode=incremental generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} error={ex.GetType().Name}");

            Dispatcher.Invoke(() =>
            {
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                    return;

                SetStatus(() => L.Format("LibraryView.OfflineShowingCachedResultsFrom.fc87ef69", (cachedEntry.CachedAtUtc.ToLocalTime())), active: false);
            });
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() => EndUserDataRequest(userDataRequestId));
        }
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        // During a pagination restart the offset moves on its own only with a size change: the
        // grid shrinking to the prefix clamps it. A move without one is the user's, and the
        // restart then leaves the offset where they put it.
        if (_restore is { } restore && e.VerticalChange != 0
            && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            restore.UserScrolled = true;
        TryLoadMore(nearEnd: e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 200);
    }

    /// <summary>Whether the grid is scrolled to within 200 px of its end, as
    /// <see cref="OnScroll"/> tests it, for a re-request that has no scroll event to go by.</summary>
    private bool NearEnd()
        => (_gridScroll ??= WheelScroll.FindScrollViewer(ItemsGrid)) is { } scroll
           && scroll.VerticalOffset >= scroll.ExtentHeight - scroll.ViewportHeight - 200;

    /// <summary>Adds the grid's next page when one is due: the user scrolled near the end, or a
    /// pagination restart (<see cref="_restore"/>) has not yet brought back the cards it took
    /// off. A restart that is done, or that no longer applies, ends here.</summary>
    private void TryLoadMore(bool nearEnd)
    {
        if (!_paged || _loadingMore || _initialLoadGeneration == _loadGeneration)
            return;
        if (_restore is { } restore && (restore.Epoch != _appendEpoch || _localMode
            || _items.Count >= Math.Min(restore.Target, _totalCount)))
            FinishRestore();
        if (_items.Count >= _totalCount || (!nearEnd && _restore is null))
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

        _ = AppendPageAsync();
    }

    /// <summary>Ends a pagination restart and puts the grid back at the scroll offset it had
    /// before, as far as the cards now loaded reach. A user who scrolled meanwhile, either way, is
    /// left where they are. At Loaded priority, so the cards the restart added are laid out and the
    /// offset is not clamped to the shorter grid.</summary>
    private void FinishRestore()
    {
        if (_restore is not { } restore)
            return;
        _restore = null;
        Diagnostics.AppLog.Detail("library",
            $"event=pagination_restart outcome=done count={_items.Count} target={restore.Target} user_scrolled={restore.UserScrolled} item={_source.Id:N}");
        if (restore.UserScrolled)
            return;
        var epoch = _appendEpoch;
        var generation = _loadGeneration;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (epoch != _appendEpoch || generation != _loadGeneration)
                return;
            if ((_gridScroll ??= WheelScroll.FindScrollViewer(ItemsGrid)) is { } scroll
                && scroll.VerticalOffset < restore.ScrollOffset)
                scroll.ScrollToVerticalOffset(restore.ScrollOffset);
        });
    }

    private enum AppendOutcome { Added, Empty, Moved, Stale, Failed }

    /// <summary>One append: the next cached cards of the default query, or the next server page
    /// at the grid's count. It carries the load generation, the append epoch and the count it
    /// started at, and lands only if none of them moved. One whose count moved (a removal took
    /// cards off the grid meanwhile) is asked again at the new count; one whose generation or
    /// epoch moved belongs to a query or a pagination that no longer exists, and whatever moved
    /// them released its flag. A pagination restart chains through here, one page at a time.</summary>
    private async Task AppendPageAsync()
    {
        _loadingMore = true;
        MoreLoading.Show();
        var generation = _loadGeneration;
        var appendEpoch = _appendEpoch;
        var offset = _items.Count;
        var profileKey = _app.ActiveSessionKey ?? "";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var outcome = AppendOutcome.Failed;
        Diagnostics.AppLog.Detail("library",
            $"event=load outcome=start mode=append generation={generation} offset={offset} item={_source.Id:N} type={_source.Type}");
        AppendOutcome? Superseded() =>
            generation != _loadGeneration || appendEpoch != _appendEpoch || profileKey != _app.ActiveSessionKey
                ? AppendOutcome.Stale
                : offset != _items.Count ? AppendOutcome.Moved : null;
        var userDataRequestId = 0;
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            if (Superseded() is { } afterDelay)
            {
                outcome = afterDelay;
                return;
            }

            if (IsDefaultQuery && _activeFolderCacheKey is not null)
            {
                if (_refreshedPrefix is { } prefix)
                {
                    // Past the prefix, a truncated folder has no run to wait for: straight to the
                    // server below.
                    if (NextCachedPage(prefix.Items, prefix.CachedAtUtc, profileKey) is { Count: > 0 } fromPrefix)
                    {
                        foreach (var item in fromPrefix)
                            _items.Add(item);
                        UpdateCount();
                        outcome = AppendOutcome.Added;
                        return;
                    }
                }
                else
                {
                    var invalidationEpoch = BrowsePrefetcher.InvalidationEpoch(profileKey);
                    var entry = await BrowseFolderCache.ReadAsync(_activeFolderCacheKey);
                    // The cached prefix answers only the default query; one changed while the read
                    // was in flight must not get default-order items appended to its own list.
                    if (Superseded() is { } afterRead)
                    {
                        outcome = afterRead;
                        return;
                    }
                    if (entry is not null && NextCachedPage(entry.Items, entry.CachedAtUtc, profileKey) is { Count: > 0 } moreCached)
                    {
                        foreach (var item in moreCached)
                            _items.Add(item);
                        _totalCount = entry.TotalCount;
                        UpdateCount();
                        if (entry.Complete && !entry.Truncated)
                        {
                            _localMode = true;
                            _all = ReconcileCachedEntry(entry.Items, entry.CachedAtUtc, profileKey);
                            _allCachedAtUtc = entry.CachedAtUtc;
                            _allInvalidationEpoch = invalidationEpoch;
                            _view = BrowseFolderCache.ApplyQuery(_all, SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter, _randomSeed);
                        }
                        outcome = AppendOutcome.Added;
                        return;
                    }

                    // Past a truncated entry's cached part nothing is being cached, so there is no
                    // page to wait for: straight to the server. The prefetcher also answers at once
                    // when no run exists for the folder.
                    if (entry is not { Truncated: true })
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var prefetched = await BrowsePrefetcher.WaitForNextPageAsync(profileKey, _source.Id, cts.Token);
                        if (Superseded() is { } afterWait)
                        {
                            outcome = afterWait;
                            return;
                        }

                        if (prefetched is { Items: var prefetchedItems, DataAtUtc: var prefetchedAtUtc }
                            && NextCachedPage(prefetchedItems, prefetchedAtUtc, profileKey) is { Count: > 0 } morePrefetched)
                        {
                            foreach (var item in morePrefetched)
                                _items.Add(item);
                            UpdateCount();
                            outcome = AppendOutcome.Added;
                            return;
                        }
                    }
                }
            }

            userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
            var requestedAtUtc = DateTime.UtcNow;
            var (more, total) = await _app.Jellyfin.GetItemsAsync(_source.Id, offset, PageSize,
                SelectedSort, SelectedOrder, SelectedFilter, SelectedGenre, _letter);
            if (Superseded() is { } afterPage)
            {
                outcome = afterPage;
                return;
            }
            var listed = more.Count;
            more = _app.DropRemovedItems(more, requestedAtUtc, profileKey);
            _totalCount = total - (listed - more.Count);
            // The offset is a count, so an item the server added before it since the last page
            // shifts this page back by one and repeats the boundary card. Paged folders hold no
            // intended repeats (playlists are not paged), so a repeat is always that.
            var shown = new HashSet<Guid>(_items.Select(item => item.Id));
            var added = 0;
            foreach (var item in ReconcileNetworkUserData(more, requestUserDataRevision))
                if (shown.Add(item.Id))
                {
                    _items.Add(item);
                    added++;
                }
            UpdateCount();
            outcome = added > 0 ? AppendOutcome.Added : AppendOutcome.Empty;
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total} item={_source.Id:N} type={_source.Type}");
        }
        catch (Exception ex)
        {
            outcome = AppendOutcome.Failed;
            Diagnostics.AppLog.Detail("library",
                $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} item={_source.Id:N} type={_source.Type} error={ex.GetType().Name}");
        }
        finally
        {
            if (userDataRequestId != 0)
                EndUserDataRequest(userDataRequestId);
            if (outcome is AppendOutcome.Moved or AppendOutcome.Stale)
                Diagnostics.AppLog.Detail("library",
                    $"event=load outcome=stale mode=append generation={generation} reason={(outcome == AppendOutcome.Moved ? "count_moved" : "superseded")} offset={offset} count={_items.Count} item={_source.Id:N}");
            // Only the append of the CURRENT load and pagination owns the flag. A superseded one
            // was already released by the bump, and the flag may belong to a newer append by now.
            if (generation == _loadGeneration && appendEpoch == _appendEpoch)
            {
                _loadingMore = false;
                MoreLoading.Hide();
                if (outcome == AppendOutcome.Moved)
                    TryLoadMore(NearEnd());
                else if (outcome == AppendOutcome.Added)
                    TryLoadMore(nearEnd: false);
                else if (outcome != AppendOutcome.Stale)
                    // Nothing came back, or the request failed: a restart stops where it got to,
                    // and the next scroll carries on.
                    FinishRestore();
            }
        }
    }

    /// <summary>A new load supersedes any append in flight: its page is dropped as stale when it
    /// lands, so it must not keep <c>_loadingMore</c> set until then and block the new query's
    /// first append for as long as the old request takes.</summary>
    private void ReleaseStaleAppend()
    {
        _loadingMore = false;
        MoreLoading.Hide();
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

    private void OnUpNextClick(object sender, RoutedEventArgs e)
    {
        if (!_hasShowLead) return;
        if (ShowLeadFlyout.IsOpen)
        {
            ShowLeadFlyout.Close();
            return;
        }
        Diagnostics.AppLog.Detail("library", $"event=interaction action=up-next-flyout item={_source.Id:N} type={_source.Type}");
        RefreshUpNext();
        ShowLeadFlyout.Open();
    }

    private void RefreshUpNext()
    {
        UpNextButtonText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        UpNextButton.GetBindingExpression(System.Windows.Automation.AutomationProperties.NameProperty)?.UpdateTarget();
        ShowLeadFlyout.Refresh();
    }

    private object UpNextPillText()
    {
        if (!_hasShowLead) return "";
        var value = ShowLeadFlyout.ShowLead is { } seconds
            ? L.Format("UpNextLeadPicker.SecondsShort", seconds)
            : L.Text("UpNextLeadPicker.Global");
        return L.Format("LibraryView.UpNextPill", value);
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

    /// <summary>Moves the grid to <paramref name="nextDisplay"/>. When the cards both lists share
    /// have the same ids, their instances are swapped in place and only the end is trimmed or
    /// extended, so the cards above stay put (no scroll jump, no re-realised containers); a full
    /// replace when the refresh added, removed or reordered anything among them.</summary>
    private void UpdateDisplay(List<MediaItem> nextDisplay)
    {
        var shared = Math.Min(_items.Count, nextDisplay.Count);
        for (var i = 0; i < shared; i++)
        {
            if (_items[i].Id != nextDisplay[i].Id)
            {
                ReplaceItems(nextDisplay);
                return;
            }
        }

        for (var i = 0; i < shared; i++)
        {
            if (!ReferenceEquals(_items[i], nextDisplay[i]))
                _items[i] = nextDisplay[i];
        }
        while (_items.Count > nextDisplay.Count)
            _items.RemoveAt(_items.Count - 1);
        for (var i = shared; i < nextDisplay.Count; i++)
            _items.Add(nextDisplay[i]);
    }

    /// <summary>See <see cref="_restore"/>. <paramref name="ScrollOffset"/> is the grid's vertical
    /// offset when the restart began, put back once the cards are unless
    /// <see cref="UserScrolled"/>: the user moved the grid while it ran.</summary>
    private sealed record PaginationRestore(int Epoch, int Target, double ScrollOffset)
    {
        public bool UserScrolled { get; set; }
    }

    /// <summary>Name sort, ascending, nothing filtered: the only query a cached prefix (which is
    /// in the canonical order) can answer.</summary>
    private bool IsDefaultQuery => SelectedSort == ItemSortBy.SortName &&
                                   SelectedOrder == SortOrder.Ascending &&
                                   SelectedFilter == null &&
                                   SelectedGenre == null &&
                                   _letter == null;

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
            BaseItemDto_Type.Playlist => $"playlist-entries:{_source.Id:N}:v1",
            BaseItemDto_Type.MusicAlbum => $"album-tracks:{_source.Id:N}:v1",
            BaseItemDto_Type.Season when _source.SeriesId is { } seriesId => $"episodes:{seriesId:N}:{_source.Id:N}",
            _ => $"library:{_source.Id:N}:{SelectedSort}:{SelectedOrder}:{SelectedFilter}:{SelectedGenre}:{_letter}",
        };
        return $"browse:{profileKey}:{queryIdentity}";
    }

    /// <summary>A list read from the disk cache: items this view has dropped as removed left out,
    /// and items the shell's removal ledger names after the entry was written, then the shell's
    /// user-data ledger (updates fanned out before this view existed, and newer than the entry),
    /// then this view's own overrides.</summary>
    private List<MediaItem> ReconcileCachedEntry(IEnumerable<MediaItem> items, DateTime cachedAtUtc,
        string? profileKey)
        => ReconcileCachedUserData(_app.ApplyUserDataLedger(
            _app.DropRemovedItems(
                _removedIds.Count == 0 ? items : items.Where(item => !_removedIds.Contains(item.Id)),
                cachedAtUtc, profileKey),
            cachedAtUtc, profileKey));

    /// <summary>The next cards from a cached list the grid shows a prefix of (the default query, in
    /// canonical order): the first items not already on screen, matched by id. A count skip was off
    /// by one whenever the list and the grid disagreed about a removed item — still in the list but
    /// gone from the grid repeated the boundary card, the reverse skipped a real one.</summary>
    private List<MediaItem> NextCachedPage(IReadOnlyList<MediaItem> cached, DateTime cachedAtUtc, string profileKey)
    {
        var shown = new HashSet<Guid>(_items.Select(item => item.Id));
        return ReconcileCachedEntry(
            cached.Where(item => !shown.Contains(item.Id) && !_removedIds.Contains(item.Id)).Take(LocalPageSize),
            cachedAtUtc, profileKey);
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
