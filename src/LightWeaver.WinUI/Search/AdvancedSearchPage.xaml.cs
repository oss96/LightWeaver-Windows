using System.ComponentModel;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Search;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Search;

public sealed partial class AdvancedSearchPage : UserControl, INavPage
{
    /// <summary>Below this page width the filter panel is not docked (design 05, 5d).</summary>
    public const double DockWidth = 1100;
    private const double PanelWidth = 300;

    private readonly AppViewModel _app;
    private readonly IAppShell _shell;
    private readonly AdvancedSearchModel _model;
    private List<string>? _genres;
    private bool _syncing;
    private readonly UserDataOverrides _overrides = new();
    private bool _panelOpen = true;
    private bool _docked = true;
    private bool? _layoutDocked;
    private bool? _layoutOpen;
    private bool _checkingMore;
    private bool _resultsUpdateQueued;
    private bool _refreshCards;

    public AdvancedSearchPage(IAppShell shell, AdvancedSearchFilters? seed = null)
    {
        _shell = shell;
        _app = shell.Model;
        _model = new AdvancedSearchModel((q, start, limit, ct) => _app.Jellyfin.AdvancedSearchAsync(q, start, limit, ct), overrides: _overrides,
            removals: ItemRemovals.ForProfile(shell.CacheProfileKey), cache: QueryCache.Searches, profileKey: shell.CacheProfileKey);
        if (seed is not null) _model.Filters = seed;
        InitializeComponent();

        Results.ItemsSource = _model.Items;
        _model.PropertyChanged += OnModelChanged;
        _model.Items.CollectionChanged += (_, _) => QueueResultsPass();
        _model.Answered += (_, _) =>
        {
            ReloadCards();
            _shell.CachedDataShown(this);
        };
        _model.Failed += (_, _) => _shell.CachedDataShown(this);
        _model.Resynced += (_, _) => ReloadCards();
        GenreBox.Suggest = SuggestGenresAsync;
        PersonBox.Suggest = SuggestPeopleAsync;
        GenreBox.ValuesChanged += (_, _) => Edit(f => f with { Genres = GenreBox.Values.Select(v => (string)v.Value).ToArray() });
        PersonBox.ValuesChanged += (_, _) => Edit(f => f with { People = PersonBox.Values.Select(v => (SearchPerson)v.Value).ToArray() });
        GenreBox.InputAutomationId = "GenreInput";
        PersonBox.InputAutomationId = "PersonInput";
        YearSlider.Minimum = AdvancedSearchFilters.YearFloor;
        YearSlider.Maximum = AdvancedSearchFilters.YearCeiling;
        YearSlider.RangeChanged += (_, _) => Edit(f => f.WithYears((int)YearSlider.LowerValue, (int)YearSlider.UpperValue));
        AutomationProperties.SetAutomationId(YearSlider.LowerThumb, "YearFromThumb");
        AutomationProperties.SetAutomationId(YearSlider.UpperThumb, "YearToThumb");
        SizeChanged += (_, _) => { UpdateLayoutMode(); CheckLoadMore(); };
        Loaded += OnLoaded;
        Unloaded += (_, _) => L.Source.PropertyChanged -= OnLanguageChanged;

        ApplyLabels();
        SyncControls();
        UpdateResults();
    }

    /// <summary>Text typed in the top bar's search field; combines with the filters.</summary>
    public string Term { get => _model.Term; set => _model.Term = value; }

    public void RemoveItems(IReadOnlySet<Guid> ids) => _model.RemoveItems(ids);

    public DateTime? CachedAtUtc => _model.ShownAtUtc;

    public void LoadCached() { if (!_model.HasResults && !_model.HasFailed) _ = _model.RefreshNowAsync(); }

    public void Reload() => _ = _model.ReloadAsync();

    public bool IsPanelOpen => _panelOpen;

    /// <summary>The panel opened or closed; the top bar keeps its tune button highlighted while open.</summary>
    public event EventHandler? PanelOpenChanged;

    /// <summary>The panel closed while it had focus and the page has nowhere to put it (docked):
    /// the window returns focus to the top bar's tune button.</summary>
    public event EventHandler? FocusReturnRequested;

    public void SetPanelOpen(bool open)
    {
        if (_panelOpen == open) return;
        var hadFocus = !open && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f && IsInside(f, Panel);
        _panelOpen = open;
        UpdateLayoutMode();
        PanelOpenChanged?.Invoke(this, EventArgs.Empty);
        if (open) FocusFirstFilter();
        else if (hadFocus)
        {
            if (_docked) FocusReturnRequested?.Invoke(this, EventArgs.Empty);
            else DrawerButton.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Opening from a person's page fills in that person and nothing else: the other
    /// filters and the typed text are cleared.</summary>
    public void ShowPerson(Guid id, string name)
    {
        _model.Term = "";
        _model.Filters = AdvancedSearchFilters.ForPerson(id, name);
        SyncControls();
    }

    /// <summary>Opens with these filters and text, from More filters, More like this or a seed.</summary>
    public void ShowFilters(AdvancedSearchFilters filters, string term)
    {
        _model.Term = term;
        _model.Filters = filters;
        SyncControls();
    }

    /// <summary>Queries on first showing, and again only after a failed request: returning through
    /// Back keeps the results and the scroll position (changes made meanwhile query by themselves).</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        L.Source.PropertyChanged += OnLanguageChanged;
        UpdateLayoutMode();
        if (_panelOpen) FocusFirstFilter();
        if (!_model.HasResults || _model.HasFailed) await _model.RefreshNowAsync();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationSource.Revision)) return;
        ApplyLabels();
        SyncControls();
        UpdateResults();
        // Realized cards: subtitles carry localized numbers, buttons localized names.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(Results); i++)
            if (VisualTreeHelper.GetChild(Results, i) is MediaCard card) card.Refresh();
    }

    // ---- filters ---------------------------------------------------------------------------

    private void Edit(Func<AdvancedSearchFilters, AdvancedSearchFilters> change)
    {
        if (_syncing) return;
        _model.Filters = change(_model.Filters);
        SyncControls();
    }

    private void ApplyLabels()
    {
        TypeMovies.Content = AdvancedSearchFilters.TypeName(SearchType.Movies);
        TypeShows.Content = AdvancedSearchFilters.TypeName(SearchType.Shows);
        TypeEpisodes.Content = AdvancedSearchFilters.TypeName(SearchType.Episodes);
        TypeCollections.Content = AdvancedSearchFilters.TypeName(SearchType.Collections);
        WatchedAll.Content = AdvancedSearchFilters.WatchedName(WatchedFilter.All);
        WatchedUnplayed.Content = AdvancedSearchFilters.WatchedName(WatchedFilter.Unplayed);
        WatchedPlayed.Content = AdvancedSearchFilters.WatchedName(WatchedFilter.Played);
        GenreBox.PlaceholderText = L.Text("AdvancedSearch.AddGenre");
        GenreBox.InputName = L.Text("AdvancedSearch.Genre");
        PersonBox.PlaceholderText = L.Text("AdvancedSearch.AddPerson");
        PersonBox.InputName = L.Text("AdvancedSearch.Person");
        AutomationProperties.SetName(YearSlider.LowerThumb, L.Text("AdvancedSearch.FromYear"));
        AutomationProperties.SetName(YearSlider.UpperThumb, L.Text("AdvancedSearch.ToYear"));
    }

    /// <summary>Puts the current filters into the controls without feeding the change back.</summary>
    private void SyncControls()
    {
        var f = _model.Filters;
        _syncing = true;
        try
        {
            TypeMovies.IsChecked = f.Types.Contains(SearchType.Movies);
            TypeShows.IsChecked = f.Types.Contains(SearchType.Shows);
            TypeEpisodes.IsChecked = f.Types.Contains(SearchType.Episodes);
            TypeCollections.IsChecked = f.Types.Contains(SearchType.Collections);
            GenreBox.SetValues(f.Genres.Select(g => new TokenSuggestion(g, g)));
            PersonBox.SetValues(f.People.Select(p => new TokenSuggestion(p.Name, p)));
            var min = f.MinYear ?? AdvancedSearchFilters.YearFloor;
            var max = f.MaxYear ?? AdvancedSearchFilters.YearCeiling;
            YearSlider.SetRange(min, max);
            if (MinYearBox.FocusState == FocusState.Unfocused) MinYearBox.Text = min.ToString(System.Globalization.CultureInfo.CurrentUICulture);
            if (MaxYearBox.FocusState == FocusState.Unfocused) MaxYearBox.Text = max.ToString(System.Globalization.CultureInfo.CurrentUICulture);
            YearValue.Text = f.IsActive(FilterKind.Years) ? L.Format("AdvancedSearch.YearsSpan", min, max) : L.Text("AdvancedSearch.Any");
            (f.Watched switch { WatchedFilter.Unplayed => WatchedUnplayed, WatchedFilter.Played => WatchedPlayed, _ => WatchedAll }).IsChecked = true;
            RatingSlider.Value = f.MinRating;
            RatingValue.Text = f.MinRating > 0 ? L.Format("AdvancedSearch.RatingValue", f.MinRating) : L.Text("AdvancedSearch.Any");
            SortLabel.Text = AdvancedSearchFilters.SortName(f.Sort);
            (f.Descending ? OrderDescending : OrderAscending).IsChecked = true;
            ResetButton.IsEnabled = !f.IsDefault;
        }
        finally { _syncing = false; }
        UpdateTokens();
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e) => Edit(f => f with
    {
        Types = new (CheckBox Box, SearchType Type)[]
            {
                (TypeMovies, SearchType.Movies), (TypeShows, SearchType.Shows),
                (TypeEpisodes, SearchType.Episodes), (TypeCollections, SearchType.Collections),
            }
            .Where(t => t.Box.IsChecked == true).Select(t => t.Type).ToArray(),
    });

    // The segment that raised Checked decides: the event can arrive after another filter change
    // has already re-synced the radio buttons.
    private void OnWatchedChanged(object sender, RoutedEventArgs e) => Edit(f => f with
    {
        Watched = ReferenceEquals(sender, WatchedUnplayed) ? WatchedFilter.Unplayed
            : ReferenceEquals(sender, WatchedPlayed) ? WatchedFilter.Played : WatchedFilter.All,
    });

    private void OnRatingChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Edit(f => f with { MinRating = Math.Round(e.NewValue * 2) / 2 });

    private void OnOrderChanged(object sender, RoutedEventArgs e) => Edit(f => f with { Descending = ReferenceEquals(sender, OrderDescending) });

    private void OnYearBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        CommitYears();
        e.Handled = true;
    }

    private void OnYearBoxCommitted(object sender, RoutedEventArgs e) => CommitYears();

    private void CommitYears()
    {
        var f = _model.Filters;
        var min = int.TryParse(MinYearBox.Text, out var a) ? a : f.MinYear ?? AdvancedSearchFilters.YearFloor;
        var max = int.TryParse(MaxYearBox.Text, out var b) ? b : f.MaxYear ?? AdvancedSearchFilters.YearCeiling;
        min = Math.Clamp(min, AdvancedSearchFilters.YearFloor, AdvancedSearchFilters.YearCeiling);
        max = Math.Clamp(max, AdvancedSearchFilters.YearFloor, AdvancedSearchFilters.YearCeiling);
        Edit(x => x.WithYears(min, max));
        MinYearBox.Text = (_model.Filters.MinYear ?? AdvancedSearchFilters.YearFloor).ToString(System.Globalization.CultureInfo.CurrentUICulture);
        MaxYearBox.Text = (_model.Filters.MaxYear ?? AdvancedSearchFilters.YearCeiling).ToString(System.Globalization.CultureInfo.CurrentUICulture);
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
        foreach (var sort in Enum.GetValues<SearchSort>())
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = AdvancedSearchFilters.SortName(sort),
                GroupName = "Sort",
                IsChecked = sort == _model.Filters.Sort,
            };
            AutomationProperties.SetAutomationId(item, "Sort" + sort);
            item.Click += (_, _) => Edit(f => f with { Sort = sort });
            menu.Items.Add(item);
        }
        menu.ShowAt(SortButton);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _model.Filters = AdvancedSearchFilters.Default;
        SyncControls();
    }

    private void OnRemoveHint(object sender, RoutedEventArgs e)
    {
        if (_model.Hint is { } hint) Edit(f => f.Without(hint.Kind));
    }

    private async Task<IReadOnlyList<TokenSuggestion>> SuggestGenresAsync(string term, CancellationToken token)
    {
        _genres ??= await _app.Jellyfin.GetGenresAsync();
        return _genres
            .Where(g => g.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(g => !g.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(g => g, StringComparer.CurrentCulture)
            .Take(10)
            .Select(g => new TokenSuggestion(g, g))
            .ToList();
    }

    private async Task<IReadOnlyList<TokenSuggestion>> SuggestPeopleAsync(string term, CancellationToken token)
    {
        await Task.Delay(150, token);
        var people = await _app.Jellyfin.SearchPeopleAsync(term, 8);
        return people.Select(p => new TokenSuggestion(p.Name, new SearchPerson(p.Id, p.Name))).ToList();
    }

    // ---- results ---------------------------------------------------------------------------

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateResults();

    private void UpdateResults()
    {
        var updating = _model.IsUpdating;
        Updating.Visibility = updating && _model.HasResults ? Visibility.Visible : Visibility.Collapsed;
        // The grid stays put and dims while a request is in flight; no skeleton swap.
        Results.Opacity = updating && !_model.IsReloading ? 0.6 : 1;
        CountText.Text = !_model.HasResults ? ""
            : _model.TotalCount == 1 ? L.Text("AdvancedSearch.OneResult")
            : L.Format("AdvancedSearch.Results", _model.TotalCount);

        var empty = _model.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty && _model.Hint is { } hint)
        {
            // The label is rebuilt from the filters so it follows a language change.
            EmptyHint.Text = L.Format("AdvancedSearch.HintWouldShow", _model.Filters.HintLabel(hint.Kind), hint.Count);
            RemoveHintButton.Content = hint.Kind switch
            {
                FilterKind.Types => L.Text("AdvancedSearch.RemoveTypeFilter"),
                FilterKind.Genres => L.Text("AdvancedSearch.RemoveGenreFilter"),
                FilterKind.People => L.Text("AdvancedSearch.RemovePersonFilter"),
                FilterKind.Years => L.Text("AdvancedSearch.RemoveYearFilter"),
                FilterKind.Watched => L.Text("AdvancedSearch.RemoveWatchedFilter"),
                _ => L.Text("AdvancedSearch.RemoveRatingFilter"),
            };
            RemoveHintButton.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Text = L.Text("AdvancedSearch.TryFewerFilters");
            RemoveHintButton.Visibility = Visibility.Collapsed;
        }
        UpdateDrawerLabel();
    }

    // "Filters · 5" with active filters, plain "Filters" without.
    private void UpdateDrawerLabel()
    {
        DrawerLabel.Text = _model.Filters.ActiveCount > 0
            ? L.Format("AdvancedSearch.FiltersCount", _model.Filters.ActiveCount)
            : L.Text("AdvancedSearch.Filters");
        AutomationProperties.SetName(DrawerButton, DrawerLabel.Text);
    }

    // Design: 24 px high, padding 0 4 0 10, radius 12, btnBg with a 1 px stroke, 12 px text.
    private void UpdateTokens()
    {
        TokenRow.Children.Clear();
        foreach (var token in _model.Filters.Tokens())
        {
            var remove = Ui.TokenRemoveButton(9);
            AutomationProperties.SetName(remove, L.Format("Controls.RemoveToken", token.Label));
            AutomationProperties.SetAutomationId(remove, "RemoveFilter" + token.Kind);
            remove.Click += (_, _) => Edit(f => f.Without(token.Kind));
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            panel.Children.Add(new TextBlock { Text = token.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(remove);
            TokenRow.Children.Add(new Border
            {
                Height = 24,
                Padding = new Thickness(10, 0, 4, 0),
                CornerRadius = new CornerRadius(12),
                Background = (Brush)Application.Current.Resources["LwButtonBrush"],
                BorderBrush = (Brush)Application.Current.Resources["LwButtonBorderBrush"],
                BorderThickness = new Thickness(1),
                Child = panel,
            });
        }
        TokenRow.Margin = new Thickness(24, 0, 24, TokenRow.Children.Count > 0 ? 16 : 0);
        ResetButton.IsEnabled = !_model.Filters.IsDefault;
        UpdateDrawerLabel();
    }

    /// <summary>Re-runs the query for what is loaded; the grid keeps its place and full opacity.</summary>
    private void OnRefresh(object sender, RoutedEventArgs e) => _ = _model.ReloadAsync();

    /// <summary>F5 refreshes while the page is on screen; under the full player the key is left
    /// to the player.</summary>
    private void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_shell.PlayerCoversPage) return;
        args.Handled = true;
        _ = _model.ReloadAsync();
    }

    /// <summary>The titles behind the cards may have changed: re-read every card on the next pass.</summary>
    private void ReloadCards()
    {
        _refreshCards = true;
        QueueResultsPass();
    }

    /// <summary>One pass per batch of changes (a page adds 60 at once, an answer may change
    /// nothing): it runs after the repeater has applied them, gives the realized cards the newest
    /// copies after an answer, and keeps loading while the results do not fill the viewport.</summary>
    private void QueueResultsPass()
    {
        if (_resultsUpdateQueued) return;
        _resultsUpdateQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resultsUpdateQueued = false;
            if (_refreshCards)
            {
                _refreshCards = false;
                RefreshCards();
            }
            UpdateResults();
            CheckLoadMore();
        });
    }

    /// <summary>Hands each realized card the newest copy of the title it shows, found by id.</summary>
    private void RefreshCards()
    {
        var newest = new Dictionary<Guid, MediaItem>();
        foreach (var item in _model.Items) newest.TryAdd(item.Id, item);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(Results); i++)
            if (VisualTreeHelper.GetChild(Results, i) is MediaCard { Item: { } shown } card && Results.GetElementIndex(card) >= 0
                && newest.TryGetValue(shown.Id, out var copy))
                card.Item = copy;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not MediaCard card) return;
        card.Session = _app.Jellyfin;
        card.Shell = _shell;
        card.Subtitle = Subtitle;
        card.Overrides = _overrides;
        card.Item = _model.Items[e.Index];
    }

    /// <summary>"2009 · ★ 7.3": year, then community rating when the server has one.</summary>
    private static string Subtitle(MediaItem item)
    {
        var year = item.ProductionYear?.ToString(System.Globalization.CultureInfo.CurrentUICulture) ?? "";
        return item.CommunityRating is { } r
            ? (year.Length > 0 ? L.Format("AdvancedSearch.CardYearRating", year, r) : L.Format("AdvancedSearch.CardRating", r))
            : year;
    }

    private void OnResultsScrolled(object? sender, ScrollViewerViewChangedEventArgs e) => CheckLoadMore();

    /// <summary>Loads the next page while the end of the list is within 800 px of the viewport,
    /// including when the loaded pages do not fill it.</summary>
    private async void CheckLoadMore()
    {
        if (_checkingMore || !IsLoaded) return;
        _checkingMore = true;
        try
        {
            while (_model.HasMore && !_model.IsUpdating
                   && ResultsScroller.VerticalOffset > ResultsScroller.ScrollableHeight - 800)
            {
                var before = _model.Items.Count;
                await _model.LoadMoreAsync();
                if (_model.Items.Count == before) break;
                ResultsScroller.UpdateLayout();
            }
        }
        finally { _checkingMore = false; }
    }

    // ---- panel -----------------------------------------------------------------------------

    private void UpdateLayoutMode()
    {
        _docked = ActualWidth == 0 || ActualWidth >= DockWidth;
        var showPanel = _panelOpen;
        if (_layoutDocked == _docked && _layoutOpen == showPanel) return;
        _layoutDocked = _docked;
        _layoutOpen = showPanel;
        PanelColumn.Width = new GridLength(_docked && showPanel ? PanelWidth : 0);
        Panel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(Panel, _docked ? 1 : 2);
        Canvas.SetZIndex(Panel, _docked ? 0 : 10);
        // Docked: the design's 2 % panel tint over the layer. Overlay: an opaque pane that light-dismisses
        // (the design's popover colour; its 3 % see-through relies on a blur WinUI panes do not have).
        Panel.Background = (Brush)Application.Current.Resources[_docked ? "LwPanelBrush" : "LwPaneBrush"];
        Scrim.Visibility = !_docked && showPanel ? Visibility.Visible : Visibility.Collapsed;
        DrawerButton.Visibility = _docked ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An opened panel takes focus on its first filter, so Esc works at once and no button
    /// in its header shows its tooltip over the top bar.</summary>
    private void FocusFirstFilter() => TypeMovies.Focus(FocusState.Programmatic);

    private void OnOpenPanel(object sender, RoutedEventArgs e) => SetPanelOpen(true);
    private void OnClosePanel(object sender, RoutedEventArgs e) => SetPanelOpen(false);
    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => SetPanelOpen(false);

    // Esc closes the panel when focus is inside it.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !_panelOpen) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, Panel))
        {
            SetPanelOpen(false);
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, container)) return true;
        return false;
    }
}
