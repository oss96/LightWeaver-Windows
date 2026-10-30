using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace LightWeaver.WinUI.Player;

public sealed partial class PlayerView : UserControl
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMilliseconds(2500);
    private static readonly double[] Steps = [-0.1, 0.1];
    private static readonly TimeSpan VolumePillHold = TimeSpan.FromMilliseconds(1200);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _volumePillHide;
    private PlaybackController? _controller;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _stats;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _cursorWatch;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _groupClick;
    private int _groupClickSequence;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _downloadedRange;
    private FlyoutBase? _openFlyout;
    private bool _updatingSeek;
    private bool _updatingVolume;
    private bool _showRemaining = true;
    private bool _chromeShown = true;
    private bool _pointerSinceShown;
    private (int X, int Y)? _lastCursor;
    private bool _cursorHidden;
    private int _thumbRequest;
    private string? _upNextImageUrl;
    private (ulong Time, Windows.Foundation.Point Position)? _lastVideoPress;
    private bool _pausedBeforeClick;
    private bool _clickRequested;
    private bool _timeEditing;

    public PlayerView()
    {
        InitializeComponent();
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = IdleAfter;
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => HideChromeIfIdle();
        _stats = DispatcherQueue.CreateTimer();
        _stats.Interval = TimeSpan.FromSeconds(1);
        _stats.Tick += (_, _) => _controller?.ViewModel.RefreshLiveStats();
        _cursorWatch = DispatcherQueue.CreateTimer();
        _cursorWatch.Interval = TimeSpan.FromMilliseconds(150);
        _cursorWatch.Tick += (_, _) => { if (!PointerOverVideo()) ShowCursor(true); };
        _groupClick = DispatcherQueue.CreateTimer();
        _groupClick.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _groupClick.IsRepeating = false;
        _groupClick.Tick += (_, _) =>
        {
            _groupClick.Stop();
            ApplyPendingGroupClick();
        };
        _downloadedRange = DispatcherQueue.CreateTimer();
        _downloadedRange.Interval = TimeSpan.FromSeconds(1);
        _downloadedRange.Tick += (_, _) => { if (_controller?.DownloadedSeconds is not null) UpdateBars(); };
        Loaded += (_, _) => _downloadedRange.Start();
        KeyDown += OnKeyDown;
        SeekArea.SizeChanged += (_, _) => { UpdateBars(); DrawChapters(); };
        ButtonRow.Measured += (_, _) => UpdateTracksFold();
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyTexts(); Refresh(); }));
        App.SettingsChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTexts);
        Unloaded += (_, _) =>
        {
            ShowCursor(true);
            _downloadedRange.Stop();
            _groupClick.Stop();
        };
    }

    /// <summary>Back: leave the full-window player.</summary>
    public event EventHandler? LeaveRequested;

    /// <summary>Esc with nothing open over the video: leave full screen, or the player.</summary>
    public event EventHandler? EscapeRequested;

    /// <summary>F, double-click or the button: the video fills the screen, or leaves full screen.</summary>
    public event EventHandler? FullscreenRequested;

    private bool _chromeHeld;

    /// <summary>Hides the controls while the video grows or shrinks, and shows them again after;
    /// nothing else brings them back in between.</summary>
    public void SetChromeHidden(bool hidden)
    {
        _chromeHeld = hidden;
        if (hidden)
        {
            EndTimeEdit();
            _idle.Stop();
            _openFlyout?.Hide();
            TrickplayPopup.IsOpen = false;
            SetChromeShown(false);
        }
        else
        {
            ShowChrome();
        }
    }

    private bool _compact;

    /// <summary>In the detail preview the player shows only its bar, always, standing in for the
    /// page's preselection bar; full, it has the title, Back and the idle hide.</summary>
    public void SetCompact(bool compact)
    {
        _compact = compact;
        EndTimeEdit();
        TopArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumePill.Margin = compact ? new Thickness(0, 10, 12, 0) : new Thickness(0, 72, 16, 0);
        if (compact)
        {
            _idle.Stop();
            _openFlyout?.Hide();
            SetChromeShown(true);
            ShowCursor(true);
        }
        else
        {
            ShowChrome();
        }
        if (Vm is { } vm) UpdateRtxToggles(vm);
        PlaceOverlays();
    }

    private PlayerViewModel? Vm => _controller?.ViewModel;

    /// <summary>Shows whether the window is in OS full screen on the button.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        FullscreenIcon.Glyph = fullscreen ? Theme.Icons.ExitFullScreen : Theme.Icons.FullScreen;
        SetName(FullscreenButton, fullscreen ? L.Text("Player.ExitFullscreen") : L.Text("Player.Fullscreen"));
    }

    internal void Attach(PlaybackController controller)
    {
        _controller = controller;
        controller.ViewModel.PropertyChanged += OnViewModelChanged;
        controller.ViewModel.ChaptersChanged += () => DispatcherQueue.TryEnqueue(DrawChapters);
        controller.Started += (_, _) => { EndTimeEdit(); HideFailure(); ShowChrome(); Refresh(); };
        controller.Stopped += (_, _) => { EndTimeEdit(); HideFailure(); ShowCursor(true); };
        controller.Failed += (_, failure) => ShowFailure(failure);
        controller.ViewModel.FullscreenRequested += () => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        controller.ViewModel.VolumeFeedback += () => DispatcherQueue.TryEnqueue(ShowVolumePill);
        Refresh();
    }

    /// <summary>Shows the level, with the mute state in its glyph, for 1.2 s after the user changes
    /// either, without waking the controls.</summary>
    private void ShowVolumePill()
    {
        if (Vm is not { } vm) return;
        VolumePillIcon.Glyph = vm.IsMuted ? Theme.Icons.Mute : Theme.Icons.Volume;
        VolumePillText.Text = vm.VolumeReadout;
        VolumePill.Visibility = Visibility.Visible;
        if (_volumePillHide is null)
        {
            _volumePillHide = DispatcherQueue.CreateTimer();
            _volumePillHide.Interval = VolumePillHold;
            _volumePillHide.IsRepeating = false;
            _volumePillHide.Tick += (_, _) => VolumePill.Visibility = Visibility.Collapsed;
        }
        _volumePillHide.Stop();
        _volumePillHide.Start();
    }

    /// <summary>Playback failed (design 14b): over the black video, Retry at the same position, the
    /// cause's second action, and Details with the error text and Copy.</summary>
    private void ShowFailure(PlaybackFailure failure)
    {
        _openFlyout?.Hide();
        FailureTitle.Text = L.Text("Player.FailedTitle");
        FailureMessage.Text = failure.Cause switch
        {
            FailureCause.LocalFile => L.Text("Player.FailedLocal"),
            FailureCause.Link => L.Text("Player.FailedLink"),
            FailureCause.Transcode => L.Text("Player.FailedTranscode"),
            _ => L.Text("Player.FailedDirect"),
        };
        FailureRetryText.Text = L.Text("Errors.Retry");
        FailureOther.Content = failure.Alternative == FailureAlternative.OriginalQuality ? L.Text("Player.PlayOriginal") : L.Text("Player.PlayOtherVersion");
        FailureOther.Visibility = failure.Alternative == FailureAlternative.None ? Visibility.Collapsed : Visibility.Visible;
        FailureDetailsButton.Content = L.Text("Errors.Details");
        FailureReason.Text = failure.Reason;
        FailureCopyText.Text = L.Text("Errors.Copy");
        FailureDetails.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Visible;
        ShowChrome();
        FailureRetry.Focus(FocusState.Programmatic);
    }

    private void HideFailure() => FailurePanel.Visibility = Visibility.Collapsed;

    private void OnFailureRetry(object sender, RoutedEventArgs e) => _controller?.Retry();

    private void OnFailureAlternative(object sender, RoutedEventArgs e) => _controller?.Retry(_controller.Failure?.Alternative ?? FailureAlternative.None);

    private void OnFailureDetails(object sender, RoutedEventArgs e)
        => FailureDetails.Visibility = FailureDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnFailureCopy(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(FailureReason.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ToastHost.Show(ToastKind.Success, L.Text("Errors.Copied"));
    }

    private StackPanel? _pills;

    /// <summary>Toasts over video (SyncPlay joins and leaves among them): compact toasts on player
    /// glass, with the toast's stroke, shadow and its kind's glyph, top left; they stack, last 3 s, take
    /// no focus and show whether or not the bar does. One with the merge key of one on screen
    /// replaces it.</summary>
    public void ShowAnnouncement(string text, string? mergeKey = null, ToastKind kind = ToastKind.Info)
    {
        if (_pills is null)
        {
            _pills = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 72, 0, 0), IsHitTestVisible = false };
            AutomationProperties.SetAutomationId(_pills, "PlayerAnnouncements");
            Root.Children.Add(_pills);
        }
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = Ui.Resource<Brush>("LwPlayerTextBrush"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(label, "PlayerAnnouncement");
        var (glyph, color) = ToastHost.Mark(kind);
        var icon = Ui.Icon(glyph, 16);
        icon.Foreground = new SolidColorBrush(color);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } };
        var pill = new Border
        {
            MinHeight = 36,
            Padding = new Thickness(12, 0, 14, 1),
            CornerRadius = Ui.Resource<CornerRadius>("LwGroupRadius"),
            Background = Ui.Resource<Brush>("LwPlayerGlassBrush"),
            BorderBrush = Ui.Resource<Brush>("LwPlayerGlassBorderBrush"),
            BorderThickness = new Thickness(1),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Shadow = new ThemeShadow(),
            Child = row,
            Tag = mergeKey,
        };
        var merged = mergeKey is null ? -1 : _pills.Children.ToList().FindIndex(c => c is FrameworkElement { Tag: string key } && key == mergeKey);
        if (merged >= 0)
        {
            _pills.Children.RemoveAt(merged);
            _pills.Children.Insert(merged, pill);
        }
        else
        {
            _pills.Children.Add(pill);
        }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _pills.Children.Remove(pill);
        timer.Start();
    }

    /// <summary>Shows the controls and starts the idle countdown again.</summary>
    /// <summary>A grow or shrink was cancelled before it ended: nothing holds the controls hidden
    /// any more, without showing them now.</summary>
    public void ReleaseChromeHold() => _chromeHeld = false;

    public void ShowChrome()
    {
        if (_chromeHeld) return;
        SetChromeShown(true);
        ShowCursor(true);
        PlaceOverlays();
        _idle.Stop();
        if (!_compact) _idle.Start();
    }

    /// <summary>Idle-hidden controls stay in the tree, invisible and click-through, so keyboard
    /// focus, screen readers and UI Automation still reach them.</summary>
    private void SetChromeShown(bool shown)
    {
        if (shown && !_chromeShown)
        {
            _pointerSinceShown = false;
            _lastCursor = PointerSite.Cursor();
        }
        _chromeShown = shown;
        Chrome.Opacity = shown ? 1 : 0;
        Chrome.IsHitTestVisible = shown;
    }

    private void HideChromeIfIdle()
    {
        if (_compact || _timeEditing || Vm is not { } vm || vm.IsPaused || _openFlyout is not null || Visibility != Visibility.Visible) return;
        SetChromeShown(false);
        TrickplayPopup.IsOpen = false;
        if (PointerOverVideo()) ShowCursor(false);
        PlaceOverlays();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.PositionSeconds):
            case nameof(PlayerViewModel.DurationSeconds):
                UpdatePosition();
                break;
            case nameof(PlayerViewModel.IsPaused):
                UpdatePause();
                if (Vm?.IsPaused == true) ShowChrome();
                break;
            default:
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        if (Vm is not { } vm) return;
        TitleText.Text = vm.NowPlayingTitle;
        SubtitleText.Text = vm.NowPlayingSubtitle;
        SubtitleText.Visibility = vm.HasNowPlayingSubtitle ? Visibility.Visible : Visibility.Collapsed;
        UpdatePosition();
        UpdatePause();
        _updatingVolume = true;
        VolumeSlider.Value = Math.Clamp(vm.Volume, 0, 100);
        _updatingVolume = false;
        MuteIcon.Glyph = vm.IsMuted ? Theme.Icons.Mute : Theme.Icons.Volume;
        SetName(MuteButton, vm.IsMuted ? L.Text("Player.Unmute") : L.Text("Player.Mute"));
        SpeedText.Text = vm.SpeedDisplay;
        SetName(SpeedButton, L.Format("Player.SpeedIs", vm.SpeedDisplay));
        NextButton.Visibility = vm.CanGoNext ? Visibility.Visible : Visibility.Collapsed;
        QueueButton.Visibility = vm.HasQueue ? Visibility.Visible : Visibility.Collapsed;
        QualityButton.Visibility = vm.QualityVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateRtxToggles(vm);
        StatusText.Text = vm.PlaybackStatusText;
        StatusText.Visibility = vm.HasPlaybackStatus ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = vm.SkipButtonVisible ? Visibility.Visible : Visibility.Collapsed;
        SkipText.Text = vm.SkipButtonLabel;
        SetName(SkipButton, vm.SkipButtonLabel);
        UpNextCard.Visibility = vm.UpNextVisible ? Visibility.Visible : Visibility.Collapsed;
        UpNextTitle.Text = vm.UpNextTitle;
        UpNextSubtitle.Text = vm.UpNextSubtitle;
        UpNextCountdown.Text = vm.UpNextCountdownText;
        UpNextCountdown.Visibility = vm.UpNextHasCountdown ? Visibility.Visible : Visibility.Collapsed;
        if (vm.UpNextVisible && vm.UpNextThumbUrl != _upNextImageUrl) _ = LoadUpNextImageAsync(vm.UpNextThumbUrl);
        PlaceOverlays();
    }

    /// <summary>RTX VSR and RTX HDR show the saved choice, and only while the GPU and an SDR source let
    /// them apply; the compact bar leaves them out, as the WPF mini player does.</summary>
    private void UpdateRtxToggles(PlayerViewModel vm)
    {
        var shown = vm.RtxAvailable && !_compact ? Visibility.Visible : Visibility.Collapsed;
        VsrToggle.Visibility = shown;
        RtxHdrToggle.Visibility = shown;
        VsrToggle.IsChecked = vm.RtxVsrEnabled;
        RtxHdrToggle.IsChecked = vm.RtxHdrEnabled;
    }

    /// <summary>Skip intro and Up Next sit above the bar while it shows and move down when it hides.</summary>
    private void PlaceOverlays()
    {
        var bottom = _chromeShown ? Math.Max(96, Transport.ActualHeight + 16) : 24;
        SkipButton.Margin = new Thickness(0, 0, 24, bottom);
        UpNextCard.Margin = new Thickness(0, 0, 24, bottom);
    }

    private void UpdatePause()
    {
        if (Vm is not { } vm) return;
        PlayPauseIcon.Glyph = vm.IsPaused ? Theme.Icons.Play : Theme.Icons.Pause;
        SetName(PlayPauseButton, vm.IsPaused ? L.Text("Player.Play") : L.Text("Player.Pause"));
    }

    private void UpdatePosition()
    {
        if (Vm is not { } vm) return;
        var duration = Math.Max(0, vm.DurationSeconds);
        var position = Math.Clamp(vm.PositionSeconds, 0, duration > 0 ? duration : double.MaxValue);
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, duration);
        if (!vm.IsSeeking) SeekSlider.Value = position;
        _updatingSeek = false;
        PositionText.Text = Clock(position);
        RemainingText.Text = _showRemaining && duration > 0 ? "-" + Clock(duration - position) : Clock(duration);
        SetName(TimeButton, _showRemaining ? L.Text("Player.ShowTotal") : L.Text("Player.ShowRemaining"));
        UpdateBars();
    }

    private void UpdateBars()
    {
        if (Vm is not { } vm || vm.DurationSeconds <= 0) return;
        var width = TrackLayer.ActualWidth;
        PlayedBar.Width = Math.Max(0, width * Math.Clamp(vm.PositionSeconds / vm.DurationSeconds, 0, 1));
        var cached = _controller?.DownloadedSeconds
            ?? (double.TryParse(_controller?.Player?.GetPropertyString("demuxer-cache-time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cacheEnd) ? cacheEnd : 0);
        BufferedBar.Width = Math.Max(0, width * Math.Clamp(cached / vm.DurationSeconds, 0, 1));
    }

    /// <summary>Chapters are 2 px gaps in the track.</summary>
    private void DrawChapters()
    {
        ChapterGaps.Children.Clear();
        if (Vm is not { } vm || vm.DurationSeconds <= 0) return;
        foreach (var start in vm.ChapterStarts.Where(s => s > 0 && s < vm.DurationSeconds))
        {
            var gap = new Rectangle { Width = 2, Height = 4, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x10, 0x10, 0x10)) };
            Canvas.SetLeft(gap, TrackLayer.ActualWidth * start / vm.DurationSeconds - 1);
            ChapterGaps.Children.Add(gap);
        }
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || Vm is not { } vm) return;
        vm.Seek(e.NewValue);
        ShowChrome();
    }

    private void OnSeekReleased(object sender, PointerRoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsSeeking = false;
    }

    /// <summary>The trickplay frame, time and chapter above the pointer, at once. The frame keeps the
    /// thumbnails' own shape and is as large as fits above the bar and inside the player, and the
    /// card stays inside the player near its edges, so nothing of it is cut off.</summary>
    private async void OnSeekHover(object sender, PointerRoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.DurationSeconds <= 0 || _controller?.Current is not { } item) return;
        ShowChrome();
        var x = e.GetCurrentPoint(SeekArea).Position.X;
        var seconds = Math.Clamp(x / Math.Max(1, SeekArea.ActualWidth), 0, 1) * vm.DurationSeconds;
        HoverTime.Text = Clock(seconds);
        var chapter = item.Chapters.LastOrDefault(c => c.StartSeconds <= seconds);
        HoverChapter.Text = chapter?.Name ?? "";
        HoverChapter.Visibility = HoverChapter.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var barTop = Transport.TransformToVisual(this).TransformPoint(default).Y;
        var trickplay = _controller.Player is not null && vm.Trickplay is { IsUsable: true } usable ? usable : null;
        var (thumbWidth, thumbHeight) = trickplay is null ? (0d, 0d) : FitThumb(trickplay, ActualWidth, barTop);
        ThumbFrame.Visibility = trickplay is null || thumbWidth < 1 ? Visibility.Collapsed : Visibility.Visible;
        ThumbFrame.Width = thumbWidth;
        ThumbFrame.Height = thumbHeight;
        ThumbFrame.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, thumbWidth, thumbHeight) };
        HoverChapter.MaxWidth = Math.Max(160, thumbWidth);
        TrickplayCard.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var card = TrickplayCard.DesiredSize;
        var pointer = SeekArea.TransformToVisual(this).TransformPoint(default).X + x;
        const double Edge = 8;
        TrickplayPopup.HorizontalOffset = Math.Clamp(pointer - card.Width / 2, Edge, Math.Max(Edge, ActualWidth - card.Width - Edge));
        TrickplayPopup.VerticalOffset = Math.Max(Edge, barTop - card.Height - 6);
        TrickplayPopup.IsOpen = true;
        if (trickplay is null || ThumbFrame.Visibility != Visibility.Visible) return;

        var request = ++_thumbRequest;
        var (url, cellX, cellY, cellWidth, _) = trickplay.Locate(seconds);
        var image = await ImageLoader.LoadAsync(_controller.PlaybackSession ?? App.Model.Jellyfin, url, trickplay.SheetWidth);
        if (request != _thumbRequest || image is null) return;
        var scale = thumbWidth / cellWidth;
        ThumbSheet.Source = image;
        ThumbSheet.Width = trickplay.SheetWidth * scale;
        Canvas.SetLeft(ThumbSheet, -cellX * scale);
        Canvas.SetTop(ThumbSheet, -cellY * scale);
    }

    /// <summary>The thumbnail size: the trickplay's own aspect ratio, at most its native width (and
    /// 320 px), shrunk to fit the room above the bar and across the player.</summary>
    private static (double Width, double Height) FitThumb(LightWeaver.Jellyfin.TrickplaySource trickplay, double playerWidth, double barTop)
    {
        const double TextAndChrome = 58;
        var aspect = trickplay.ThumbHeight > 0 ? (double)trickplay.ThumbWidth / trickplay.ThumbHeight : 16.0 / 9;
        var width = Math.Min(Math.Min(trickplay.ThumbWidth, 320), playerWidth - 26);
        width = Math.Min(width, (barTop - TextAndChrome) * aspect);
        width = Math.Max(0, width);
        return (width, width / aspect);
    }

    private void OnSeekExited(object sender, PointerRoutedEventArgs e) => TrickplayPopup.IsOpen = false;

    private void OnTimeToggle(object sender, RoutedEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdatePosition();
    }

    /// <summary>Opens the selected position for editing and holds the controls visible.</summary>
    private void OnTimeEditBegin(object sender, RoutedEventArgs e)
    {
        if (_timeEditing || Vm is not { } vm) return;
        _timeEditing = true;
        _idle.Stop();
        TimeEditBox.Text = Clock(vm.PositionSeconds);
        PositionButton.Visibility = Visibility.Collapsed;
        TimeEditFrame.Visibility = Visibility.Visible;
        if (!FocusTimeEdit()) DispatcherQueue.TryEnqueue(() => FocusTimeEdit());
    }

    private bool FocusTimeEdit()
    {
        if (!_timeEditing || !TimeEditBox.Focus(FocusState.Programmatic)) return false;
        TimeEditBox.SelectAll();
        return true;
    }

    /// <summary>Enter seeks through the transport gate; Escape cancels.</summary>
    private void OnTimeEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var text = TimeEditBox.Text;
            EndTimeEdit();
            if (Vm is { } vm && TimeInput.TryParse(text, out var seconds)) vm.Seek(TimeInput.Target(seconds, vm.DurationSeconds));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            EndTimeEdit();
            e.Handled = true;
        }
    }

    private void OnTimeEditLostFocus(object sender, RoutedEventArgs e) => EndTimeEdit();

    /// <summary>Closes the editor and returns its focus to the player.</summary>
    private void EndTimeEdit()
    {
        if (!_timeEditing) return;
        _timeEditing = false;
        if (TimeEditBox.FocusState != FocusState.Unfocused) Focus(FocusState.Programmatic);
        TimeEditFrame.Visibility = Visibility.Collapsed;
        PositionButton.Visibility = Visibility.Visible;
        ShowChrome();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var now = PointerSite.Cursor();
        var moved = PlayerTooltips.Moved(_lastCursor, now);
        _lastCursor = now;
        ShowChrome();
        if (moved) _pointerSinceShown = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!PointerOverVideo()) ShowCursor(true);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    /// <summary>A click on the video closes an open flyout, or pauses and resumes at once. The second
    /// press of a double-click does nothing itself; the double-tap then puts back the state from before
    /// the first. In a SyncPlay group the click waits out the double-click time instead, and a second
    /// press cancels it, so a double-click for full screen sends the group nothing.</summary>
    private void OnVideoPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Root)) return;
        Focus(FocusState.Pointer);
        ShowChrome();
        if (_openFlyout is not null)
        {
            _openFlyout.Hide();
            return;
        }
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || Vm is not { } vm) return;
        if (_lastVideoPress is { } last && point.Timestamp - last.Time <= GetDoubleClickTime() * 1000UL
            && Math.Abs(point.Position.X - last.Position.X) <= 6 && Math.Abs(point.Position.Y - last.Position.Y) <= 6)
        {
            _lastVideoPress = null;
            if (_groupClick.IsRunning)
            {
                _groupClick.Stop();
                _clickRequested = false;
            }
            return;
        }
        _lastVideoPress = (point.Timestamp, point.Position);
        var pausedBefore = vm.IsPaused;
        if (_groupClick.IsRunning)
        {
            _groupClick.Stop();
            if (ApplyPendingGroupClick()) pausedBefore = !_pausedBeforeClick;
        }
        _pausedBeforeClick = pausedBefore;
        _clickRequested = true;
        if (vm.SyncPlay is not null)
        {
            _groupClickSequence = _controller?.Sequence ?? -1;
            _groupClick.Start();
            return;
        }
        vm.IsPaused = !_pausedBeforeClick;
    }

    /// <summary>Applies a group click that was waiting out the double-click time; false, and the
    /// click dropped, when another playback has started since it was made.</summary>
    private bool ApplyPendingGroupClick()
    {
        if (!_clickRequested || _controller?.Sequence != _groupClickSequence || Vm is not { } vm)
        {
            _clickRequested = false;
            return false;
        }
        vm.IsPaused = !_pausedBeforeClick;
        return true;
    }

    /// <summary>A double-click on the video toggles full screen, playing or paused as before it. The
    /// first press's request is answered by an explicit opposite one even before a SyncPlay group
    /// echoes it, since the gate only posts and the echo may still be on its way.</summary>
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Root)) return;
        e.Handled = true;
        if (_clickRequested && Vm is { } vm) vm.IsPaused = _pausedBeforeClick;
        _clickRequested = false;
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnBack(object sender, RoutedEventArgs e) => LeaveRequested?.Invoke(this, EventArgs.Empty);
    private void OnPlayPause(object sender, RoutedEventArgs e) => Vm?.TogglePauseCommand.Execute(null);
    private void OnJumpBack(object sender, RoutedEventArgs e) => Vm?.JumpBack();
    private void OnJumpForward(object sender, RoutedEventArgs e) => Vm?.JumpForwardLarge();
    private void OnNext(object sender, RoutedEventArgs e) => Vm?.GoNext();
    private void OnMute(object sender, RoutedEventArgs e) => Vm?.ToggleMute();
    private void OnFullscreen(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    private void OnSkip(object sender, RoutedEventArgs e) => Vm?.SkipActiveSegment();
    private void OnUpNextPlay(object sender, RoutedEventArgs e) => Vm?.PlayUpNextNow();
    private void OnUpNextCancel(object sender, RoutedEventArgs e) => Vm?.DismissUpNext();

    /// <summary>The toggles change the Settings defaults; saving them applies the change to the
    /// playing video the way the Settings page does.</summary>
    private void OnVsrToggle(object sender, RoutedEventArgs e)
    {
        App.Settings.RtxVsrDefault = VsrToggle.IsChecked == true;
        App.SaveSettings();
    }

    private void OnRtxHdrToggle(object sender, RoutedEventArgs e)
    {
        App.Settings.RtxHdrDefault = RtxHdrToggle.IsChecked == true;
        App.SaveSettings();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingVolume && Vm is { } vm) vm.Volume = e.NewValue;
    }

    /// <summary>Esc closes the open flyout first, then leaves the player.</summary>
    public bool HandleEscape()
    {
        if (_timeEditing)
        {
            EndTimeEdit();
            return true;
        }
        if (_openFlyout is not null)
        {
            _openFlyout.Hide();
            return true;
        }
        if (Vm?.UpNextVisible == true)
        {
            Vm.DismissUpNext();
            return true;
        }
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e) => HandleKey(e, fromOutside: false);

    /// <summary>Runs the player shortcut for the key; true when it was one. A key that reached the
    /// window from elsewhere on the page (<paramref name="fromOutside"/>) leaves Esc to the page
    /// while the player is compact.</summary>
    public bool HandleKey(KeyRoutedEventArgs e, bool fromOutside)
    {
        if (Vm is not { } vm || e.Handled || _timeEditing) return false;
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = Down(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Escape when fromOutside && _compact:
                return false;
            case VirtualKey.Escape:
                HandleEscape();
                break;
            case VirtualKey.F when !ctrl:
                FullscreenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.I when ctrl:
                ToggleFlyout(InfoButton, BuildInfo);
                break;
            default:
                if (KeyChords.Of(e.Key, ctrl, Down(VirtualKey.Menu), Down(VirtualKey.Shift)) is not { } chord) return false;
                var bound = PlayerActionDispatcher.Resolve(App.Settings.KeyBindings).FirstOrDefault(b => PlayerActionDispatcher.SameChord(b.Value, chord));
                if (bound.Value is null) return false;
                if (e.KeyStatus.WasKeyDown && !PlayerActionDispatcher.RepeatsWhileHeld(bound.Key)) break;
                if (bound.Key == PlayerAction.SubtitleEarlier) AdjustSubtitleDelay(Steps[0]);
                else if (bound.Key == PlayerAction.SubtitleLater) AdjustSubtitleDelay(Steps[1]);
                else PlayerActionDispatcher.Dispatch(bound.Key, vm);
                break;
        }
        ShowChrome();
        e.Handled = true;
        return true;
    }

    private void AdjustSubtitleDelay(double step)
    {
        if (_controller?.Player is not { } player) return;
        player.SubtitleDelay += step;
        ToastHost.Show(ToastKind.Info, L.Format("Player.SubtitleOffsetIs", player.SubtitleDelay.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentUICulture)));
    }

    // Flyouts: drawn inside the window above the transport bar, one at a time.

    private void OnTracks(object sender, RoutedEventArgs e) => ToggleFlyout(TracksButton, BuildTracks);
    private void OnAudioTracks(object sender, RoutedEventArgs e) => ToggleFlyout(AudioButton, BuildAudio);
    private void OnSubtitleTracks(object sender, RoutedEventArgs e) => ToggleFlyout(SubtitlesButton, BuildSubtitles);
    private void OnQuality(object sender, RoutedEventArgs e) => ToggleFlyout(QualityButton, BuildQuality);
    private void OnSpeed(object sender, RoutedEventArgs e) => ToggleFlyout(SpeedButton, BuildSpeed);
    private void OnQueue(object sender, RoutedEventArgs e) => ToggleFlyout(QueueButton, BuildQueue);
    private void OnInfo(object sender, RoutedEventArgs e) => ToggleFlyout(InfoButton, BuildInfo);

    private void ToggleFlyout(FrameworkElement anchor, Func<FrameworkElement> content)
    {
        if (_openFlyout is not null)
        {
            var wasHere = ReferenceEquals(_openFlyout.Target, anchor);
            _openFlyout.Hide();
            if (wasHere) return;
        }
        var flyout = FastUi.Flyout(new Flyout { Placement = FlyoutPlacementMode.Top, ShouldConstrainToRootBounds = true, FlyoutPresenterStyle = Ui.Resource<Style>("LwPlayerFlyoutPresenter"), Content = content() });
        flyout.Opened += (_, _) => { _openFlyout = flyout; ShowChrome(); };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openFlyout, flyout)) _openFlyout = null;
            _stats.Stop();
            ShowChrome();
        };
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top, Position = AboveTransport(anchor) });
    }

    /// <summary>The gap between a flyout and the top of the transport bar (design 13).</summary>
    private const double FlyoutGap = 8;

    /// <summary>Where a flyout of <paramref name="anchor"/> sits, in its coordinates: centred over the
    /// button, its bottom edge above the whole transport bar, so the seek bar and the times stay in
    /// view whichever row the button is on.</summary>
    private Windows.Foundation.Point AboveTransport(FrameworkElement anchor)
        => new(anchor.ActualWidth / 2, Transport.TransformToVisual(anchor).TransformPoint(default).Y - FlyoutGap);

    private bool _tracksFolded;

    /// <summary>Audio and Subtitles fold into the Tracks button while the bar's two groups would
    /// not fit on one row with them, so the compact bar in a small preview stays one row.</summary>
    private void UpdateTracksFold()
    {
        var fold = PlayerBar.FoldsTracks(ButtonRow.AvailableWidth, ButtonRow.OneRowWidth, _tracksFolded, SubtitlesButton.Width + EndGroup.HorizontalSpacing);
        if (fold == _tracksFolded) return;
        _tracksFolded = fold;
        if (_openFlyout?.Target is { } target && (target == AudioButton || target == SubtitlesButton || target == TracksButton)) _openFlyout.Hide();
        AudioButton.Visibility = SubtitlesButton.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        TracksButton.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Audio and subtitles side by side (design 13c); a choice applies at once and the
    /// flyout stays open.</summary>
    private FrameworkElement BuildTracks()
    {
        var both = new Grid { ColumnSpacing = 16 };
        both.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        both.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var subtitles = BuildSubtitles();
        Grid.SetColumn(subtitles, 1);
        both.Children.Add(BuildAudio());
        both.Children.Add(subtitles);
        return both;
    }

    /// <summary>The audio tracks; a choice applies at once and the flyout stays open.</summary>
    private FrameworkElement BuildAudio()
    {
        var vm = Vm!;
        var audio = Column(L.Text("Player.Audio"));
        audio.MinWidth = 220;
        foreach (var option in vm.AudioTracks)
            audio.Children.Add(Choice("PlayerAudioOption", "PlayerAudio", option.Display, option.IsSelected, () => vm.SelectAudio(option)));
        return audio;
    }

    /// <summary>The subtitle tracks and the subtitle offset; a choice applies at once and the flyout
    /// stays open.</summary>
    private FrameworkElement BuildSubtitles()
    {
        var vm = Vm!;
        var subtitles = Column(L.Text("Player.Subtitles"));
        subtitles.MinWidth = 220;
        var rows = new StackPanel();
        var status = new TextBlock { FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "PlayerExternalSubtitleStatus");
        status.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = vm, Path = new PropertyPath(nameof(vm.ExternalSubtitleStatus)) });
        void UpdateRows()
        {
            var focused = rows.Children.OfType<RadioButton>().FirstOrDefault(r => r.FocusState != FocusState.Unfocused);
            var focusedId = focused?.Tag;
            var focusState = focused?.FocusState ?? FocusState.Programmatic;
            rows.Children.Clear();
            foreach (var option in vm.SubtitleTracks)
            {
                var row = Choice("PlayerSubtitleOption", "PlayerSubtitle", option.LiveDisplay, option.IsSelected, () => vm.SelectSubtitle(option));
                row.Tag = option.Id;
                rows.Children.Add(row);
                if (Equals(focusedId, option.Id)) row.Focus(focusState);
            }
            foreach (var option in vm.UnavailableSubtitles)
            {
                var row = Choice("PlayerSubtitleUnavailable", "PlayerSubtitle", option.Display, false, () => { });
                row.IsEnabled = false;
                rows.Children.Add(row);
            }
        }
        var refreshQueued = false;
        var subscribed = false;
        void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (refreshQueued) return;
            refreshQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                refreshQueued = false;
                if (subscribed) UpdateRows();
            });
        }
        void Subscribe()
        {
            subscribed = true;
            vm.SubtitleTracks.CollectionChanged += OnRowsChanged;
            vm.UnavailableSubtitles.CollectionChanged += OnRowsChanged;
        }
        rows.Loaded += (_, _) =>
        {
            if (subscribed) return;
            Subscribe();
            UpdateRows();
        };
        rows.Unloaded += (_, _) =>
        {
            subscribed = false;
            vm.SubtitleTracks.CollectionChanged -= OnRowsChanged;
            vm.UnavailableSubtitles.CollectionChanged -= OnRowsChanged;
        };
        Subscribe();
        UpdateRows();
        subtitles.Children.Add(rows);
        subtitles.Children.Add(status);
        var offset = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetAutomationId(value, "PlayerSubtitleOffset");
        void ShowOffset() => value.Text = (_controller?.Player?.SubtitleDelay ?? 0).ToString("+0.0 s;-0.0 s;0.0 s", CultureInfo.CurrentUICulture);
        offset.Children.Add(new TextBlock { Text = L.Text("Player.SubtitleOffset"), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Resource<Brush>("LwText2Brush"), FontSize = 12 });
        offset.Children.Add(SmallButton("PlayerSubtitleEarlier", "−", L.Text("Player.SubtitlesEarlier"), () => { if (_controller?.Player is { } p) p.SubtitleDelay -= 0.1; ShowOffset(); }));
        offset.Children.Add(value);
        offset.Children.Add(SmallButton("PlayerSubtitleLater", "+", L.Text("Player.SubtitlesLater"), () => { if (_controller?.Player is { } p) p.SubtitleDelay += 0.1; ShowOffset(); }));
        ShowOffset();
        subtitles.Children.Add(offset);
        return subtitles;
    }

    /// <summary>The quality tiers, each with its plain name and bitrate (High · 1080p · 20 Mbps).</summary>
    private FrameworkElement BuildQuality()
    {
        var vm = Vm!;
        var panel = Column(L.Text("Player.Quality"));
        string[] tiers = [.. PlayerViewModel.QualityTierMbps.Select(mbps => Details.QualityNames.Tier(mbps, App.Settings.MaxStreamingBitrateMbps))];
        for (var i = 0; i < tiers.Length; i++)
        {
            var index = i;
            var radio = Choice("PlayerQualityOption", "PlayerQuality", tiers[i], vm.QualityTierIndex == i, () => vm.QualityTierIndex = index);
            radio.IsEnabled = vm.QualityAvailable;
            panel.Children.Add(radio);
        }
        var force = new ToggleSwitch { Header = L.Text("Player.ForceTranscode"), IsOn = vm.QualityForceTranscode, IsEnabled = vm.QualityAvailable };
        AutomationProperties.SetAutomationId(force, "PlayerForceTranscode");
        force.Toggled += (_, _) => vm.QualityForceTranscode = force.IsOn;
        panel.Children.Add(force);
        if (vm.HasVersionChoices)
        {
            panel.Children.Add(Heading(L.Text("Player.Version")));
            foreach (var version in vm.Versions)
                panel.Children.Add(Choice("PlayerVersionOption", "PlayerVersion", version.Display, version.IsSelected, () => vm.SelectVersion(version)));
        }
        var status = new TextBlock { Text = vm.QualityStatus, FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 260, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(status, "PlayerQualityStatus");
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>Six fixed speeds; the button shows the current one.</summary>
    private FrameworkElement BuildSpeed()
    {
        var vm = Vm!;
        var panel = Column(L.Text("Player.Speed"));
        foreach (var option in vm.SpeedOptions)
            panel.Children.Add(Choice("PlayerSpeedOption", "PlayerSpeed", option.Display, option.IsSelected, () => { vm.SelectSpeed(option); _openFlyout?.Hide(); }));
        return panel;
    }

    /// <summary>The current item marked; clicking another plays it, × removes it.</summary>
    private FrameworkElement BuildQueue()
    {
        var vm = Vm!;
        var panel = Column(L.Text("Player.Queue"));
        panel.MinWidth = 320;
        foreach (var row in vm.QueueRows)
        {
            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var play = new Button
            {
                Style = Ui.Resource<Style>("LwSubtleButton"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Content = new TextBlock { Text = $"{row.Position} {row.LiveDisplay}", FontWeight = row.IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = row.IsCurrent ? Ui.Resource<Brush>("LwAccentFgBrush") : Ui.Resource<Brush>("LwTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis },
            };
            AutomationProperties.SetAutomationId(play, "PlayerQueueRow");
            AutomationProperties.SetName(play, row.LiveDisplay);
            AutomationProperties.SetItemStatus(play, row.IsCurrent ? "current" : "");
            play.Click += (_, _) => { vm.JumpToQueueRow(row); _openFlyout?.Hide(); };
            line.Children.Add(play);
            if (row.CanRemove)
            {
                var remove = SmallButton("PlayerQueueRemove", "×", L.Format("Player.RemoveFromQueue", row.LiveDisplay), () => { vm.RemoveQueueRow(row); _openFlyout?.Hide(); });
                Grid.SetColumn(remove, 1);
                line.Children.Add(remove);
            }
            panel.Children.Add(line);
        }
        return new ScrollViewer { Content = panel, MaxHeight = 420 };
    }

    /// <summary>Live stats in monospace, refreshed every second while open.</summary>
    private FrameworkElement BuildInfo()
    {
        var vm = Vm!;
        vm.RefreshLiveStats();
        var panel = Column(L.Text("Player.Info"));
        var stats = new TextBlock { FontFamily = Ui.Resource<FontFamily>("LwMonoFont"), FontSize = 12, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(stats, "PlayerStats");
        void Fill() => stats.Text = string.Join("\n", vm.LiveStats.Concat(vm.MediaInfoRows).Select(r => $"{r.LiveLabel}: {r.LiveValue}"));
        Fill();
        vm.LiveStats.CollectionChanged += (_, _) => Fill();
        panel.Children.Add(stats);
        _stats.Start();
        return panel;
    }

    private static StackPanel Column(string title)
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 180 };
        panel.Children.Add(Heading(title));
        return panel;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Resource<Brush>("LwText3Brush"), Margin = new Thickness(0, 4, 0, 4) };

    private static RadioButton Choice(string automationId, string group, string label, bool selected, Action choose)
    {
        var radio = new RadioButton { Content = label, GroupName = group, IsChecked = selected, MinWidth = 0 };
        AutomationProperties.SetAutomationId(radio, automationId);
        radio.Checked += (_, _) => choose();
        return radio;
    }

    private static Button SmallButton(string automationId, string glyph, string name, Action action)
    {
        var button = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 28, Height = 28, Padding = new Thickness(0), Content = glyph };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private async Task LoadUpNextImageAsync(string? url)
    {
        _upNextImageUrl = url;
        UpNextImage.Source = null;
        if (url is null) return;
        if (await ImageLoader.LoadAsync(_controller?.PlaybackSession ?? App.Model.Jellyfin, url, 224) is { } image && url == _upNextImageUrl) UpNextImage.Source = image;
    }

    private void ApplyTexts()
    {
        SetName(BackButton, L.Text("Player.Back"));
        SetName(PositionButton, L.Text("Player.GoToTime"));
        SetName(TimeEditBox, L.Text("Player.GoToTime"), L.Text("OverlayWindow.SsMmSsOrHhMmSs.f3d1e5a4"));
        SetName(JumpBackButton, L.Format("Player.JumpBackBy", App.Settings.SkipBackwardSeconds));
        SetName(JumpForwardButton, L.Format("Player.JumpForwardBy", App.Settings.LargeSkipSeconds));
        SetName(NextButton, L.Text("Player.Next"));
        SetName(AudioButton, L.Text("Player.Audio"));
        SetName(SubtitlesButton, L.Text("Player.Subtitles"));
        SetName(TracksButton, L.Text("Player.Tracks"));
        SetName(QualityButton, L.Text("Player.Quality"));
        SetName(QueueButton, L.Text("Player.Queue"));
        VsrToggle.Content = L.Text("Player.RtxVsr");
        RtxHdrToggle.Content = L.Text("Player.RtxHdr");
        SetName(VsrToggle, L.Text("Player.RtxVsrName"));
        SetName(RtxHdrToggle, L.Text("Player.RtxHdrName"));
        SetName(InfoButton, L.Text("Player.InfoShortcut"));
        AutomationProperties.SetName(SeekSlider, L.Text("Player.Seek"));
        AutomationProperties.SetName(VolumeSlider, L.Text("Player.Volume"));
        UpNextLabel.Text = L.Text("Player.UpNext");
        UpNextPlay.Content = L.Text("Player.PlayNow");
        UpNextCancel.Content = L.Text("Player.Cancel");
        SetFullscreen(false);
    }

    /// <summary>Names a control for UI Automation and its tooltip (<paramref name="tooltip"/> when the
    /// tooltip says more than the name); the tooltip opens only as
    /// <see cref="PlayerTooltips.Opens"/> allows.</summary>
    private void SetName(FrameworkElement element, string name, string? tooltip = null)
    {
        AutomationProperties.SetName(element, name);
        if (ToolTipService.GetToolTip(element) is ToolTip shown)
        {
            shown.Content = tooltip ?? name;
            return;
        }
        var tip = new ToolTip { Content = tooltip ?? name };
        tip.Opened += (_, _) =>
        {
            if (!PlayerTooltips.Opens(element is Control { FocusState: FocusState.Keyboard }, _pointerSinceShown)) tip.IsOpen = false;
        };
        ToolTipService.SetToolTip(element, tip);
    }

    /// <summary>Win32's ShowCursor hides the pointer over every part of the window, title bar and
    /// borders included, so while it is hidden a watch shows it again once it leaves the video.</summary>
    private void ShowCursor(bool show)
    {
        if (show == !_cursorHidden) return;
        _cursorHidden = !show;
        NativeShowCursor(show);
        if (show) _cursorWatch.Stop();
        else _cursorWatch.Start();
    }

    /// <summary>True while the pointer rests on this player in the active window; false over the
    /// title bar, caption buttons, resize borders, another window, or a closed player.</summary>
    private bool PointerOverVideo()
    {
        if (XamlRoot is not { ContentIslandEnvironment: { } island } root || PointerSite.OverContent(Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId)) is not { } pixels) return false;
        var point = new Windows.Foundation.Point(pixels.X / root.RasterizationScale, pixels.Y / root.RasterizationScale);
        return VisualTreeHelper.FindElementsInHostCoordinates(point, Root).Any();
    }

    [LibraryImport("user32.dll", EntryPoint = "ShowCursor")]
    private static partial int NativeShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
