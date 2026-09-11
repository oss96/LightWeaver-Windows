using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// One intercepted external move of the overlay: where it was asked to go (PHYSICAL pixels,
/// the proposed <c>WINDOWPOS</c> origin) and which monitor that lands on. The owner fills in
/// <see cref="OwnerState"/> and <see cref="Action"/> while handling
/// <see cref="OverlayWindow.ExternalMoveRequested"/>; the overlay then writes exactly one log
/// line from them. Mutable-on-the-way-back rather than a return value because the owner's
/// decision has three outcomes and the log line has to name the one that happened.
/// </summary>
public sealed class OverlayExternalMove(int x, int y, nint monitor)
{
    public int X { get; } = x;
    public int Y { get; } = y;

    /// <summary>Monitor the proposed rect's centre falls on. Never <c>0</c> — the hook drops a
    /// proposal that is on no monitor before raising the event.</summary>
    public nint Monitor { get; } = monitor;

    /// <summary>Normal | Maximized | Mini, as the owner sees itself.</summary>
    public string OwnerState { get; set; } = "Normal";

    /// <summary>translate | remaximize | ignore | skipped. "ignore" is the DELIBERATE refusal —
    /// the veto alone is the whole answer — while "skipped" means the owner could not act at all
    /// (no overlay, re-entrant call, no HWND yet). They look the same on screen and have to be
    /// told apart in the log. Stays "ignore" when nothing handled the event.</summary>
    public string Action { get; set; } = "ignore";
}

/// <summary>
/// Transparent overlay that floats over the mpv video HWND (which punches through
/// WPF airspace — controls can't live in MainWindow). Owned by MainWindow; bounds
/// are synced by the owner. The alpha-1 root grid captures all mouse input over
/// the video: idle auto-hide, click-to-pause, double-click fullscreen, drag-drop.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>How long the volume pill holds after the LAST adjustment (P10 M10) — the
    /// timer is restarted per change, so holding a key down keeps it up throughout.</summary>
    private static readonly TimeSpan VolumePillHold = TimeSpan.FromMilliseconds(1200);

    private readonly PlayerViewModel _viewModel;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _volumePillTimer;
    private bool _controlsVisible = true;
    private bool _volumePillVisible;

    /// <summary>Single-click on empty video area (used for pause toggle).</summary>
    public event Action? VideoAreaClicked;

    /// <summary>Double-click on the video area (used for fullscreen toggle).</summary>
    public event Action? VideoAreaDoubleClicked;

    /// <summary>A media file was dropped onto the video area.</summary>
    public event Action<string>? FileDropped;

    /// <summary>The Back button was pressed — leave playback and return to browsing.</summary>
    public event Action? BackRequested;

    /// <summary>Enter mini-player mode (overlay button).</summary>
    public event Action? MiniPlayerRequested;

    /// <summary>Leave mini-player mode (restore button).</summary>
    public event Action? MiniRestoreRequested;

    /// <summary>Mouse went down on the video area while in mini mode — the owner
    /// starts a window drag (mini windows are chrome-less).</summary>
    public event Action? MiniDragRequested;

    /// <summary>Bottom-right grip pressed in mini mode — the owner starts a native
    /// HTBOTTOMRIGHT sizing loop (M12).</summary>
    public event Action? MiniResizeRequested;

    /// <summary>Something outside the app moved this window with its own <c>SetWindowPos</c> —
    /// a shell snap gesture, a window-management utility. The owner drags the video after it
    /// (<c>MainWindow.MoveWithOverlay</c>) and reports back what it did, so the one log line is
    /// written in one place. See <see cref="ExternalMoveHook"/>.</summary>
    public event Action<OverlayExternalMove>? ExternalMoveRequested;

    /// <summary>A key was pressed while this window held focus. The owner routes it through
    /// the same handler its own <c>OnKeyDown</c> uses — plus its <c>InputBindings</c> — so
    /// every keyboard affordance works regardless of which of the two windows the keystroke
    /// was delivered to (Phase 9 M1).</summary>
    public event Action<KeyEventArgs>? KeyPressed;

    public OverlayWindow(PlayerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _idleTimer = new DispatcherTimer { Interval = IdleHideDelay };
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); HideControls(); };
        _idleTimer.Start();

        _volumePillTimer = new DispatcherTimer { Interval = VolumePillHold };
        _volumePillTimer.Tick += (_, _) => { _volumePillTimer.Stop(); HideVolumePill(); };
        _viewModel.VolumeFeedback += ShowVolumePill;

        // handledEventsToo: Slider's IsMoveToPointEnabled class handler marks the
        // preview-down handled, which would skip a XAML-attached handler entirely.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekMouseDown), handledEventsToo: true);

        // Mouse thumb buttons over the video (Phase 7 M7): XButton1 = back (stop playback
        // and return); XButton2 is reserved (no-op in the player). The overlay is a
        // separate top-level window, so it receives all mouse input over the video HWND.
        PreviewMouseDown += OnOverlayMouseDown;

        // Player keys must work even when this window holds focus (M1). PreviewKeyDown, not
        // KeyDown: the tunneling pass reaches this window BEFORE the focused child, which
        // matters because a focused Slider otherwise consumes the arrow keys as its own
        // SmallChange nudge (measured: volume moved 50 -> 50.1 instead of 55) and marks them
        // handled, so a bubbling handler here would never run.
        PreviewKeyDown += OnOverlayKeyDown;
        // '?' over the video. Matched on the character for the same reason MainWindow does
        // (layout-independent, and a chord would eat the key from a focused text field).
        TextInput += (_, e) =>
        {
            // Not while the time editor holds the keyboard: there '?' is a character being typed,
            // and opening the panel would take the keyboard off a field that is still up. Same
            // guard MainWindow.OnWindowTextInput carries (IsTextEntryFocused), for the same reason.
            if (e.Text != "?" || TimeEditBox.IsKeyboardFocusWithin)
                return;
            ShortcutsRequested?.Invoke();
            e.Handled = true;
        };

        _viewModel.ChaptersChanged += RedrawChapterMarkers;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.DurationSeconds))
                RedrawChapterMarkers();
            if (e.PropertyName == nameof(PlayerViewModel.IsLoading))
                OnLoadingChanged();
            // Anything that adds or removes a control in the transport row changes what the
            // track buttons have left to fit into — including the optional pairs, which is why
            // the fit test cannot be a width threshold. QueueBadge and SpeedDisplay are here for
            // the same reason one step down: they resize a button that is already on screen.
            if (e.PropertyName is nameof(PlayerViewModel.HasVideoChoices)
                or nameof(PlayerViewModel.HasAudioChoices)
                or nameof(PlayerViewModel.HasSubtitleChoices)
                or nameof(PlayerViewModel.HasTrackChoices)
                or nameof(PlayerViewModel.HasQueue)
                or nameof(PlayerViewModel.QueueBadge)
                or nameof(PlayerViewModel.HasChapters)
                or nameof(PlayerViewModel.ShowNextPrevButtons)
                or nameof(PlayerViewModel.HasAudioDeviceChoices)
                or nameof(PlayerViewModel.SpeedDisplay))
                RequestTrackButtonUpdate();
            // The flyout's sections are code-owned (no bindings), so a track list that changes
            // while it is up has to be pushed in.
            if (TracksPopup.IsOpen && e.PropertyName is nameof(PlayerViewModel.HasVideoChoices)
                or nameof(PlayerViewModel.HasAudioChoices)
                or nameof(PlayerViewModel.HasSubtitleChoices))
                ApplyTrackSections();
        };

        // Keep the subtitle lift in sync with the bar geometry and playback start
        // (the overlay is shown per playback; controls may already be visible then).
        SizeChanged += (_, _) => { UpdateSubtitlePosition(); UpdateCompactControls(); RequestTrackButtonUpdate(); };
        // The row is the thing the track buttons have to fit into, and it moves for reasons the
        // window size does not cover — UpdateCompactControls' padding switch, a control the view
        // model showed or hid.
        ControlsRow.SizeChanged += (_, _) => RequestTrackButtonUpdate();
        IsVisibleChanged += (_, _) => UpdateSubtitlePosition();
        // Settings live-apply used to write sub-pos itself, which the next bar show/hide
        // then overwrote; it now routes here so this stays the only writer (Phase 10 M4).
        _viewModel.SubtitleBasePositionChanged += UpdateSubtitlePosition;

        // A seek gesture interrupted by Alt+Tab never gets its mouse-up (B8). Losing activation
        // does not necessarily release WPF's mouse capture, so this is a separate trigger from
        // the slider's LostMouseCapture rather than a duplicate of it.
        //
        // An open time editor is abandoned the same way and for the same reason: Alt+Tab away
        // mid-edit and the state that holds the OSD awake would otherwise never be released.
        Deactivated += (_, _) =>
        {
            AbandonSeekGesture("deactivated");
            CancelTimeEdit("deactivated");
        };
    }

    /// <summary>
    /// The ONE writer of mpv's sub-pos. Lifts mpv-rendered subtitles above the control bar while
    /// it is visible so the controls never cover them. The resting value is the user's configured
    /// base position (settings), not a hardcoded 100 — the lift composes: whichever is higher
    /// (smaller sub-pos) wins while the bar shows. Every input funnels through here (SizeChanged,
    /// IsVisibleChanged, SetMiniMode, control show/hide, settings live-apply), so the recompute is
    /// idempotent and carries no ordering assumptions.
    ///
    /// Letterbox-band placement is deliberately NOT computed here (Phase 10 M4). It is entirely
    /// mpv-side, from `sub-use-margins`/`sub-ass-force-margins` set at init: measurement showed
    /// sub-pos has no value that reaches the band (above 100 the text is clipped off the surface
    /// and renders nowhere), and the band anchor does not move with band height, so there is no
    /// band position to compute and nothing to gate on the letterbox geometry. See MpvPlayer.Create.
    /// </summary>
    private void UpdateSubtitlePosition()
    {
        var resting = _viewModel.SubtitleBasePosition;
        if (!_controlsVisible || ActualHeight < 1 || ControlsPanel.ActualHeight < 1)
        {
            _viewModel.SetSubtitlePosition(resting);
            return;
        }
        var barTop = ControlsPanel.ActualHeight + ControlsPanel.Margin.Bottom;
        var percent = (int)(100 * (1 - barTop / ActualHeight));
        _viewModel.SetSubtitlePosition(Math.Min(resting, Math.Clamp(percent, 60, 100)));
    }

    private void OnUpNextCardClick(object sender, MouseButtonEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=up-next-card");
        _viewModel.PlayUpNextNow();
    }

    private void OnUpNextPlay(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=up-next-play");
        _viewModel.PlayUpNextNow();
    }

    private void OnUpNextDismiss(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=up-next-dismiss");
        _viewModel.DismissUpNext();
    }

    private void OnJumpBack(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=jump-back");
        _viewModel.JumpBack();
    }

    private void OnJumpForward(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=jump-forward");
        _viewModel.JumpForward();
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=toggle-mute");
        _viewModel.ToggleMute();
    }

    private void OnPrevChapter(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=previous-chapter");
        _viewModel.PreviousChapter();
    }

    private void OnNextChapter(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=next-chapter");
        _viewModel.NextChapter();
    }

    private void OnMarkerCanvasSize(object sender, SizeChangedEventArgs e) => RedrawChapterMarkers();

    private void RedrawChapterMarkers()
    {
        ChapterMarkerCanvas.Children.Clear();
        var duration = _viewModel.DurationSeconds;
        var width = ChapterMarkerCanvas.ActualWidth;
        if (duration <= 0 || width <= 0)
            return;
        foreach (var start in _viewModel.ChapterStarts)
        {
            var fraction = start / duration;
            if (fraction is < 0.005 or > 0.995)
                continue;
            var tick = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 8,
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x80, 0xDD, 0xF1, 0xFA)),
            };
            System.Windows.Controls.Canvas.SetLeft(tick, fraction * width);
            ChapterMarkerCanvas.Children.Add(tick);
        }
    }

    // Diagnostics: LIGHTWEAVER_UI_LOG=<path> appends overlay input events to a file.
    private static readonly string? UiLogPath = Environment.GetEnvironmentVariable("LIGHTWEAVER_UI_LOG");

    private static void UiLog(string msg)
    {
        if (UiLogPath is null)
            return;
        try
        {
            System.IO.File.AppendAllText(UiLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics only — never disturb playback
        }
    }

    private Point? _lastMousePos;

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        // Only a real change of pointer position counts as user activity. WPF synthesizes a
        // MouseMove whenever hit-testing has to be re-evaluated — including the moment
        // HideControls drops IsHitTestVisible on the faded layer, which otherwise woke the OSD
        // ~1 ms after it faded (measured: ControlsHitTestable=False immediately followed by
        // =True) and made the idle hide effectively impossible to reach.
        var pos = e.GetPosition(this);
        if (_lastMousePos is { } last && Math.Abs(pos.X - last.X) < 0.5 && Math.Abs(pos.Y - last.Y) < 0.5)
            return;
        _lastMousePos = pos;
        ShowControls();
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    /// <summary>Wheel over the player adjusts the volume (Phase 9 M2), in the same ±5 step the
    /// Up/Down shortcuts use so the two agree. The popups are separate HWNDs, so a wheel over
    /// an open flyout scrolls that list and never reaches this handler.
    ///
    /// P10 M10: this used to call <c>ShowControls()</c> so the user could see the volume
    /// slider move. It no longer does — the volume pill carries that job now, and waking the
    /// whole bottom bar, title, scrims and gem pills for a wheel notch is what the user asked
    /// to be rid of. The pill is raised by the view-model's <c>VolumeFeedback</c>, so this
    /// handler only has to move the value.</summary>
    private void OnRootMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        var step = e.Delta > 0 ? 5 : -5;
        _viewModel.Volume = Math.Clamp(_viewModel.Volume + step, 0, 100);
        e.Handled = true;
    }

    /// <summary>Thumb buttons over the video: XButton1 returns to browsing (same as the
    /// overlay Back button); XButton2 is reserved. Suppressed in mini mode (video drag).</summary>
    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_miniMode)
            return;
        if (e.ChangedButton == MouseButton.XButton1)
        {
            Diagnostics.AppLog.Detail("overlay", "event=interaction action=back source=mouse-x1");
            UiLog("XButton1 back");
            BackRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;   // reserved in the player
        }
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        UiLog($"RootMouseDown src={e.OriginalSource.GetType().Name} overPanel={ControlsPanel.IsMouseOver} clicks={e.ClickCount} mini={_miniMode}");
        // Only clicks on the empty video area — not on the controls panel.
        if (ControlsPanel.IsMouseOver)
            return;

        // A press out on the video dismisses an open time editor, and does ONLY that. Two reasons
        // it cannot be left to LostKeyboardFocus: the video area is not focusable, so a click there
        // need not move the keyboard at all, and an editor left open pins the OSD awake. The click
        // is consumed rather than also toggling pause — dismissing a field is the intent, the pause
        // would be an unasked-for side effect (the same call OwnerActivationHook makes for the
        // activating click). This is BELOW the ControlsPanel bail on purpose: a press inside the
        // bar, including one aimed at the field's own caret, must not cancel the edit.
        if (_timeEditActive)
        {
            CancelTimeEdit("video-click");
            return;
        }

        // Mini mode: the chrome-less window moves by dragging the video; pause and
        // fullscreen stay on the slim controls only.
        if (_miniMode)
        {
            Diagnostics.AppLog.Detail("overlay", "event=interaction action=mini-drag");
            MiniDragRequested?.Invoke();
            return;
        }

        // Every press on the video toggles pause, IMMEDIATELY — including the second press of a
        // double-click, which also toggles fullscreen. That second toggle is what makes the gesture
        // whole: WPF raises MouseDown twice for a double-click (ClickCount 1, then 2), so press 1
        // has already paused by the time press 2 arrives, and undoing it returns playback to where
        // the user left it. Two toggles net to zero.
        //
        // This replaces a deferral (2026-08-09). B34's fix waited out the system double-click time
        // before committing the single click, on the reasoning that nothing can know at press 1
        // which gesture is being made. True, but the conclusion does not follow: press 2 can UNDO
        // press 1 instead of press 1 waiting for press 2. Measured cost of the old answer, on a
        // machine with the default 500 ms mouse setting: 554 ms from click to pause, against 21 ms
        // for the same action from the OSD button and 6 ms from the keyboard. No mainstream player
        // defers — YouTube, Netflix and MPC-HC all act on press 1 and let press 2 cancel it, while
        // VLC and mpv leave single-click unbound so the conflict never arises. Chosen by the user.
        //
        // The visible artefact is the one those players have: playback is paused for as long as the
        // gesture takes, typically ~150 ms, and the OSD glyph flickers with it. That is the trade
        // for an instant response, and it is the right way round — the artefact costs a double-click
        // a frame, the deferral cost EVERY single click half a second.
        Diagnostics.AppLog.Detail("overlay", $"event=interaction action=video-click clicks={e.ClickCount}");
        VideoAreaClicked?.Invoke();
        if (e.ClickCount == 2)
        {
            Diagnostics.AppLog.Detail("overlay", "event=interaction action=toggle-fullscreen source=video-double-click");
            UiLog("double click -> fullscreen; press 1's pause toggled back");
            VideoAreaDoubleClicked?.Invoke();
        }
    }

    // ---- Mini-player mode (Phase 5 M9) ----

    private bool _miniMode;

    /// <summary>Grip press: start the native resize instead of the drag-to-move that a
    /// plain video-area press would trigger (Handled stops OnRootMouseDown).</summary>
    private void OnMiniResizeGrip(object sender, MouseButtonEventArgs e)
    {
        if (!_miniMode)
            return;
        UiLog("MiniResizeGrip");
        MiniResizeRequested?.Invoke();
        e.Handled = true;
    }

    /// <summary>Slims the overlay for the mini window: pause + seek + time + restore. The row is
    /// collapsed with SetCurrentValue and re-evaluated with InvalidateProperty on the way back, so
    /// a control whose Visibility comes from XAML or a trigger gets that value back. The four
    /// track buttons are NOT such controls — their Visibility is a code-owned local value since
    /// the split-pill work — so InvalidateProperty would restore the decision made at the row
    /// width BEFORE mini mode; see the re-decide at the end of this method.</summary>
    public void SetMiniMode(bool on)
    {
        Diagnostics.AppLog.Detail("overlay", $"event=state mini={on}");
        _miniMode = on;
        // Mini mode has no time editor (OnTimeTextClick early-returns, like ToggleShortcuts), so a
        // transition must neither carry an open field into a 480x270 window nor leave the hover
        // affordance promising a click that mode will not answer.
        CancelTimeEdit("mini-mode");
        SetTimeAffordance(hovering: false);
        MiniRestoreButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MiniResizeGrip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement child in ControlsRow.Children)
        {
            if (ReferenceEquals(child, PauseButton) || ReferenceEquals(child, TimeText)
                || ReferenceEquals(child, MiniRestoreButton))
                continue;
            if (on)
                child.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            else
                child.InvalidateProperty(VisibilityProperty);
        }
        BackButton.SetCurrentValue(VisibilityProperty, on ? Visibility.Collapsed : Visibility.Visible);
        TitlePanel.SetCurrentValue(VisibilityProperty, on ? Visibility.Collapsed : Visibility.Visible);
        GemPillPanel.SetCurrentValue(VisibilityProperty, on ? Visibility.Collapsed : Visibility.Visible);
        // The volume pill is not collapsed with the rest of the furniture — see
        // UpdateVolumePillScale for why it is the exception.
        UpdateVolumePillScale(on);
        UpdateSubtitlePosition();
        // The four track buttons carry a LOCAL Visibility, which the InvalidateProperty above
        // restores — but it restores whatever was decided at the width the row had BEFORE mini
        // mode, and the window has been resized twice since. Re-decide from the restored row.
        if (!on)
            RequestTrackButtonUpdate();
    }

    // ---- Keyboard-shortcuts panel (P10 M9) ----

    public bool ShortcutsOpen => Shortcuts.IsOpen;

    /// <summary>Opens or closes the player-side panel. Inert in mini mode, like the rest of the
    /// OSD furniture: a 480x270 window cannot show it.</summary>
    public void ToggleShortcuts(Settings.AppSettings settings)
    {
        if (_miniMode)
            return;
        if (Shortcuts.IsOpen)
            Shortcuts.Hide();
        else
            Shortcuts.Show(settings);
    }

    public void HideShortcuts() => Shortcuts.Hide();

    private void OnShortcutsButton(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=toggle-shortcuts");
        UiLog("ShortcutsClick");
        ShortcutsRequested?.Invoke();
    }

    /// <summary>Raised by the OSD button and by '?' typed while this window holds focus. The
    /// owner routes it, so the button and the key land on the same code path.</summary>
    public event Action? ShortcutsRequested;

    private void OnFullscreenButton(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=toggle-fullscreen source=button");
        UiLog("FullscreenClick");
        FullscreenRequested?.Invoke();
    }

    /// <summary>Raised by the OSD fullscreen button. Routed by the owner to the same
    /// <c>ToggleFullscreen</c> that F11 and the video double-click already call, so all three
    /// share one implementation and one notion of the current state.</summary>
    public event Action? FullscreenRequested;

    /// <summary>Pushed by the owner after every fullscreen change — the overlay cannot observe the
    /// owner's WindowState, and a button whose glyph is inferred locally drifts out of step the
    /// first time fullscreen is toggled by a route the button did not initiate (F11, the
    /// double-click, or Escape).</summary>
    public void SetFullscreen(bool on)
    {
        FullscreenButton.Content = (string)FindResource(on ? "IconFullscreenExit" : "IconFullscreen");
        FullscreenButton.ToolTip = on ? "Leave fullscreen (F11)" : "Fullscreen (F11)";
        System.Windows.Automation.AutomationProperties.SetName(
            FullscreenButton, on ? "Leave fullscreen" : "Fullscreen");
    }

    private void OnMiniPlayer(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=mini-enter");
        UiLog("MiniPlayerClick");
        MiniPlayerRequested?.Invoke();
    }

    private void OnMiniRestore(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=mini-restore");
        UiLog("MiniRestoreClick");
        MiniRestoreRequested?.Invoke();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=back source=button");
        UiLog("BackClick");
        BackRequested?.Invoke();
    }

    private void OnSkipSegment(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=skip-segment");
        UiLog("SkipSegmentClick");
        _viewModel.SkipActiveSegment();
    }

    // Live stats refresh only while the info panel is open.
    private DispatcherTimer? _infoTimer;

    private void OnInfoButton(object sender, RoutedEventArgs e)
        => InfoPopup.IsOpen = !InfoPopup.IsOpen;

    private void OnInfoPopupOpened(object sender, EventArgs e)
    {
        _viewModel.RefreshLiveStats();
        UiLog($"InfoPopupOpened rows={_viewModel.LiveStats.Count}");
        if (_infoTimer is null)
        {
            _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _infoTimer.Tick += (_, _) => _viewModel.RefreshLiveStats();
        }
        _infoTimer.Start();
    }

    private void OnInfoPopupClosed(object sender, EventArgs e) => _infoTimer?.Stop();

    private void OnSpeedButton(object sender, RoutedEventArgs e)
        => SpeedPopup.IsOpen = !SpeedPopup.IsOpen;

    private void OnSpeedPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SpeedList.SelectedItem is SpeedOption option)
        {
            Diagnostics.AppLog.Detail("overlay", FormattableString.Invariant(
                $"event=interaction action=set-speed value={option.Value}"));
            UiLog(FormattableString.Invariant($"SpeedPicked {option.Value}"));
            SpeedList.SelectedItem = null;
            SpeedPopup.IsOpen = false;
            _viewModel.SelectSpeed(option);
        }
    }

    // M14/M17: the OSD pair follows the transport mapping (queue step with the Up Next
    // fallback, chapter step, or seek); N/P keys keep the plain queue-only navigation.
    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=previous");
        UiLog("PrevClick");
        _viewModel.GoPrevious();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", "event=interaction action=next");
        UiLog("NextClick");
        _viewModel.GoNext();
    }

    private void OnQueueButton(object sender, RoutedEventArgs e)
        => QueuePopup.IsOpen = !QueuePopup.IsOpen;

    private void OnQueueRowPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueRow row)
        {
            Diagnostics.AppLog.Detail("overlay", $"event=interaction action=queue-jump index={row.Index}");
            UiLog($"QueueRowPicked index={row.Index}");
            QueueList.SelectedItem = null;
            QueuePopup.IsOpen = false;
            _viewModel.JumpToQueueRow(row);
        }
    }

    private void OnQueueRowRemove(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: QueueRow row })
        {
            Diagnostics.AppLog.Detail("overlay", $"event=interaction action=queue-remove index={row.Index}");
            UiLog($"QueueRowRemove index={row.Index}");
            _viewModel.RemoveQueueRow(row);
        }
    }

    // ---- Track pickers: one popup, four buttons (M2) ----

    /// <summary>Which track types a click on a track button asks the flyout for. The merged
    /// pill asks for all three; each per-type pill asks for its own.</summary>
    [Flags]
    private enum TrackKinds
    {
        None = 0,
        Video = 1,
        Audio = 2,
        Subtitles = 4,
        All = Video | Audio | Subtitles,
    }

    private TrackKinds _tracksMode = TrackKinds.All;

    private void OnTracksButton(object sender, RoutedEventArgs e)
        => OpenTracks(TrackKinds.All, TracksButton);

    private void OnVideoTracksButton(object sender, RoutedEventArgs e)
        => OpenTracks(TrackKinds.Video, VideoTracksButton);

    private void OnAudioTracksButton(object sender, RoutedEventArgs e)
        => OpenTracks(TrackKinds.Audio, AudioTracksButton);

    private void OnSubtitleTracksButton(object sender, RoutedEventArgs e)
        => OpenTracks(TrackKinds.Subtitles, SubtitleTracksButton);

    /// <summary>
    /// Shows the ONE track flyout under <paramref name="placementTarget"/> with only the
    /// sections <paramref name="mode"/> asks for. Clicking the button the flyout is already
    /// showing closes it (the toggle every OSD flyout has); clicking a DIFFERENT track button
    /// switches sections and leaves it open, which is what the user means by that click.
    /// </summary>
    private void OpenTracks(TrackKinds mode, UIElement placementTarget)
    {
        var switching = TracksPopup.IsOpen && _tracksMode != mode;
        TracksPopup.PlacementTarget = placementTarget;
        _tracksMode = mode;
        ApplyTrackSections();
        if (switching)
        {
            // A Popup re-runs its placement when its CHILD's size changes, not when its
            // PlacementTarget does, so switching pills under an open flyout swapped the sections
            // and left the flyout hanging off the pill that opened it. Closing and reopening is
            // a fresh placement pass; it is one message-pump-free pair of writes, so nothing is
            // rendered in between.
            TracksPopup.IsOpen = false;
            TracksPopup.IsOpen = true;
            return;
        }
        TracksPopup.IsOpen = !TracksPopup.IsOpen;
    }

    /// <summary>Sections are shown only when the mode asks for them AND the type has something
    /// to choose — so the per-type pills can never open an empty flyout, and the merged pill
    /// still shows exactly the types that have a choice.</summary>
    private void ApplyTrackSections()
    {
        VideoSection.Visibility = _tracksMode.HasFlag(TrackKinds.Video) && _viewModel.HasVideoChoices
            ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = _tracksMode.HasFlag(TrackKinds.Audio) && _viewModel.HasAudioChoices
            ? Visibility.Visible : Visibility.Collapsed;
        SubsSection.Visibility = _tracksMode.HasFlag(TrackKinds.Subtitles) && _viewModel.HasSubtitleChoices
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Wheel over the flyout's content. The three lists have their own scrollers
    /// disabled, so the outer one is the only thing that can scroll — and it only sees the
    /// wheel if nothing inside eats it first. Preview, so this beats the lists.</summary>
    private void OnTracksWheel(object sender, MouseWheelEventArgs e)
        => WheelScroll.ForwardTo(TracksScroller, e);

    /// <summary>true = the Video/Audio/Subs trio is up, false = the merged Tracks pill.
    /// Null until the first measured decision, so the first one is logged.</summary>
    private bool? _trackSplit;

    private bool _trackButtonsPending;

    private double _videoPillWidth;
    private double _audioPillWidth;
    private double _subsPillWidth;

    /// <summary>Queues a track-button re-fit at Loaded priority — below layout, so every
    /// ActualWidth the fit test reads is the one the user is looking at. Repeated requests
    /// inside one layout pass coalesce into a single evaluation.</summary>
    private void RequestTrackButtonUpdate()
    {
        if (_trackButtonsPending)
            return;
        _trackButtonsPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _trackButtonsPending = false;
            UpdateTrackButtons();
        }));
    }

    /// <summary>
    /// Chooses between the Video/Audio/Subs trio and the merged Tracks pill by MEASURING the
    /// row rather than by a width threshold. A threshold would have to be re-guessed for every
    /// combination of optional controls (queue pair, chapter pair, next/prev pair, audio-device
    /// button) and would be wrong for most of them.
    ///
    /// The width it sums EXCLUDES all four track buttons and adds back only the pills that
    /// would be on screen in split mode. That is what makes the decision state-independent:
    /// a sum that counted the current buttons would answer a different question in each of the
    /// two states and the pair would oscillate at the boundary.
    /// </summary>
    private void UpdateTrackButtons()
    {
        // Mini mode collapses the whole row by hand; SetMiniMode(false) calls back in.
        if (_miniMode || ControlsRow.ActualWidth <= 0)
            return;

        double used = 0;
        foreach (UIElement child in ControlsRow.Children)
        {
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible
                || IsTrackButton(element))
                continue;
            used += element.ActualWidth + element.Margin.Left + element.Margin.Right;
        }

        var need = used + 6;   // slack, so a hairline fit does not flip on a rounding difference
        if (_viewModel.HasVideoChoices)
            need += PillWidth(VideoTracksButton, ref _videoPillWidth);
        if (_viewModel.HasAudioChoices)
            need += PillWidth(AudioTracksButton, ref _audioPillWidth);
        if (_viewModel.HasSubtitleChoices)
            need += PillWidth(SubtitleTracksButton, ref _subsPillWidth);

        var split = _viewModel.HasTrackChoices && need <= ControlsRow.ActualWidth;
        if (_trackSplit != split)
        {
            _trackSplit = split;
            Diagnostics.AppLog.Detail("overlay", FormattableString.Invariant(
                $"event=state track-buttons split={split} used={used:F0} row={ControlsRow.ActualWidth:F0}"));
            // choices is appended LAST: the suites match on split=(True|False) and read the
            // fields by name, so a new field at the end cannot disturb them. It answers the one
            // question the other three cannot — "no buttons at all" and "no room for the trio"
            // are both split=False, and only this tells them apart.
            UiLog(FormattableString.Invariant(
                $"TrackButtons split={split} used={used:F0} row={ControlsRow.ActualWidth:F0} choices={_viewModel.HasTrackChoices}"));
            // The flyout is anchored to a pill that is about to collapse. A Popup does not follow
            // a PlacementTarget that leaves the screen, so it would sit over the wrong control
            // (and the merged-pill flyout would offer sections its new owner never asked for).
            if (TracksPopup.IsOpen)
                TracksPopup.IsOpen = false;
        }

        VideoTracksButton.Visibility = split && _viewModel.HasVideoChoices
            ? Visibility.Visible : Visibility.Collapsed;
        AudioTracksButton.Visibility = split && _viewModel.HasAudioChoices
            ? Visibility.Visible : Visibility.Collapsed;
        SubtitleTracksButton.Visibility = split && _viewModel.HasSubtitleChoices
            ? Visibility.Visible : Visibility.Collapsed;
        TracksButton.Visibility = !split && _viewModel.HasTrackChoices
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsTrackButton(FrameworkElement element)
        => ReferenceEquals(element, TracksButton) || ReferenceEquals(element, VideoTracksButton)
           || ReferenceEquals(element, AudioTracksButton) || ReferenceEquals(element, SubtitleTracksButton);

    /// <summary>Width a pill WOULD take, margins included. A collapsed element measures to
    /// zero by definition, so it is shown for the length of this call — no render pass can
    /// run in between — and the answer is cached: the content and the style are fixed.</summary>
    private static double PillWidth(FrameworkElement pill, ref double cached)
    {
        if (cached > 0)
            return cached;
        var was = pill.Visibility;
        pill.Visibility = Visibility.Visible;
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = pill.DesiredSize.Width;   // DesiredSize already carries the margins
        pill.Visibility = was;
        if (width > 0)
            cached = width;
        return width;
    }

    private bool? _compact;

    /// <summary>
    /// Reclaims horizontal room in the transport row at narrow widths (P10 M11). Merging the
    /// three track pills into one Tracks button was necessary but NOT sufficient — measured at
    /// the 700x480 window minimum the row was still ~100 physical px over budget, so the
    /// DockPanel squeezed the new button to zero width exactly as it had squeezed the old
    /// three, and the speed pill still slid 10 px under the info button.
    ///
    /// Both levers give space back without hiding or relocating anything: the bar's generous
    /// 40 DIP side padding is a large-window luxury, and the volume slider only needs enough
    /// travel to be draggable. Measured after: the whole row fits at the minimum with slack.
    /// </summary>
    private void UpdateCompactControls()
    {
        // The threshold is the width at which the full-padding row stops fitting, with margin.
        var compact = ActualWidth > 0 && ActualWidth < 900;
        if (_compact == compact)
            return;
        _compact = compact;
        ControlsPanel.Padding = compact
            ? new Thickness(16, 40, 16, 24)
            : new Thickness(40, 40, 40, 24);
        VolumeSlider.Width = compact ? 56 : 96;
    }

    private void OnAudioDeviceButton(object sender, RoutedEventArgs e)
    {
        // Devices can appear/disappear mid-session — rebuild on open.
        if (!AudioDevicePopup.IsOpen)
            _viewModel.RefreshAudioDevices();
        AudioDevicePopup.IsOpen = !AudioDevicePopup.IsOpen;
    }

    private void OnAudioDevicePicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AudioDeviceList.SelectedItem is AudioDeviceOption option)
        {
            Diagnostics.AppLog.Detail("overlay",
                $"event=interaction action=set-audio-device value={(option.Name == "auto" ? "default" : "custom")}");
            UiLog($"AudioDevicePicked {(option.Name == "auto" ? "default" : "custom")}");
            AudioDeviceList.SelectedItem = null;
            AudioDevicePopup.IsOpen = false;
            _viewModel.SelectAudioDevice(option);
        }
    }

    private void OnVideoTrackPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VideoList.SelectedItem is TrackOption option)
        {
            Diagnostics.AppLog.Detail("overlay", $"event=interaction action=set-video-track id={option.Id}");
            UiLog($"VideoTrackPicked id={option.Id}");
            VideoList.SelectedItem = null;
            TracksPopup.IsOpen = false;
            _viewModel.SelectVideo(option);
        }
    }

    private void OnAudioTrackPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AudioList.SelectedItem is TrackOption option)
        {
            Diagnostics.AppLog.Detail("overlay", $"event=interaction action=set-audio-track id={option.Id}");
            UiLog($"AudioTrackPicked id={option.Id}");
            AudioList.SelectedItem = null;
            TracksPopup.IsOpen = false;
            _viewModel.SelectAudio(option);
        }
    }

    private void OnSubtitleTrackPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SubsList.SelectedItem is TrackOption option)
        {
            Diagnostics.AppLog.Detail("overlay", $"event=interaction action=set-subtitle-track id={option.Id}");
            UiLog($"SubtitleTrackPicked id={option.Id}");
            SubsList.SelectedItem = null;
            TracksPopup.IsOpen = false;
            _viewModel.SelectSubtitle(option);
        }
    }

    /// <summary>
    /// Puts this window back above its owner. A hidden-then-reshown owned window can
    /// land BELOW the owner (and its video child HWND), silently swallowing all mouse
    /// input — found live when Playing-state overlay clicks hit the video window.
    /// </summary>
    public void EnsureAboveOwner()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Adds <c>WS_EX_NOACTIVATE</c> — half of the Phase 9 M1 fix for flaky player shortcuts.
    ///
    /// The shortcuts all live in <c>MainWindow</c>; this window had no key handling of its own.
    /// <c>ShowActivated="False"</c> only governs <c>Show()</c>, so any *click* on the overlay —
    /// click-to-pause, the seek slider, any OSD button — moved keyboard focus here and silently
    /// killed every shortcut until the user re-activated the main window by other means. That
    /// is the "sometimes works, sometimes not" the user reported.
    ///
    /// NOACTIVATE stops the <c>WM_MOUSEACTIVATE</c> path, but it is **not sufficient on its
    /// own**: WPF focuses the clicked element, and that <c>SetFocus</c> makes this HWND the
    /// focus window anyway. Measured before the second half landed — after a video click, Up
    /// moved the volume 50 → 50.1, i.e. the arrow key was consumed by the focused Slider as its
    /// own SmallChange nudge instead of reaching the player. So the keystroke path is fixed by
    /// forwarding <c>PreviewKeyDown</c> to the owner (see the constructor); this ex-style still
    /// earns its place by keeping activation churn and title-bar flicker down.
    ///
    /// Mini-mode drag/resize are unaffected: those forward <c>WM_NCLBUTTONDOWN</c> to the
    /// *owner*. Consistent with <see cref="EnsureAboveOwner"/>, which already re-Z-orders with
    /// <c>SWP_NOACTIVATE</c>.
    ///
    /// The ex-style has one consequence this comment used to miss, and it was a user-visible bug
    /// for as long as it stood: see <see cref="OwnerActivationHook"/>.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
            return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(OwnerActivationHook);
        source?.AddHook(ExternalMoveHook);
    }

    /// <summary>Where the owner's last <see cref="SetBoundsFromOwner"/> put this window, in
    /// PHYSICAL pixels. Null until the first sync, which is also the only state in which
    /// <see cref="ExternalMoveHook"/> cannot tell our own moves from anyone else's.</summary>
    private (int X, int Y)? _expectedOrigin;

    /// <summary>Set across the four property writes in <see cref="SetBoundsFromOwner"/>. WPF
    /// applies <c>Left</c> and <c>Top</c> as two separate <c>SetWindowPos</c> calls, so the
    /// first of them lands at (new left, OLD top) — an origin that matches no expectation and
    /// would otherwise read as an external move and drag the owner in a loop.</summary>
    private bool _settingBounds;

    /// <summary>
    /// Places this window on the owner's video rect (DIUs, as the owner computes them) and
    /// records the resulting origin in physical pixels for <see cref="ExternalMoveHook"/>.
    /// <para>The skip when nothing moved is not an optimisation only: this runs per
    /// <c>WM_WINDOWPOSCHANGED</c>, i.e. per step of a window drag, and every write here is a
    /// <c>SetWindowPos</c> on this HWND (BUGS.md B11). The expectation is refreshed even when
    /// the writes are skipped, so it can never go stale.</para>
    /// <para>The expectation that MATTERS is the one read back from <c>GetWindowRect</c> — see
    /// <see cref="RecordExpectedOrigin"/>. The scaled value written before the writes is an
    /// interim only, for the window of time in which the real origin does not exist yet.</para>
    /// </summary>
    public void SetBoundsFromOwner(double left, double top, double width, double height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Math.Abs(Left - left) <= 0.05 && Math.Abs(Top - top) <= 0.05
            && Math.Abs(Width - width) <= 0.05 && Math.Abs(Height - height) <= 0.05)
        {
            RecordExpectedOrigin(hwnd);
            return;
        }
        if (hwnd != nint.Zero)
        {
            // Interim, recorded BEFORE the writes: setting Left is a synchronous SetWindowPos,
            // so the hook runs inside them. Only while the HWND exists — before the first Show()
            // there is no window to compare against and no hook attached, and leaving the
            // expectation null is exactly the hook's "no expectation yet" case.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            _expectedOrigin = ((int)Math.Round(left * dpi.DpiScaleX), (int)Math.Round(top * dpi.DpiScaleY));
        }
        _settingBounds = true;
        try
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            RecordExpectedOrigin(hwnd);
        }
        finally
        {
            _settingBounds = false;
        }
    }

    /// <summary>
    /// Records where this window ACTUALLY is, in physical pixels, as the expectation
    /// <see cref="ExternalMoveHook"/> compares a proposed origin against. No-op before the HWND
    /// exists, which leaves <see cref="_expectedOrigin"/> null and the hook on its "no
    /// expectation yet" path.
    ///
    /// <para>Read back rather than scaled from DIUs on purpose. <c>GetDpi</c> on a window with
    /// no HWND answers with the PRIMARY monitor's scale, and even afterwards the arithmetic is
    /// only right for the monitor this window is on — on a desktop that mixes 150% and 100% the
    /// scaled value can miss the real origin by more than the hook's 1 px tolerance, and a sync
    /// of our own then reads as somebody else's move.</para>
    /// </summary>
    private void RecordExpectedOrigin(nint hwnd)
    {
        if (hwnd != nint.Zero && GetWindowRect(hwnd, out var rect))
            _expectedOrigin = (rect.Left, rect.Top);
    }

    /// <summary>
    /// Turns an EXTERNAL move of the overlay into a move of the owner, and refuses it otherwise.
    ///
    /// <para>The overlay covers the whole video, so it is routinely the foreground window after
    /// any click on it (<see cref="OwnerActivationHook"/> detail 1 measures exactly that). A
    /// shell gesture aimed at "the current window" — <c>Win+Shift+Arrow</c> to the next monitor,
    /// <c>Win+Left/Right</c> to snap — therefore issues its <c>SetWindowPos</c> against THIS
    /// hwnd, and nothing observed the overlay's own position: the OSD tore off the video and the
    /// main window stayed where it was. This is the mirror image of BUGS.md B11, which was the
    /// owner moving and the overlay not following.</para>
    ///
    /// <para>What is filtered out before a move counts as external, in order: a
    /// <c>SWP_NOMOVE</c> call (z-order and size-only work, including
    /// <see cref="EnsureAboveOwner"/>); no expectation recorded yet; an origin within 1 px of
    /// what <see cref="SetBoundsFromOwner"/> asked for (our own sync); <c>SWP_STATECHANGED</c>
    /// or the <c>-32000</c> minimize sentinel (a state change, not a move); and a proposed rect
    /// whose centre is on no monitor at all.</para>
    ///
    /// <para>The move is then VETOED — <c>SWP_NOMOVE | SWP_NOSIZE</c> written back into the
    /// <c>WINDOWPOS</c>, the same trick the owner's mini-player clamp uses. The overlay must not
    /// move on its own: either the owner moves and the resulting sync brings the overlay to the
    /// proposed origin exactly, or the owner refuses and the OSD stays on the video.</para>
    /// </summary>
    private nint ExternalMoveHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING || _settingBounds || _expectedOrigin is not { } expected)
            return nint.Zero;
        var wp = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((wp.Flags & SWP_NOMOVE) != 0 || (wp.Flags & SWP_STATECHANGED) != 0 || wp.X <= MinimizedX)
            return nint.Zero;
        if (Math.Abs(wp.X - expected.X) <= 1 && Math.Abs(wp.Y - expected.Y) <= 1)
            return nint.Zero;

        var width = wp.Cx;
        var height = wp.Cy;
        if ((wp.Flags & SWP_NOSIZE) != 0 && GetWindowRect(hwnd, out var current))
        {
            width = current.Right - current.Left;
            height = current.Bottom - current.Top;
        }
        // DEFAULTTONULL, not DEFAULTTONEAREST: a proposal whose centre is on no monitor is not a
        // move anyone asked for. Where it IS on a monitor the two flags return the same handle,
        // so this one call answers both questions.
        var monitor = MonitorFromPoint(
            new Win32Point { X = wp.X + (width / 2), Y = wp.Y + (height / 2) }, MONITOR_DEFAULTTONULL);
        if (monitor == nint.Zero)
            return nint.Zero;

        var move = new OverlayExternalMove(wp.X, wp.Y, monitor);
        ExternalMoveRequested?.Invoke(move);
        wp.Flags |= SWP_NOMOVE | SWP_NOSIZE;
        Marshal.StructureToPtr(wp, lParam, false);
        UiLog($"OverlayExternalMove to={move.X},{move.Y} owner={move.OwnerState} action={move.Action}");
        return nint.Zero;
    }

    /// <summary>
    /// Makes a click on the video bring the app forward, which <c>WS_EX_NOACTIVATE</c> above
    /// otherwise makes impossible.
    ///
    /// The overlay covers the WHOLE video area, so while the player runs it is the window the
    /// user's mouse actually lands on. A <c>NOACTIVATE</c> window answers
    /// <c>WM_MOUSEACTIVATE</c> with <c>MA_NOACTIVATE</c> and does not pass activation to its
    /// owner, so clicking the app did nothing at all: the previous app kept the foreground and
    /// the keystrokes. Alt+Tab worked throughout, because it activates MainWindow directly and
    /// never consults the overlay — which is exactly how the user described it ("doesn't regain
    /// focus when I click with the mouse; when I ALT+Tab, it works").
    ///
    /// This is NOT the mechanism proposed on 2026-08-05 — that one had Windows redirecting owner
    /// activation to the last active popup, and <c>GetLastActivePopup</c> disproved it by
    /// returning MAIN. That retraction stands; this is a different route to the same symptom.
    ///
    /// Activating the OWNER rather than this window is what keeps Phase 9 M1 intact. The bug that
    /// ex-style was added for was keyboard focus landing HERE; sending activation to MainWindow is
    /// the same direction that fix wanted, not a reversal of it.
    ///
    /// Two details carry the correctness:
    /// 1. The guard is not optional, and it tests the foreground window's PROCESS, not its handle.
    ///    <c>WM_MOUSEACTIVATE</c> arrives on EVERY video click, because this owned popup is never
    ///    itself the active window even while the app is frontmost — so without a guard every
    ///    click-to-pause is eaten and the pause toggle appears dead. Comparing against the owner's
    ///    handle is not enough either: any other window of ours that holds the foreground (this
    ///    overlay, an OSD flyout, a settings popup) is not the owner, so a handle comparison ate
    ///    those clicks too and yanked focus to MainWindow. Measured — the click-to-pause leg of
    ///    test-focus-on-click failed exactly that way on the handle version while the
    ///    click-to-activate legs both still passed. Anything of ours in front means "already ours".
    ///    That this window itself is a routine foreground holder is not hypothetical:
    ///    test-shortcut-focus reports <c>foreground = 'overlay'</c> after a video click, after an
    ///    OSD button click, and after a seek-bar drag — all three of its interaction paths.
    /// 2. The activating click is PASSED THROUGH (<c>MA_NOACTIVATE</c>), so one click both brings
    ///    the app forward and does what it was aimed at.
    ///
    ///    <para>This was the other way round until 2026-08-09. The click used to be eaten
    ///    (<c>MA_NOACTIVATEANDEAT</c>) on the argument that click-to-focus is the intent and the
    ///    pause an unasked-for side effect. The user reported the result as a bug and stated the
    ///    behaviour they want: click on LightWeaver while it is unfocused and the video pauses, in
    ///    ONE click, not two. So the argument was ours rather than theirs, and it lost.</para>
    ///
    ///    <para>The negative control that originally justified eating does NOT justify it here, and
    ///    the difference is the whole point. That control ran with the hook OFF: the click paused
    ///    the video while the app stayed in the BACKGROUND — side effect performed, focus not
    ///    gained. This path still calls <c>SetForegroundWindow(owner)</c> first and only then lets
    ///    the click through, so the app comes forward AND acts. Both, which is what was wanted.</para>
    ///
    /// What passing the click through changes — all consequences of the same one-click rule, and
    /// none of them separable, because <c>WM_MOUSEACTIVATE</c> carries no notion of WHERE the click
    /// landed. This is a per-window decision, not a per-control one:
    /// * A click on an OSD button (pause, seek, mute, fullscreen) now acts on the first press
    ///   instead of only focusing.
    /// * A double-click on unfocused video now reaches fullscreen. It used to pause instead: the
    ///   eaten first press reset WPF's click count, so the second arrived as a fresh single click.
    ///   That was listed here as an accepted cost and is simply gone.
    /// * Dragging the mini window from an unfocused state works on the first press —
    ///   <c>MiniDragRequested</c> fires from <c>OnRootMouseDown</c>, which the eaten down skipped.
    /// * XButton1-back likewise acts while unfocused.
    /// </summary>
    private nint OwnerActivationHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE)
            return nint.Zero;

        var owner = Owner is null ? nint.Zero : new WindowInteropHelper(Owner).Handle;
        if (owner == nint.Zero || ForegroundIsOurs())
            return nint.Zero;   // already ours — let the click reach its handler unchanged

        // Log the RESULT, not the attempt. Windows can refuse a foreground change while another
        // process holds the lock (a menu or a drag), and ok=False then means the click acted on a
        // player that stayed in the background. That is the residual case the old eat-the-click
        // behaviour traded everything else to avoid; it is rare, it is now the lesser cost, and a
        // field report of it should be readable from the UI log rather than guessed at.
        var ok = SetForegroundWindow(owner);
        UiLog($"MouseActivate: foreground is another app -> activated owner={ok}, click passed through");
        handled = true;
        return MA_NOACTIVATE;
    }

    /// <summary>Does any window of this process hold the foreground? See
    /// <see cref="OwnerActivationHook"/> detail 1 for why the question is per-process and not
    /// per-window.</summary>
    private static bool ForegroundIsOurs()
    {
        var fg = GetForegroundWindow();
        if (fg == nint.Zero)
            return false;
        _ = GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == Environment.ProcessId;
    }

    /// <summary>Every flyout in the OSD. One list, because the key handler and the idle-hide
    /// both need it and a second hand-written list is how AudioDevicePopup came to be missing
    /// from the idle-hide check (Phase 9 M3).</summary>
    private IEnumerable<System.Windows.Controls.Primitives.Popup> Flyouts
        => [TracksPopup, InfoPopup, QueuePopup, SpeedPopup, AudioDevicePopup];

    private bool AnyFlyoutOpen => Flyouts.Any(p => p.IsOpen);

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        // The time editor owns the keyboard while it is up, and it is checked FIRST because focus
        // decides ownership: Enter commits, Escape cancels, and NOTHING else is forwarded. The M1
        // forward deliberately beats the focused element, so without this the arrows would seek and
        // Space would pause while the user typed a timestamp. Escape is marked handled so it cannot
        // also bubble to the owner and leave fullscreen on the way out.
        //
        // Scoped to this ONE field, deliberately. The broad shape - "any focused TextBoxBase", which
        // is right for MainWindow.IsTextEntryFocused because the browse views really do have several
        // - would silently kill every player shortcut the day any other overlay text control took
        // focus. Both halves earn their place: the flag so Escape still reaches the editor even if
        // the field never managed to take focus, the focus test so a keystroke aimed at the field is
        // never mistaken for a shortcut. Neither can outlive the edit — EndTimeEdit clears the flag
        // AND hands the keyboard back to this window, so a closed editor suppresses nothing.
        if (_timeEditActive || TimeEditBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter)
            {
                CommitTimeEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTimeEdit("escape");
                e.Handled = true;
            }
            return;
        }
        // With the shortcuts panel up, the keys belong to the panel: Escape closes it and
        // nothing else reaches playback. Same rule as an open flyout, one line below.
        if (Shortcuts.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                Shortcuts.Hide();
                e.Handled = true;
            }
            return;
        }
        // The M1 forward deliberately beats the focused element (so a focused Slider can't eat
        // the arrow keys as its own SmallChange nudge), but with a flyout open that was plain
        // wrong: measured with the audio flyout up, Down moved the VOLUME 100 -> 95 while the
        // list selection stayed put, and Space paused playback (BUGS.md B7). Mouse and UIA both
        // work either way, which is why no suite caught it. While a flyout is open, leave the
        // keys alone — not marked handled, so the focused element still gets its chance.
        if (AnyFlyoutOpen)
        {
            // Escape is the one key worth acting on: it closes the flyout, which otherwise
            // needs a click-away (a Popup does not close on Escape by itself).
            if (e.Key == Key.Escape)
            {
                foreach (var flyout in Flyouts)
                    flyout.IsOpen = false;
                e.Handled = true;
            }
            return;
        }
        Diagnostics.AppLog.Detail("input",
            $"event=bridge source=overlay key={e.Key} modifiers={FormatModifiers(Keyboard.Modifiers)}");
        KeyPressed?.Invoke(e);
    }

    private static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("win");
        return parts.Count == 0 ? "none" : string.Join('+', parts);
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Undocumented but universal: the window's show state is changing (minimize,
    /// restore, maximize). The origin that rides along is the shell's, not a user move.</summary>
    private const uint SWP_STATECHANGED = 0x8000;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>A minimizing window is parked far off-screen (-32000). Anything at or left of
    /// that is the shell hiding a window, not a move to act on.</summary>
    private const int MinimizedX = -32000;

    private const uint MONITOR_DEFAULTTONULL = 0;

    /// <summary>Do not activate THIS window, but let the click through to the normal
    /// mouse-message path. Activation still happens — we send it to the owner ourselves first.
    /// <para>Not <c>MA_NOACTIVATEANDEAT</c> (4), which additionally discards the click: that is
    /// what made the first click on an unfocused player do nothing but focus it. See
    /// <see cref="OwnerActivationHook"/>.</para></summary>
    private const int MA_NOACTIVATE = 3;

    private static readonly nint HWND_TOP = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    // SetWindowLongPtr is the 64-bit-correct entry point, but GWL_EXSTYLE fits an int on
    // every architecture and the -W suffix keeps the marshalling explicit.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Win32Point point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }

    /// <summary>The <c>WINDOWPOS</c> a <c>WM_WINDOWPOSCHANGING</c> carries — read, amended and
    /// written back by <see cref="ExternalMoveHook"/>, exactly as the owner's mini-player clamp
    /// does with its own copy.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Hwnd, HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    private void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Diagnostics.AppLog.Detail("overlay", "event=interaction action=open-dropped-file");
            FileDropped?.Invoke(files[0]);
        }
    }

    private bool _thumbDragActive;

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Slider's own class handler (IsMoveToPointEnabled) has already moved the
        // value to the click point. Freeze position feedback NOW — otherwise mpv's
        // next time-pos update overwrites the clicked value before mouse-up commits
        // it, and the click randomly seeks to where playback already is.
        UiLog(FormattableString.Invariant($"SeekMouseDown value={SeekSlider.Value:F1}"));
        _viewModel.IsSeeking = true;
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        UiLog("SeekDragStarted");
        _thumbDragActive = true;
        _viewModel.IsSeeking = true;
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        Diagnostics.AppLog.Detail("overlay", FormattableString.Invariant(
            $"event=seek outcome=commit source=drag target_seconds={SeekSlider.Value:F1}"));
        UiLog(FormattableString.Invariant($"SeekDragCompleted value={SeekSlider.Value:F1}"));
        _thumbDragActive = false;
        _viewModel.IsSeeking = false;
        _viewModel.Seek(SeekSlider.Value);
    }

    private void OnSeekLostCapture(object sender, MouseEventArgs e) => AbandonSeekGesture("lost-capture");

    /// <summary>The gesture ended abnormally — capture lost, or the window deactivated under it
    /// (Alt+Tab, a window-management gesture, anything that takes over the input loop). Without
    /// this `IsSeeking` stayed true forever: position feedback is suppressed while it is set, so
    /// the seek bar and the time display froze until the slider was clicked again (BUGS.md B8).
    /// Two triggers because they are not the same event — a cancelled mode releases capture,
    /// while losing activation need not — and only the flag is released: an interrupted gesture
    /// is deliberately not committed as a seek.</summary>
    private void AbandonSeekGesture(string cause)
    {
        if (!_viewModel.IsSeeking && !_thumbDragActive)
            return;
        Diagnostics.AppLog.Detail("overlay",
            $"event=seek outcome=abandon source=slider cause={cause}");
        UiLog(FormattableString.Invariant(
            $"SeekAbandoned cause={cause} value={SeekSlider.Value:F1} thumbDrag={_thumbDragActive}"));
        _thumbDragActive = false;
        _viewModel.IsSeeking = false;
    }

    // ---- Seek preview (trickplay thumb / time tooltip) ----

    private int _previewThumbIndex = -1;
    private int _previewGeneration;

    private void OnSeekSliderMouseMove(object sender, MouseEventArgs e)
        => UpdateSeekPreview(e.GetPosition(SeekSlider).X);

    private void OnSeekSliderMouseLeave(object sender, MouseEventArgs e)
    {
        SeekPreviewPopup.IsOpen = false;
        _previewThumbIndex = -1;
        _previewGeneration++;   // orphan any in-flight fetch
    }

    private async void UpdateSeekPreview(double x)
    {
        var duration = _viewModel.DurationSeconds;
        if (duration <= 0 || SeekSlider.ActualWidth < 1)
        {
            SeekPreviewPopup.IsOpen = false;
            return;
        }
        var seconds = Math.Clamp(x / SeekSlider.ActualWidth, 0, 1) * duration;
        SeekPreviewTime.Text = FormatPreviewTime(seconds);

        var trickplay = _viewModel.Trickplay;
        if (trickplay is not null)
        {
            SeekPreviewImage.Width = trickplay.ThumbWidth;
            SeekPreviewImage.Height = trickplay.ThumbHeight;
            SeekPreviewImage.Visibility = Visibility.Visible;
        }
        else
        {
            SeekPreviewImage.Visibility = Visibility.Collapsed;
        }

        SeekPreviewPopup.IsOpen = true;
        // Measured size is 0 until the first open has laid out; estimate then.
        var rootWidth = SeekPreviewRoot.ActualWidth > 1
            ? SeekPreviewRoot.ActualWidth
            : (trickplay?.ThumbWidth ?? 60) + 10;
        var rootHeight = SeekPreviewRoot.ActualHeight > 1
            ? SeekPreviewRoot.ActualHeight
            : (trickplay is null ? 30 : trickplay.ThumbHeight + 34);
        SeekPreviewPopup.HorizontalOffset = x - rootWidth / 2;
        SeekPreviewPopup.VerticalOffset = -(rootHeight + 10);

        if (trickplay is null)
            return;
        var thumbIndex = trickplay.ThumbIndex(seconds);
        if (thumbIndex == _previewThumbIndex)
            return;
        _previewThumbIndex = thumbIndex;
        var generation = ++_previewGeneration;
        var preview = await trickplay.GetPreviewAsync(seconds);
        // Sheet fetch may outlive the hover or lose to a newer position.
        if (generation != _previewGeneration || preview is null)
            return;
        SeekPreviewImage.Source = new CroppedBitmap(preview.Value.Sheet, preview.Value.Crop);
    }

    /// <summary>The hover timestamp — and, since the inline time edit, the field's prefill, whose
    /// input is mpv's live position rather than a hover fraction. Hence the NaN arm of the guard:
    /// <c>Math.Max(0, NaN)</c> is NaN and <c>TimeSpan.FromSeconds(NaN)</c> throws, and
    /// <c>PlayerViewModel.Format</c> carries the same guard for the same reason.</summary>
    private static string FormatPreviewTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(double.IsNaN(seconds) ? 0 : Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        UiLog(FormattableString.Invariant(
            $"SeekClick value={SeekSlider.Value:F1} thumbDrag={_thumbDragActive}"));
        // Commit a track click as a seek. A thumb drag commits in DragCompleted
        // instead (its mouse-up arrives here first, so skip while dragging).
        if (!_thumbDragActive)
        {
            Diagnostics.AppLog.Detail("overlay", FormattableString.Invariant(
                $"event=seek outcome=commit source=track target_seconds={SeekSlider.Value:F1}"));
            _viewModel.Seek(SeekSlider.Value);
            _viewModel.IsSeeking = false;
        }
    }

    // ---- Inline time edit: click the time to type a position (2026-08-07) ----

    /// <summary>True between <see cref="BeginTimeEdit"/> and <see cref="EndTimeEdit"/>. It does two
    /// jobs beyond bookkeeping: it holds the OSD awake (<see cref="HideControls"/>) and it keeps the
    /// player shortcuts off the keyboard (<see cref="OnOverlayKeyDown"/>).</summary>
    private bool _timeEditActive;

    /// <summary>Opens the editor. Inert in mini mode, like <see cref="ToggleShortcuts"/> — and the
    /// affordance is hidden there too, so the click is never invited before it is refused. The press
    /// cannot reach click-to-pause or the double-click either way: the bar's own gradient background
    /// makes <see cref="OnRootMouseDown"/> bail on <c>ControlsPanel.IsMouseOver</c> long before this
    /// element existed, so there is no guard to duplicate here.</summary>
    private void OnTimeTextClick(object sender, MouseButtonEventArgs e)
    {
        if (_miniMode)
            return;
        BeginTimeEdit();
        e.Handled = true;
    }

    private void BeginTimeEdit()
    {
        if (_timeEditActive)
            return;
        _timeEditActive = true;
        // Prefilled with the ELAPSED half only, through FormatPreviewTime — the field opens reading
        // exactly what the label read. Not PlayerViewModel.Format simply because that one is private;
        // the two produce identical output, so either would do.
        TimeEditBox.Text = FormatPreviewTime(_viewModel.PositionSeconds);
        TimeText.Visibility = Visibility.Collapsed;
        TimeEditBox.Visibility = Visibility.Visible;
        // Focus first, then select: taking focus can reset the selection, so the other order
        // occasionally opens the field with the caret at 0 and nothing selected.
        TimeEditBox.Focus();
        TimeEditBox.SelectAll();
        // Stop the idle timer for the duration of the edit — the 2.5 s delay is shorter than typing
        // a timestamp. Restarted by EndTimeEdit; the same stop/restart pair OnLoadingChanged uses.
        _idleTimer.Stop();
        UiLog($"TimeEditBegin prefill={TimeEditBox.Text}");
    }

    /// <summary>Enter: parse, clamp, seek. Unparseable input closes the editor and seeks NOWHERE,
    /// silently and with no error styling — nothing else in this OSD scolds the user, and a typo is
    /// not worth a first one. The seek goes through the same absolute <see cref="PlayerViewModel.Seek"/>
    /// the slider commit above uses, so a typed position and a dragged one are one operation.</summary>
    private void CommitTimeEdit()
    {
        if (!_timeEditActive)
            return;
        var text = TimeEditBox.Text.Trim();
        EndTimeEdit();
        if (!TryParseTimeInput(text, out var seconds))
        {
            Diagnostics.AppLog.Detail("overlay",
                $"event=seek outcome=abandon source=time-entry input_length={text.Length} parsed=false");
            UiLog($"TimeEditReverted length={text.Length} parsed=false");
            return;
        }
        // Clamp into the file. DurationSeconds is 0 until mpv reports one — the transport bar is
        // hidden for the whole of that window (TransportVisible), so an edit cannot start there, but
        // clamping to [0,0] would silently swallow the seek if one somehow did.
        var duration = _viewModel.DurationSeconds;
        var target = duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
        Diagnostics.AppLog.Detail("overlay", FormattableString.Invariant(
            $"event=seek outcome=commit source=time-entry input_length={text.Length} parsed=true target_seconds={target:F1}"));
        UiLog(FormattableString.Invariant(
            $"TimeEditCommit length={text.Length} parsed=true target={target:F1}"));
        _viewModel.Seek(target);
    }

    /// <summary>Closes the editor without seeking — Escape, losing the keyboard, a click out on the
    /// video, entering mini mode, and the window deactivating. That last one is there for the reason
    /// <see cref="AbandonSeekGesture"/> exists (B8): an Alt+Tab mid-gesture never delivers the event
    /// that would normally end it, and an abandoned edit would pin the OSD awake for the rest of
    /// playback.</summary>
    private void CancelTimeEdit(string cause)
    {
        if (!_timeEditActive)
            return;
        Diagnostics.AppLog.Detail("overlay", $"event=seek outcome=abandon source=time-entry cause={cause}");
        UiLog($"TimeEditCancel cause={cause}");
        EndTimeEdit();
    }

    private void OnTimeEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CancelTimeEdit("lost-focus");

    private void EndTimeEdit()
    {
        _timeEditActive = false;
        // Hand the keyboard back BEFORE collapsing the field, and only if it still holds it (on the
        // lost-focus path it does not, and stealing focus back from whatever the user just clicked
        // would be wrong). A collapsed element keeps WPF's keyboard focus, and the key guard in
        // OnOverlayKeyDown reads that focus — so skipping this would leave every player shortcut
        // suppressed for the rest of playback. The window itself is the right target: it is where
        // focus sits before any OSD control is clicked, and its PreviewKeyDown is the forward.
        if (TimeEditBox.IsKeyboardFocusWithin)
            Keyboard.Focus(this);
        TimeEditBox.Visibility = Visibility.Collapsed;
        TimeText.Visibility = Visibility.Visible;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    /// <summary><c>ss</c>, <c>mm:ss</c> or <c>hh:mm:ss</c>, parsed positionally: 1–3 parts, each
    /// weighted by where it sits, so <c>83:45</c> is a legal 83 minutes rather than an error.
    /// Invariant culture and <see cref="NumberStyles.None"/> rather than a lenient parse — a
    /// timestamp has no sign, no group separator and no surrounding space, and a permissive parse
    /// would accept a different set of strings on a machine with different regional settings.</summary>
    private static bool TryParseTimeInput(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length > 3)
            return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return false;
            total = total * 60 + value;
        }
        seconds = total;
        return true;
    }

    private void OnTimeTextMouseEnter(object sender, MouseEventArgs e) => SetTimeAffordance(true);

    private void OnTimeTextMouseLeave(object sender, MouseEventArgs e) => SetTimeAffordance(false);

    /// <summary>The click affordance: I-beam plus an underline while the pointer is on the time.
    /// Both are suppressed in mini mode, where the click does nothing — a cursor that promises an
    /// edit a 480x270 window will not give is worse than no cursor at all. Imperative rather than a
    /// Style trigger because the mini state is a field, not something a trigger can bind to; a null
    /// cursor is the inherited one, which matters because HideControls hides the pointer via the
    /// window.</summary>
    private void SetTimeAffordance(bool hovering)
    {
        var on = hovering && !_miniMode;
        TimeText.Cursor = on ? Cursors.IBeam : null;
        TimeText.TextDecorations = on ? System.Windows.TextDecorations.Underline : null;
    }

    private void ShowControls()
    {
        if (_controlsVisible)
            return;
        _controlsVisible = true;
        Cursor = Cursors.Arrow;
        SetControlsHitTestable(true);
        var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150));
        ControlsPanel.BeginAnimation(OpacityProperty, fadeIn);
        TopScrim.BeginAnimation(OpacityProperty, fadeIn);
        TitlePanel.BeginAnimation(OpacityProperty, fadeIn);
        BackButton.BeginAnimation(OpacityProperty, fadeIn);
        GemPillPanel.BeginAnimation(OpacityProperty, fadeIn);
        UpdateSubtitlePosition();
    }

    private void HideControls()
    {
        // AudioDevicePopup was once missing from the open-flyout check (Phase 9 M3): the OSD faded
        // and the cursor vanished while the device flyout was open. Both this and the key handler
        // now read the single `Flyouts` list so they cannot drift apart again.
        // _timeEditActive joins the flyout check for the same reason: the idle delay is 2.5 s, which
        // is shorter than typing a timestamp, so without it the OSD fades out from under the field.
        if (!_controlsVisible || ControlsPanel.IsMouseOver || BackButton.IsMouseOver
            || GemPillPanel.IsMouseOver || AnyFlyoutOpen || _timeEditActive || _viewModel.IsLoading)
            return;
        _controlsVisible = false;
        Cursor = Cursors.None;
        var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300));
        // An Opacity-0 element is still fully hit-testable in WPF, and the cursor is hidden
        // by now — so without this a click into the apparently-empty video lands on an
        // invisible control (top-left = Back, which stops playback; top-right = the RTX
        // toggles) and click-to-pause is swallowed by ControlsPanel.IsMouseOver.
        fadeOut.Completed += (_, _) =>
        {
            if (_controlsVisible)
                return;   // ShowControls won the race during the fade
            SetControlsHitTestable(false);
        };
        ControlsPanel.BeginAnimation(OpacityProperty, fadeOut);
        TopScrim.BeginAnimation(OpacityProperty, fadeOut);
        TitlePanel.BeginAnimation(OpacityProperty, fadeOut);
        BackButton.BeginAnimation(OpacityProperty, fadeOut);
        GemPillPanel.BeginAnimation(OpacityProperty, fadeOut);
        UpdateSubtitlePosition();
    }

    /// <summary>While a file is loading the OSD stays awake and the idle timer is held off, so
    /// Back and the now-playing title remain on screen beside the activity ring. Without this the
    /// timer can have expired before playback even began — measured: the first capture of the
    /// loading state showed the ring alone on black, with no way out of a slow load short of
    /// moving the mouse. The transport bar is separately hidden by its TransportVisible binding,
    /// so "awake" here means the top chrome only.</summary>
    private void OnLoadingChanged()
    {
        if (_viewModel.IsLoading)
        {
            ShowControls();
            _idleTimer.Stop();
        }
        else
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }
    }

    // ---- Volume indicator (P10 M10) ----

    /// <summary>Shows the transient volume readout and (re)arms its hold. Driven by
    /// <see cref="PlayerViewModel.VolumeFeedback"/>, which every user path to the level or the
    /// mute flag raises — the Up/Down actions, the wheel gesture, M / the OSD mute button, and
    /// the OSD slider — so this method never has to know which one fired.
    ///
    /// Deliberately does NOT touch <c>_idleTimer</c> or <c>ShowControls</c>: the pill and the
    /// bottom bar are independent lifecycles, which is the point of the milestone.</summary>
    private void ShowVolumePill()
    {
        // The glyph carries the mute flag; the text ALWAYS carries the level. mpv's mute is
        // independent of the level, so a muted "Up" really does move the volume — a pill
        // reading only "Muted" would show the same thing five presses running while the level
        // climbed behind it. Crossed speaker + "65%" says both things at once.
        VolumePillGlyph.Text = _viewModel.IsMuted ? IconMute : IconVolume;
        // Invariant: every user-visible number in this app is (the visual-QA round found a
        // locale comma in the detail rating and fixed the whole class).
        VolumePillText.Text =
            ((int)Math.Round(_viewModel.Volume)).ToString(CultureInfo.InvariantCulture) + "%";

        VolumePill.Visibility = Visibility.Visible;
        if (!_volumePillVisible)
        {
            _volumePillVisible = true;
            VolumePill.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)));
        }
        _volumePillTimer.Stop();
        _volumePillTimer.Start();
    }

    private void HideVolumePill()
    {
        if (!_volumePillVisible)
            return;
        _volumePillVisible = false;
        var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300));
        // Collapse only if a new adjustment did not re-show the pill during the fade — the
        // same race guard HideControls carries, for the same reason.
        fadeOut.Completed += (_, _) =>
        {
            if (!_volumePillVisible)
                VolumePill.Visibility = Visibility.Collapsed;
        };
        VolumePill.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>The glyphs are resource strings rather than literals so the pill uses the same
    /// two icons the OSD mute button does.</summary>
    private string IconVolume => (string)FindResource("IconVolume");
    private string IconMute => (string)FindResource("IconMute");

    /// <summary>Scales the pill for the 480x270 mini window. It is the ONE piece of OSD
    /// furniture that stays alive in mini mode (title, gem pills, back button and the
    /// shortcuts panel all go): the wheel still changes the volume there, and with the bar
    /// no longer waking, this pill is the only feedback that a mini-mode volume change
    /// happened at all.</summary>
    private void UpdateVolumePillScale(bool mini)
    {
        VolumePill.Height = mini ? 26 : 34;
        VolumePill.Padding = mini ? new Thickness(10, 0, 10, 0) : new Thickness(14, 0, 14, 0);
        VolumePill.Margin = mini ? new Thickness(0, 10, 12, 0) : new Thickness(0, 64, 36, 0);
        VolumePillGlyph.FontSize = mini ? 13 : 17;
        VolumePillText.FontSize = mini ? 11 : 13.5;
        VolumePillText.MinWidth = mini ? 36 : 46;
        VolumePillText.Margin = mini ? new Thickness(6, 0, 0, 0) : new Thickness(9, 0, 0, 0);
    }

    private bool _controlsHitTestable = true;

    /// <summary>Gates the faded OSD layer against the mouse. The Up Next card, the skip
    /// button and the mini-player grip live outside it and stay clickable — they carry
    /// their own visibility, not the idle fade.</summary>
    private void SetControlsHitTestable(bool on)
    {
        // The fade-out animation is one Timeline applied to five elements, so its Completed
        // fires five times — and every IsHitTestVisible write costs a WPF hit-test pass.
        if (_controlsHitTestable == on)
            return;
        _controlsHitTestable = on;
        UiLog($"ControlsHitTestable={on}");
        ControlsPanel.IsHitTestVisible = on;
        TitlePanel.IsHitTestVisible = on;
        BackButton.IsHitTestVisible = on;
        GemPillPanel.IsHitTestVisible = on;
    }
}
