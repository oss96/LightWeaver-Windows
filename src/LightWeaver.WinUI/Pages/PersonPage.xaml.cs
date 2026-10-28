using System.Globalization;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.People;
using LightWeaver.Search;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

public sealed partial class PersonPage : UserControl, INavPage
{
    private const double Narrow = 1000;
    private const double MinCard = 128;
    private const double Gap = 14;
    private readonly IAppShell _shell;
    private readonly ItemRemovals _removals;
    private readonly Guid _personId;
    private readonly UserDataOverrides _overrides = new();
    private MediaItem? _person;
    private IReadOnlyList<Credit> _credits = [];
    private string? _role;
    private CreditSort _sort = CreditSort.Newest;
    private bool _bioExpanded;
    private bool _loaded;

    public PersonPage(IAppShell shell, Guid personId, string name)
    {
        _shell = shell;
        _removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        _personId = personId;
        InitializeComponent();
        NameText.Text = name;
        PhotoFrame.SizeChanged += (_, e) => PhotoFrame.Height = e.NewSize.Width * 4 / 3;
        SizeChanged += (_, _) => { ApplyLayout(); FitCards(); };
        Scroller.SizeChanged += (_, _) => FitCards();
        BioText.SizeChanged += (_, _) => UpdateBioMore();
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyTexts(); ShowCredits(); }));
        Loaded += (_, _) => { if (!_loaded) _ = LoadAsync(); };
    }

    public void Reload() => _ = LoadAsync();

    private JellyfinService Session => _shell.Model.Jellyfin;

    private async Task LoadAsync()
    {
        var readAt = _removals.Mark();
        try
        {
            var person = Session.GetItemAsync(_personId);
            var credits = Session.GetPersonCreditsAsync(_personId);
            await Task.WhenAll(person, credits);
            if (_removals.Contains(_personId, readAt)) return;
            _person = person.Result;
            _credits = PersonCredits.From(_removals.Filter(credits.Result, readAt), _personId);
            _loaded = true;
            ShowPerson();
            ShowCredits();
        }
        catch (Exception ex) when (JellyfinService.IsNotFound(ex))
        {
            if (_removals.Contains(_personId, readAt)) return;
            _removals.Remove([_personId]);
            RemoveItems(new HashSet<Guid> { _personId });
        }
        catch (Exception ex)
        {
            _shell.ReportFailure(ex);
        }
    }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        _credits = _credits.Where(credit => !ids.Contains(credit.Item.Id)).ToList();
        ShowCredits();
        if (!ids.Contains(_personId)) return;
        _person = null;
        _credits = [];
        Sections.Children.Clear();
        Photo.Source = null;
        BornText.Visibility = BioMore.Visibility = RoleTrack.Visibility = RolesText.Visibility = Visibility.Collapsed;
        BioText.Text = "";
        EmptyText.Text = L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7");
        EmptyText.Visibility = Visibility.Visible;
    }

    private void ShowPerson()
    {
        if (_person is not { } person) return;
        NameText.Text = person.Name;
        BornText.Text = PersonCredits.BornLine(person.PremiereDate, person.ProductionLocations.FirstOrDefault());
        BornText.Visibility = BornText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BioText.Text = person.Overview ?? "";
        BioText.Visibility = BioText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadPhotoAsync(person);
    }

    private async Task LoadPhotoAsync(MediaItem person)
    {
        var readAt = _removals.Mark();
        if (person.PrimaryImageTag is not { Length: > 0 } tag) return;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Ceiling(Math.Max(200, PhotoFrame.ActualWidth) * scale);
        if (await ImageLoader.LoadAsync(Session, step => Session.GetPrimaryImageUrl(person.Id, tag, step), width) is { } image
            && !_removals.Contains(person.Id, readAt)) Photo.Source = image;
    }

    private void ShowCredits()
    {
        RolesText.Text = PersonCredits.RolesLine(_credits);
        RolesText.Visibility = RolesText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildRoleFilter();
        Sections.Children.Clear();
        foreach (var (role, credits) in PersonCredits.Sections(_credits, _role, _sort))
            Sections.Children.Add(Section(role, credits));
        EmptyText.Visibility = _loaded && _credits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_removals.Contains(_personId))
        {
            EmptyText.Text = L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7");
            EmptyText.Visibility = Visibility.Visible;
        }
        FitCards();
    }

    private void BuildRoleFilter()
    {
        RoleFilter.Children.Clear();
        var roles = PersonCredits.Roles(_credits);
        RoleTrack.Visibility = roles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RoleFilter.Children.Add(RoleSegment(null, L.Text("Person.All"), _credits.Count));
        foreach (var (role, count) in roles) RoleFilter.Children.Add(RoleSegment(role, PersonCredits.RoleLabel(role), count));
    }

    private RadioButton RoleSegment(string? role, string label, int count)
    {
        var number = count.ToString(CultureInfo.CurrentUICulture);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = number, FontSize = 11.5, Foreground = Ui.Resource<Brush>("LwText3Brush"), VerticalAlignment = VerticalAlignment.Center });
        var segment = new RadioButton { Style = Ui.Resource<Style>("LwSegment"), GroupName = "PersonRole", Padding = new Thickness(12, 0, 12, 0), Content = content, IsChecked = role == _role };
        AutomationProperties.SetAutomationId(segment, role is null ? "PersonRoleAll" : "PersonRole" + role);
        AutomationProperties.SetName(segment, $"{label} {number}");
        segment.Checked += (_, _) =>
        {
            if (_role == role) return;
            _role = role;
            DispatcherQueue.TryEnqueue(ShowCredits);
        };
        return segment;
    }

    private StackPanel Section(string role, IReadOnlyList<Credit> credits)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(0, 12, 0, 0) };
        var name = new TextBlock { Text = PersonCredits.RoleLabel(role), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(name, "PersonSection" + role);
        header.Children.Add(name);
        header.Children.Add(new TextBlock { Text = credits.Count.ToString(CultureInfo.CurrentUICulture), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText3Brush"), VerticalAlignment = VerticalAlignment.Center });
        var grid = new WrapPanel { HorizontalSpacing = Gap, VerticalSpacing = 18 };
        foreach (var credit in credits)
        {
            var card = new MediaCard
            {
                Kind = CardKind.Poster,
                Shell = _shell,
                Session = Session,
                Overrides = _overrides,
                Subtitle = _ => PersonCredits.Subtitle(credit),
            };
            card.Item = credit.Item;
            grid.Children.Add(card);
        }
        var section = new StackPanel { Spacing = 10, BorderBrush = Ui.Resource<Brush>("LwSeparatorBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        section.Children.Add(header);
        section.Children.Add(grid);
        return section;
    }

    /// <summary>Columns of at least 128 px that fill the width (CSS auto-fill, minmax 128px 1fr).</summary>
    private void FitCards()
    {
        var available = Scroller.ActualWidth - 4;
        if (available <= 0) return;
        var columns = Math.Max(1, Math.Floor((available + Gap) / (MinCard + Gap)));
        var width = Math.Floor((available - (columns - 1) * Gap) / columns);
        foreach (var grid in Sections.Children.OfType<StackPanel>().Select(s => s.Children[1]).OfType<WrapPanel>())
            foreach (var card in grid.Children.OfType<MediaCard>()) card.Width = width;
    }

    private void ApplyLayout()
    {
        var narrow = ActualWidth < Narrow;
        LeftColumn.Width = new GridLength(narrow ? 200 : 280);
        if (!_bioExpanded) BioText.MaxLines = narrow ? 4 : 6;
    }

    private void UpdateBioMore()
    {
        BioMore.Visibility = _bioExpanded || BioText.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
        BioMore.Content = _bioExpanded ? L.Text("Person.Less") : L.Text("Person.More");
    }

    /// <summary>More expands the bio in place; no dialog.</summary>
    private void OnBioMore(object sender, RoutedEventArgs e)
    {
        _bioExpanded = !_bioExpanded;
        BioText.MaxLines = _bioExpanded ? 0 : ActualWidth < Narrow ? 4 : 6;
        UpdateBioMore();
    }

    private void OnAdvanced(object sender, RoutedEventArgs e)
        => _shell.OpenAdvancedSearch(AdvancedSearchFilters.ForPerson(_personId, _person?.Name ?? NameText.Text));

    private void OnSort(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        foreach (var sort in Enum.GetValues<CreditSort>())
        {
            var item = new RadioMenuFlyoutItem { Text = SortLabel(sort), IsChecked = sort == _sort, GroupName = "PersonSort" };
            AutomationProperties.SetAutomationId(item, "PersonSort" + sort);
            item.Click += (_, _) => { _sort = sort; ApplyTexts(); ShowCredits(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(SortButton);
    }

    private static string SortLabel(CreditSort sort) => sort switch
    {
        CreditSort.Oldest => L.Text("Person.SortOldest"),
        CreditSort.Name => L.Text("Person.SortName"),
        _ => L.Text("Person.SortNewest"),
    };

    private void ApplyTexts()
    {
        SortText.Text = SortLabel(_sort);
        SortArrow.Glyph = _sort == CreditSort.Oldest ? Theme.Icons.ArrowUpward : _sort == CreditSort.Name ? Theme.Icons.Sort : Theme.Icons.ArrowDownward;
        AutomationProperties.SetName(SortButton, L.Format("Person.SortedBy", SortText.Text));
        UpdateBioMore();
        if (_person is not null) BornText.Text = PersonCredits.BornLine(_person.PremiereDate, _person.ProductionLocations.FirstOrDefault());
    }
}
