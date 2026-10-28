using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Search;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

/// <summary>Live search results (design 04, 4b to 4d): typed into the top bar, 150 ms after the
/// last key. Groups flow side by side in a fixed order and clip at the trailing edge; category
/// chips with counts filter in place; More filters hands the query to Advanced Search. Each group
/// shows as soon as its own request answers and keeps the previous query's results until then;
/// groups, chips and cards are updated in place, so an unchanged card is never rebuilt.</summary>
public sealed partial class SearchPage : Grid, INavPage
{
    private const int GroupLimit = 8;
    private const double CardGap = 14;
    private readonly IAppShell _shell;
    private readonly ItemRemovals _removals;
    private readonly UserDataOverrides _overrides = new();
    private readonly TextBlock _heading = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _count = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Dictionary<SearchCategory, Chip> _chipViews = [];
    private readonly Dictionary<SearchCategory, GroupView> _views = [];
    private readonly Button _moreFilters;
    private readonly WrapPanel _groups = new() { HorizontalSpacing = 44, VerticalSpacing = 22 };
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _empty;
    private readonly TextBlock _emptyTitle = new() { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _emptyHint = new() { FontSize = 13, MaxWidth = 340, TextWrapping = TextWrapping.WrapWholeWords, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _emptyAdvanced;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounce;
    private QuickSearchResult? _result;
    private int? _headerTotal;
    private SearchCategory _category = SearchCategory.All;
    private CancellationTokenSource? _cts;
    private string _term = "";

    public SearchPage(IAppShell shell)
    {
        _shell = shell;
        _removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        AutomationProperties.SetAutomationId(_heading, "SearchHeading");
        AutomationProperties.SetHeadingLevel(_heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        AutomationProperties.SetAutomationId(_count, "SearchCount");
        _count.Foreground = Ui.Resource<Brush>("LwText2Brush");
        _emptyHint.Foreground = Ui.Resource<Brush>("LwText2Brush");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(_heading);
        title.Children.Add(_count);
        _moreFilters = new Button { Style = Ui.Resource<Style>("LwButton"), Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 0, 12, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetAutomationId(_moreFilters, "SearchMoreFilters");
        _moreFilters.Click += (_, _) => MoreFilters();
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chipRow.Children.Add(_chips);
        chipRow.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0), Background = Ui.Resource<Brush>("LwSeparatorBrush"), VerticalAlignment = VerticalAlignment.Center });
        chipRow.Children.Add(_moreFilters);
        var header = new StackPanel { Padding = new Thickness(24, 16, 24, 6), Spacing = 12 };
        header.Children.Add(title);
        header.Children.Add(new ScrollViewer { Content = chipRow, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false });
        Children.Add(header);

        _groups.Margin = new Thickness(24, 12, 24, 24);
        _scroller = new ScrollViewer { Content = _groups, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(_scroller, "SearchScroller");
        _scroller.SizeChanged += (_, _) => Show();
        SetRow(_scroller, 1);
        Children.Add(_scroller);

        _emptyAdvanced = new Button { Style = Ui.Resource<Style>("LwButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(_emptyAdvanced, "SearchEmptyAdvanced");
        _emptyAdvanced.Click += (_, _) => MoreFilters();
        AutomationProperties.SetAutomationId(_emptyTitle, "SearchEmptyTitle");
        _empty = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 24, 90), Visibility = Visibility.Collapsed };
        _empty.Children.Add(new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = Theme.Icons.Search, FontSize = 44, Foreground = Ui.Resource<Brush>("LwText3Brush") });
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyHint);
        _empty.Children.Add(_emptyAdvanced);
        SetRow(_empty, 1);
        Children.Add(_empty);

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = QuickSearch.Debounce;
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = LoadAsync(_term); };
        foreach (var category in QuickSearch.Chips) _chipViews[category] = CreateChip(category);
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTexts();
            _views.Clear();
            _groups.Children.Clear();
            Show();
        }));
        Unloaded += (_, _) => _cts?.Cancel();
    }

    /// <summary>The query in the top bar; results follow 150 ms after the last change.</summary>
    public string Term
    {
        get => _term;
        set
        {
            if (_term == value) return;
            _term = value;
            _heading.Text = L.Format("Search.ResultsFor", value.Trim());
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>The selected chip, which More filters turns into the Type filter.</summary>
    public SearchCategory Category => _category;

    /// <summary>Enter in the search field opens the first result on screen.</summary>
    public void OpenFirst()
    {
        if (_result?.First(_category) is { } first) Open(first);
    }

    public void Reload() => _ = LoadAsync(_term);

    private async Task LoadAsync(string term)
    {
        term = term.Trim();
        if (term.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var session = _shell.Model.Jellyfin;
        _result = QuickSearch.Start(_result, term);
        try
        {
            await QuickSearch.LoadAsync(async (category, ct) =>
            {
                var readAt = _removals.Mark();
                return FilterGroup(await QuickSearch.FetchAsync(session, category, term, ct), readAt);
            }, group =>
            {
                if (cts.IsCancellationRequested || _result is null) return;
                var result = _result = QuickSearch.Merge(_result, FilterGroup(group));
                if (_category != SearchCategory.All && !result.Pending.Contains(_category) && result.CountOf(_category) == 0) _category = SearchCategory.All;
                Show();
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _shell.ReportFailure(ex);
        }
    }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        if (_result is null) return;
        _result = _result with { Groups = _result.Groups.Select(group => FilterGroup(group)).Where(group => group.Items.Count > 0).ToList() };
        if (_category != SearchCategory.All && _result.CountOf(_category) == 0) _category = SearchCategory.All;
        _headerTotal = _result.Total;
        Show();
    }

    private SearchGroup FilterGroup(SearchGroup group)
    {
        var (items, total) = _removals.FilterPage(group.Items, group.Total);
        return group with { Items = items, Total = total };
    }

    private SearchGroup FilterGroup(SearchGroup group, long readAt)
    {
        var (items, total) = _removals.FilterPage(group.Items, group.Total, readAt);
        return group with { Items = items, Total = total };
    }

    private void Show()
    {
        if (_result is not { } result) return;
        _headerTotal = QuickSearch.HeaderTotal(result, _headerTotal);
        _count.Text = _headerTotal is { } total ? L.Format("Search.ResultCount", total.ToString("N0", CultureInfo.CurrentUICulture)) : "";
        UpdateChips(result);
        _empty.Visibility = result.ShowsEmptyState ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = result.ShowsEmptyState ? Visibility.Collapsed : Visibility.Visible;
        _emptyTitle.Text = L.Format("Search.NoResults", result.Term);
        var single = _category != SearchCategory.All;
        var shown = new List<UIElement>();
        foreach (var group in result.Shown(_category))
        {
            if (!_views.TryGetValue(group.Category, out var view)) _views[group.Category] = view = CreateGroup(group.Category);
            Update(view, group, single);
            shown.Add(view.Root);
        }
        Sync(_groups.Children, shown);
        FitGroups();
    }

    /// <summary>A category chip; its label and count change in place, so a focused chip keeps focus.</summary>
    private sealed record Chip(Button Button, TextBlock Label, TextBlock Number);

    private Chip CreateChip(SearchCategory category)
    {
        var label = new TextBlock { FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        var number = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(label);
        content.Children.Add(number);
        var button = new Button { Style = Ui.Resource<Style>("LwButton"), Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 0, 12, 0), Content = content, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(button, "SearchChip" + category);
        button.Click += (_, _) => { _category = category; Show(); };
        _chips.Children.Add(button);
        return new Chip(button, label, number);
    }

    private void UpdateChips(QuickSearchResult result)
    {
        foreach (var (category, chip) in _chipViews)
        {
            var count = result.CountOf(category);
            chip.Button.Visibility = category == SearchCategory.All || count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var selected = category == _category;
            var style = Ui.Resource<Style>(selected ? "LwAccentButton" : "LwButton");
            if (!ReferenceEquals(chip.Button.Style, style)) chip.Button.Style = style;
            chip.Label.Text = CategoryName(category);
            chip.Number.Text = count.ToString("N0", CultureInfo.CurrentUICulture);
            chip.Number.Foreground = selected ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF)) : Ui.Resource<Brush>("LwText3Brush");
            AutomationProperties.SetName(chip.Button, $"{chip.Label.Text} {chip.Number.Text}");
            AutomationProperties.SetItemStatus(chip.Button, selected ? "selected" : "");
        }
    }

    /// <summary>One result group on screen, kept across queries with its cards by item id.</summary>
    private sealed class GroupView(StackPanel root, TextBlock badge, Button seeAll, ScrollViewer row, CardKind kind, double width)
    {
        public StackPanel Root { get; } = root;
        public TextBlock Badge { get; } = badge;
        public Button SeeAll { get; } = seeAll;
        public ScrollViewer Row { get; } = row;
        public CardKind Kind { get; } = kind;
        public double Width { get; } = width;
        public SearchGroup? Group { get; set; }
        public bool Single { get; set; }
        public int Limit { get; set; }
        public Panel? Cards { get; set; }
        public Dictionary<Guid, MediaCard> CardsById { get; set; } = [];
    }

    private GroupView CreateGroup(SearchCategory category)
    {
        var (kind, width) = category switch
        {
            SearchCategory.Episodes => (CardKind.Wide, 220.0),
            SearchCategory.People => (CardKind.Person, 104.0),
            _ => (CardKind.Poster, 104.0),
        };
        var name = new TextBlock { Text = CategoryName(category), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(name, "SearchGroup" + category);
        var total = new TextBlock { FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush") };
        var badge = new Border
        {
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = new CornerRadius(9),
            Background = Ui.Resource<Brush>("LwBoxBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = total,
        };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(name);
        SetColumn(badge, 1);
        header.Children.Add(badge);

        var seeAllContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        seeAllContent.Children.Add(new TextBlock { Text = L.Text("Search.SeeAll"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        seeAllContent.Children.Add(Ui.DirectionalIcon(Theme.Icons.ArrowForward, 15));
        var seeAll = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Padding = new Thickness(6, 3, 6, 3), Foreground = Ui.Resource<Brush>("LwAccentFgBrush"), Content = seeAllContent, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(seeAll, "SearchSeeAll" + category);
        AutomationProperties.SetName(seeAll, $"{L.Text("Search.SeeAll")} {name.Text}");
        seeAll.Click += (_, _) =>
        {
            var focus = seeAll.FocusState;
            _category = category;
            Show();
            if (focus != FocusState.Unfocused) _chipViews[category].Button.Focus(focus);
        };
        SetColumn(seeAll, 3);
        header.Children.Add(seeAll);

        var row = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(row, "SearchItems" + category);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(header);
        panel.Children.Add(row);
        return new GroupView(panel, total, seeAll, row, kind, width);
    }

    /// <summary>Shows a group's answer in its view: a card whose item is unchanged stays as it is, a
    /// card for the same item with new state takes the new item, and only new items get new cards.
    /// Under All a row holds as many cards as reach into the page's width.</summary>
    private void Update(GroupView view, SearchGroup group, bool single)
    {
        var (fit, whole) = QuickSearch.RowFit(RowWidth, view.Width, CardGap);
        var limit = fit > 0 ? fit : GroupLimit;
        var inRow = Math.Min(limit, group.Items.Count);
        view.SeeAll.Visibility = !single && QuickSearch.NeedsSeeAll(group.Total, inRow, fit > 0 ? whole : inRow) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(view.Group, group) && view.Single == single && view.Limit == limit && view.Cards is not null) return;
        view.Group = group;
        view.Limit = limit;
        view.Badge.Text = group.Total.ToString("N0", CultureInfo.CurrentUICulture);
        var items = single ? group.Items : group.Items.Take(limit).ToList();
        if (view.Cards is null || view.Single != single)
        {
            view.Cards?.Children.Clear();
            view.Cards = single ? new WrapPanel { HorizontalSpacing = CardGap, VerticalSpacing = 18 } : new StackPanel { Orientation = Orientation.Horizontal, Spacing = CardGap };
            view.Row.Content = view.Cards;
            view.Single = single;
        }
        var byId = new Dictionary<Guid, MediaCard>();
        var cards = new List<UIElement>();
        foreach (var item in items)
        {
            if (byId.ContainsKey(item.Id)) continue;
            if (view.CardsById.TryGetValue(item.Id, out var card))
            {
                if (card.Item is not { } shown || !SameCard(shown, item)) card.Item = item;
            }
            else card = Card(item, view.Kind, view.Width);
            byId[item.Id] = card;
            cards.Add(card);
        }
        view.CardsById = byId;
        Sync(view.Cards.Children, cards);
    }

    /// <summary>Whether a card showing <paramref name="shown"/> already shows everything of <paramref name="next"/>.</summary>
    private static bool SameCard(MediaItem shown, MediaItem next)
        => shown.Id == next.Id && shown.Name == next.Name && shown.Played == next.Played && shown.IsFavorite == next.IsFavorite
            && shown.PlayedPercentage == next.PlayedPercentage && shown.ChildCount == next.ChildCount && shown.Overview == next.Overview
            && shown.PrimaryImageTag == next.PrimaryImageTag && shown.BackdropImageTag == next.BackdropImageTag
            && shown.SeriesPrimaryImageTag == next.SeriesPrimaryImageTag
            && shown.PosterUrl == next.PosterUrl && shown.ThumbUrl == next.ThumbUrl;

    /// <summary>Puts exactly <paramref name="desired"/> into <paramref name="children"/>, in order,
    /// moving the elements already there instead of removing and re-adding them.</summary>
    private static void Sync(UIElementCollection children, IReadOnlyList<UIElement> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < children.Count && ReferenceEquals(children[i], desired[i])) continue;
            var at = children.IndexOf(desired[i]);
            if (at >= 0) children.Move((uint)at, (uint)i);
            else children.Insert(i, desired[i]);
        }
        while (children.Count > desired.Count) children.RemoveAt(children.Count - 1);
    }

    private MediaCard Card(MediaItem item, CardKind kind, double width)
    {
        var card = new MediaCard
        {
            Width = width,
            Kind = kind,
            Shell = _shell,
            Session = _shell.Model.Jellyfin,
            Overrides = _overrides,
            ShowActions = kind != CardKind.Person,
            Subtitle = Subtitle,
            LoadsPictureWhenVisible = true,
        };
        card.Invoked += (_, i) => Open(i);
        card.Item = item;
        return card;
    }

    private static string Subtitle(MediaItem item) => item.Type switch
    {
        BaseItemDto_Type.Person => item.Overview ?? "",
        BaseItemDto_Type.Episode => string.Join(" · ", new[] { item.SeriesName ?? "", Details.DetailModel.EpisodeTag(item) }.Where(p => p.Length > 0)),
        BaseItemDto_Type.BoxSet => L.Text("Search.Collection"),
        BaseItemDto_Type.Series when item.ChildCount is 1 => L.Text("Search.OneSeason"),
        BaseItemDto_Type.Series when item.ChildCount is > 1 and var seasons => L.Format("Search.Seasons", seasons),
        _ => item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>The page's width inside its gutters, which a group's row fills.</summary>
    private double RowWidth => _scroller.ActualWidth - _groups.Margin.Left - _groups.Margin.Right;

    /// <summary>Keeps each group to the page's width so a long row clips at the trailing edge.</summary>
    private void FitGroups()
    {
        var available = RowWidth;
        if (available <= 0) return;
        foreach (var group in _groups.Children.OfType<FrameworkElement>()) group.MaxWidth = available;
    }

    private void Open(MediaItem item)
    {
        RecentSearches.Add(_shell.Model.ActiveSessionKey ?? "", _term);
        if (item.Type == BaseItemDto_Type.Person) _shell.OpenPerson(item.Id, item.Name);
        else _shell.OpenItem(item);
    }

    private void MoreFilters()
    {
        RecentSearches.Add(_shell.Model.ActiveSessionKey ?? "", _term);
        _shell.OpenAdvancedSearch(QuickSearch.MoreFilters(_category), _term.Trim());
    }

    private void ApplyTexts()
    {
        _heading.Text = L.Format("Search.ResultsFor", _term.Trim());
        var moreContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        moreContent.Children.Add(Ui.Icon(Theme.Icons.Tune, 16));
        moreContent.Children.Add(new TextBlock { Text = L.Text("Search.MoreFilters"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        _moreFilters.Content = moreContent;
        AutomationProperties.SetName(_moreFilters, L.Text("Search.MoreFilters"));
        _emptyHint.Text = L.Format("Search.NoResultsHint", _shell.ServerName);
        var advanced = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        advanced.Children.Add(Ui.Icon(Theme.Icons.Tune, 16));
        advanced.Children.Add(new TextBlock { Text = L.Text("Search.AdvancedSearch"), VerticalAlignment = VerticalAlignment.Center });
        _emptyAdvanced.Content = advanced;
        AutomationProperties.SetName(_emptyAdvanced, L.Text("Search.AdvancedSearch"));
    }

    private static string CategoryName(SearchCategory category) => category switch
    {
        SearchCategory.Movies => L.Text("Search.Movies"),
        SearchCategory.Shows => L.Text("Search.Shows"),
        SearchCategory.Episodes => L.Text("Search.Episodes"),
        SearchCategory.People => L.Text("Search.People"),
        SearchCategory.Collections => L.Text("Search.Collections"),
        _ => L.Text("Search.All"),
    };
}
