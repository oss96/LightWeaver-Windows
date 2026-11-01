using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

public sealed partial class LibraryPage : UserControl, INavPage
{
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);
    private const double CaptionHeight = 44;
    private readonly IAppShell _shell;
    private readonly MediaItem _library;
    private readonly string _profileKey;
    private readonly ItemRemovals _removals;
    private readonly UserDataOverrides _overrides = new();
    private IReadOnlyList<MediaItem> _all = [];
    private IReadOnlyList<MediaItem> _shown = [];
    private List<string>? _genres;
    private LibraryQuery _query = LibraryQuery.Default;
    private LibraryView _view;
    private int _total;
    private bool _loading;
    private bool _loaded;
    private DateTime? _itemsAtUtc;
    private CancellationTokenSource? _loadCts;

    /// <summary>The library this page shows, or a collection (box set) whose titles it lists.</summary>
    internal MediaItem Library => _library;

    /// <summary>A collection (box set): its titles are linked to it rather than filed under it, so the
    /// genre filter reads their genres, and it has its own empty state.</summary>
    private bool IsCollection => _library.Type == BaseItemDto_Type.BoxSet;

    public LibraryPage(IAppShell shell, MediaItem library)
    {
        _shell = shell;
        _library = library;
        _profileKey = shell.CacheProfileKey;
        _removals = ItemRemovals.ForProfile(_profileKey);
        _view = LibraryViewStore.Load(_profileKey, library.Id);
        InitializeComponent();
        TitleText.Text = library.LibraryDisplayName;
        Skeleton.ItemsSource = Enumerable.Range(0, 40).ToList();
        if (shell.Model.Jellyfin.ServerUrl is { } server && Uri.TryCreate(server + "/web/#/dashboard", UriKind.Absolute, out var dashboard)) DashboardLink.NavigateUri = dashboard;
        Header.SizeChanged += (_, _) => LayoutHeader();
        Scroller.ViewChanged += (_, _) => UpdateActiveLetter();
        SizeChanged += (_, _) => Rail.Compressed = XamlRoot is { } root && root.Size.Height < 700;
        ApplyLayout();
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyTexts(); Show(); }));
        Loaded += (_, _) => { if (!_loaded && !LoadRunning) _ = LoadAsync(); };
        Unloaded += (_, _) => _loadCts?.Cancel();
    }

    /// <summary>A load is under way and was not cancelled by leaving the page; a cancelled one still
    /// unwinding is replaced when the page comes back, and its own completion leaves the state to
    /// the replacement.</summary>
    private bool LoadRunning => _loading && _loadCts is { IsCancellationRequested: false };

    public void OnShown() { if (_loaded) _ = LoadAsync(); }

    public void Reload() => _ = LoadAsync();

    public DateTime? CachedAtUtc => _itemsAtUtc;

    public void LoadCached() { if (!_loaded && !LoadRunning) _ = LoadAsync(); }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        var kept = _all.Where(i => !ids.Contains(i.Id)).ToList();
        _total = Math.Max(kept.Count, _total - _all.Count + kept.Count);
        _all = kept;
        _genres = null;
        Show();
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _loading = true;
        LoadingRing.IsActive = !_loaded;
        if (!_loaded)
        {
            _ = Task.Delay(SkeletonDelay).ContinueWith(_ =>
            {
                if (_loading && _all.Count == 0 && !cts.IsCancellationRequested) SkeletonScroller.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        try
        {
            await LibraryLoader.LoadAsync(_shell.Model.Jellyfin, _profileKey, _library.Id, (items, total, done, atUtc) =>
            {
                if (cts.IsCancellationRequested || _profileKey != _shell.CacheProfileKey) return;
                var filtered = _removals.FilterPage(items, total);
                _all = filtered.Items;
                _total = filtered.TotalCount;
                _itemsAtUtc = atUtc;
                if (done) _loaded = true;
                SkeletonScroller.Visibility = Visibility.Collapsed;
                Show();
                _shell.CachedDataShown(this);
            }, cts.Token, cachedOnly: _shell.Connectivity.IsOffline);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _shell.ReportFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(cts, _loadCts))
            {
                _loading = false;
                LoadingRing.IsActive = false;
                SkeletonScroller.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Lists the items through the current query. The same items in the same order (the
    /// cached copy, then the server's) update the cards in place and keep the scroll position.</summary>
    private void Show()
    {
        var shown = _query.Apply(_all);
        Guid? keep = null;
        if (Items.ItemsSource is not null && ItemLists.SameIds(_shown, shown))
        {
            _shown = shown;
            MediaCard.RefreshRealized(Items, shown);
        }
        else
        {
            var firstVisible = _shown.Count > 0 ? TopIndex() : 0;
            keep = firstVisible > 0 && _shown.Count > firstVisible ? _shown[firstVisible].Id : null;
            _shown = shown;
            Items.ItemsSource = _shown;
        }
        var nameSorted = _query.Sort == LibrarySort.Name;
        Rail.Visibility = nameSorted && _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Rail.SetPresent(_shown.Select(LibraryQuery.Letter).Distinct());
        Rail.IsHitTestVisibleRail = _shown.Count > 0;
        CountText.Text = _query.IsFiltered ? L.Format("Library.CountFiltered", _shown.Count.ToString("N0", CultureInfo.CurrentCulture), CountAll()) : CountAll();
        ShowEmptyState();
        UpdateChips();
        if (keep is { } id && _shown.ToList().FindIndex(i => i.Id == id) is var index and >= 0) BringIntoView(index);
        UpdateActiveLetter();
    }

    private string CountAll()
    {
        var count = _total.ToString("N0", CultureInfo.CurrentCulture);
        return _library.CollectionType switch
        {
            "movies" => L.Format("Library.CountMovies", count),
            "tvshows" => L.Format("Library.CountShows", count),
            _ => L.Format("Library.CountItems", count),
        };
    }

    private void ShowEmptyState()
    {
        var empty = _shown.Count == 0 && (_loaded || _all.Count > 0);
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        var filtered = _query.IsFiltered && _all.Count > 0;
        EmptyIcon.Glyph = filtered ? Theme.Icons.FilterAltOff : Theme.Icons.VideoLibrary;
        EmptyTitle.Text = filtered ? L.Text("Library.NothingMatches") : IsCollection ? L.Text("Library.CollectionEmpty") : L.Text("Library.Empty");
        EmptyText.Text = filtered ? string.Join(" · ", ActiveFilterNames()) : IsCollection ? L.Text("Library.CollectionEmptyHint") : L.Text("Library.EmptyHint");
        ClearFiltersButton.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        DashboardLink.Visibility = filtered || IsCollection || DashboardLink.NavigateUri is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private IEnumerable<string> ActiveFilterNames()
    {
        if (_query.Watched == LibraryWatched.Unplayed) yield return L.Text("Library.WatchedUnplayed");
        if (_query.Watched == LibraryWatched.Played) yield return L.Text("Library.WatchedPlayed");
        if (_query.SelectedGenres.Count > 0) yield return string.Join(", ", _query.SelectedGenres);
    }

    private void UpdateChips()
    {
        ChipRow.Children.Clear();
        if (_query.SelectedGenres.Count > 0)
        {
            var remove = Ui.TokenRemoveButton(9);
            var label = L.Format("Library.RemoveGenres", string.Join(", ", _query.SelectedGenres));
            AutomationProperties.SetName(remove, label);
            AutomationProperties.SetAutomationId(remove, "LibraryRemoveGenres");
            ToolTipService.SetToolTip(remove, label);
            remove.Click += (_, _) => Edit(_query with { Genres = [] });
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = string.Join(", ", _query.SelectedGenres), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(remove);
            ChipRow.Children.Add(new Border
            {
                Height = 24,
                Padding = new Thickness(10, 0, 4, 0),
                CornerRadius = Ui.Resource<CornerRadius>("LwTokenRadius"),
                Background = Ui.Resource<Brush>("LwButtonBrush"),
                BorderBrush = Ui.Resource<Brush>("LwButtonBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = panel,
            });
        }
        ChipRow.Margin = new Thickness(24, 0, 24, ChipRow.Children.Count > 0 ? 14 : 0);
    }

    private void Edit(LibraryQuery query)
    {
        _query = query;
        ApplyTexts();
        Show();
        if (query.Sort == LibrarySort.Name || query.IsFiltered) Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not MediaCard card) return;
        card.Shell = _shell;
        card.Session = _shell.Model.Jellyfin;
        card.Kind = CardKind.Poster;
        card.Overrides = _overrides;
        card.Subtitle = CardLine;
        card.ShowCaption = _view.ShowTitles;
        card.Item = _shown[e.Index];
    }

    /// <summary>When an upcoming episode airs or that it is missing, else the year.</summary>
    private static string CardLine(MediaItem item)
        => DetailModel.Availability(item, DateTimeOffset.Now) is { Length: > 0 } availability ? availability
            : item.ProductionYear?.ToString(CultureInfo.CurrentUICulture) ?? "";

    private void OnWatchedChecked(object sender, RoutedEventArgs e)
    {
        var watched = ReferenceEquals(sender, WatchedUnplayed) ? LibraryWatched.Unplayed : ReferenceEquals(sender, WatchedPlayed) ? LibraryWatched.Played : LibraryWatched.All;
        if (watched != _query.Watched) Edit(_query with { Watched = watched });
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        WatchedAll.IsChecked = true;
        Edit(_query with { Watched = LibraryWatched.All, Genres = [] });
    }

    /// <summary>Multi-select genres; toggling keeps the flyout open and updates the grid behind it.</summary>
    private async void OnGenres(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel { Spacing = 0, MinWidth = 220 };
        var flyout = FastUi.Flyout(new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = new ScrollViewer { Content = list, MaxHeight = 380, Padding = new Thickness(0, 0, 8, 0) } });
        AutomationProperties.SetAutomationId(flyout, "LibraryGenreFlyout");
        try
        {
            if (!IsCollection) _genres ??= _shell.Connectivity.IsOffline
                ? _all.SelectMany(i => i.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList()
                : await _shell.Model.Jellyfin.GetGenresAsync(_library.Id);
        }
        catch (Exception ex)
        {
            _shell.ReportFailure(ex);
            return;
        }
        var counts = _all.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var genre in (_genres ?? [.. counts.Keys.Order(StringComparer.CurrentCultureIgnoreCase)]).Where(counts.ContainsKey))
        {
            var box = new CheckBox
            {
                Content = L.Format("Library.GenreWithCount", genre, counts[genre].ToString("N0", CultureInfo.CurrentCulture)),
                IsChecked = _query.SelectedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase),
                MinWidth = 0,
            };
            AutomationProperties.SetAutomationId(box, "LibraryGenreOption");
            AutomationProperties.SetName(box, genre);
            box.Click += (_, _) =>
            {
                var selected = _query.SelectedGenres.Where(g => !string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)).ToList();
                if (box.IsChecked == true) selected.Add(genre);
                Edit(_query with { Genres = selected });
            };
            list.Children.Add(box);
        }
        flyout.ShowAt(GenreButton);
    }

    /// <summary>Two groups, key and order; choosing applies at once and the flyout stays open for
    /// the second choice (design 02, 2b).</summary>
    private void OnSort(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 0, MinWidth = 200 };
        panel.Children.Add(new TextBlock { Text = L.Text("Library.SortBy"), Style = Ui.Resource<Style>("LwFilterLabel"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (var sort in Enum.GetValues<LibrarySort>())
        {
            var radio = new RadioButton { Content = LibraryQuery.SortName(sort), GroupName = "LibrarySortKey", IsChecked = sort == _query.Sort, MinWidth = 0 };
            AutomationProperties.SetAutomationId(radio, "LibrarySort" + sort);
            radio.Checked += (_, _) => Edit(_query with { Sort = sort });
            panel.Children.Add(radio);
        }
        panel.Children.Add(new Border { Height = 1, Background = Ui.Resource<Brush>("LwSeparatorBrush"), Margin = new Thickness(0, 6, 0, 6) });
        foreach (var descending in new[] { false, true })
        {
            var radio = new RadioButton { Content = descending ? L.Text("Library.Descending") : L.Text("Library.Ascending"), GroupName = "LibrarySortOrder", IsChecked = descending == _query.Descending, MinWidth = 0 };
            AutomationProperties.SetAutomationId(radio, descending ? "LibraryDescending" : "LibraryAscending");
            radio.Checked += (_, _) => Edit(_query with { Descending = descending });
            panel.Children.Add(radio);
        }
        var flyout = FastUi.Flyout(new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = panel });
        flyout.ShowAt(SortButton);
    }

    /// <summary>Poster size snaps to four steps and reflows live; titles can be hidden. Both are
    /// remembered for this library.</summary>
    private void OnDisplay(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8, Width = 240 };
        panel.Children.Add(new TextBlock { Text = L.Text("Library.PosterSize"), Style = Ui.Resource<Style>("LwFilterLabel") });
        var slider = new Slider { Minimum = 0, Maximum = LibraryViewStore.PosterWidths.Count - 1, StepFrequency = 1, TickFrequency = 1, TickPlacement = TickPlacement.Outside, SnapsTo = SliderSnapsTo.Ticks, Value = _view.SizeStep, IsThumbToolTipEnabled = false };
        AutomationProperties.SetAutomationId(slider, "LibraryPosterSize");
        AutomationProperties.SetName(slider, L.Text("Library.PosterSize"));
        slider.ValueChanged += (_, args) => SetView(_view with { SizeStep = (int)Math.Round(args.NewValue) });
        panel.Children.Add(slider);
        var titles = new ToggleSwitch { Header = L.Text("Library.ShowTitles"), IsOn = _view.ShowTitles, OnContent = "", OffContent = "" };
        AutomationProperties.SetAutomationId(titles, "LibraryShowTitles");
        titles.Toggled += (_, _) => SetView(_view with { ShowTitles = titles.IsOn });
        panel.Children.Add(titles);
        var flyout = FastUi.Flyout(new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = panel });
        flyout.ShowAt(DisplayButton);
    }

    private void SetView(LibraryView view)
    {
        if (view == _view) return;
        var keep = TopIndex();
        _view = view;
        LibraryViewStore.Save(_profileKey, _library.Id, view);
        ApplyLayout();
        Items.ItemsSource = null;
        Items.ItemsSource = _shown;
        if (keep > 0) BringIntoView(keep);
    }

    private void ApplyLayout()
    {
        var width = LibraryViewStore.PosterWidths[_view.SizeStep];
        var height = width * 1.5 + (_view.ShowTitles ? CaptionHeight : 0);
        GridLayout.MinItemWidth = SkeletonLayout.MinItemWidth = width;
        GridLayout.MinItemHeight = height;
        SkeletonLayout.MinItemHeight = width * 1.5 + CaptionHeight;
    }

    private void OnRailJump(object? sender, string letter)
    {
        if (letter.Length == 0) { Scroller.ChangeView(null, 0, null, disableAnimation: true); return; }
        if (LibraryQuery.FirstIndexOf(_shown, letter, _query.Descending) is { } index) BringIntoView(index);
    }

    private void BringIntoView(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        var element = Items.GetOrCreateElement(index);
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false, VerticalOffset = -4 });
    }

    /// <summary>Index of the first card in the topmost visible row.</summary>
    private int TopIndex()
    {
        var columns = Math.Max(1, (int)((Items.ActualWidth + GridLayout.MinColumnSpacing) / (GridLayout.MinItemWidth + GridLayout.MinColumnSpacing)));
        var rowHeight = Items.ActualWidth > 0 ? (Items.ActualWidth - (columns - 1) * GridLayout.MinColumnSpacing) / columns / GridLayout.MinItemWidth * GridLayout.MinItemHeight + GridLayout.MinRowSpacing : 0;
        return rowHeight <= 0 ? 0 : Math.Min(Math.Max(0, _shown.Count - 1), (int)(Scroller.VerticalOffset / rowHeight) * columns);
    }

    private void UpdateActiveLetter()
        => Rail.SetActive(_query.Sort == LibrarySort.Name && _shown.Count > 0 ? LibraryQuery.Letter(_shown[TopIndex()]) : null);

    /// <summary>The toolbar sits beside the title while both fit, else wraps under it (2g).</summary>
    private void LayoutHeader()
    {
        TitleBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Toolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var fits = TitleBlock.DesiredSize.Width + Toolbar.DesiredSize.Width + 12 <= Header.ActualWidth - Header.Padding.Left - Header.Padding.Right;
        Grid.SetRow(Toolbar, fits ? 0 : 1);
        Grid.SetColumn(Toolbar, fits ? 1 : 0);
        Toolbar.HorizontalAlignment = fits ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private void ApplyTexts()
    {
        TitleText.Text = _library.LibraryDisplayName;
        GenreText.Text = _query.SelectedGenres.Count > 0 ? L.Format("Library.GenreSelected", string.Join(", ", _query.SelectedGenres)) : L.Text("Library.GenreAll");
        SortText.Text = LibraryQuery.SortName(_query.Sort);
        SortArrow.Glyph = _query.Descending ? Theme.Icons.ArrowDownward : Theme.Icons.ArrowUpward;
        AutomationProperties.SetName(SortButton, L.Format("Library.SortedBy", SortText.Text, _query.Descending ? L.Text("Library.Descending") : L.Text("Library.Ascending")));
        AutomationProperties.SetName(GenreButton, GenreText.Text);
        var display = L.Text("Library.DisplayOptions");
        ToolTipService.SetToolTip(DisplayButton, display);
        AutomationProperties.SetName(DisplayButton, display);
        Rail.ApplyTexts();
    }
}
