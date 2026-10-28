using LightWeaver.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Advanced Search Screen (Phase 7 M4, redesigned per design/advanced-search-redesign/SPEC.md).
/// Free text + multi-select type + multi-select genre + watched state + year range, sorted,
/// across the whole server. Mixed-type results render as type-grouped sections of native
/// cards (P1); a single selected type keeps the infinite-scroll grid. With no constraint the
/// screen shows a prompt state instead of dumping the server (P7). Deep-link seeds (M5 person
/// / M6 genre) render as a removable garnet context chip. The
/// <see cref="AdvancedSearchQuery"/> record remains the single filter contract.
/// </summary>
public partial class AdvancedSearchView : UserControl
{
    private const int PageSize = 50;
    private const int SectionCap = 24;   // mixed mode caps each section at its first page

    private static (string Label, string Plural, BaseItemKind Kind)[] TypeChoices =>
    [
        (L.Text("AdvancedSearchView.Movies.b932e79c"), L.Text("AdvancedSearchView.Movies.d5bfa6ca"), BaseItemKind.Movie),
        (L.Text("AdvancedSearchView.Series.ceca6d59"), L.Text("AdvancedSearchView.Series.57b4e8b8"), BaseItemKind.Series),
        (L.Text("AdvancedSearchView.Episodes.01fc855d"), L.Text("AdvancedSearchView.Episodes.76883a65"), BaseItemKind.Episode),
    ];

    private static (string Label, ItemSortBy Sort)[] SortChoices =>
    [
        (L.Text("AdvancedSearchView.Name.2b6dd5ff"), ItemSortBy.SortName),
        (L.Text("AdvancedSearchView.DateAdded.cf93c1b6"), ItemSortBy.DateCreated),
        (L.Text("AdvancedSearchView.PremiereDate.98567950"), ItemSortBy.PremiereDate),
        (L.Text("AdvancedSearchView.CommunityRating.247aeee3"), ItemSortBy.CommunityRating),
        (L.Text("AdvancedSearchView.Random.79107541"), ItemSortBy.Random),
    ];

    private static (string Label, ItemFilter? Filter)[] WatchedChoices =>
    [
        (L.Text("AdvancedSearchView.All.93e8893c"), null),
        (L.Text("AdvancedSearchView.Unwatched.2487a533"), ItemFilter.IsUnplayed),
        (L.Text("AdvancedSearchView.Watched.0d662522"), ItemFilter.IsPlayed),
    ];

    private readonly AppViewModel _app;
    private readonly ObservableCollection<MediaItem> _items = [];
    private readonly List<Guid> _personIds;
    private string? _personName;
    private string? _seedGenre;

    private bool _mixedMode;
    private int _totalCount;
    /// <summary>The summary line's per-type totals for a mixed query; null for a typed one.
    /// Kept so a removal can lower them.</summary>
    private List<(BaseItemKind Kind, int Total)>? _summaryBreakdown;
    /// <summary>Mixed mode's sections, kept so a removal can edit them in place.</summary>
    private readonly List<ResultSection> _sections = [];
    /// <summary>The query <see cref="_sections"/> answer, for re-asking one section's first page
    /// (see <see cref="RefillSectionAsync"/>) without picking up an edit not yet applied.</summary>
    private AdvancedSearchQuery? _sectionsQuery;
    private bool _loadingMore;
    private bool _initializing = true;
    private int _loadGeneration;
    /// <summary>Bumped when <see cref="RemoveItems"/> takes cards off the paged grid. An append asks
    /// for the page at the grid's count, so one in flight across a removal would land one card off
    /// per removed card: a skipped result, or a boundary card twice. It carries the epoch and the
    /// count it started at, is dropped when either moved, and is asked again at the new count.</summary>
    private int _appendEpoch;
    /// <summary>Asks again when a grid append or a section refill ran out of retries with results
    /// left (see <see cref="RemovalRefill"/>).</summary>
    private readonly RefillBackoff _refillBackoff = new();
    private System.Windows.Threading.DispatcherTimer? _termTimer;

    // Person type-ahead (2026-08-05). The box is a transient PICKER, not a second piece of
    // state: the applied filter lives in _personIds/_personName exactly as the cast-face deep
    // link leaves it, and the box is empty whenever nothing is being typed.
    private System.Windows.Threading.DispatcherTimer? _personTimer;
    private int _personSuggestGeneration;
    private bool _suppressPersonText;
    private bool _personKeyNavigating;
    private bool _genresLoaded;
    private ScrollViewer? _gridScroll;

    public event Action<MediaItem>? ItemSelected;

    public AdvancedSearchView(AppViewModel app, AdvancedSearchQuery? seed = null)
    {
        InitializeComponent();
        LiveText.Watch(this);
        _app = app;
        seed ??= new AdvancedSearchQuery();
        _personIds = seed.PersonIds.ToList();
        _personName = seed.PersonName;
        // The genre chip marks a deep-link seed (M6 passes exactly one genre); a person
        // seed takes the chip slot instead when both are present.
        _seedGenre = _personIds.Count == 0 && seed.Genres.Count == 1 ? seed.Genres[0] : null;

        TypeFilter.Placeholder = L.Text("AdvancedSearchView.AllTypes.9b322cbd");
        TypeFilter.SetOptions(TypeChoices.Select(c => new CheckOption(c.Kind.ToString(), c.Label)));
        TypeFilter.SelectionChanged += OnFacetChanged;

        GenreFilter.Placeholder = L.Text("AdvancedSearchView.AnyGenre.f3c243e1");
        GenreFilter.SelectionChanged += OnFacetChanged;

        foreach (var (label, _) in WatchedChoices)
            WatchedFilter.Items.Add(label);
        WatchedFilter.SelectionChanged += OnWatchedChanged;   // exact-signature wiring (M22 foot-gun)
        foreach (var (label, _) in SortChoices)
            SortBox.Items.Add(label);

        ApplySeed(seed);
        UpdateSeedChip();

        ItemsGrid.ItemsSource = _items;
        _initializing = false;
        LiveText.Observe(this, OnLanguageChanged);

        Loaded += async (_, _) =>
        {
            // Load genres (and apply any seed selection) FIRST so the initial query includes
            // a seed genre from M6; then run the first query (prompt state if unconstrained).
            if (!_genresLoaded)
                await LoadGenresAsync(seed.Genres);
            if (_items.Count == 0 && _loadGeneration == 0)
                await ResetAndReloadAsync();
            else
                RefillPending();   // shown again: an emptied grid or section with results left asks now
            SearchBox.Focus();   // P5: the on-screen field is the single search input
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

    /// <summary>Apply a seed query to the controls (guarded so it doesn't trigger a reload
    /// per control). Genres are applied once the server list arrives in <see cref="LoadGenresAsync"/>.</summary>
    private void ApplySeed(AdvancedSearchQuery seed)
    {
        SearchBox.Text = seed.Term ?? "";
        // Types: empty seed set means the broad default (leave all unselected = "all types").
        var types = seed.ItemTypes.Count > 0 && seed.ItemTypes.Count < TypeChoices.Length
            ? seed.ItemTypes
            : [];
        TypeFilter.SetSelectedKeys(types.Select(t => t.ToString()));

        WatchedFilter.SelectedIndex = Math.Max(0, Array.FindIndex(WatchedChoices, c => c.Filter == seed.Watched));
        SortBox.SelectedIndex = Math.Max(0, Array.FindIndex(SortChoices, c => c.Sort == seed.Sort));
        OrderButton.IsChecked = seed.Order == SortOrder.Descending;
        MinYearBox.Text = seed.MinYear?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaxYearBox.Text = seed.MaxYear?.ToString(CultureInfo.InvariantCulture) ?? "";
        UpdateOrderUi();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Query assembly ----

    private AdvancedSearchQuery BuildQuery()
    {
        var types = TypeFilter.SelectedKeys
            .Select(k => Enum.TryParse<BaseItemKind>(k, out var kind) ? (BaseItemKind?)kind : null)
            .Where(k => k is not null).Select(k => k!.Value).ToList();
        return new AdvancedSearchQuery
        {
            Term = SearchBox.Text.Trim() is { Length: > 0 } t ? t : null,
            ItemTypes = types.Count > 0 ? types : AdvancedSearchQuery.DefaultTypes,
            Genres = GenreFilter.SelectedKeys.ToList(),
            PersonIds = _personIds,
            MinYear = ParseYear(MinYearBox.Text),
            MaxYear = ParseYear(MaxYearBox.Text),
            Watched = WatchedChoices[Math.Max(0, WatchedFilter.SelectedIndex)].Filter,
            Sort = SortChoices[Math.Max(0, SortBox.SelectedIndex)].Sort,
            Order = OrderButton.IsChecked == true ? SortOrder.Descending : SortOrder.Ascending,
        };
    }

    private static int? ParseYear(string text) =>
        int.TryParse(text.Trim(), out var y) && y is >= 1800 and <= 2999 ? y : null;

    /// <summary>P7: the first query runs only once the user constrains it (a term or any
    /// facet). Sort/order alone is no constraint — there is nothing to sort yet.</summary>
    private bool HasAnyConstraint() =>
        SearchBox.Text.Trim().Length > 0
        || TypeFilter.SelectedKeys.Count > 0
        || GenreFilter.SelectedKeys.Count > 0
        || _personIds.Count > 0
        || ParseYear(MinYearBox.Text) is not null
        || ParseYear(MaxYearBox.Text) is not null
        || WatchedFilter.SelectedIndex > 0;

    // ---- Loading ----

    private async Task LoadGenresAsync(IReadOnlyList<string> seedGenres)
    {
        _genresLoaded = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("advanced-search", "event=facet_load outcome=start facet=genres");
        try
        {
            var genres = await _app.Jellyfin.GetGenresAsync();
            GenreFilter.SetOptions(genres.Select(g => new CheckOption(g, g)));
            if (seedGenres.Count > 0)
                GenreFilter.SetSelectedKeys(seedGenres);
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=facet_load outcome=success facet=genres elapsed_ms={started.ElapsedMilliseconds} count={genres.Count}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=facet_load outcome=failure facet=genres elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            // no genre facet then — the dropdown stays empty
        }
    }

    private async Task ResetAndReloadAsync()
    {
        UpdateEngagedStates();
        UpdateSeedChip();
        if (!HasAnyConstraint())
        {
            ++_loadGeneration;   // drop any in-flight load
            ReleaseStaleAppend();
            _refillBackoff.Cancel();
            Diagnostics.AppLog.Detail("advanced-search", $"event=query outcome=prompt generation={_loadGeneration}");
            ShowPrompt();
            return;
        }
        _items.Clear();
        _totalCount = 0;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        ReleaseStaleAppend();
        _refillBackoff.Cancel();
        var profileKey = _app.ActiveSessionKey;
        var started = System.Diagnostics.Stopwatch.StartNew();
        StatusText.Visibility = Visibility.Collapsed;
        HideCenter();
        // Skeleton only if the query takes noticeable time — avoids a flicker on LAN-fast
        // responses. The env hook keeps it testable. This gate started here and is now the
        // app-wide rule for indicators inside an already-painted page; the threshold moved
        // to SkeletonFactory.RevealDelayMs (2026-08-02) so all of them share one number.
        _ = Task.Delay(SkeletonFactory.RevealDelayMs).ContinueWith(_ =>
        {
            if (generation == _loadGeneration && _queryRunning)
                ShowSkeleton();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        _queryRunning = true;
        if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
            await Task.Delay(slowMs);

        var q = BuildQuery();
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=load outcome=start generation={generation} term_length={q.Term?.Length ?? 0} types={q.ItemTypes.Count} genres={q.Genres.Count} people={q.PersonIds.Count} years={(q.MinYear is null && q.MaxYear is null ? 0 : 1)} watched={q.Watched?.ToString() ?? "any"} sort={q.Sort} order={q.Order}");
        try
        {
            var requestedAtUtc = DateTime.UtcNow;
            if (q.ItemTypes.Count <= 1)
            {
                var (items, total) = await _app.Jellyfin.AdvancedSearchAsync(q, 0, PageSize);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                {
                    Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale generation={generation} mode=single");
                    return;
                }
                var listedCount = items.Count;
                (items, total) = DropRemoved(items, total, requestedAtUtc, profileKey);
                total = RemovalRefill.RemainingTotal(items.Count, listedCount, total);
                _queryRunning = false;
                _mixedMode = false;
                _totalCount = total;
                _items.Clear();
                foreach (var item in items)
                    _items.Add(item);
                _sections.Clear();
                _summaryBreakdown = null;
                BindSummary(total);
                ShowSingle();
                // Every card it listed was removed while it ran, with results left: the append
                // asks for the first page again, as a removal that empties the grid does.
                RefillPending();
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=success generation={generation} mode=single elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={total}");
            }
            else
            {
                // Mixed mode (P1): one capped query per type; the totals feed the summary.
                var kinds = q.ItemTypes.ToList();
                var tasks = kinds.Select(k =>
                    _app.Jellyfin.AdvancedSearchAsync(q with { ItemTypes = [k] }, 0, SectionCap)).ToList();
                var results = (await Task.WhenAll(tasks))
                    .Select(result =>
                    {
                        var (kept, adjusted) = DropRemoved(result.Items, result.TotalCount, requestedAtUtc, profileKey);
                        return (Items: kept, Total: RemovalRefill.RemainingTotal(kept.Count, result.Items.Count, adjusted));
                    })
                    .ToArray();
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                {
                    Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale generation={generation} mode=mixed");
                    return;
                }
                _queryRunning = false;

                var sections = new List<(BaseItemKind Kind, List<MediaItem> Items, int Total)>();
                for (var i = 0; i < kinds.Count; i++)
                    if (results[i].Total > 0)
                        sections.Add((kinds[i], results[i].Items, results[i].Total));

                var grand = sections.Sum(s => s.Total);
                _sectionsQuery = q;
                _sections.Clear();
                _summaryBreakdown = kinds.Select((k, i) => (k, results[i].Total)).ToList();
                BindSummary(grand);

                if (sections.Count == 0)
                {
                    _mixedMode = false;
                    _items.Clear();
                    _totalCount = 0;
                    ShowSingle();   // renders the zero-results state
                }
                else if (sections.Count == 1)
                {
                    // Hits in one section only → single-type layout (SPEC): the paged grid.
                    // Paging keeps the full mixed query — only this type matches, so the
                    // pages are identical to a typed query.
                    _mixedMode = false;
                    _totalCount = sections[0].Total;
                    _items.Clear();
                    foreach (var item in sections[0].Items)
                        _items.Add(item);
                    ShowSingle();
                }
                else
                {
                    _mixedMode = true;
                    BuildSections(sections);
                    ShowSections();
                }
                // A preview, or the handed-off grid, whose every card was removed while the
                // requests were out still counts results: its first page again, as a removal that
                // empties it does. Without this an emptied section stayed up empty for good.
                RefillPending();
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=success generation={generation} mode={(_mixedMode ? "mixed" : "single")} elapsed_ms={started.ElapsedMilliseconds} count={sections.Sum(s => s.Items.Count)} total={grand} sections={sections.Count}");
            }
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=stale generation={generation} phase=exception");
                return;
            }
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=load outcome=failure generation={generation} elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
            _queryRunning = false;
            SkeletonHost.Visibility = Visibility.Collapsed;
            ItemsGrid.Visibility = Visibility.Collapsed;
            SectionsScroll.Visibility = Visibility.Collapsed;
            HideCenter();
            LiveText.Bind(StatusText, "Text", () => L.Format("AdvancedSearchView.FailedToLoad.8cefcd6d", (ex.Message)));
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _queryRunning = false;
                // An emptied grid with results left keeps the skeleton as its loading state.
                if (!GridPending)
                    SkeletonHost.Visibility = Visibility.Collapsed;
            }
        }
    }

    private bool _queryRunning;

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200)
            return;
        TryAppend();
    }

    /// <summary>Starts the paged grid's next append unless one is running or the grid is complete.</summary>
    private void TryAppend()
    {
        if (_mixedMode || _loadingMore || _items.Count >= _totalCount)
            return;
        _ = AppendPageAsync();
    }

    /// <summary>One append at the grid's count. It lands only if the load generation, the append
    /// epoch and the count it started at are all unchanged. One dropped for a moved epoch or count
    /// (a removal took cards off the grid meanwhile) is asked again at the new count; one dropped
    /// for a new load is not, and the load released its flag.</summary>
    private async Task AppendPageAsync()
    {
        _loadingMore = true;
        // Append-owned: hidden in the finally by the append of the current load. See LibraryView.
        MoreLoading.Show();
        var generation = _loadGeneration;
        var appendEpoch = _appendEpoch;
        var offset = _items.Count;
        var profileKey = _app.ActiveSessionKey;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var requeue = false;
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=load outcome=start mode=append generation={generation} offset={offset}");
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            List<MediaItem> more;
            int total;
            int listedCount;
            for (var attempt = 0; ; attempt++)
            {
                var requestedAtUtc = DateTime.UtcNow;
                var (listed, serverTotal) = await _app.Jellyfin.AdvancedSearchAsync(BuildQuery(), offset, PageSize);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                {
                    Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale mode=append generation={generation}");
                    return;
                }
                if (appendEpoch != _appendEpoch || offset != _items.Count)
                {
                    Diagnostics.AppLog.Detail("advanced-search",
                        $"event=load outcome=stale mode=append generation={generation} reason=count_moved offset={offset} count={_items.Count}");
                    requeue = true;
                    return;
                }
                listedCount = listed.Count;
                (more, total) = DropRemoved(listed, serverTotal, requestedAtUtc, profileKey);
                // Every card it listed was removed while it ran, and the server still counts more
                // past this offset: ask again. A removal of cards not on the grid moves no epoch,
                // so a refill of an emptied grid otherwise took this page for the end of the
                // results and showed none.
                if (!RemovalRefill.AskAgain(more.Count, listedCount, total, offset, attempt))
                    break;
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=retry mode=append generation={generation} reason=all_removed offset={offset} total={total} attempt={attempt + 1}");
            }
            foreach (var item in more)
                _items.Add(item);
            // Out of retries with every card removed is not the end: the total stays, and an
            // emptied grid keeps its loading state and asks again after a backoff. Only a page
            // that listed nothing, or a count that drops to what is shown, ends the results.
            _totalCount = RemovalRefill.RemainingTotal(_items.Count, listedCount, total);
            if (GridPending)
            {
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=exhausted mode=append generation={generation} offset={offset} total={_totalCount}");
                ArmRefillRetry(generation, profileKey);
            }
            else
                _refillBackoff.Succeeded();
            SettleGrid();
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=stale mode=append generation={generation} phase=exception elapsed_ms={started.ElapsedMilliseconds}");
            else
            {
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}");
                // next scroll retries; an emptied grid has no scroll, so it asks again after a backoff
                if (GridPending)
                    ArmRefillRetry(generation, profileKey);
            }
        }
        finally
        {
            // A new load released the flag when it bumped the generation, and the flag may belong
            // to its own append by now.
            if (generation == _loadGeneration)
            {
                _loadingMore = false;
                MoreLoading.Hide();
                if (requeue)
                    TryAppend();
            }
        }
    }

    /// <summary>A new load supersedes any append in flight; its page is dropped when it lands, so
    /// it must not hold <c>_loadingMore</c> until then and block the new query's first append.</summary>
    private void ReleaseStaleAppend()
    {
        _loadingMore = false;
        MoreLoading.Hide();
    }

    // ---- Presentation states ----

    private void ShowPrompt()
    {
        _queryRunning = false;
        _mixedMode = false;
        SkeletonHost.Visibility = Visibility.Collapsed;
        ItemsGrid.Visibility = Visibility.Collapsed;
        SectionsScroll.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ResultSummary.Text = "";
        EmptyView.Show(() => L.Text("AdvancedSearchView.SearchEverything.ed0f1661"), () => L.Text("AdvancedSearchView.CombineTextWithTypeGenreYearAnd.0c235b79"));
        StarterChips.Visibility = Visibility.Visible;
        ClearFiltersBtn.Visibility = Visibility.Collapsed;
    }

    private void ShowSingle()
    {
        SkeletonHost.Visibility = Visibility.Collapsed;
        SectionsScroll.Visibility = Visibility.Collapsed;
        ItemsGrid.Visibility = Visibility.Visible;
        switch (RemovalRefill.Classify(_items.Count, _totalCount))
        {
            case RemovalRefill.State.Showing:
                HideCenter();
                AnimateIn(ItemsGrid, 0);
                break;
            case RemovalRefill.State.Pending:
                // Every card gone with results left: the loading skeleton while the refill runs.
                ShowSkeleton();
                break;
            default:
                EmptyView.Show(() => L.Text("AdvancedSearchView.NothingHere.256616a3"), () => L.Text("AdvancedSearchView.NoItemsMatchTheseFiltersTryLoosening.20ccc9be"));
                StarterChips.Visibility = Visibility.Collapsed;
                ClearFiltersBtn.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>The paged grid is on screen with no cards and results left (see
    /// <see cref="RemovalRefill"/>).</summary>
    private bool GridPending =>
        !_mixedMode && ItemsGrid.Visibility == Visibility.Visible
        && RemovalRefill.Classify(_items.Count, _totalCount) == RemovalRefill.State.Pending;

    /// <summary>Shows the paged grid's state under <see cref="RemovalRefill"/> after an append or a
    /// removal changed its cards or its total: cards, the loading skeleton while it is empty with
    /// results left, or the zero-results state with the summary at zero once nothing is.</summary>
    private void SettleGrid()
    {
        if (_mixedMode || _queryRunning || ItemsGrid.Visibility != Visibility.Visible)
            return;
        switch (RemovalRefill.Classify(_items.Count, _totalCount))
        {
            case RemovalRefill.State.Showing:
                if (SkeletonHost.Visibility == Visibility.Visible)
                {
                    SkeletonHost.Visibility = Visibility.Collapsed;
                    HideCenter();
                }
                break;
            case RemovalRefill.State.Pending:
                ShowSkeleton();
                break;
            default:
                _summaryBreakdown = _summaryBreakdown?.Select(entry => (entry.Kind, 0)).ToList();
                BindSummary(0);
                ShowSingle();
                break;
        }
    }

    /// <summary>Starts a refill for whatever <see cref="RemovalRefill"/> counts as pending: the
    /// paged grid's first page again, or each emptied mixed section's. A grid append or a section
    /// refill already running is left to land; each re-checks its state when it does.</summary>
    private void RefillPending()
    {
        if (_queryRunning)
            return;
        if (_mixedMode)
        {
            foreach (var section in _sections.ToList())
                if (RemovalRefill.Classify(section.Items.Count, section.Total) == RemovalRefill.State.Pending)
                    _ = RefillSectionAsync(section);
        }
        else if (GridPending)
            TryAppend();
    }

    /// <summary>One more ask after the backoff, for the load that ran out of retries, on this
    /// profile, while the view is shown. Hidden (navigated away, or collapsed under the player,
    /// which leaves it loaded), it waits for the next reveal instead.</summary>
    private void ArmRefillRetry(int generation, string? profileKey)
        => _refillBackoff.Arm(() =>
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey || !IsVisible)
                return;
            Diagnostics.AppLog.Detail("advanced-search", $"event=refill outcome=backoff_retry generation={generation}");
            RefillPending();
        });

    private void ShowSections()
    {
        SkeletonHost.Visibility = Visibility.Collapsed;
        ItemsGrid.Visibility = Visibility.Collapsed;
        SectionsScroll.Visibility = Visibility.Visible;
        SectionsScroll.ScrollToTop();
        HideCenter();
    }

    private void HideCenter()
    {
        EmptyView.Visibility = Visibility.Collapsed;
        StarterChips.Visibility = Visibility.Collapsed;
        ClearFiltersBtn.Visibility = Visibility.Collapsed;
    }

    private void ShowSkeleton()
    {
        if (SkeletonHost.Content is null)
            SkeletonHost.Content = SkeletonFactory.Grid(this, 12, "AsSkeleton");
        SkeletonHost.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        HideCenter();
    }

    /// <summary>P8: mono result summary — invariant formatting (locale foot-gun class).</summary>
    private static string FormatSummary(int total, List<(BaseItemKind Kind, int Total)>? breakdown)
    {
        var inv = CultureInfo.InvariantCulture;
        var head = total == 1 ? L.Text("AdvancedSearchView.1Result.6c5fbe10") : L.Format("AdvancedSearchView.Results.035513e7", total);
        if (breakdown is null)
            return head;
        var parts = new List<string> { head };
        foreach (var (kind, count) in breakdown)
        {
            var choice = TypeChoices.First(c => c.Kind == kind);
            var noun = count == 1 && kind == BaseItemKind.Movie ? L.Text("AdvancedSearchView.Movie.70d184d4")
                     : count == 1 && kind == BaseItemKind.Episode ? L.Text("AdvancedSearchView.Episode.757c1c06")
                     : choice.Plural;   // L.Text("AdvancedSearchView.Series.57b4e8b8") is invariant
            parts.Add(string.Create(inv, $"{count} {noun}"));
        }
        return string.Join(" · ", parts);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var initializing = _initializing;
        _initializing = true;
        try
        {
            TypeFilter.Placeholder = L.Text("AdvancedSearchView.AllTypes.9b322cbd");
            TypeFilter.SetOptions(TypeChoices.Select(choice => new CheckOption(choice.Kind.ToString(), choice.Label)));
            GenreFilter.Placeholder = L.Text("AdvancedSearchView.AnyGenre.f3c243e1");
            LiveText.ReplaceChoices(WatchedFilter, WatchedChoices.Select(choice => choice.Label));
            LiveText.ReplaceChoices(SortBox, SortChoices.Select(choice => choice.Label));
        }
        finally { _initializing = initializing; }
    }

    /// <summary>Builds the type-grouped sections (P1): Marcellus header + mono count +
    /// "Show only …" link over a native-card wrap grid, stagger-faded in.</summary>
    private void BuildSections(List<(BaseItemKind Kind, List<MediaItem> Items, int Total)> sections)
    {
        SectionsHost.Children.Clear();
        var index = 0;
        foreach (var (kind, items, total) in sections)
        {
            var choice = TypeChoices.First(c => c.Kind == kind);

            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, index == 0 ? 14 : 26, 0, 12) };
            var title = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("LwFontDisplay"),
                FontSize = 22,
                Foreground = (Brush)FindResource("LwText1Brush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            LiveText.Bind(title, "Text", () => TypeChoices.First(choice => choice.Kind == kind).Label);
            System.Windows.Automation.AutomationProperties.SetAutomationId(title, $"AsSectionHeader{kind}");
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);

            var count = new TextBlock
            {
                Text = SectionCountText(total),
                Style = (Style)FindResource("LwTypeMono"),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("LwText3Brush"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(count, Dock.Left);
            header.Children.Add(count);

            var showOnly = LiveText.Bind(new Button
            {
                Style = (Style)FindResource("LwLinkButton"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            }, "Content", () => L.Format("AdvancedSearchView.ShowOnly.eed6db84", TypeChoices.First(t => t.Kind == kind).Plural));
            System.Windows.Automation.AutomationProperties.SetAutomationId(showOnly, $"AsShowOnly{kind}");
            var narrowKind = kind;
            showOnly.Click += async (_, _) =>
            {
                TypeFilter.SetSelectedKeys([narrowKind.ToString()]);   // programmatic: no double reload
                await ResetAndReloadAsync();
            };
            DockPanel.SetDock(showOnly, Dock.Right);
            header.Children.Add(showOnly);

            // Observable so a removal drops the card in place.
            var cards = new ObservableCollection<MediaItem>(items);
            var list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemTemplateSelector = (DataTemplateSelector)FindResource("MediaCardSelector"),
                ItemContainerStyle = (Style)FindResource("LwMediaGridItem"),
                Template = (ControlTemplate)Resources["PlainList"],
                ItemsSource = cards,
                ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(WrapPanel))),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(list, $"AsSection{kind}");
            list.SelectionChanged += OnSelection;

            var section = new StackPanel();
            section.Children.Add(header);
            section.Children.Add(list);
            AnimateIn(section, index * 60);
            SectionsHost.Children.Add(section);
            _sections.Add(new ResultSection(kind, cards, count, section) { Total = total });
            index++;
        }
    }

    private static string SectionCountText(int total)
        => string.Create(CultureInfo.InvariantCulture, $"· {total}");

    /// <summary>One mixed-mode section: its type, its cards, the header count and the panel.</summary>
    private sealed class ResultSection(BaseItemKind kind, ObservableCollection<MediaItem> items,
        TextBlock count, FrameworkElement host)
    {
        public BaseItemKind Kind { get; } = kind;
        public ObservableCollection<MediaItem> Items { get; } = items;
        public TextBlock Count { get; } = count;
        public FrameworkElement Host { get; } = host;
        public int Total { get; set; }
        /// <summary>A <see cref="RefillSectionAsync"/> is in flight for it.</summary>
        public bool Refilling { get; set; }
    }

    private void BindSummary(int total)
    {
        var breakdown = _summaryBreakdown?.ToList();
        LiveText.Bind(ResultSummary, "Text", () => FormatSummary(total, breakdown));
    }

    /// <summary>A page answered for a request that started before a removal still lists the item;
    /// the shell's removal ledger drops it, and the server's count, which still has it, shrinks
    /// with it.</summary>
    private (List<MediaItem> Items, int Total) DropRemoved(List<MediaItem> items, int total,
        DateTime requestedAtUtc, string? profileKey)
    {
        var kept = _app.DropRemovedItems(items, requestedAtUtc, profileKey);
        return (kept, total - (items.Count - kept.Count));
    }

    /// <summary>Drops items the server no longer has, in place: from the paged grid or from the
    /// mixed sections, with the counts in the summary and the section headers lowered to match. A
    /// section whose cards are all gone closes. Without it, opening a result deleted since the
    /// search answered 404 and went back to the same dead card.</summary>
    public void RemoveItems(IReadOnlySet<Guid> itemIds)
    {
        var removedByKind = new Dictionary<BaseItemKind, int>();
        var removed = 0;
        for (var i = _items.Count - 1; i >= 0; i--)
            if (itemIds.Contains(_items[i].Id))
            {
                if (Enum.TryParse<BaseItemKind>(_items[i].Type.ToString(), out var kind))
                    removedByKind[kind] = removedByKind.GetValueOrDefault(kind) + 1;
                _items.RemoveAt(i);
                removed++;
            }
        var fromGrid = removed;
        // The grid pages by count: an append in flight was asked at the old one.
        if (fromGrid > 0)
            _appendEpoch++;
        foreach (var section in _sections)
        {
            var inSection = 0;
            for (var i = section.Items.Count - 1; i >= 0; i--)
                if (itemIds.Contains(section.Items[i].Id))
                {
                    section.Items.RemoveAt(i);
                    inSection++;
                }
            if (inSection == 0)
                continue;
            removed += inSection;
            removedByKind[section.Kind] = removedByKind.GetValueOrDefault(section.Kind) + inSection;
            section.Total = Math.Max(section.Items.Count, section.Total - inSection);
            // A preview emptied while the server still has results of its type is refilled below;
            // only a section with nothing left closes.
            SettleSection(section);
        }
        if (removed == 0)
            return;
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=items_removed count={removed} mode={(_mixedMode ? "mixed" : "single")}");
        // The prompt state keeps the last results behind it; there is no summary to lower.
        if (ItemsGrid.Visibility != Visibility.Visible && SectionsScroll.Visibility != Visibility.Visible)
            return;

        if (_summaryBreakdown is not null)
            _summaryBreakdown = _summaryBreakdown
                .Select(entry => (entry.Kind, Math.Max(0, entry.Total - removedByKind.GetValueOrDefault(entry.Kind))))
                .ToList();
        if (_mixedMode)
        {
            BindSummary(_sections.Sum(section => section.Total));
            if (!ShowZeroResultsIfSectionsEmpty())
                RefillPending();
        }
        else
        {
            _totalCount = Math.Max(_items.Count, _totalCount - fromGrid);
            BindSummary(_totalCount);
            // The same for the paged grid: with results left on the server, the loading state and
            // its first page again rather than "Nothing here" under a summary that still counts them.
            SettleGrid();
            RefillPending();
        }
    }

    /// <summary>A section's header count and visibility under <see cref="RemovalRefill"/>: it
    /// closes only at a confirmed total of zero. Emptied with results left, it keeps its header
    /// and count while its refill runs.</summary>
    private static void SettleSection(ResultSection section)
    {
        section.Count.Text = SectionCountText(section.Total);
        section.Host.Visibility = RemovalRefill.Classify(section.Items.Count, section.Total) == RemovalRefill.State.Empty
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>The zero-results state, once every mixed section's remaining total is 0. A
    /// section with cards gone but results left is refilled instead. Returns whether it was
    /// shown.</summary>
    private bool ShowZeroResultsIfSectionsEmpty()
    {
        if (!_sections.All(section => RemovalRefill.Classify(section.Items.Count, section.Total) == RemovalRefill.State.Empty))
            return false;
        _mixedMode = false;
        _totalCount = 0;
        ShowSingle();
        return true;
    }

    /// <summary>Removals emptied a mixed section's preview while the server still has results of
    /// its type: its first page again, as the load asked for it. Mixed sections preview
    /// <see cref="SectionCap"/> cards and do not page, so without this the section stayed an
    /// empty header and, once every preview was empty, the view showed "Nothing here" under a
    /// summary that still counted the rest. The server's count replaces the section's, and the
    /// summary follows it.</summary>
    private async Task RefillSectionAsync(ResultSection section)
    {
        if (section.Refilling || _sectionsQuery is not { } query)
            return;
        section.Refilling = true;
        var generation = _loadGeneration;
        var profileKey = _app.ActiveSessionKey;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=section_refill outcome=start generation={generation} kind={section.Kind} total={section.Total}");
        bool Stale() => generation != _loadGeneration || profileKey != _app.ActiveSessionKey
                        || !_mixedMode || !_sections.Contains(section);
        try
        {
            List<MediaItem> items;
            int total;
            int listedCount;
            for (var attempt = 0; ; attempt++)
            {
                var requestedAtUtc = DateTime.UtcNow;
                var (listed, serverTotal) = await _app.Jellyfin.AdvancedSearchAsync(
                    query with { ItemTypes = [section.Kind] }, 0, SectionCap);
                if (Stale())
                {
                    Diagnostics.AppLog.Detail("advanced-search",
                        $"event=section_refill outcome=stale generation={generation} kind={section.Kind}");
                    return;
                }
                listedCount = listed.Count;
                (items, total) = DropRemoved(listed, serverTotal, requestedAtUtc, profileKey);
                // Every card it listed was removed while it ran; the server has them out by now.
                if (!RemovalRefill.AskAgain(items.Count, listedCount, total, 0, attempt))
                    break;
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=section_refill outcome=retry generation={generation} kind={section.Kind} total={total} attempt={attempt + 1}");
            }
            section.Items.Clear();
            foreach (var item in items)
                section.Items.Add(item);
            // A first page that listed nothing, or a count at zero, closes the section. Out of
            // retries with every card removed keeps the count: the section stays up, empty, and
            // asks again after a backoff.
            section.Total = RemovalRefill.RemainingTotal(items.Count, listedCount, total);
            SettleSection(section);
            _summaryBreakdown = _summaryBreakdown?
                .Select(entry => entry.Kind == section.Kind ? (entry.Kind, section.Total) : entry)
                .ToList();
            BindSummary(_sections.Sum(other => other.Total));
            var pending = RemovalRefill.Classify(section.Items.Count, section.Total) == RemovalRefill.State.Pending;
            if (pending)
                ArmRefillRetry(generation, profileKey);
            else
                _refillBackoff.Succeeded();
            ShowZeroResultsIfSectionsEmpty();
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=section_refill outcome={(pending ? "exhausted" : "success")} generation={generation} elapsed_ms={started.ElapsedMilliseconds} kind={section.Kind} count={items.Count} total={section.Total}");
        }
        catch (Exception ex)
        {
            var stale = Stale();
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=section_refill outcome={(stale ? "stale" : "failure")} generation={generation} elapsed_ms={started.ElapsedMilliseconds} kind={section.Kind} error={ex.GetType().Name}");
            // The section keeps its header and count and asks again after a backoff.
            if (!stale)
                ArmRefillRetry(generation, profileKey);
        }
        finally
        {
            section.Refilling = false;
        }
    }

    /// <summary>SPEC motion: new results cross-fade + 8 px rise over LwDurDrift (480 ms,
    /// ease-out); sections stagger by 60 ms.</summary>
    private static void AnimateIn(FrameworkElement el, int beginMs)
    {
        var tx = new TranslateTransform(0, 8);
        el.RenderTransform = tx;
        el.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(beginMs);
        var dur = TimeSpan.FromMilliseconds(480);
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease });
        tx.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, dur) { BeginTime = begin, EasingFunction = ease });
    }

    // ---- Engaged facets + seed chip ----

    /// <summary>P9: every facet holding a non-default value lights a stormlight ring so
    /// active filters are visible at a glance.</summary>
    private void UpdateEngagedStates()
    {
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        SearchEngaged.Visibility = V(SearchBox.Text.Trim().Length > 0);
        TypeEngaged.Visibility = V(TypeFilter.SelectedKeys.Count > 0);
        GenreEngaged.Visibility = V(GenreFilter.SelectedKeys.Count > 0);
        WatchedEngaged.Visibility = V(WatchedFilter.SelectedIndex > 0);
        MinYearEngaged.Visibility = V(ParseYear(MinYearBox.Text) is not null);
        MaxYearEngaged.Visibility = V(ParseYear(MaxYearBox.Text) is not null);
        UpdateMoreFiltersBadge();
    }

    /// <summary>
    /// How many of the disclosed filters (P10 M11) currently hold a non-default value, shown
    /// as a badge on the toggle. This is the whole reason a disclosure is acceptable here: a
    /// collapsed panel can narrow the result set, and without the count nothing on screen
    /// would say so. Year counts once, not twice - "1912-1930" is one constraint to a reader.
    /// </summary>
    private void UpdateMoreFiltersBadge()
    {
        var count = 0;
        if (GenreFilter.SelectedKeys.Count > 0) count++;
        if (WatchedFilter.SelectedIndex > 0) count++;
        if (ParseYear(MinYearBox.Text) is not null || ParseYear(MaxYearBox.Text) is not null) count++;
        MoreFiltersCount.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MoreFiltersBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // An active hidden filter opens the panel rather than merely badging it: a seeded
        // deep link (M5 person / M6 genre) lands here with a filter already applied.
        if (count > 0 && MoreFiltersButton.IsChecked != true)
            MoreFiltersButton.IsChecked = true;
    }

    private void OnMoreFiltersToggled(object sender, RoutedEventArgs e)
    {
        var open = MoreFiltersButton.IsChecked == true;
        MoreFiltersPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MoreFiltersGlyph.Text = (string)FindResource(open ? "IconExpandLess" : "IconExpandMore");
    }

    /// <summary>The deep-link seed renders as a removable garnet chip (state f). A genre
    /// chip stays in sync with the Genre dropdown row both ways.</summary>
    private void UpdateSeedChip()
    {
        if (_personIds.Count > 0)
        {
            LiveText.Bind(SeedCaption, "Text", () => L.Text("AdvancedSearchView.Person.e7b4d265"));
            LiveText.Bind(SeedChipText, "Text", () => _personName ?? L.Text("AdvancedSearchView.Person.d4c5f321"));
            SeedFacet.Visibility = Visibility.Visible;
            return;
        }
        // Only sync chip→dropdown once the async genre list has actually been applied —
        // at construction time the dropdown is empty and the seed would self-clear.
        if (_seedGenre is not null && GenreFilter.HasOptions && !GenreFilter.SelectedKeys.Contains(_seedGenre))
            _seedGenre = null;   // user unchecked the seeded genre — the chip follows
        if (_seedGenre is not null)
        {
            LiveText.Bind(SeedCaption, "Text", () => L.Text("AdvancedSearchView.Genre.e093f2c2"));
            SeedChipText.Text = _seedGenre;
            SeedFacet.Visibility = Visibility.Visible;
            return;
        }
        SeedFacet.Visibility = Visibility.Collapsed;
    }

    private async void OnSeedRemoved(object sender, RoutedEventArgs e)
    {
        if (_personIds.Count > 0)
        {
            _personIds.Clear();
            _personName = null;
            // A lookup may be in flight from the picker; drop it, or its result would reopen
            // the popup after the chip has gone.
            _personTimer?.Stop();
            _personSuggestGeneration++;
            ClearPersonBox();
            ClosePersonSuggestions();
        }
        else if (_seedGenre is not null)
        {
            var remaining = GenreFilter.SelectedKeys.Where(g => g != _seedGenre);
            GenreFilter.SetSelectedKeys(remaining);   // programmatic: no double reload
            _seedGenre = null;
        }
        await ResetAndReloadAsync();
    }

    // ---- Control events ----

    private async void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        UpdateOrderUi();
        await ResetAndReloadAsync();
    }

    private async void OnWatchedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        await ResetAndReloadAsync();
    }

    private async void OnFacetChanged()
    {
        if (_initializing)
            return;
        await ResetAndReloadAsync();
    }

    /// <summary>P10: the direction toggle is labelled per sort field (A–Z / Newest / Highest)
    /// and hidden for Random, where direction is meaningless.</summary>
    private void UpdateOrderUi()
    {
        var desc = OrderButton.IsChecked == true;
        OrderGlyph.Text = (string)FindResource(desc ? "IconSortDesc" : "IconSortAsc");
        var sort = SortChoices[Math.Max(0, SortBox.SelectedIndex)].Sort;
        OrderFacet.Visibility = sort == ItemSortBy.Random ? Visibility.Collapsed : Visibility.Visible;
        LiveText.Bind(OrderLabel, "Text", () => sort switch
        {
            ItemSortBy.SortName => desc ? L.Text("AdvancedSearchView.ZA.b0b49ab3") : L.Text("AdvancedSearchView.AZ.b6238d3c"),
            ItemSortBy.DateCreated or ItemSortBy.PremiereDate => desc ? L.Text("AdvancedSearchView.Newest.11182163") : L.Text("AdvancedSearchView.Oldest.d1af3ebe"),
            ItemSortBy.CommunityRating => desc ? L.Text("AdvancedSearchView.Highest.39875e69") : L.Text("AdvancedSearchView.Lowest.05a52637"),
            _ => desc ? L.Text("AdvancedSearchView.Descending.aafc8c46") : L.Text("AdvancedSearchView.Ascending.e8a49187"),
        });
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_initializing)
            return;
        // Debounce: re-query 300 ms after the last keystroke (Enter applies immediately).
        _termTimer ??= CreateTermTimer();
        _termTimer.Stop();
        _termTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateTermTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await ResetAndReloadAsync();
        };
        return timer;
    }

    // ---- Person type-ahead ----

    /// <summary>Arms the lookup debounce. Programmatic text changes (clearing the box after a
    /// commit) must not re-arm it, hence <c>_suppressPersonText</c> — without it, committing a
    /// person schedules a lookup for the empty string 300 ms later.</summary>
    private void OnPersonTextChanged(object sender, TextChangedEventArgs e)
    {
        PersonPlaceholder.Visibility = PersonBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suppressPersonText || _initializing)
            return;
        _personTimer ??= CreatePersonTimer();
        _personTimer.Stop();
        // Under two characters there is nothing worth asking the server (same threshold as the
        // shell's quick search); close rather than showing a stale list.
        if (PersonBox.Text.Trim().Length < 2)
        {
            _personSuggestGeneration++;   // invalidate anything in flight
            ClosePersonSuggestions();
            return;
        }
        _personTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreatePersonTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await LookupPeopleAsync();
        };
        return timer;
    }

    /// <summary>Fetches suggestions for the current text. Guarded twice — by generation and by
    /// re-reading the box — because <c>WithRetry</c> can hold a stalled lookup for seconds, so
    /// an older request can land after a newer one. Unguarded, typing "tom" then "hanks" can
    /// leave the Hanks list replaced by tom-matches while the box reads "hanks", and Enter then
    /// commits a person the user never saw.</summary>
    private async Task LookupPeopleAsync()
    {
        var generation = ++_personSuggestGeneration;
        var profileKey = _app.ActiveSessionKey;
        var term = PersonBox.Text.Trim();
        if (term.Length < 2)
            return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=person_lookup outcome=start generation={generation} term_length={term.Length}");
        List<PersonEntry> people;
        try
        {
            people = await _app.Jellyfin.SearchPeopleAsync(term);
        }
        catch (Exception ex)
        {
            if (generation != _personSuggestGeneration || PersonBox.Text.Trim() != term
                || profileKey != _app.ActiveSessionKey)
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=person_lookup outcome=stale generation={generation} phase=exception elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}");
            else
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=person_lookup outcome=failure generation={generation} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length} error={ex.GetType().Name}");
            // Degrade the way the genre facet does (LoadGenresAsync swallows its failure): a
            // facet lookup must not seize StatusText, which is the results region's channel.
            if (generation == _personSuggestGeneration)
                ClosePersonSuggestions();
            return;
        }
        if (generation != _personSuggestGeneration || PersonBox.Text.Trim() != term
            || profileKey != _app.ActiveSessionKey)
        {
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=person_lookup outcome=stale generation={generation} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}");
            return;   // a newer keystroke owns the list now
        }

        PersonSuggestList.ItemsSource = people;
        // PersonEntry.ToString() returns the name, so a plain row renders it and the UIA
        // ListItem name is the person's name for free.
        PersonSuggestStatus.Visibility = people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PersonPopup.IsOpen = true;
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=person_lookup outcome=success generation={generation} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length} count={people.Count}");
    }

    private void ClosePersonSuggestions()
    {
        PersonPopup.IsOpen = false;
        PersonSuggestList.ItemsSource = null;
        PersonSuggestStatus.Visibility = Visibility.Collapsed;
    }

    /// <summary>Arrow keys move the highlight without moving focus (the rows are not focusable,
    /// so the caret stays in the box); Enter commits; Esc closes.</summary>
    private void OnPersonKeyDown(object sender, KeyEventArgs e)
    {
        if (!PersonPopup.IsOpen)
            return;
        var count = PersonSuggestList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _personKeyNavigating = true;
                PersonSuggestList.SelectedIndex = (PersonSuggestList.SelectedIndex + 1) % count;
                _personKeyNavigating = false;
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _personKeyNavigating = true;
                PersonSuggestList.SelectedIndex =
                    (PersonSuggestList.SelectedIndex <= 0 ? count : PersonSuggestList.SelectedIndex) - 1;
                _personKeyNavigating = false;
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePersonSuggestions();
                e.Handled = true;
                break;
            case Key.Enter when PersonSuggestList.SelectedItem is PersonEntry picked:
                // Handled so Enter never also reaches the search box's apply-now handler.
                e.Handled = true;
                _ = CommitPersonAsync(picked);
                break;
        }
    }

    private void OnPersonLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ClosePersonSuggestions();

    private void OnPersonSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(PersonSuggestList, source) is not ListBoxItem { Content: PersonEntry picked })
            return;
        // Non-focusable rows do not run WPF's mouse selection. Activate explicitly,
        // including a row already highlighted by an arrow key, before focus can move.
        e.Handled = true;
        _ = CommitPersonAsync(picked);
    }

    /// <summary>Selecting a suggestion commits it. Arrow-key highlighting also raises
    /// SelectionChanged, so that path sets <c>_personKeyNavigating</c> to opt out and commits on
    /// Enter instead.
    /// <para>Deliberately NOT gated on <c>IsMouseOver</c>, which was the first shape here: a UIA
    /// <c>SelectionItemPattern.Select()</c> sets no mouse state, so a mouse-only guard makes the
    /// commit undrivable by the live suite — untestable by construction.</para></summary>
    private void OnPersonSuggestionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_personKeyNavigating || PersonSuggestList.SelectedItem is not PersonEntry picked)
            return;
        _ = CommitPersonAsync(picked);
    }

    /// <summary>Applies a picked person. Replaces rather than appends: Jellyfin ANDs
    /// <c>PersonIds</c>, so appending intersects and usually collapses the results to nothing,
    /// and the chip can only display one name. The contract keeps a list, so multi-person could
    /// be layered on later without a schema change. Deliberately does NOT touch the type chips —
    /// the deep link forces Movie+Series, but an in-band pick respects the user's types.</summary>
    private async Task CommitPersonAsync(PersonEntry picked)
    {
        Diagnostics.AppLog.Detail("advanced-search", $"event=interaction action=select-person item={picked.Id:N}");
        _personIds.Clear();
        _personIds.Add(picked.Id);
        _personName = picked.Name;
        _personSuggestGeneration++;   // nothing in flight may overwrite the committed list
        _personTimer?.Stop();
        ClearPersonBox();
        ClosePersonSuggestions();
        PersonBox.Focus();            // retain typing focus after the transient picker closes
        UpdateSeedChip();             // the applied person shows as the same garnet chip
        await ResetAndReloadAsync();
    }

    /// <summary>Empties the picker without re-arming the debounce.</summary>
    private void ClearPersonBox()
    {
        _suppressPersonText = true;
        PersonBox.Text = "";
        _suppressPersonText = false;
        PersonPlaceholder.Visibility = Visibility.Visible;
        PersonSuggestList.SelectedIndex = -1;
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _initializing)
            return;
        _termTimer?.Stop();
        await ResetAndReloadAsync();
    }

    private void OnYearTextChanged(object sender, TextChangedEventArgs e)
    {
        MinYearPlaceholder.Visibility = MinYearBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MaxYearPlaceholder.Visibility = MaxYearBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnYearKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_initializing)
            await ResetAndReloadAsync();
    }

    private async void OnYearCommitted(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
            await ResetAndReloadAsync();
    }

    // ---- Prompt-state affordances ----

    private async void OnStarterMovies(object sender, RoutedEventArgs e) => await StartTyped(BaseItemKind.Movie);
    private async void OnStarterSeries(object sender, RoutedEventArgs e) => await StartTyped(BaseItemKind.Series);
    private async void OnStarterEpisodes(object sender, RoutedEventArgs e) => await StartTyped(BaseItemKind.Episode);

    private async Task StartTyped(BaseItemKind kind)
    {
        Diagnostics.AppLog.Detail("advanced-search", $"event=interaction action=starter type={kind}");
        TypeFilter.SetSelectedKeys([kind.ToString()]);
        await ResetAndReloadAsync();
    }

    private async void OnClearAllFilters(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("advanced-search", "event=interaction action=clear-filters");
        _initializing = true;   // bulk clear: one reload at the end, not one per control
        _termTimer?.Stop();
        _personTimer?.Stop();
        _personSuggestGeneration++;
        ClearPersonBox();
        ClosePersonSuggestions();
        SearchBox.Text = "";
        TypeFilter.SetSelectedKeys([]);
        GenreFilter.SetSelectedKeys([]);
        MinYearBox.Text = "";
        MaxYearBox.Text = "";
        WatchedFilter.SelectedIndex = 0;
        _personIds.Clear();
        _personName = null;
        _seedGenre = null;
        _initializing = false;
        await ResetAndReloadAsync();
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("advanced-search", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            list.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }

    /// <summary>The filter band sits outside the results scroller, so the wheel is dead
    /// over it; forward it to whichever results surface is active (scroll-wheel audit).</summary>
    private void OnFilterWheel(object sender, MouseWheelEventArgs e)
    {
        var target = _mixedMode
            ? SectionsScroll
            : _gridScroll ??= WheelScroll.FindScrollViewer(ItemsGrid);
        WheelScroll.ForwardTo(target, e);
    }
}
