using System.Text.Json;
using LightWeaver.Localization;
using LightWeaver.Player;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace LightWeaver.WinUI.Shell;

/// <summary>What the WPF app could already do outside the library: open a local file (Ctrl+O, the
/// profile menu, a drop on the window, or a path on the command line), play a direct link (Ctrl+L,
/// the profile menu, or a drop), shrink the player into an always-on-top mini-player (Ctrl+M), and
/// come back where the window was.</summary>
public sealed partial class MainWindow
{
    private static string PlacementPath => Path.Combine(AppPaths.Root, "window-placement.json");
    private RectInt32? _normalBounds;
    private bool _miniPlayer;
    private Grid? _dropOverlay;
    private FontIcon? _dropIcon;
    private TextBlock? _dropHeading;
    private TextBlock? _dropName;
    private DropPlay? _drop;

    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    private void InitializeFiles()
    {
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenFileAsync());
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, () => _ = OpenStreamAsync());
        AddAccelerator(VirtualKey.M, VirtualKeyModifiers.Control, ToggleMiniPlayer);
        InitializeDrop();
        AppWindow.Changed += (_, e) =>
        {
            if ((e.DidSizeChange || e.DidPositionChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && !_miniPlayer && !IsOsFullscreen)
                _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        };
        Closed += (_, _) =>
        {
            FlushWindowChange();
            SavePlacement();
        };
    }

    /// <summary>A media file named on the command line plays once the window is up.</summary>
    private void PlayStartupFile()
    {
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (file is not null) Playback().PlayUrl(Path.GetFullPath(file));
    }

    private async Task OpenFileAsync()
    {
        if (_nav.Current?.Key == "login") return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        if (await picker.PickSingleFileAsync() is { } file) PlayAddress(file.Path);
    }

    /// <summary>Play a link: a direct HTTP(S) media link, played with the usual controls. A link on
    /// the clipboard is filled in and selected; Play stays off while the field is empty. No history
    /// is kept.</summary>
    private async Task OpenStreamAsync()
    {
        if (_nav.Current?.Key == "login" || _playerMode == PlayerMode.Full) return;
        var box = new TextBox { PlaceholderText = "https://", MinWidth = 412, FlowDirection = FlowDirection.LeftToRight };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "StreamUrlBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, L.Text("OpenStreamView.VideoUrl.b9f5332d"));
        var error = new TextBlock { Foreground = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwErrorBrush"), FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(error, "StreamUrlError");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(box);
        content.Children.Add(error);
        var dialog = new Controls.LwDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = L.Text("Files.PlayLinkTitle"),
            Content = content,
            PrimaryButtonText = L.Text("OpenStreamView.Play.67cefb55"),
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
            error.Visibility = Visibility.Collapsed;
        };
        if (await ClipboardLinkAsync() is { } copied) box.Text = copied;
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        Uri? address = null;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (StreamAddress.TryParse(box.Text, out address)) return;
            error.Text = StreamLink.HasCredentials(box.Text) ? L.Text("OpenStreamView.EnterAFullHttpOrHttpsUrl.aa9a2cb3") : L.Text("OpenStreamView.UseAFullHttpOrHttpsLink.163b1c5c");
            error.Visibility = Visibility.Visible;
            e.Cancel = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && address is not null) PlayAddress(address.AbsoluteUri);
    }

    /// <summary>The clipboard's text or web link when it is a link that plays; null otherwise.</summary>
    private static async Task<string?> ClipboardLinkAsync()
    {
        try
        {
            var clip = Clipboard.GetContent();
            var text = clip.Contains(StandardDataFormats.WebLink) ? (await clip.GetWebLinkAsync()).OriginalString
                : clip.Contains(StandardDataFormats.Text) ? await clip.GetTextAsync() : null;
            return DropPlay.Link(text) is not null ? text!.Trim() : null;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("files", $"event=clipboard_read outcome=failure error={ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Plays a local file or a link in the full player. It no longer belongs to a detail
    /// page's preview, so Back stops it rather than shrinking it onto another title's page. Over the
    /// preview, or during a grow or shrink, it takes the full window at once; a full player at rest
    /// (the mini-player included) stays as it is.</summary>
    private void PlayAddress(string address)
    {
        var playback = Playback();
        var intent = playback.Intent;
        playback.PlayUrl(address);
        if (playback.Intent == intent) return;
        DetachPreviewPage();
        if (_transition is not null || _playerMode != PlayerMode.Full) ShowFull();
    }

    /// <summary>Files and links dragged over the window (C1): the content layer dims and a card names
    /// what will play; anything else, or a drag over Login, gets no overlay and the no-drop cursor.
    /// A drop plays it and replaces what plays. Leaving one element for another inside the window
    /// raises a leave and an enter, so the overlay goes only when no enter follows within 50 ms. A
    /// read that finishes after a leave, a drop or a newer enter is dropped (<see cref="DragReads"/>).</summary>
    private void InitializeDrop()
    {
        Root.AllowDrop = true;
        var reads = new DragReads();
        var leave = DispatcherQueue.CreateTimer();
        leave.Interval = TimeSpan.FromMilliseconds(50);
        leave.IsRepeating = false;
        leave.Tick += (_, _) => HideDropOverlay();
        Root.DragEnter += async (_, e) =>
        {
            leave.Stop();
            var read = reads.Begin();
            var deferral = e.GetDeferral();
            try
            {
                var found = await DropPlayOf(e.DataView);
                e.AcceptedOperation = found is null ? DataPackageOperation.None : DataPackageOperation.Link;
                if (!reads.IsCurrent(read)) return;
                _drop = found;
                if (found is not null) ShowDropOverlay(found);
                else HideDropOverlay();
            }
            finally
            {
                deferral.Complete();
            }
        };
        Root.DragOver += (_, e) =>
        {
            leave.Stop();
            e.AcceptedOperation = _drop is null ? DataPackageOperation.None : DataPackageOperation.Link;
        };
        Root.DragLeave += (_, _) =>
        {
            reads.End();
            leave.Start();
        };
        Root.Drop += async (_, e) =>
        {
            reads.End();
            leave.Stop();
            HideDropOverlay();
            var deferral = e.GetDeferral();
            try
            {
                if (await DropPlayOf(e.DataView) is { } drop) PlayAddress(drop.Address);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    /// <summary>What a drag carries that would play: its first file, else a web link, else text that
    /// is a link.</summary>
    private async Task<DropPlay?> DropPlayOf(DataPackageView data)
    {
        if (_nav.Current?.Key == "login") return null;
        try
        {
            if (data.Contains(StandardDataFormats.StorageItems)
                && DropPlay.File((await data.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault()?.Path) is { } file)
                return file;
            if (data.Contains(StandardDataFormats.WebLink)) return DropPlay.Link((await data.GetWebLinkAsync()).OriginalString);
            if (data.Contains(StandardDataFormats.Text)) return DropPlay.Link(await data.GetTextAsync());
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("files", $"event=drop_read outcome=failure error={ex.GetType().Name}");
        }
        return null;
    }

    /// <summary>The drop overlay: <c>LwOverlayBrush</c> over the content layer (over the whole window
    /// while the full player shows), with one <c>LwPopupBrush</c> card naming the link's host and
    /// path or the file's name.</summary>
    private void ShowDropOverlay(DropPlay drop)
    {
        if (_dropOverlay is null)
        {
            _dropIcon = Controls.Ui.Icon(Theme.Icons.Link, 36);
            _dropIcon.Foreground = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwText2Brush");
            _dropIcon.Margin = new Thickness(0, 0, 0, 6);
            _dropHeading = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwTextBrush") };
            _dropName = new TextBlock { FontSize = 13, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwText2Brush") };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_dropName, "DropOverlayName");
            var text = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            text.Children.Add(_dropIcon);
            text.Children.Add(_dropHeading);
            text.Children.Add(_dropName);
            var card = new Border
            {
                Width = 360,
                Padding = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwPopupBrush"),
                BorderBrush = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwPopupBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = Controls.Ui.Resource<CornerRadius>("LwGroupRadius"),
                Translation = new System.Numerics.Vector3(0, 0, 24),
                Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow(),
                Child = text,
            };
            _dropOverlay = new Grid { Background = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwOverlayBrush"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_dropOverlay, "DropOverlay");
            _dropOverlay.Children.Add(card);
            Root.Children.Add(_dropOverlay);
        }
        _dropIcon!.Glyph = drop.IsLink ? Theme.Icons.Link : Theme.Icons.OpenFile;
        _dropHeading!.Text = L.Text("Files.DropToPlay");
        _dropName!.Text = drop.Name;
        _dropName.FlowDirection = drop.IsLink ? FlowDirection.LeftToRight : Root.FlowDirection;
        var overVideo = _playerMode == PlayerMode.Full;
        Grid.SetRow(_dropOverlay, overVideo ? 0 : 1);
        Grid.SetRowSpan(_dropOverlay, overVideo ? 2 : 1);
        _dropOverlay.Visibility = Visibility.Visible;
    }

    private void HideDropOverlay()
    {
        _drop = null;
        if (_dropOverlay is not null) _dropOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ctrl+M while video plays full: a compact always-on-top window with the player's
    /// small bar; again (or Esc) returns.</summary>
    private void ToggleMiniPlayer()
    {
        if (!_miniPlayer && _playerMode != PlayerMode.Full) return;
        SetOsFullscreen(false);
        FlushWindowChange();
        SuppressWindowTransitions();
        _miniPlayer = !_miniPlayer;
        AppWindow.SetPresenter(_miniPlayer ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.Default);
        _playerView?.SetCompact(_miniPlayer);
        if (_playerZoom is not null) _playerZoom.FollowsZoom = !_miniPlayer;
        BarZoom.Visibility = _miniPlayer ? Visibility.Collapsed : Visibility.Visible;
        _playerView?.Focus(FocusState.Programmatic);
    }

    private bool LeaveMiniPlayer()
    {
        if (!_miniPlayer) return false;
        ToggleMiniPlayer();
        return true;
    }

    /// <summary>The last size and place of the window, when it still fits a screen.</summary>
    private bool RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementPath) || JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath)) is not { } saved) return false;
            var bounds = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            var work = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None)?.WorkArea;
            if (work is not { } area || saved.Width < 200 || saved.Height < 200) return false;
            AppWindow.MoveAndResize(new RectInt32(Math.Max(area.X, saved.X), Math.Max(area.Y, saved.Y), Math.Min(saved.Width, area.Width), Math.Min(saved.Height, area.Height)));
            if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SavePlacement()
    {
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_WINDOW_RECT") is not null || _normalBounds is not { } bounds) return;
        try
        {
            var maximized = _fullscreen?.WasMaximized ?? AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Detail("window", $"event=placement_save outcome=failure error={ex.GetType().Name}");
        }
    }
}
