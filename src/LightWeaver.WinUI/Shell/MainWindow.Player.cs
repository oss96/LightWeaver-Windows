using LightWeaver.Details;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.WinUI.Pages;
using LightWeaver.WinUI.Player;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>The player in the window (design 13, 13a): one video surface that plays in the detail
/// page's preview and, on full screen (the button, F, a double-click, or Play with "full screen on
/// play" set), grows over 180 ms to fill the window and then the screen; leaving full screen, Esc or
/// Back puts the window back in one move and shrinks the video onto the preview. mpv keeps the same file throughout. Played from a card anywhere else, it
/// grows out of the card's artwork across the window in the same 180 ms; Esc stops it. Until the
/// first frame of the new title the artwork (the card's, or the preview's backdrop) stands in.</summary>
public sealed partial class MainWindow
{
    private enum PlayerMode { Hidden, Preview, Full }

    private PlaybackController? _playback;
    private Grid? _playerLayer;
    private Canvas? _playerBackdrop;
    private VideoSurface? _surface;
    private PlayerView? _playerView;
    private Controls.ZoomBox? _playerZoom;
    private PlayerMode _playerMode = PlayerMode.Hidden;
    private DetailPage? _previewPage;
    private LayerTransition? _transition;
    private LayerTransition? _fullscreenGrow;
    private EventHandler<object>? _transitionRetarget;
    private IDisposable? _transitionHold;
    private Services.FrameLog.Recording? _transitionFrames;
    private WindowFullscreen? _fullscreen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transitionsBack;
    private bool _localPlay;
    private readonly VolumeWheel _volumeWheel = new();

    private CompositeTransform LayerTransform => (CompositeTransform)_playerLayer!.RenderTransform;

    /// <summary>Builds the player layer the first time something plays.</summary>
    private PlaybackController Playback()
    {
        if (_playback is not null) return _playback;
        _surface = new VideoSurface();
        UpdateDisplaySize();
        _playerView = new PlayerView();
        _playerView.PreviewKeyDown += OnZoomKey;
        _playerView.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnZoomWheel), handledEventsToo: true);
        _playerView.PointerWheelChanged += OnPlayerWheel;
        _playerLayer = new Grid
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new CompositeTransform(),
        };
        _playerLayer.Children.Add(_surface);
        _playerZoom = new Controls.ZoomBox { Child = _playerView, FollowsZoom = !_miniPlayer };
        _playerLayer.Children.Add(_playerZoom);
        Grid.SetRowSpan(_playerLayer, 2);
        _playerBackdrop = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        Grid.SetRowSpan(_playerBackdrop, 2);
        Root.Children.Insert(Root.Children.IndexOf(ToastZoom), _playerBackdrop);
        Root.Children.Insert(Root.Children.IndexOf(ToastZoom), _playerLayer);
        Grid.SetRowSpan(ToastZoom, 2);

        _playback = new PlaybackController(Model, this, _surface, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _playerView.Attach(_playback);
        _playerView.LeaveRequested += (_, _) => LeavePlayer();
        _playerView.EscapeRequested += (_, _) =>
        {
            if (IsOsFullscreen) ExitVideoFullscreen();
            else LeavePlayer();
        };
        _playerView.FullscreenRequested += (_, _) => ToggleVideoFullscreen();
        _playback.Stopped += (_, _) => HidePlayer();
        InitializeMediaControls(_playback);
        ToastHost.OverVideo = (kind, text, mergeKey) =>
        {
            if (_playerMode != PlayerMode.Full || _playerView is null) return false;
            _playerView.ShowAnnouncement(text, mergeKey, kind);
            return true;
        };
        _playback.Started += (_, _) =>
        {
            if (_localPlay) return;
            if (_transition is not null && _previewPage is not null && !ReferenceEquals(_nav.CurrentPage, _previewPage))
            {
                DetachPreviewPage();
                ShowFull();
            }
            else if (_playerMode == PlayerMode.Hidden)
            {
                ShowFull();
            }
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange || e.DidSizeChange || e.DidPresenterChange) UpdateDisplaySize();
            if (e.DidPositionChange && _playerMode != PlayerMode.Hidden) _playback?.Retarget();
            if ((e.DidPositionChange || e.DidSizeChange) && _fullscreen is { } fullscreen) fullscreen.Reassert();
            if (e.DidPresenterChange) _playerView?.SetFullscreen(IsOsFullscreen);
        };
        _nav.Navigated += (_, _) =>
        {
            if (_playerMode == PlayerMode.Preview && !ReferenceEquals(_nav.CurrentPage, _previewPage)) _playback?.Stop();
        };
        return _playback;
    }

    /// <summary>The wheel over the full or mini player steps the volume as Up and Down do, shown by the
    /// volume readout alone; Ctrl+wheel stays the zoom, and the detail preview leaves the wheel alone.</summary>
    private void OnPlayerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_playerMode != PlayerMode.Full || _playback is null || _playerView is null
            || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        var wheel = e.GetCurrentPoint(_playerView).Properties;
        if (wheel.IsHorizontalMouseWheel) return;
        e.Handled = true;
        var steps = _volumeWheel.Steps(wheel.MouseWheelDelta);
        for (var i = 0; i < Math.Abs(steps); i++)
            PlayerActionDispatcher.Dispatch(steps > 0 ? PlayerAction.VolumeUp : PlayerAction.VolumeDown, _playback.ViewModel);
    }

    /// <summary>The video's swapchain is the size of the display the window is on.</summary>
    private void UpdateDisplaySize()
    {
        if (_surface is null) return;
        var bounds = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        _surface.SetDisplaySize(bounds.Width, bounds.Height);
    }

    private bool IsOsFullscreen => _fullscreen is not null;

    /// <summary>The window covers its monitor, or goes back to the rect and state it had, each in one
    /// window change with DWM's own window animations off, and with the content already laid out
    /// for the new size. <paramref name="landed"/> runs once the window has changed (at once when
    /// it is already in that state).</summary>
    private void SetOsFullscreen(bool fullscreen, Action? landed = null)
    {
        if (fullscreen == IsOsFullscreen)
        {
            if (_windowChange is { Changed: false } waiting) waiting.Landed += landed;
            else landed?.Invoke();
            return;
        }
        FlushWindowChange();
        SuppressWindowTransitions();
        WindowFullscreen change;
        LayerRect? content;
        if (fullscreen)
        {
            change = _fullscreen = new WindowFullscreen(WindowHandle);
            _windowedContent = WindowBounds;
            content = Root.XamlRoot is { } root ? change.Monitor.InDips(root.RasterizationScale) : null;
        }
        else
        {
            change = _fullscreen!;
            _fullscreen = null;
            content = change.Minimized ? null : new LayerRect(0, 0, _windowedContent.Width, _windowedContent.Height);
        }
        _playerView?.SetFullscreen(fullscreen);
        ChangeWindowAfterLayout(content, () =>
        {
            if (fullscreen) change.Enter();
            else change.Exit();
            _lastRegions = "";
            UpdateDragRegions();
            UpdateDisplaySize();
        }, landed);
    }

    private Windows.Foundation.Rect _windowedContent;
    private PendingWindowChange? _windowChange;
    private EventHandler<object>? _windowChangeFrame;
    private LayerTransition? _windowHold;

    /// <summary>A window change waiting for its layout to be on screen, then for XAML to take the
    /// window's new size.</summary>
    private sealed class PendingWindowChange(LayerRect size, Action change)
    {
        public LayerRect Size { get; } = size;
        public Action Change { get; } = change;
        public Action? Landed { get; set; }
        public int Frames { get; set; }
        public long ChangedAt { get; set; }
        public bool Changed => ChangedAt != 0;
    }

    /// <summary>Lays the content out at the size the window is about to have, and changes the window
    /// once that frame is on screen: the new rect and its layout show together, never the old layout
    /// in the new rect. The content keeps that size until XAML itself reports the window's new
    /// size, which comes a few frames after the window changed; let go earlier, the content is laid
    /// out again for the size XAML still has, the old one.</summary>
    private void ChangeWindowAfterLayout(LayerRect? content, Action change, Action? landed)
    {
        if (content is not { } size || (Math.Abs(size.Width - Root.ActualWidth) < 0.5 && Math.Abs(size.Height - Root.ActualHeight) < 0.5))
        {
            change();
            landed?.Invoke();
            return;
        }
        HoldPlayer(WindowBounds, size);
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.VerticalAlignment = VerticalAlignment.Top;
        Root.Width = size.Width;
        Root.Height = size.Height;
        var pending = _windowChange = new PendingWindowChange(size, change) { Landed = landed };
        _windowChangeFrame = (_, _) =>
        {
            if (!pending.Changed)
            {
                if (++pending.Frames >= WindowChange.LayoutFrames) CommitWindowChange(pending);
            }
            else if (Root.XamlRoot is not { } root
                || WindowChange.CanRelease(root.Size.Width, root.Size.Height, pending.Size, System.Diagnostics.Stopwatch.GetElapsedTime(pending.ChangedAt)))
            {
                ReleaseWindowChange();
            }
        };
        CompositionTarget.Rendering += _windowChangeFrame;
    }

    private void CommitWindowChange(PendingWindowChange pending)
    {
        WindowFullscreen.WaitForComposition();
        pending.Change();
        pending.ChangedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        ReleasePlayerHold();
        pending.Landed?.Invoke();
    }

    /// <summary>While the content is laid out for a window size it does not have yet, the full
    /// player is drawn where it was, at <paramref name="shown"/> (the window before the change),
    /// with its controls hidden: the new layout never shows in the old window rect either.</summary>
    private void HoldPlayer(Windows.Foundation.Rect shown, LayerRect size)
    {
        if (_playerMode != PlayerMode.Full || _transition is not null || _playerLayer is not { Visibility: Visibility.Visible } layer) return;
        _playerView?.SetChromeHidden(true);
        _windowHold?.Stop();
        _windowHold = new LayerTransition(layer, new LayerRect(shown.X, shown.Y, shown.Width, shown.Height), new LayerRect(0, 0, size.Width, size.Height), _surface!.Content, 0, _playerBackdrop);
        _windowHold.Hold();
    }

    /// <summary>The window has its new size: the player is drawn as laid out, with its controls.</summary>
    private void ReleasePlayerHold()
    {
        if (_windowHold is null) return;
        _windowHold.Stop();
        _windowHold = null;
        _playerView?.SetChromeHidden(false);
    }

    /// <summary>Makes a window change still waiting for its layout now, before anything else changes
    /// the window (the mini-player, closing, the opposite change), and lets the content go.</summary>
    private void FlushWindowChange()
    {
        if (_windowChange is not { } pending) return;
        if (!pending.Changed) CommitWindowChange(pending);
        ReleaseWindowChange();
    }

    private void ReleaseWindowChange()
    {
        _windowChange = null;
        CompositionTarget.Rendering -= _windowChangeFrame;
        _windowChangeFrame = null;
        Root.Width = double.NaN;
        Root.Height = double.NaN;
        Root.HorizontalAlignment = HorizontalAlignment.Stretch;
        Root.VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>Keeps DWM's minimize, maximize and restore animations off while the window changes
    /// shape for the player, and for half a second after it is back to normal.</summary>
    private void SuppressWindowTransitions()
    {
        WindowFullscreen.SetTransitionsEnabled(WindowHandle, false);
        if (_transitionsBack is null)
        {
            _transitionsBack = DispatcherQueue.CreateTimer();
            _transitionsBack.Interval = TimeSpan.FromMilliseconds(500);
            _transitionsBack.IsRepeating = false;
            _transitionsBack.Tick += (_, _) =>
            {
                if (!IsOsFullscreen && !_miniPlayer) WindowFullscreen.SetTransitionsEnabled(WindowHandle, true);
            };
        }
        _transitionsBack.Stop();
        _transitionsBack.Start();
    }

    public void Play(MediaItem item, TimeSpan? start = null, PlayOrigin? from = null)
        => Play(item, new PlaybackChoice(null, null, null, start ?? DetailModel.DefaultStart(item), null), fullscreen: false, from);

    /// <summary>Plays with the detail page's preselection. From the detail page the video plays in
    /// its preview (full screen at once with "full screen on play"); the same title already in the
    /// preview just resumes.</summary>
    public void Play(MediaItem item, PlaybackChoice choice) => Play(item, choice, fullscreen: false);

    public void Play(MediaItem item, PlaybackChoice choice, bool fullscreen) => Play(item, choice, fullscreen, null);

    private void Play(MediaItem item, PlaybackChoice choice, bool fullscreen, PlayOrigin? from)
    {
        if (ItemUnavailable(item)) return;
        var playback = Playback();
        _localPlay = true;
        try
        {
            PlayLocal(playback, item, choice, fullscreen || App.Settings.AutoFullscreen, from);
        }
        finally
        {
            _localPlay = false;
        }
    }

    private void PlayLocal(PlaybackController playback, MediaItem item, PlaybackChoice choice, bool fullscreen, PlayOrigin? from)
    {
        var page = _nav.CurrentPage as DetailPage;
        if (_playerMode == PlayerMode.Preview && playback.Current?.Id == item.Id && ReferenceEquals(page, _previewPage))
        {
            if (playback.Player is { } player)
            {
                if (Math.Abs(player.TimePos - choice.Start.TotalSeconds) > 2) player.TimePos = choice.Start.TotalSeconds;
                player.Pause = false;
            }
            if (fullscreen) EnterVideoFullscreen();
            else _playerView?.Focus(FocusState.Programmatic);
            return;
        }
        if (!ReferenceEquals(_previewPage, page)) DetachPreviewPage();
        _previewPage = page;
        if (page is not null) _surface!.Hold(page.BackdropSource, Stretch.UniformToFill);
        else _surface!.Hold(from?.Picture, Stretch.Uniform);
        playback.Play(item, choice);
        if (page is not null)
        {
            PlaceInPreview(page);
            if (fullscreen) EnterVideoFullscreen();
        }
        else
        {
            OpenFull(from, fullscreen);
        }
    }

    public void PlayQueue(IReadOnlyList<MediaItem> queue, int start, PlayOrigin? from = null)
    {
        if (queue.Count == 0) return;
        var target = queue[Math.Clamp(start, 0, queue.Count - 1)];
        if (ItemUnavailable(target)) return;
        queue = ItemRemovals.ForProfile(Model.ActiveSessionKey).Filter(queue);
        start = queue.ToList().FindIndex(item => item.Id == target.Id);
        _previewPage = null;
        var playback = Playback();
        _surface!.Hold(from?.Picture, Stretch.Uniform);
        _localPlay = true;
        try
        {
            playback.Play(queue[Math.Clamp(start, 0, queue.Count - 1)], null, queue, start);
        }
        finally
        {
            _localPlay = false;
        }
        OpenFull(from, App.Settings.AutoFullscreen);
    }

    public void PlayDownload(DownloadItem download)
    {
        var current = Downloads.Snapshot().FirstOrDefault(d => d.ItemId == download.ItemId) ?? download;
        if (!DownloadsModel.CanPlay(current))
        {
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.NotEnoughOfThisDownloadHasArrived.6f4c9c8c"), mergeKey: "download-not-ready");
            return;
        }
        var item = current.PlaybackMetadata ?? new MediaItem
        {
            Id = current.ItemId, Name = current.Title, RuntimeTicks = current.RuntimeTicks,
            Type = global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
        };
        _previewPage = null;
        var playback = Playback();
        _surface!.Hold(null, Stretch.Uniform);
        _localPlay = true;
        try
        {
            playback.PlayDownload(current, item, Model.FindSessionByServer(current.ServerUrl));
        }
        finally
        {
            _localPlay = false;
        }
        OpenFull(null, App.Settings.AutoFullscreen);
    }

    private bool ItemUnavailable(MediaItem item)
    {
        if (!ItemRemovals.ForProfile(Model.ActiveSessionKey).Contains(item.Id)) return false;
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7"), mergeKey: "item-missing");
        return true;
    }

    /// <summary>The full-window player for a play from outside the detail page: grown out of the
    /// card's artwork when it is still on screen, at once otherwise.</summary>
    private void OpenFull(PlayOrigin? from, bool fullscreen)
    {
        if (_playerMode == PlayerMode.Hidden && from is not null && OriginBounds(from) is { } rect)
        {
            _surface!.SeeThrough = false;
            _playerLayer!.CornerRadius = Controls.Ui.Resource<CornerRadius>("LwCardRadius");
            if (fullscreen) GrowIntoFullscreen(then => GrowFrom("player_grow_card", rect, then), null);
            else GrowFrom("player_grow_card", rect, null);
            return;
        }
        ShowFull();
        if (fullscreen) SetOsFullscreen(true);
    }

    /// <summary>The card's artwork in window coordinates; null once it has left the window.</summary>
    private Windows.Foundation.Rect? OriginBounds(PlayOrigin from)
    {
        var art = from.Art;
        if (!art.IsLoaded || art.XamlRoot != Root.XamlRoot || art.ActualWidth <= 0 || art.ActualHeight <= 0) return null;
        return art.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, art.ActualWidth, art.ActualHeight));
    }

    /// <summary>Back in the full player, or Esc outside full screen: back to the preview, paused, or
    /// stop. The shrink starts once the window is back.</summary>
    private void LeavePlayer()
    {
        if (_playerMode != PlayerMode.Full) return;
        if (CanReturnToPreview(out _) && _playback is { Failure: null, Player: { } player })
        {
            player.Pause = true;
            SetOsFullscreen(false, ShrinkToPreview);
        }
        else
        {
            SetOsFullscreen(false);
            _playback?.Stop();
        }
    }

    /// <summary>The full player shrinks onto the preview it came from, when that page is still shown.</summary>
    private void ShrinkToPreview()
    {
        if (_playerMode == PlayerMode.Full && CanReturnToPreview(out var page) && _playback?.Failure is null) Shrink(page);
    }

    private bool CanReturnToPreview(out DetailPage page)
    {
        page = _previewPage!;
        return _previewPage is not null && ReferenceEquals(_nav.CurrentPage, _previewPage);
    }

    /// <summary>The button, F or a double-click: the video fills the screen, or leaves full screen
    /// back to where it was; playing or paused stays as it is. Pressed again while a grow into full
    /// screen still runs, the grow ends in the window: back in the preview when it grew from there.</summary>
    private void ToggleVideoFullscreen()
    {
        if (_playerMode == PlayerMode.Hidden) return;
        switch (FullscreenIntent.Toggle(_fullscreenGrow is not null && ReferenceEquals(_fullscreenGrow, _transition), IsOsFullscreen))
        {
            case FullscreenToggle.CancelGrow: _fullscreenGrow = null; break;
            case FullscreenToggle.Exit: ExitVideoFullscreen(); break;
            default: EnterVideoFullscreen(); break;
        }
    }

    private void EnterVideoFullscreen()
    {
        if (_playerMode == PlayerMode.Preview)
        {
            GrowIntoFullscreen(Grow, ShrinkToPreview);
        }
        else
        {
            TakeOverFromTransition();
            SetOsFullscreen(true);
            TakeOverFromTransition();
            _playerView?.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Cancels a running shrink for the full player. Called before the window change, so it
    /// can hold the full player, and after it, for a pending exit the change flushed into a shrink.</summary>
    private void TakeOverFromTransition()
    {
        if (_transition is null) return;
        ShowFull();
        _playerView?.SetChromeHidden(false);
    }

    /// <summary>Starts <paramref name="grow"/> with full screen at its end, unless the toggle is
    /// pressed while it runs: then <paramref name="cancelled"/> runs at its end instead.</summary>
    private void GrowIntoFullscreen(Action<Action> grow, Action? cancelled)
    {
        LayerTransition? started = null;
        var ended = false;
        grow(() =>
        {
            ended = true;
            var wanted = started is null || ReferenceEquals(_fullscreenGrow, started);
            _fullscreenGrow = null;
            if (wanted) SetOsFullscreen(true);
            else cancelled?.Invoke();
        });
        if (!ended) started = _fullscreenGrow = _transition;
    }

    private void ExitVideoFullscreen() => SetOsFullscreen(false, ShrinkToPreview);

    private void ShowFull()
    {
        if (_playerLayer is null || _playerView is null) return;
        CancelTransition();
        _playerMode = PlayerMode.Full;
        _playerLayer.Visibility = Visibility.Visible;
        _playerLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _playerLayer.VerticalAlignment = VerticalAlignment.Stretch;
        _playerLayer.Width = double.NaN;
        _playerLayer.Height = double.NaN;
        _playerLayer.CornerRadius = new CornerRadius(0);
        _surface!.SeeThrough = false;
        ResetTransform();
        _playerView.SetCompact(false);
        _previewPage?.SetPlayerAttached(true);
        _playerView.Focus(FocusState.Programmatic);
    }

    /// <summary>Puts the surface over the detail page's preview, which hides its own bar while the
    /// player's compact bar stands in for it, and gives the player the keyboard. A grow or shrink
    /// still running for another page is cancelled, so its end cannot move or stop this one.</summary>
    private void PlaceInPreview(DetailPage page)
    {
        if (_playerLayer is null || _playerView is null) return;
        CancelTransition();
        var bounds = PreviewBounds(page);
        _playerMode = PlayerMode.Preview;
        _playerLayer.Visibility = Visibility.Visible;
        _playerLayer.HorizontalAlignment = HorizontalAlignment.Left;
        _playerLayer.VerticalAlignment = VerticalAlignment.Top;
        _playerLayer.Width = bounds.Width;
        _playerLayer.Height = bounds.Height;
        _playerLayer.CornerRadius = Controls.Ui.Resource<CornerRadius>("LwGroupRadius");
        _surface!.SeeThrough = true;
        ResetTransform();
        LayerTransform.TranslateX = bounds.X;
        LayerTransform.TranslateY = bounds.Y;
        _playerView.SetCompact(true);
        page.SetPlayerAttached(true);
        page.PreviewMoved -= OnPreviewMoved;
        page.PreviewMoved += OnPreviewMoved;
        _playerView.Focus(FocusState.Programmatic);
    }

    private void OnPreviewMoved(object? sender, EventArgs e)
    {
        if (_playerMode != PlayerMode.Preview || sender is not DetailPage page || !ReferenceEquals(page, _previewPage) || _transition is not null) return;
        SetLayerRect(PreviewBounds(page));
    }

    private Windows.Foundation.Rect PreviewBounds(DetailPage page)
    {
        var host = page.PreviewHost;
        return host.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, Math.Max(1, host.ActualWidth), Math.Max(1, host.ActualHeight)));
    }

    private Windows.Foundation.Rect WindowBounds => new(0, 0, Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));

    private void SetLayerRect(Windows.Foundation.Rect rect)
    {
        _playerLayer!.Width = rect.Width;
        _playerLayer.Height = rect.Height;
        LayerTransform.TranslateX = rect.X;
        LayerTransform.TranslateY = rect.Y;
    }

    /// <summary>From the preview to the full window over 180 ms, ease-out; a video still loading
    /// grows as the preview's backdrop.</summary>
    private void Grow(Action? then = null)
    {
        if (_playerLayer is null || _previewPage is not { } page || _playerMode == PlayerMode.Full)
        {
            ShowFull();
            then?.Invoke();
            return;
        }
        _surface!.SetArt(page.BackdropSource, Stretch.UniformToFill);
        _surface.SeeThrough = false;
        GrowFrom("player_grow", PreviewBounds(page), then);
    }

    /// <summary>Grows the layer from <paramref name="from"/> to the full window with the controls
    /// hidden; the player has the keyboard from the first frame.</summary>
    private void GrowFrom(string name, Windows.Foundation.Rect from, Action? then)
    {
        _playerMode = PlayerMode.Full;
        _playerView?.SetCompact(false);
        _playerView?.SetChromeHidden(true);
        _playerView?.Focus(FocusState.Programmatic);
        Animate(name, () => from, () => WindowBounds, () =>
        {
            ShowFull();
            _playerView?.SetChromeHidden(false);
            then?.Invoke();
        });
    }

    /// <summary>The same transition in reverse, ending in the preview at the current position. Out of
    /// full screen it starts once the window is back, with the page already laid out for it, so the
    /// window change (a DwmFlush and the move, up to a few hundred ms on a slow machine) never eats
    /// its 180 ms; a layout pass that still moves the preview retargets it. When the page was left
    /// meanwhile, playback stops, as it does for a preview whose page is left.</summary>
    private void Shrink(DetailPage page)
    {
        if (_playerLayer is null) return;
        _playerView?.SetChromeHidden(true);
        Animate("player_shrink", () => WindowBounds, () => PreviewBounds(page), () =>
        {
            _playerView?.SetChromeHidden(false);
            if (ReferenceEquals(_nav.CurrentPage, page)) PlaceInPreview(page);
            else _playback?.Stop();
        });
    }

    /// <summary>Moves and sizes the layer from <paramref name="from"/> to <paramref name="to"/>, all four
    /// edges in the same frame, on the compositor (<see cref="LayerTransition"/>): the layer is laid out
    /// at <paramref name="to"/> once and drawn at each frame's rect, so a busy UI thread cannot stall
    /// it, and artwork waits in <see cref="Services.ImageQueue"/> meanwhile. Both ends are measured
    /// again after every layout pass, so a window or page that is still settling moves the target and
    /// the last frame lands on where it ends up. The swapchain keeps the display's size and its fit
    /// for the laid-out rect; only the compositor's scale of the picture changes.
    /// <paramref name="name"/> labels it in the frame log.</summary>
    private void Animate(string name, Func<Windows.Foundation.Rect> from, Func<Windows.Foundation.Rect> to, Action done)
    {
        if (_playerLayer is null) return;
        StopTransition();
        _windowHold?.Stop();
        _windowHold = null;
        _playerLayer.HorizontalAlignment = HorizontalAlignment.Left;
        _playerLayer.VerticalAlignment = VerticalAlignment.Top;
        _playerLayer.Visibility = Visibility.Visible;
        var radius = _playerLayer.CornerRadius.TopLeft;
        _playerLayer.CornerRadius = new CornerRadius(0);
        static LayerRect Of(Windows.Foundation.Rect r) => new(r.X, r.Y, r.Width, r.Height);
        var end = to();
        SetLayerRect(end);
        var motion = new LayerTransition(_playerLayer, Of(from()), Of(end), _surface!.Content, radius, _surface.SeeThrough ? null : _playerBackdrop);
        motion.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_transition, motion)) return;
            StopTransition();
            done();
        };
        EventHandler<object> retarget = (_, _) =>
        {
            if (!ReferenceEquals(_transition, motion)) return;
            var moved = to();
            if (motion.Retarget(Of(from()), Of(moved))) SetLayerRect(moved);
        };
        _transition = motion;
        _transitionRetarget = retarget;
        _transitionHold = Services.ImageQueue.Hold();
        _transitionFrames = Services.FrameLog.Start(name);
        Root.LayoutUpdated += retarget;
        motion.Start();
    }

    /// <summary>Ends a grow or shrink before its last frame (the player shown at once): the
    /// controls it kept hidden may show again. A transition replaced by the next one
    /// goes through <see cref="StopTransition"/> and keeps them hidden.</summary>
    private void CancelTransition()
    {
        if (_transition is null) return;
        StopTransition();
        _playerView?.ReleaseChromeHold();
    }

    private void StopTransition()
    {
        if (_transitionRetarget is { } retarget) Root.LayoutUpdated -= retarget;
        _transitionRetarget = null;
        _transition?.Stop();
        _transition = null;
        _transitionHold?.Dispose();
        _transitionHold = null;
        _transitionFrames?.Dispose();
        _transitionFrames = null;
    }

    private void ResetTransform()
    {
        var transform = LayerTransform;
        transform.ScaleX = transform.ScaleY = 1;
        transform.TranslateX = transform.TranslateY = 0;
    }

    private void HidePlayer()
    {
        LeaveMiniPlayer();
        StopTransition();
        _playerView?.ReleaseChromeHold();
        if (_playerLayer is not null) _playerLayer.Visibility = Visibility.Collapsed;
        _surface?.Reset();
        SetOsFullscreen(false);
        DetachPreviewPage();
        _playerMode = PlayerMode.Hidden;
    }

    /// <summary>The detail page the player belongs to gets its own preview and bar back.</summary>
    private void DetachPreviewPage()
    {
        if (_previewPage is { } page)
        {
            page.SetPlayerAttached(false);
            page.PreviewMoved -= OnPreviewMoved;
        }
        _previewPage = null;
    }

    /// <summary>Esc while the player is full goes to the player first.</summary>
    private bool HandlePlayerEscape() => _playerMode == PlayerMode.Full && (LeaveMiniPlayer() || _playerView?.HandleEscape() == true);

    /// <summary>A player key pressed while the focus is elsewhere on the page (the detail page
    /// around the preview): handled by the player when it is on screen.</summary>
    private bool HandlePlayerKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        => _playerMode != PlayerMode.Hidden && _playerView?.HandleKey(e, fromOutside: true) == true;
}
