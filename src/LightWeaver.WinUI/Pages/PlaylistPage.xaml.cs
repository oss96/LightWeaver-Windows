using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Playlists;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace LightWeaver.WinUI.Pages;

/// <summary>One row of a playlist page. The number and the hover state change, so they notify.</summary>
public sealed partial class PlaylistRow(MediaItem item) : INotifyPropertyChanged
{
    private int _number;
    private bool _hovered;
    private BitmapImage? _thumb;

    public MediaItem Item { get; } = item;
    public string Title { get; } = PlaylistModel.RowTitle(item);
    public string Subtitle { get; } = PlaylistModel.RowSubtitle(item);
    public string Type { get; } = PlaylistModel.TypeLabel(item);
    public string Duration { get; } = PlaylistModel.Duration(item.RuntimeTicks);
    public string RemoveLabel => L.Text("Playlist.RemoveFromPlaylist");
    public string MoreLabel => L.Text("Card.More");

    public int Number { get => _number; set => Set(ref _number, value); }

    public bool Hovered
    {
        get => _hovered;
        set { if (Set(ref _hovered, value)) { OnChanged(nameof(ShowNumber)); OnChanged(nameof(ActionsOpacity)); } }
    }

    public bool ShowNumber => !_hovered;

    /// <summary>Remove and â‹¯ stay in the tree for keyboard and UI Automation; they show on hover.</summary>
    public double ActionsOpacity => _hovered ? 1 : 0;

    public BitmapImage? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Subtitle.Length > 0 ? $"{Title}, {Subtitle}" : Title;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class PlaylistPage : UserControl, INavPage
{
    private const double Narrow = 1000;
    private readonly IAppShell _shell;
    private readonly ItemRemovals _removals;
    private readonly ObservableCollection<PlaylistRow> _rows = [];
    private MediaItem _playlist;
    private bool _loaded;
    private string? _artTag;
    private int _artRequest;

    /// <summary>The playlist this page shows.</summary>
    internal MediaItem Playlist => _playlist;

    public PlaylistPage(IAppShell shell, MediaItem playlist)
    {
        _shell = shell;
        _removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        _playlist = playlist;
        InitializeComponent();
        Rows.ItemsSource = _rows;
        TitleText.Text = playlist.Name;
        SizeChanged += (_, _) => Art.Width = Art.Height = ActualWidth < Narrow ? 120 : 168;
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(ApplyTexts));
        Loaded += (_, _) => { if (!_loaded) _ = LoadAsync(); };
    }

    public void OnShown() { if (_loaded) _ = LoadAsync(); }

    public void Reload() => _ = LoadAsync();

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (ids.Contains(_rows[i].Item.Id)) _rows.RemoveAt(i);
        Renumber();
        if (!ids.Contains(_playlist.Id)) return;
        _rows.Clear();
        Renumber();
    }

    private JellyfinService Session => _shell.Model.Jellyfin;

    private async Task LoadAsync()
    {
        if (_removals.Contains(_playlist.Id)) { RemoveItems(new HashSet<Guid> { _playlist.Id }); return; }
        var readAt = _removals.Mark();
        try
        {
            var items = Session.GetPlaylistItemsAsync(_playlist.Id);
            var fresh = Session.GetItemAsync(_playlist.Id);
            await Task.WhenAll(items, fresh);
            if (_removals.Contains(_playlist.Id, readAt)) return;
            if (fresh.Result is { } playlist) _playlist = playlist;
            TitleText.Text = _playlist.Name;
            _loaded = true;
            Show(_removals.Filter(items.Result, readAt));
            UpdateArt(reload: true);
        }
        catch (Exception ex) when (JellyfinService.IsNotFound(ex))
        {
            if (_removals.Contains(_playlist.Id, readAt)) return;
            _removals.Remove([_playlist.Id]);
            RemoveItems(new HashSet<Guid> { _playlist.Id });
        }
        catch (Exception ex)
        {
            _shell.ReportFailure(ex);
        }
    }

    /// <summary>The rows. The same entries in the same order (a refresh) keep their rows; only an
    /// entry whose line changed is replaced.</summary>
    private void Show(IReadOnlyList<MediaItem> items)
    {
        if (_removals.Contains(_playlist.Id)) { RemoveItems(new HashSet<Guid> { _playlist.Id }); return; }
        items = _removals.Filter(items);
        if (_rows.Select(r => (r.Item.Id, r.Item.PlaylistItemId)).SequenceEqual(items.Select(i => (i.Id, i.PlaylistItemId))))
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ItemLists.SameShown(_rows[i].Item, items[i])) continue;
                var row = new PlaylistRow(items[i]) { Thumb = _rows[i].Thumb };
                _rows[i] = row;
                _ = LoadThumbAsync(row);
            }
            Renumber();
            return;
        }
        _rows.Clear();
        foreach (var item in items) _rows.Add(new PlaylistRow(item));
        Renumber();
        foreach (var row in _rows) _ = LoadThumbAsync(row);
    }

    private void Renumber()
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Number = i + 1;
        MetaText.Text = PlaylistModel.Summary([.. _rows.Select(r => r.Item)]);
        Empty.Visibility = _loaded && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_removals.Contains(_playlist.Id))
        {
            Empty.Visibility = Visibility.Visible;
            if (Empty.Children[0] is TextBlock title) title.Text = L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7");
            if (Empty.Children[1] is TextBlock hint) hint.Visibility = Visibility.Collapsed;
        }
        PlayAllButton.IsEnabled = ShuffleButton.IsEnabled = _rows.Count > 0;
        UpdateArt();
    }

    /// <summary>Shows the header artwork for the entries the page has now: the glyph once the last
    /// one is gone, the collage again when one comes back. A load still under way for artwork no
    /// longer wanted is dropped; <paramref name="reload"/> loads the artwork again unchanged.</summary>
    private void UpdateArt(bool reload = false)
    {
        var tag = PlaylistModel.ArtworkTag(_playlist, _rows.Count);
        if (tag == _artTag && !reload) return;
        _artTag = tag;
        var request = ++_artRequest;
        if (tag is null) ArtImage.Source = null;
        else _ = LoadArtAsync(tag, request);
    }

    private async Task LoadArtAsync(string tag, int request)
    {
        var id = _playlist.Id;
        var width = (int)Math.Ceiling(168 * (XamlRoot?.RasterizationScale ?? 1));
        var image = await ImageLoader.LoadAsync(Session, step => Session.GetPrimaryImageUrl(id, tag, step), width);
        if (request == _artRequest && image is not null) ArtImage.Source = image;
    }

    private async Task LoadThumbAsync(PlaylistRow row)
    {
        var item = row.Item;
        var width = (int)Math.Ceiling(96 * (XamlRoot?.RasterizationScale ?? 1));
        string? Url(int step) => item.Type == global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode && item.PrimaryImageTag is { Length: > 0 } still
            ? Session.GetPrimaryImageUrl(item.Id, still, step)
            : item.BackdropImageTag is { Length: > 0 } backdrop ? Session.GetBackdropImageUrl(item.Id, backdrop, step) : item.ThumbUrl ?? item.PosterUrl;
        var load = ImageLoader.LoadAsync(Session, Url, width);
        var fromMemory = load.IsCompleted;
        if (await load is { } image) ImageQueue.Land(fromMemory, () => row.Thumb = image);
    }

    private IReadOnlyList<MediaItem> Queue => [.. _rows.Select(r => r.Item)];

    private void OnPlayAll(object sender, RoutedEventArgs e) => _shell.PlayQueue(Queue, 0);

    private void OnShuffle(object sender, RoutedEventArgs e) => _shell.PlayQueue(PlaylistModel.Shuffled(Queue, Random.Shared), 0);

    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Rows.SelectedIndex >= 0) _shell.PlayQueue(Queue, Rows.SelectedIndex);
    }

    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter when Rows.SelectedIndex >= 0:
                _shell.PlayQueue(Queue, Rows.SelectedIndex);
                e.Handled = true;
                break;
            case VirtualKey.Delete or VirtualKey.Back when Rows.SelectedItems.Count > 0:
                _ = RemoveAsync([.. Rows.SelectedItems.OfType<PlaylistRow>()]);
                e.Handled = true;
                break;
            case VirtualKey.Up when alt && Rows.SelectedIndex > 0:
                _ = MoveAsync(Rows.SelectedIndex, Rows.SelectedIndex - 1);
                e.Handled = true;
                break;
            case VirtualKey.Down when alt && Rows.SelectedIndex >= 0 && Rows.SelectedIndex < _rows.Count - 1:
                _ = MoveAsync(Rows.SelectedIndex, Rows.SelectedIndex + 1);
                e.Handled = true;
                break;
        }
    }

    private void OnRowEntered(object sender, PointerRoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is PlaylistRow row) row.Hovered = true; }

    private void OnRowExited(object sender, PointerRoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is PlaylistRow row) row.Hovered = false; }

    private void OnRowRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlaylistRow row) _ = RemoveAsync([row]);
    }

    private void OnRowMore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PlaylistRow row) return;
        var index = _rows.IndexOf(row);
        var menu = FastUi.Flyout(new MenuFlyout());
        menu.Items.Add(Entry("PlaylistRowPlay", L.Text("Playlist.PlayFromHere"), Theme.Icons.Play, () => _shell.PlayQueue(Queue, _rows.IndexOf(row))));
        menu.Items.Add(new MenuFlyoutSeparator());
        var up = Entry("PlaylistRowUp", L.Text("Playlist.MoveUp"), Theme.Icons.ArrowUpward, () => _ = MoveAsync(_rows.IndexOf(row), _rows.IndexOf(row) - 1));
        up.IsEnabled = index > 0;
        up.KeyboardAcceleratorTextOverride = L.Text("Playlist.MoveUpShortcut");
        menu.Items.Add(up);
        var down = Entry("PlaylistRowDown", L.Text("Playlist.MoveDown"), Theme.Icons.ArrowDownward, () => _ = MoveAsync(_rows.IndexOf(row), _rows.IndexOf(row) + 1));
        down.IsEnabled = index < _rows.Count - 1;
        down.KeyboardAcceleratorTextOverride = L.Text("Playlist.MoveDownShortcut");
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Entry("PlaylistRowInfo", L.Text("ItemMenu.Info"), Theme.Icons.Info, () => _shell.ShowInfo(row.Item)));
        menu.Items.Add(Entry("PlaylistRowRemoveEntry", L.Text("Playlist.RemoveFromPlaylist"), Theme.Icons.RemoveCircle, () => _ = RemoveAsync([row])));
        menu.ShowAt((FrameworkElement)sender);
    }

    private static MenuFlyoutItem Entry(string automationId, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { FontFamily = Ui.Resource<Microsoft.UI.Xaml.Media.FontFamily>("LwIconFont"), Glyph = glyph } };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Removal is immediate, with no confirmation; a toast offers Undo for 6 s, which adds
    /// the items back and returns them to their places.</summary>
    private async Task RemoveAsync(IReadOnlyList<PlaylistRow> rows)
    {
        var indices = rows.Select(r => _rows.IndexOf(r)).Where(i => i >= 0).ToList();
        var removed = PlaylistModel.Remove(_rows, indices);
        if (removed.Count == 0) return;
        Renumber();
        try
        {
            await Session.RemoveFromPlaylistAsync(_playlist.Id, [.. removed.Select(r => r.Item.Item.PlaylistItemId).OfType<string>()]);
        }
        catch (Exception ex)
        {
            PlaylistModel.Restore(_rows, removed);
            Renumber();
            Diagnostics.AppLog.Error("playlists", $"event=remove outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.RemoveFailed"));
            return;
        }
        var message = removed.Count == 1
            ? L.Format("Playlist.RemovedOne", removed[0].Item.Title, _playlist.Name)
            : L.Format("Playlist.RemovedMany", removed.Count, _playlist.Name);
        ToastHost.Show(ToastKind.Success, message, L.Text("Toast.Undo"), () => _ = UndoRemoveAsync(removed));
    }

    private async Task UndoRemoveAsync(IReadOnlyList<(int Index, PlaylistRow Item)> removed)
    {
        try
        {
            await Session.AddToPlaylistAsync(_playlist.Id, [.. removed.Select(r => r.Item.Item.Id)]);
            var items = await Session.GetPlaylistItemsAsync(_playlist.Id);
            var restoredEntries = items.Skip(items.Count - removed.Count).ToList();
            for (var i = 0; i < removed.Count; i++)
                if (restoredEntries[i].PlaylistItemId is { } entry) await Session.MovePlaylistItemAsync(_playlist.Id, entry, removed[i].Index);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=undo_remove outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.UndoFailed"));
        }
        await LoadAsync();
    }

    private async Task MoveAsync(int from, int to)
    {
        if (from < 0 || to < 0 || to >= _rows.Count || _rows[from].Item.PlaylistItemId is not { } entry) return;
        _rows.Move(from, to);
        Rows.SelectedIndex = to;
        Renumber();
        await CommitMoveAsync(entry, to);
    }

    /// <summary>Drag reorder: the ListView has already moved the row; tell the server.</summary>
    private void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        Renumber();
        if (args.Items.FirstOrDefault() is PlaylistRow row && row.Item.PlaylistItemId is { } entry) _ = CommitMoveAsync(entry, _rows.IndexOf(row));
    }

    private async Task CommitMoveAsync(string entry, int to)
    {
        try
        {
            await Session.MovePlaylistItemAsync(_playlist.Id, entry, to);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=move outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.MoveFailed"));
            await LoadAsync();
        }
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        menu.Items.Add(Entry("PlaylistRename", L.Text("Playlist.Rename"), Theme.Icons.Edit, () => _ = RenameAsync()));
        menu.Items.Add(Entry("PlaylistDelete", L.Text("Playlist.DeletePlaylist"), Theme.Icons.Delete, () => _ = DeleteAsync()));
        menu.ShowAt(MoreButton);
    }

    private async Task RenameAsync()
    {
        if (await _shell.AskPlaylistNameAsync(L.Text("Playlist.RenameTitle"), L.Text("Playlist.Save"), _playlist.Name) is not { } name || name == _playlist.Name) return;
        try
        {
            await Session.RenamePlaylistAsync(_playlist.Id, name);
            TitleText.Text = name;
            await _shell.RefreshPlaylistsAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=rename outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.RenameFailed"));
        }
    }

    private async Task DeleteAsync()
    {
        if (!await PlaylistsPage.ConfirmDeleteAsync(XamlRoot, _playlist.Name)) return;
        try
        {
            await Session.DeleteItemAsync(_playlist.Id);
            await _shell.RefreshPlaylistsAsync();
            ToastHost.Show(ToastKind.Success, L.Format("Playlist.Deleted", _playlist.Name));
            _shell.OpenPlaylists();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=delete outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.DeleteFailed"));
        }
    }

    private void ApplyTexts()
    {
        AutomationProperties.SetName(MoreButton, L.Text("Card.More"));
        ToolTipService.SetToolTip(MoreButton, L.Text("Card.More"));
        if (_loaded) Show(Queue);
    }
}
