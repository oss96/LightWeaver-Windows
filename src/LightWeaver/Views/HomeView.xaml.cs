using LightWeaver.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LightWeaver.Jellyfin;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Views;

public partial class HomeView : UserControl
{
    private readonly AppViewModel _app;

    // Per-profile rail order + toggles (Phase 7 M11); loaded per reveal, saved on edit.
    private List<HomeSectionPref> _layout = [];
    private string _profileKey = "";
    private List<MediaItem>? _libraries;   // cached: Latest needs ids even if the rail is off
    private Popup? _customizePopup;
    private bool _loadedOnce;              // gates the on-reveal refresh until the first load settled
    private bool _refreshing;              // reentrancy guard for rapid reveal flips
    private bool _refreshPending;          // a library change that arrived mid-refresh (see NotifyLibraryChanged)
    private readonly Dictionary<Guid, PendingAdvance> _pendingAdvances = [];
    private int _nextUpRowGeneration;

    /// <summary>Raised when the user picks an item in any row.</summary>
    public event Action<MediaItem>? ItemSelected;

    /// <summary>Raised when a section header's see-all link is clicked (Phase 7).
    /// MainWindow opens the full-screen SectionView for the section.</summary>
    public event Action<HomeSectionId>? SeeAllRequested;

    public HomeView(AppViewModel app)
    {
        InitializeComponent();
        LiveText.Watch(this);
        // M25: structured skeletons instead of a bare "Loading..." label.
        SkeletonHost.Children.Add(SkeletonFactory.Rail(this, 4, landscape: true, "HomeSkeleton"));
        SkeletonHost.Children.Add(SkeletonFactory.Rail(this, 7, landscape: false, "HomeSkeleton2"));
        _app = app;
        Loaded += async (_, _) => await LoadAsync();
        // Home is created once and re-shown (never re-Loaded) on return from the player
        // or a library/detail view, so its rows go stale. Re-fetch the watching-driven
        // rows each reveal, exactly as ItemDetailView refreshes on IsVisibleChanged.
        IsVisibleChanged += OnIsVisibleChanged;

        // The horizontal rows' internal ScrollViewers swallow the vertical mouse
        // wheel (they have no vertical extent), leaving the page stuck. Forward
        // plain wheel input to the page; Shift+wheel still scrolls a row sideways.
        foreach (var row in new[] { ResumeRow, NextUpRow, LatestRow, FavoritesRow, LibrariesRow })
            row.PreviewMouseWheel += OnRowPreviewMouseWheel;
    }

    private void OnRowPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && sender is ListBox row
            && FindScrollViewer(row) is { } rowScroll)
        {
            rowScroll.ScrollToHorizontalOffset(rowScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }

    private (StackPanel Section, ListBox Row) Ui(HomeSectionId id) => id switch
    {
        HomeSectionId.Resume => (ResumeSection, ResumeRow),
        HomeSectionId.NextUp => (NextUpSection, NextUpRow),
        HomeSectionId.Latest => (LatestSection, LatestRow),
        HomeSectionId.Favorites => (FavoritesSection, FavoritesRow),
        HomeSectionId.Libraries => (LibrariesSection, LibrariesRow),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static string DisplayName(HomeSectionId id) => id switch
    {
        HomeSectionId.Resume => L.Text("HomeView.ContinueWatching.be7224b6"),
        HomeSectionId.NextUp => L.Text("HomeView.NextUp.6c2092fe"),
        HomeSectionId.Latest => L.Text("HomeView.RecentlyAdded.8a99c3ce"),
        HomeSectionId.Favorites => L.Text("HomeView.Favorites.5b6dab09"),
        HomeSectionId.Libraries => L.Text("HomeView.Libraries.0e9d6add"),
        _ => id.ToString(),
    };

    private bool IsOn(HomeSectionId id) => _layout.FirstOrDefault(p => p.Id == id)?.Enabled ?? true;

    private async Task LoadAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("home", "event=load outcome=start mode=initial");
        try
        {
            if (ResumeRow.ItemsSource is null && NextUpRow.ItemsSource is null)
                SkeletonHost.Visibility = Visibility.Visible;   // first load only
            // Test hook (see LibraryView): makes the skeleton observable on fast LANs.
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            _profileKey = HomeLayoutStore.ProfileKey(_app.Jellyfin.UserId, _app.Jellyfin.ServerUrl);
            _layout = [.. HomeLayoutStore.Load(_profileKey).Where(p => p.Id != HomeSectionId.Downloads)];
            ApplyOrder();

            var jf = _app.Jellyfin;
            var profileKey = _app.ActiveSessionKey;
            // A disabled rail is NOT fetched (saves the round-trip). Latest still needs
            // the library ids even when the Libraries rail itself is switched off.
            var requestedAtUtc = DateTime.UtcNow;
            var librariesTask = IsOn(HomeSectionId.Libraries) || IsOn(HomeSectionId.Latest)
                ? jf.GetLibrariesAsync() : null;
            var resumeTask = IsOn(HomeSectionId.Resume) ? jf.GetResumeItemsAsync() : null;
            var nextUpTask = IsOn(HomeSectionId.NextUp) ? jf.GetNextUpAsync() : null;
            await Task.WhenAll(new Task?[] { librariesTask, resumeTask, nextUpTask }.OfType<Task>());

            _libraries = librariesTask?.Result;
            if (resumeTask is not null)
                FillRow(ResumeSection, ResumeRow,
                    ReconcileRow(HomeSectionId.Resume, resumeTask.Result, requestedAtUtc, profileKey));
            if (nextUpTask is not null)
                FillRow(NextUpSection, NextUpRow,
                    ReconcileRow(HomeSectionId.NextUp, nextUpTask.Result, requestedAtUtc, profileKey));
            if (IsOn(HomeSectionId.Libraries) && _libraries is not null)
                FillRow(LibrariesSection, LibrariesRow, _libraries);

            SkeletonHost.Visibility = Visibility.Collapsed;

            // Below the fold: latest (needs the library ids), then favorites.
            if (IsOn(HomeSectionId.Latest) && _libraries is { } libraries)
                FillRow(LatestSection, LatestRow, await FetchRowAsync(HomeSectionId.Latest,
                    () => jf.GetLatestAsync(libraries.Select(l => l.Id)), profileKey));
            if (IsOn(HomeSectionId.Favorites))
                FillRow(FavoritesSection, FavoritesRow,
                    await FetchRowAsync(HomeSectionId.Favorites, () => jf.GetFavoritesAsync(), profileKey));
            var count = new[] { ResumeRow, NextUpRow, LatestRow, FavoritesRow, LibrariesRow }
                .Sum(row => row.Items.Count);
            Diagnostics.AppLog.Detail("home",
                $"event=load outcome=success mode=initial elapsed_ms={started.ElapsedMilliseconds} count={count} sections={_layout.Count(p => p.Enabled)}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("home",
                $"event=load outcome=failure mode=initial elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            SkeletonHost.Visibility = Visibility.Collapsed;
            LiveText.Bind(ErrorText, "Text", () => L.Format("HomeView.FailedToLoadHomeScreen.b18674b7", (ex.Message)));
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            // Arm the on-reveal refresh only after the first load has settled, so the
            // initial reveal (which fires IsVisibleChanged before this completes) can't
            // race the load below with a redundant fetch.
            _loadedOnce = true;
        }
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _loadedOnce && !_refreshing)
            await RefreshDynamicRowsAsync();
    }

    /// <summary>A server-side library change landed: re-fetch the watching-driven rows now rather
    /// than at the next reveal, so an item deleted on the server stops being offered here. Same
    /// path as the on-reveal refresh, plus Favorites: the reveal skips it because it changes
    /// rarely, but a deleted favorite is exactly the change this is for, and the row would
    /// otherwise keep offering it until the next full Home load.
    /// <para>One that arrives mid-refresh is REMEMBERED, not dropped. A server scan emits a batch
    /// every thirty seconds or so, which is the same order as a refresh takes on a slow LAN — so
    /// the batch that lands while the rows are in flight is a likely one, and its rows may have
    /// been fetched before the change. The flag is cleared before the re-run, so a change during
    /// THAT run books one more and no further.</para></summary>
    public void NotifyLibraryChanged()
    {
        if (!_loadedOnce)
            return;
        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }
        _ = RefreshDynamicRowsAsync(includeFavorites: true);
    }

    /// <summary>Drops items the server no longer has from every row. Home keeps no cache of its
    /// own, so this is only the screen; the next refresh asks the server again anyway. A row fetch
    /// already in flight goes through the removal ledger in <see cref="ReconcileRow"/>.</summary>
    public void RemoveItems(IReadOnlySet<Guid> itemIds)
    {
        foreach (var pref in _layout)
        {
            var (section, row) = Ui(pref.Id);
            if (row.ItemsSource is not ObservableCollection<MediaItem> items)
                continue;
            var removed = false;
            for (var i = items.Count - 1; i >= 0; i--)
                if (itemIds.Contains(items[i].Id))
                {
                    items.RemoveAt(i);
                    removed = true;
                }
            if (removed && items.Count == 0)
                section.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-fetches the rows that change from watching — Continue Watching, Next Up,
    /// Recently Added — each time Home is re-shown (back from the player or a library/detail
    /// view), so watched items drop out, resume positions advance, Next Up rolls to the next
    /// episode, and server-side additions appear. Mirrors ItemDetailView: await the in-flight
    /// stop report first (capped) so the GET can't outrun the fire-and-forget stop POST.
    /// No skeletons and the page keeps its scroll offset — FillRow only swaps row items.
    /// Favorites/Libraries are left as-is on a reveal (they change rarely); a library change asks
    /// for Favorites too. Disabled rails are skipped.</summary>
    private async Task RefreshDynamicRowsAsync(bool includeFavorites = false)
    {
        _refreshing = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("home", "event=load outcome=start mode=refresh");
        try
        {
            if (_app.PendingStopReport is { } pending)
            {
                await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
                _app.PendingStopReport = null;
            }
            var jf = _app.Jellyfin;
            var profileKey = _app.ActiveSessionKey;
            if (IsOn(HomeSectionId.Resume))
                FillRow(ResumeSection, ResumeRow,
                    await FetchRowAsync(HomeSectionId.Resume, () => jf.GetResumeItemsAsync(), profileKey));
            if (IsOn(HomeSectionId.NextUp))
                FillRow(NextUpSection, NextUpRow,
                    await FetchRowAsync(HomeSectionId.NextUp, () => jf.GetNextUpAsync(), profileKey));
            if (IsOn(HomeSectionId.Latest))
            {
                _libraries ??= await jf.GetLibrariesAsync();
                var libraryIds = _libraries.Select(l => l.Id).ToList();
                FillRow(LatestSection, LatestRow,
                    await FetchRowAsync(HomeSectionId.Latest, () => jf.GetLatestAsync(libraryIds), profileKey));
            }
            if (includeFavorites && IsOn(HomeSectionId.Favorites))
                FillRow(FavoritesSection, FavoritesRow,
                    await FetchRowAsync(HomeSectionId.Favorites, () => jf.GetFavoritesAsync(), profileKey));
            var count = ResumeRow.Items.Count + NextUpRow.Items.Count + LatestRow.Items.Count;
            Diagnostics.AppLog.Detail("home",
                $"event=load outcome=success mode=refresh elapsed_ms={started.ElapsedMilliseconds} count={count}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("home",
                $"event=load outcome=failure mode=refresh elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            // Keep whatever the rows already show; the next reveal retries.
        }
        finally
        {
            _refreshing = false;
        }

        if (_refreshPending)
        {
            // Only NotifyLibraryChanged books one, so the re-run is a library-change refresh.
            _refreshPending = false;
            await RefreshDynamicRowsAsync(includeFavorites: true);
        }
    }

    /// <summary>Reorders the section hosts to the layout and hides disabled ones.
    /// Enabled sections stay collapsed until their row has items (FillRow).</summary>
    private void ApplyOrder()
    {
        SectionsHost.Children.Clear();
        foreach (var pref in _layout)
        {
            var (section, row) = Ui(pref.Id);
            SectionsHost.Children.Add(section);
            if (!pref.Enabled)
                section.Visibility = Visibility.Collapsed;
            else if (row.ItemsSource is ObservableCollection<MediaItem> { Count: > 0 })
                section.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Fetches one row and reconciles it with the user-data updates made while the request
    /// was in flight (see <see cref="ReconcileRow"/>).</summary>
    private async Task<(List<MediaItem> Items, List<(Guid SeriesId, int Slot)> Rolls)> FetchRowAsync(
        HomeSectionId id, Func<Task<List<MediaItem>>> fetch, string? profileKey)
    {
        var requestedAtUtc = DateTime.UtcNow;
        var items = await fetch();
        return ReconcileRow(id, items, requestedAtUtc, profileKey);
    }

    /// <summary>A row fetched from the server, brought up to date before it replaces the one on
    /// screen. A request that started before a toggle can land after it: the toggle's fan-out
    /// already changed the old row, and the stale response would put the old state back — on a
    /// library scan, whose change batches refresh Favorites about every 30 s, an un-favorited item
    /// came straight back. The shell's user-data ledger (the store the library and detail views
    /// apply to cached lists) holds every update with its time; the ones newer than the request's
    /// start are applied, and an item they changed leaves a row it no longer belongs in, by the
    /// same rules as <see cref="ApplyUserDataUpdate"/>. A Next Up episode watched meanwhile rolls
    /// to the series' next episode the same way too, in the slot it leaves: those rolls are
    /// returned, and the FillRow that installs the row queues them.</summary>
    private (List<MediaItem> Items, List<(Guid SeriesId, int Slot)> Rolls) ReconcileRow(HomeSectionId id,
        List<MediaItem> fetched, DateTime requestedAtUtc, string? profileKey)
    {
        // An item removed since the request started was already dropped from the row on screen;
        // the removal ledger keeps the response from putting it back.
        fetched = _app.DropRemovedItems(fetched, requestedAtUtc, profileKey);
        // ApplyUserDataLedger returns the input instance wherever nothing applied.
        var applied = _app.ApplyUserDataLedger(fetched, requestedAtUtc, profileKey);
        var kept = new List<MediaItem>(applied.Count);
        List<(Guid SeriesId, int Slot)> rolls = [];
        for (var i = 0; i < applied.Count; i++)
        {
            if (ReferenceEquals(applied[i], fetched[i]) || BelongsInRow(id, applied[i]))
                kept.Add(applied[i]);
            else if (id == HomeSectionId.NextUp && applied[i].Played && applied[i].SeriesId is { } seriesId)
                rolls.Add((seriesId, kept.Count));
        }
        return (kept, rolls);
    }

    private static bool BelongsInRow(HomeSectionId id, MediaItem item) => id switch
    {
        HomeSectionId.Resume => !item.Played && item.ResumePositionTicks != 0,
        HomeSectionId.NextUp or HomeSectionId.Latest => !item.Played,
        // Watched and favorite are independent: only an un-favorited item leaves.
        HomeSectionId.Favorites => item.IsFavorite,
        _ => true,
    };

    private void FillRow(StackPanel section, ListBox row, List<MediaItem> items)
    {
        // ObservableCollection so ApplyUserDataUpdate's in-place swap re-renders the card
        // (a plain List gives no CollectionChanged and the badge would go stale).
        row.ItemsSource = new ObservableCollection<MediaItem>(items);
        section.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(row, NextUpRow))
            _nextUpRowGeneration++;
    }

    /// <summary>Installs a reconciled row, then queues its Next Up rolls against the generation
    /// this fill produced. Queued any earlier, they guessed that generation, and an update that
    /// replaced the row in the await before the fill moved it past the guess and dropped them.</summary>
    private void FillRow(StackPanel section, ListBox row,
        (List<MediaItem> Items, List<(Guid SeriesId, int Slot)> Rolls) reconciled)
    {
        FillRow(section, row, reconciled.Items);
        foreach (var (seriesId, slot) in reconciled.Rolls)
            QueueNextUpAdvance(seriesId, slot, _nextUpRowGeneration);
    }

    /// <summary>Swap a refreshed item into whichever rows hold it so card badges follow
    /// watched/favorite changes made elsewhere (detail view or the card's own action bar).
    /// Clears completed items from Continue Watching and Recently Added, and rolls Next Up
    /// to the next episode for TV shows. Favorites follow the server's own rule: the row is the
    /// favorited items, watched or not.
    /// MediaItem is immutable, so swap instances.</summary>
    public void ApplyUserDataUpdate(MediaItem fresh)
    {
        int? nextUpReplaceIndex = null;
        int? nextUpRowGeneration = null;
        Guid? seriesToAdvance = null;
        // 1. Continue Watching: if played or resume ticks == 0, remove it
        if (ResumeRow.ItemsSource is ObservableCollection<MediaItem> resumeItems)
        {
            for (var i = 0; i < resumeItems.Count; i++)
            {
                if (resumeItems[i].Id == fresh.Id)
                {
                    if (fresh.Played || fresh.ResumePositionTicks == 0)
                    {
                        resumeItems.RemoveAt(i);
                        if (resumeItems.Count == 0 && IsOn(HomeSectionId.Resume))
                            ResumeSection.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        resumeItems[i] = fresh;
                    }
                    break;
                }
            }
        }

        // 2. Recently Added: remove if played
        if (LatestRow.ItemsSource is ObservableCollection<MediaItem> latestItems)
        {
            for (var i = 0; i < latestItems.Count; i++)
            {
                if (latestItems[i].Id == fresh.Id)
                {
                    if (fresh.Played)
                    {
                        latestItems.RemoveAt(i);
                        if (latestItems.Count == 0 && IsOn(HomeSectionId.Latest))
                            LatestSection.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        latestItems[i] = fresh;
                    }
                    break;
                }
            }
        }

        // 3. Next Up: remove if played, and for a TV show roll to the next episode
        if (NextUpRow.ItemsSource is ObservableCollection<MediaItem> nextUpItems)
        {
            for (var i = 0; i < nextUpItems.Count; i++)
            {
                if (nextUpItems[i].Id == fresh.Id)
                {
                    if (fresh.Played)
                    {
                        var replaceIndex = i;
                        nextUpItems.RemoveAt(i);
                        if (nextUpItems.Count == 0 && IsOn(HomeSectionId.NextUp))
                            NextUpSection.Visibility = Visibility.Collapsed;
                        if (fresh.SeriesId is { } sourceSeriesId)
                        {
                            seriesToAdvance = sourceSeriesId;
                            nextUpReplaceIndex = replaceIndex;
                            nextUpRowGeneration = ++_nextUpRowGeneration;
                        }
                    }
                    else
                    {
                        nextUpItems[i] = fresh;
                    }
                    break;
                }
            }
        }

        // 4. Favorites: favorite and watched are independent. The row is what the server lists
        // for IsFavorite, and the server keeps a watched favorite favorited; removing it here
        // (the old Next Up-style rule) only had it reappear on the next full Home load. So a
        // watched favorite stays, with its badge, and only an un-favorited one leaves.
        if (FavoritesRow.ItemsSource is ObservableCollection<MediaItem> favItems)
        {
            for (var i = 0; i < favItems.Count; i++)
            {
                if (favItems[i].Id == fresh.Id)
                {
                    if (!fresh.IsFavorite)
                    {
                        favItems.RemoveAt(i);
                        if (favItems.Count == 0 && IsOn(HomeSectionId.Favorites))
                            FavoritesSection.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        favItems[i] = fresh;
                    }
                    break;
                }
            }
        }
        if (seriesToAdvance is { } seriesId && nextUpReplaceIndex is { } replaceAt)
            QueueNextUpAdvance(seriesId, replaceAt, nextUpRowGeneration);
    }

    private void QueueNextUpAdvance(Guid seriesId, int nextUpReplaceIndex, int? nextUpRowGeneration)
    {
        var profileKey = _app.ActiveSessionKey;
        if (profileKey is null || _profileKey != profileKey)
            return;

        if (_pendingAdvances.TryGetValue(seriesId, out var pending)
            && pending.ProfileKey == profileKey)
        {
            pending.Merge(nextUpReplaceIndex, nextUpRowGeneration);
            return;
        }
        // A fetch belonging to a prior profile may still be unwinding. Replace its entry so
        // its finally cannot remove the current profile's pending advancement.
        _pendingAdvances.Remove(seriesId);
        pending = new PendingAdvance(nextUpReplaceIndex, nextUpRowGeneration, profileKey);
        _pendingAdvances.Add(seriesId, pending);
        AdvanceNextUpAsync(seriesId, pending);
    }

    private async void AdvanceNextUpAsync(Guid seriesId, PendingAdvance pending)
    {
        var profileKey = pending.ProfileKey;
        try
        {
            while (IsCurrentAdvance(seriesId, pending, profileKey))
            {
                var version = pending.Version;
                var requestedAtUtc = DateTime.UtcNow;
                var nextUpList = await _app.Jellyfin.GetNextUpAsync(24);
                // A later completion can change both the target slot and the episode the
                // server returns. Refetch rather than applying this superseded response.
                if (!IsCurrentAdvance(seriesId, pending, profileKey))
                    return;
                if (version != pending.Version)
                    continue;

                var nextEp = _app.DropRemovedItems(nextUpList, requestedAtUtc, profileKey)
                    .FirstOrDefault(e => e.SeriesId == seriesId);
                if (nextEp is null)
                    return;
                if (pending.NextUpRowGeneration == _nextUpRowGeneration
                    && NextUpRow.ItemsSource is ObservableCollection<MediaItem> nextUpItems
                    && !nextUpItems.Any(item => item.Id == nextEp.Id))
                {
                    var insertPos = Math.Min(pending.NextUpReplaceIndex, nextUpItems.Count);
                    nextUpItems.Insert(insertPos, nextEp);
                    if (IsOn(HomeSectionId.NextUp))
                        NextUpSection.Visibility = Visibility.Visible;
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("home", $"event=nextup_advance outcome=failure error={ex.GetType().Name}");
        }
        finally
        {
            if (_pendingAdvances.TryGetValue(seriesId, out var current)
                && ReferenceEquals(current, pending))
                _pendingAdvances.Remove(seriesId);
        }
    }

    private bool IsCurrentAdvance(Guid seriesId, PendingAdvance pending, string profileKey)
        => profileKey == _app.ActiveSessionKey
            && _profileKey == profileKey
            && _pendingAdvances.TryGetValue(seriesId, out var current)
            && ReferenceEquals(current, pending);

    private sealed class PendingAdvance(int nextUpReplaceIndex, int? nextUpRowGeneration,
        string profileKey)
    {
        public int NextUpReplaceIndex { get; private set; } = nextUpReplaceIndex;
        public int? NextUpRowGeneration { get; private set; } = nextUpRowGeneration;
        public string ProfileKey { get; } = profileKey;
        public int Version { get; private set; }

        public void Merge(int nextUpReplaceIndex, int? nextUpRowGeneration)
        {
            NextUpReplaceIndex = nextUpReplaceIndex;
            NextUpRowGeneration = nextUpRowGeneration;
            Version++;
        }
    }

    private void OnSeeAll(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<HomeSectionId>(tag, out var id))
        {
            Diagnostics.AppLog.Detail("home", $"event=interaction action=see-all section={id}");
            SeeAllRequested?.Invoke(id);
        }
    }

    private void OnRowSelection(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MediaItem item } box)
        {
            Diagnostics.AppLog.Detail("home", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            box.SelectedItem = null; // cards act as buttons, not persistent selection
            ItemSelected?.Invoke(item);
        }
    }

    // ---- Customize flyout (M11): reorder + on/off, save-on-change per profile --------

    private void OnCustomize(object sender, RoutedEventArgs e)
    {
        if (_customizePopup is { IsOpen: true })
        {
            Diagnostics.AppLog.Detail("home", "event=interaction action=customize-close");
            _customizePopup.IsOpen = false;
            return;
        }
        var rows = new StackPanel();
        RebuildEditor(rows);
        // The id sits on the header TextBlock, not the Border/StackPanel — neither has
        // an automation peer, so an id there is invisible to UIA (repo-known gotcha).
        var header = LiveText.Bind(new TextBlock
        {
            // Explicit font: the Popup inherits properties through PlacementTarget (the
            // customize BUTTON, styled with the icon font) — without this the icon
            // font's ligature renders "home" as the house glyph (seen live).
            FontFamily = (FontFamily)FindResource("LwFontUi"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("LwText2Brush"),
            Margin = new Thickness(0, 0, 0, 8)
        }, "Text", () => L.Text("HomeView.CustomizeHome.57d1d6db"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(header, "HomeCustomizePopup");
        var panel = new StackPanel { MinWidth = 250 };
        panel.Children.Add(header);
        panel.Children.Add(rows);
        var border = new Border
        {
            Background = (Brush)FindResource("LwStorm2Brush"),
            BorderBrush = (Brush)FindResource("LwHairlineBrush"),
            BorderThickness = new Thickness(1),
            // P10 M7: on the ladder by name (code cannot use StaticResource). This was one
            // of the three coincidental 10s the shape pass split apart.
            CornerRadius = (CornerRadius)FindResource("LwR3"),
            Padding = new Thickness(12, 10, 12, 10),
            Child = panel,
        };
        _customizePopup = new Popup
        {
            Child = border,
            PlacementTarget = CustomizeButton,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -220,
            StaysOpen = false,
            AllowsTransparency = true,
            // Outside the tree, so it inherits no direction; the offset is in the button's.
            FlowDirection = CustomizeButton.FlowDirection,
        };
        // Created in code, outside the visual tree: nothing would zoom it otherwise.
        Theme.ZoomPopups.SetFollow(_customizePopup, true);
        _customizePopup.IsOpen = true;
        Diagnostics.AppLog.Detail("home", "event=interaction action=customize-open");
    }

    private void RebuildEditor(StackPanel panel)
    {
        panel.Children.Clear();
        for (var i = 0; i < _layout.Count; i++)
        {
            var index = i;
            var pref = _layout[i];
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var up = EditorArrow("IconSortAsc", $"SectionUp{pref.Id}", index > 0,
                () => MoveSection(index, -1, panel));
            var down = EditorArrow("IconSortDesc", $"SectionDown{pref.Id}", index < _layout.Count - 1,
                () => MoveSection(index, +1, panel));
            Grid.SetColumn(down, 1);

            var toggle = new CheckBox
            {
                Style = (Style)FindResource("LwToggleSwitch"),
                IsChecked = pref.Enabled,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, $"SectionToggle{pref.Id}");
            LiveText.Bind(toggle, "Content", () => DisplayName(pref.Id));
            toggle.Checked += (_, _) => SetSectionEnabled(pref.Id, true);
            toggle.Unchecked += (_, _) => SetSectionEnabled(pref.Id, false);
            Grid.SetColumn(toggle, 2);

            var handle = EditorDragHandle(pref.Id, panel);
            Grid.SetColumn(handle, 3);

            grid.Children.Add(up);
            grid.Children.Add(down);
            grid.Children.Add(toggle);
            grid.Children.Add(handle);

            // The insertion line lives on the row's own Border so it can be drawn above or below
            // without a separate adorner layer (a Popup has no adorner layer of the main window's).
            var row = new Border
            {
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = (System.Windows.Media.Brush)FindResource("LwLightCoreBrush"),
                BorderThickness = new Thickness(0),
                Child = grid,
                Tag = index,
            };
            panel.Children.Add(row);
        }
    }

    /// <summary>The drag grip. A dedicated handle rather than a draggable row, because the row
    /// already holds two arrow buttons and a toggle switch that all need ordinary clicks — a
    /// row-wide drag would have to guess which presses were drags.</summary>
    private Button EditorDragHandle(HomeSectionId id, StackPanel panel)
    {
        // Six dots in two columns: the conventional grip, drawn rather than iconised because the
        // icon font has no drag_indicator glyph (checked).
        var dots = new Grid { VerticalAlignment = VerticalAlignment.Center };
        dots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var c = 0; c < 2; c++)
        {
            var col = new StackPanel { Margin = new Thickness(c == 0 ? 0 : 3, 0, 0, 0) };
            for (var r = 0; r < 3; r++)
                col.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Margin = new Thickness(0, r == 0 ? 0 : 3, 0, 0),
                    Fill = (System.Windows.Media.Brush)FindResource("LwText3Brush"),
                });
            Grid.SetColumn(col, c);
            dots.Children.Add(col);
        }

        // A Button, not the Border this started as. A Border has no automation peer, so the id had
        // to ride a transparent FontSize=1 TextBlock over the dots - and that 6x1 px marker proved
        // an unreliable anchor: it was found by UIA on one run and absent on the next. A Button
        // brings its own peer with the handle's real 26x26 rect (which a synthetic drag needs in
        // order to know where to press) and is keyboard-focusable for free.
        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        chrome.SetValue(Border.CornerRadiusProperty, (CornerRadius)FindResource("LwR1"));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        template.VisualTree = chrome;

        var handle = LiveText.Bind(new Button
        {
            Template = template,
            Content = dots,
            Width = 26,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeNS,
            VerticalAlignment = VerticalAlignment.Center
        }, "ToolTip", () => L.Text("HomeView.DragToReorder.10c3f0cc"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(handle, $"SectionGrip{id}");
        LiveText.Bind(handle, System.Windows.Automation.AutomationProperties.NameProperty, () => L.Format("HomeView.Reorder.b60fbcb8", (DisplayName(id))));
        handle.PreviewMouseLeftButtonDown += (s, e) => BeginEditorDrag((FrameworkElement)s, panel, e);
        handle.PreviewMouseMove += (s, e) => UpdateEditorDrag((FrameworkElement)s, panel, e);
        handle.PreviewMouseLeftButtonUp += (s, e) => EndEditorDrag((FrameworkElement)s, panel, e);
        handle.LostMouseCapture += (_, _) => ResetEditorDrag(panel);
        return handle;
    }

    private Point? _editorDragStart;
    private int _editorDragFrom = -1;
    private bool _editorDragging;

    // Reordering uses plain mouse capture, NOT DragDrop.DoDragDrop, and that is deliberate. The
    // editor lives in a Popup, and an OLE drag inside a Popup does not work here: the drag loop
    // takes capture, the StaysOpen=false Popup reads that as a click elsewhere and dismisses
    // itself, and the rows the drop needed are gone before it lands. Measured - the gesture
    // completed and home-layout.json was never written, twice, once with the Popup pinned open.
    // Explicit capture keeps every mouse event on the handle, so the gesture is self-contained
    // and (usefully) drivable by synthetic input in a suite.

    private void BeginEditorDrag(FrameworkElement handle, StackPanel panel, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindRow(handle) is not { Tag: int from })
            return;
        _editorDragStart = e.GetPosition(panel);
        _editorDragFrom = from;
        _editorDragging = false;
        handle.CaptureMouse();
    }

    /// <summary>Tracks the gesture and draws the insertion line. The line is drawn from the same
    /// calculation the commit uses, so feedback and outcome cannot disagree.</summary>
    private void UpdateEditorDrag(FrameworkElement handle, StackPanel panel, System.Windows.Input.MouseEventArgs e)
    {
        if (_editorDragFrom < 0 || _editorDragStart is not { } start)
            return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(panel);
        // The threshold gate stops a click that jitters by a pixel from counting as a reorder.
        if (!_editorDragging &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        _editorDragging = true;
        ShowInsertionLine(panel, InsertIndexAt(panel, now.Y));
    }

    private void EndEditorDrag(FrameworkElement handle, StackPanel panel, System.Windows.Input.MouseButtonEventArgs e)
    {
        var from = _editorDragFrom;
        var dragged = _editorDragging;
        var insertAt = dragged ? InsertIndexAt(panel, e.GetPosition(panel).Y) : -1;
        handle.ReleaseMouseCapture();   // raises LostMouseCapture -> ResetEditorDrag
        if (dragged && from >= 0)
            MoveSectionTo(from, insertAt, panel);
    }

    private void ResetEditorDrag(StackPanel panel)
    {
        _editorDragStart = null;
        _editorDragFrom = -1;
        _editorDragging = false;
        ClearInsertionLines(panel);
    }

    /// <summary>The gap the pointer is currently over, as an index into the row list: 0 means
    /// above the first row, Count means below the last.</summary>
    private static int InsertIndexAt(StackPanel panel, double y)
    {
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement row)
                continue;
            var top = row.TransformToAncestor(panel).Transform(new Point(0, 0)).Y;
            if (y < top + row.ActualHeight / 2)
                return i;
        }
        return panel.Children.Count;
    }

    private static void ShowInsertionLine(StackPanel panel, int insertAt)
    {
        ClearInsertionLines(panel);
        // Past the last row there is no row to draw a top edge on, so use the last row's bottom.
        if (insertAt >= panel.Children.Count)
        {
            if (panel.Children.Count > 0 && panel.Children[^1] is Border last)
                last.BorderThickness = new Thickness(0, 0, 0, 2);
            return;
        }
        if (panel.Children[insertAt] is Border row)
            row.BorderThickness = new Thickness(0, 2, 0, 0);
    }

    private static void ClearInsertionLines(StackPanel panel)
    {
        foreach (var child in panel.Children)
            if (child is Border b)
                b.BorderThickness = new Thickness(0);
    }

    private static Border? FindRow(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is Border { Tag: int } b)
                return b;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    /// <summary>Moves one entry to an insertion point. Deliberately NOT the swap that
    /// <see cref="MoveSection"/> does: dragging row 1 onto row 4 means "put it there", and a swap
    /// would instead fling row 4 up to position 1 — correct for a single-step arrow, wrong for a
    /// drag across several rows.</summary>
    private void MoveSectionTo(int from, int insertAt, StackPanel panel)
    {
        if (from < 0 || from >= _layout.Count)
            return;
        // Removing shifts everything after `from` down one, so an insertion point past it has to
        // come back by one to still mean the same gap.
        if (insertAt > from)
            insertAt--;
        insertAt = Math.Clamp(insertAt, 0, _layout.Count - 1);
        if (insertAt == from)
            return;
        var moved = _layout[from];
        _layout.RemoveAt(from);
        _layout.Insert(insertAt, moved);
        Diagnostics.AppLog.Detail("home",
            $"event=interaction action=reorder section={moved.Id} from={from} to={insertAt}");
        HomeLayoutStore.Save(_profileKey, _layout);
        ApplyOrder();
        RebuildEditor(panel);
    }

    private Button EditorArrow(string iconKey, string automationId, bool enabled, Action onClick)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("LwCardActionButton"),
            Content = (string)FindResource(iconKey),
            Width = 26,
            Height = 26,
            FontSize = 14,
            IsEnabled = enabled,
            Margin = new Thickness(0, 0, 2, 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(btn, automationId);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void MoveSection(int index, int delta, StackPanel panel)
    {
        var target = index + delta;
        if (target < 0 || target >= _layout.Count)
            return;
        (_layout[index], _layout[target]) = (_layout[target], _layout[index]);
        Diagnostics.AppLog.Detail("home",
            $"event=interaction action=reorder section={_layout[target].Id} from={index} to={target}");
        HomeLayoutStore.Save(_profileKey, _layout);
        ApplyOrder();
        RebuildEditor(panel);   // arrows/enabled states follow the new order
    }

    private async void SetSectionEnabled(HomeSectionId id, bool enabled)
    {
        var i = _layout.FindIndex(p => p.Id == id);
        if (i < 0 || _layout[i].Enabled == enabled)
            return;
        _layout[i] = _layout[i] with { Enabled = enabled };
        Diagnostics.AppLog.Detail("home", $"event=interaction action=toggle-section section={id} enabled={enabled}");
        HomeLayoutStore.Save(_profileKey, _layout);
        ApplyOrder();
        if (!enabled)
            return;
        // Switched on: the rail was never fetched this session — fetch just it.
        try
        {
            var jf = _app.Jellyfin;
            var profileKey = _app.ActiveSessionKey;
            switch (id)
            {
                case HomeSectionId.Resume:
                    FillRow(ResumeSection, ResumeRow,
                        await FetchRowAsync(id, () => jf.GetResumeItemsAsync(), profileKey)); break;
                case HomeSectionId.NextUp:
                    FillRow(NextUpSection, NextUpRow,
                        await FetchRowAsync(id, () => jf.GetNextUpAsync(), profileKey)); break;
                case HomeSectionId.Latest:
                    _libraries ??= await jf.GetLibrariesAsync();
                    var libraryIds = _libraries.Select(l => l.Id).ToList();
                    FillRow(LatestSection, LatestRow,
                        await FetchRowAsync(id, () => jf.GetLatestAsync(libraryIds), profileKey)); break;
                case HomeSectionId.Favorites:
                    FillRow(FavoritesSection, FavoritesRow,
                        await FetchRowAsync(id, () => jf.GetFavoritesAsync(), profileKey)); break;
                case HomeSectionId.Libraries:
                    _libraries ??= await jf.GetLibrariesAsync();
                    FillRow(LibrariesSection, LibrariesRow, _libraries); break;
            }
        }
        catch
        {
            // rail stays hidden until the next full Home load
        }
    }
}
