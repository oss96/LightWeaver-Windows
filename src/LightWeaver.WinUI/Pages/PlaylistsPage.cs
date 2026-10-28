using System.Globalization;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Playlists;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

/// <summary>All playlists (design 06, 6a): reached from Playlists â†’ All. Square cards with the
/// usual âœ“ / â‹¯ (Play, Shuffle, Rename, Delete), a sort and New playlist.</summary>
public sealed partial class PlaylistsPage : Grid, INavPage
{
    private const double MinCard = 190;
    private const double Gap = 16;
    private readonly IAppShell _shell;
    private readonly ItemRemovals _removals;
    private readonly UserDataOverrides _overrides = new();
    private readonly TextBlock _count = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) };
    private readonly TextBlock _sortText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sort;
    private readonly WrapPanel _cards = new() { HorizontalSpacing = Gap, VerticalSpacing = 22 };
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _empty;
    private IReadOnlyList<MediaItem> _playlists = [];
    /// <summary>The page's sort, for the session; the Playlists morph lists in the same order.</summary>
    internal static PlaylistSort Order { get; private set; } = PlaylistSort.DateCreated;
    private bool _loaded;

    public PlaylistsPage(IAppShell shell)
    {
        _shell = shell;
        _removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -10, Text = L.Text("Playlist.AllTitle") };
        AutomationProperties.SetAutomationId(title, "PlaylistsTitle");
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        _count.Foreground = Ui.Resource<Brush>("LwText2Brush");
        AutomationProperties.SetAutomationId(_count, "PlaylistsCount");
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(title);
        heading.Children.Add(_count);

        var sortContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sortContent.Children.Add(Ui.Icon(Theme.Icons.ArrowDownward, 16));
        sortContent.Children.Add(_sortText);
        var chevron = Ui.Icon(Theme.Icons.ExpandMore, 14);
        chevron.Foreground = Ui.Resource<Brush>("LwText2Brush");
        sortContent.Children.Add(chevron);
        _sort = new Button { Style = Ui.Resource<Style>("LwButton"), Padding = new Thickness(10, 0, 8, 0), Content = sortContent };
        AutomationProperties.SetAutomationId(_sort, "PlaylistsSort");
        _sort.Click += (_, _) => ShowSortMenu();

        var newContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        newContent.Children.Add(Ui.Icon(Theme.Icons.Add, 16));
        newContent.Children.Add(new TextBlock { Text = L.Text("Playlist.New"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        var create = new Button { Style = Ui.Resource<Style>("LwAccentButton"), Padding = new Thickness(10, 0, 12, 0), Content = newContent };
        AutomationProperties.SetAutomationId(create, "PlaylistsNew");
        AutomationProperties.SetName(create, L.Text("Playlist.New"));
        create.Click += async (_, _) => await CreateAsync();

        var header = new Grid { Padding = new Thickness(24, 16, 24, 16), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading);
        SetColumn(_sort, 1);
        header.Children.Add(_sort);
        SetColumn(create, 2);
        header.Children.Add(create);
        Children.Add(header);

        _cards.Margin = new Thickness(24, 0, 24, 24);
        _scroller = new ScrollViewer { Content = _cards, HorizontalScrollMode = ScrollMode.Disabled };
        AutomationProperties.SetAutomationId(_scroller, "PlaylistsScroller");
        _scroller.SizeChanged += (_, _) => FitCards();
        SetRow(_scroller, 1);
        Children.Add(_scroller);

        var emptyTitle = new TextBlock { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Text = L.Text("Playlist.NoneTitle") };
        AutomationProperties.SetAutomationId(emptyTitle, "PlaylistsEmpty");
        _empty = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 80), Visibility = Visibility.Collapsed };
        _empty.Children.Add(emptyTitle);
        _empty.Children.Add(new TextBlock { FontSize = 13, Foreground = Ui.Resource<Brush>("LwText2Brush"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 360, Text = L.Text("Playlist.NoneHint") });
        SetRow(_empty, 1);
        Children.Add(_empty);

        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            title.Text = L.Text("Playlist.AllTitle");
            ((TextBlock)newContent.Children[1]).Text = L.Text("Playlist.New");
            AutomationProperties.SetName(create, L.Text("Playlist.New"));
            ApplyTexts();
            Show();
        }));
        Loaded += (_, _) => { if (!_loaded) _ = LoadAsync(); };
    }

    public void OnShown() { if (_loaded) _ = LoadAsync(); }

    public void Reload() => _ = LoadAsync();

    private JellyfinService Session => _shell.Model.Jellyfin;

    private async Task LoadAsync()
    {
        var readAt = _removals.Mark();
        await _shell.RefreshPlaylistsAsync();
        _playlists = _removals.Filter(_shell.KnownPlaylists, readAt);
        _loaded = true;
        Show();
    }

    /// <summary>The cards in the chosen order; the same playlists in the same order (a refresh)
    /// update the cards in place.</summary>
    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        _playlists = _removals.Filter(_playlists);
        Show();
    }

    private void Show()
    {
        _playlists = _removals.Filter(_playlists);
        _count.Text = _playlists.Count == 1 ? L.Text("Playlist.OnePlaylist") : L.Format("Playlist.Playlists", _playlists.Count.ToString(CultureInfo.CurrentUICulture));
        _empty.Visibility = _loaded && _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sorted = PlaylistModel.Sorted(_playlists, Order).ToList();
        var shown = _cards.Children.OfType<Tile>().ToList();
        if (shown.Count == _cards.Children.Count && shown.Select(t => t.Card.Item?.Id).SequenceEqual(sorted.Select(p => (Guid?)p.Id)))
        {
            for (var i = 0; i < sorted.Count; i++) shown[i].Show(sorted[i]);
            return;
        }
        _cards.Children.Clear();
        var options = new ItemMenuOptions(Rename: p => _ = RenameAsync(p), Delete: p => _ = DeleteAsync(p));
        foreach (var playlist in sorted)
        {
            var card = new MediaCard
            {
                Kind = CardKind.Square,
                Shell = _shell,
                Session = Session,
                Overrides = _overrides,
                MenuOptions = options,
                Subtitle = PlaylistModel.CardLine,
                IsEmpty = p => !PlaylistModel.HasEntries(p),
            };
            var tile = new Tile(card);
            tile.Show(playlist);
            _cards.Children.Add(tile);
        }
        FitCards();
    }

    /// <summary>A playlist's card, with the playlist glyph its page shows while it has no artwork or
    /// no entries.</summary>
    private sealed partial class Tile : Grid
    {
        private const double GlyphSize = 40;
        private readonly FontIcon _glyph = Ui.Icon(Theme.Icons.PlaylistPlay, GlyphSize);

        public Tile(MediaCard card)
        {
            Card = card;
            _glyph.Foreground = Ui.Resource<Brush>("LwText3Brush");
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Top;
            _glyph.IsHitTestVisible = false;
            Children.Add(card);
            Children.Add(_glyph);
        }

        public MediaCard Card { get; }

        public void Show(MediaItem playlist)
        {
            Card.Item = playlist;
            _glyph.Visibility = PlaylistModel.ArtworkTag(playlist, playlist.ChildCount ?? 0) is null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The card's width, with the glyph in the middle of its square artwork.</summary>
        public void Fit(double width)
        {
            Card.Width = width;
            _glyph.Margin = new Thickness(0, (width - GlyphSize) / 2, 0, 0);
        }
    }

    /// <summary>Columns of at least 190 px that fill the width.</summary>
    private void FitCards()
    {
        var available = _scroller.ActualWidth - 48;
        if (available <= 0) return;
        var columns = Math.Max(1, Math.Floor((available + Gap) / (MinCard + Gap)));
        var width = Math.Floor((available - (columns - 1) * Gap) / columns);
        foreach (var tile in _cards.Children.OfType<Tile>()) tile.Fit(width);
    }

    private void ShowSortMenu()
    {
        var menu = FastUi.Flyout(new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        foreach (var sort in Enum.GetValues<PlaylistSort>())
        {
            var item = new RadioMenuFlyoutItem { Text = SortLabel(sort), IsChecked = sort == Order, GroupName = "PlaylistsSort" };
            AutomationProperties.SetAutomationId(item, "PlaylistsSort" + sort);
            item.Click += (_, _) => { Order = sort; ApplyTexts(); Show(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(_sort);
    }

    private static string SortLabel(PlaylistSort sort) => sort == PlaylistSort.Name ? L.Text("Playlist.SortName") : L.Text("Playlist.SortDateCreated");

    private async Task CreateAsync()
    {
        if (await _shell.AskPlaylistNameAsync(L.Text("Playlist.NewTitle"), L.Text("Playlist.Create")) is not { } name) return;
        try
        {
            if (await Session.CreatePlaylistAsync(name, []) is null) throw new InvalidOperationException("the server returned no playlist id");
            ToastHost.Show(ToastKind.Success, L.Format("Playlist.Created", name));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=create outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.CreateFailed"));
        }
    }

    private async Task RenameAsync(MediaItem playlist)
    {
        if (await _shell.AskPlaylistNameAsync(L.Text("Playlist.RenameTitle"), L.Text("Playlist.Save"), playlist.Name) is not { } name || name == playlist.Name) return;
        try
        {
            await Session.RenamePlaylistAsync(playlist.Id, name);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=rename outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.RenameFailed"));
        }
    }

    private async Task DeleteAsync(MediaItem playlist)
    {
        if (!await ConfirmDeleteAsync(XamlRoot, playlist.Name)) return;
        try
        {
            await Session.DeleteItemAsync(playlist.Id);
            ToastHost.Show(ToastKind.Success, L.Format("Playlist.Deleted", playlist.Name));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=delete outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.DeleteFailed"));
        }
    }

    /// <summary>Deleting a whole playlist cannot be undone, so it asks first.</summary>
    internal static async Task<bool> ConfirmDeleteAsync(XamlRoot root, string name)
    {
        var dialog = new LwDialog
        {
            XamlRoot = root,
            Title = L.Format("Playlist.DeleteTitle", name),
            Content = L.Text("Playlist.DeleteText"),
            PrimaryButtonText = L.Text("Playlist.Delete"),
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "PlaylistDeleteDialog");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ApplyTexts()
    {
        _sortText.Text = SortLabel(Order);
        AutomationProperties.SetName(_sort, L.Format("Playlist.SortedBy", _sortText.Text));
    }
}
