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

    private static readonly (string Label, string Plural, BaseItemKind Kind)[] TypeChoices =
    [
        ("Movies", "movies", BaseItemKind.Movie),
        ("Series", "series", BaseItemKind.Series),
        ("Episodes", "episodes", BaseItemKind.Episode),
    ];

    private static readonly (string Label, ItemSortBy Sort)[] SortChoices =
    [
        ("Name", ItemSortBy.SortName),
        ("Date added", ItemSortBy.DateCreated),
        ("Premiere date", ItemSortBy.PremiereDate),
        ("Community rating", ItemSortBy.CommunityRating),
        ("Random", ItemSortBy.Random),
    ];

    private static readonly (string Label, ItemFilter? Filter)[] WatchedChoices =
    [
        ("All", null),
        ("Unwatched", ItemFilter.IsUnplayed),
        ("Watched", ItemFilter.IsPlayed),
    ];

    private readonly AppViewModel _app;
    private readonly ObservableCollection<MediaItem> _items = [];
    private readonly List<Guid> _personIds;
    private string? _personName;
    private string? _seedGenre;

    private bool _mixedMode;
    private int _totalCount;
    private bool _loadingMore;
    private bool _initializing = true;
    private int _loadGeneration;
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
        _app = app;
        seed ??= new AdvancedSearchQuery();
        _personIds = seed.PersonIds.ToList();
        _personName = seed.PersonName;
        // The genre chip marks a deep-link seed (M6 passes exactly one genre); a person
        // seed takes the chip slot instead when both are present.
        _seedGenre = _personIds.Count == 0 && seed.Genres.Count == 1 ? seed.Genres[0] : null;

        TypeFilter.Placeholder = "All types";
        TypeFilter.SetOptions(TypeChoices.Select(c => new CheckOption(c.Kind.ToString(), c.Label)));
        TypeFilter.SelectionChanged += OnFacetChanged;

        GenreFilter.Placeholder = "Any genre";
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

        Loaded += async (_, _) =>
        {
            // Load genres (and apply any seed selection) FIRST so the initial query includes
            // a seed genre from M6; then run the first query (prompt state if unconstrained).
            if (!_genresLoaded)
                await LoadGenresAsync(seed.Genres);
            if (_items.Count == 0 && _loadGeneration == 0)
                await ResetAndReloadAsync();
            SearchBox.Focus();   // P5: the on-screen field is the single search input
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
                $"event=facet_load outcome=failure facet=genres elapsed_ms={started.ElapsedMilliseconds}", ex);
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
            if (q.ItemTypes.Count <= 1)
            {
                var (items, total) = await _app.Jellyfin.AdvancedSearchAsync(q, 0, PageSize);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                {
                    Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale generation={generation} mode=single");
                    return;
                }
                _queryRunning = false;
                _mixedMode = false;
                _totalCount = total;
                _items.Clear();
                foreach (var item in items)
                    _items.Add(item);
                ResultSummary.Text = FormatSummary(total, null);
                ShowSingle();
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=success generation={generation} mode=single elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={total}");
            }
            else
            {
                // Mixed mode (P1): one capped query per type; the totals feed the summary.
                var kinds = q.ItemTypes.ToList();
                var tasks = kinds.Select(k =>
                    _app.Jellyfin.AdvancedSearchAsync(q with { ItemTypes = [k] }, 0, SectionCap)).ToList();
                var results = await Task.WhenAll(tasks);
                if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                {
                    Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale generation={generation} mode=mixed");
                    return;
                }
                _queryRunning = false;

                var sections = new List<(BaseItemKind Kind, List<MediaItem> Items, int Total)>();
                for (var i = 0; i < kinds.Count; i++)
                    if (results[i].TotalCount > 0)
                        sections.Add((kinds[i], results[i].Items, results[i].TotalCount));

                var grand = sections.Sum(s => s.Total);
                ResultSummary.Text = FormatSummary(grand,
                    kinds.Select((k, i) => (k, results[i].TotalCount)).ToList());

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
                $"event=load outcome=failure generation={generation} elapsed_ms={started.ElapsedMilliseconds}", ex);
            _queryRunning = false;
            SkeletonHost.Visibility = Visibility.Collapsed;
            ItemsGrid.Visibility = Visibility.Collapsed;
            SectionsScroll.Visibility = Visibility.Collapsed;
            HideCenter();
            StatusText.Text = $"Failed to load: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _queryRunning = false;
                SkeletonHost.Visibility = Visibility.Collapsed;
            }
        }
    }

    private bool _queryRunning;

    private async void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_mixedMode || _loadingMore || _items.Count >= _totalCount)
            return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200)
            return;

        _loadingMore = true;
        // Append-owned: hidden unconditionally in the finally. See LibraryView.OnScroll.
        MoreLoading.Show();
        var generation = _loadGeneration;
        var profileKey = _app.ActiveSessionKey;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("advanced-search",
            $"event=load outcome=start mode=append generation={generation} offset={_items.Count}");
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var (more, total) = await _app.Jellyfin.AdvancedSearchAsync(BuildQuery(), _items.Count, PageSize);
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("advanced-search", $"event=load outcome=stale mode=append generation={generation}");
                return;
            }
            _totalCount = total;
            foreach (var item in more)
                _items.Add(item);
            Diagnostics.AppLog.Detail("advanced-search",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || profileKey != _app.ActiveSessionKey)
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=stale mode=append generation={generation} phase=exception elapsed_ms={started.ElapsedMilliseconds}");
            else
                Diagnostics.AppLog.Detail("advanced-search",
                    $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds}", ex);
            // next scroll retries
        }
        finally
        {
            _loadingMore = false;
            MoreLoading.Hide();
        }
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
        EmptyView.Show("Search everything",
            "Combine text with type, genre, year and watched filters — across every library.");
        StarterChips.Visibility = Visibility.Visible;
        ClearFiltersBtn.Visibility = Visibility.Collapsed;
    }

    private void ShowSingle()
    {
        SkeletonHost.Visibility = Visibility.Collapsed;
        SectionsScroll.Visibility = Visibility.Collapsed;
        ItemsGrid.Visibility = Visibility.Visible;
        if (_items.Count == 0)
        {
            EmptyView.Show("Nothing here", "No items match these filters - try loosening them.");
            StarterChips.Visibility = Visibility.Collapsed;
            ClearFiltersBtn.Visibility = Visibility.Visible;
        }
        else
        {
            HideCenter();
            AnimateIn(ItemsGrid, 0);
        }
    }

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
        var head = total == 1 ? "1 result" : string.Create(inv, $"{total} results");
        if (breakdown is null)
            return head;
        var parts = new List<string> { head };
        foreach (var (kind, count) in breakdown)
        {
            var choice = TypeChoices.First(c => c.Kind == kind);
            var noun = count == 1 && kind == BaseItemKind.Movie ? "movie"
                     : count == 1 && kind == BaseItemKind.Episode ? "episode"
                     : choice.Plural;   // "series" is invariant
            parts.Add(string.Create(inv, $"{count} {noun}"));
        }
        return string.Join(" · ", parts);
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
                Text = choice.Label,
                FontFamily = (FontFamily)FindResource("LwFontDisplay"),
                FontSize = 22,
                Foreground = (Brush)FindResource("LwText1Brush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(title, $"AsSectionHeader{kind}");
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);

            var count = new TextBlock
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"· {total}"),
                Style = (Style)FindResource("LwTypeMono"),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("LwText3Brush"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(count, Dock.Left);
            header.Children.Add(count);

            var showOnly = new Button
            {
                Style = (Style)FindResource("LwLinkButton"),
                FontSize = 13,
                Content = $"Show only {choice.Plural} →",
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(showOnly, $"AsShowOnly{kind}");
            var narrowKind = kind;
            showOnly.Click += async (_, _) =>
            {
                TypeFilter.SetSelectedKeys([narrowKind.ToString()]);   // programmatic: no double reload
                await ResetAndReloadAsync();
            };
            DockPanel.SetDock(showOnly, Dock.Right);
            header.Children.Add(showOnly);

            var list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemTemplateSelector = (DataTemplateSelector)FindResource("MediaCardSelector"),
                ItemContainerStyle = (Style)FindResource("LwMediaGridItem"),
                Template = (ControlTemplate)Resources["PlainList"],
                ItemsSource = items,
                ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(WrapPanel))),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(list, $"AsSection{kind}");
            list.SelectionChanged += OnSelection;

            var section = new StackPanel();
            section.Children.Add(header);
            section.Children.Add(list);
            AnimateIn(section, index * 60);
            SectionsHost.Children.Add(section);
            index++;
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
            SeedCaption.Text = "PERSON";
            SeedChipText.Text = _personName ?? "Person";
            SeedFacet.Visibility = Visibility.Visible;
            return;
        }
        // Only sync chip→dropdown once the async genre list has actually been applied —
        // at construction time the dropdown is empty and the seed would self-clear.
        if (_seedGenre is not null && GenreFilter.HasOptions && !GenreFilter.SelectedKeys.Contains(_seedGenre))
            _seedGenre = null;   // user unchecked the seeded genre — the chip follows
        if (_seedGenre is not null)
        {
            SeedCaption.Text = "GENRE";
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
        OrderLabel.Text = sort switch
        {
            ItemSortBy.SortName => desc ? "Z–A" : "A–Z",
            ItemSortBy.DateCreated or ItemSortBy.PremiereDate => desc ? "Newest" : "Oldest",
            ItemSortBy.CommunityRating => desc ? "Highest" : "Lowest",
            _ => desc ? "Descending" : "Ascending",
        };
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
                    $"event=person_lookup outcome=failure generation={generation} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}", ex);
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
