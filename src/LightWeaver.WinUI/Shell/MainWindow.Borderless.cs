using System.Diagnostics;
using LightWeaver.Player;
using LightWeaver.WinUI.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>The borderless full screen: the video grows from the player straight to the whole monitor and
/// shrinks back on the <see cref="FullscreenOverlay"/>, while the window changes under it.</summary>
public sealed partial class MainWindow
{
    private enum ScreenMove { None, Growing, Covering, Shrinking }

    private FullscreenOverlay? _overlay;
    private ScreenMove _screenMove;
    private bool _screenGrowCancelled;
    private bool _screenGrowEscaped;
    private int _screenMoveId;
    private EventHandler<object>? _screenSettle;
    private Services.FrameLog.Recording? _screenFrames;
    private IDisposable? _screenHold;
    private long _screenMoveStarted;
    private nint _screenFocus;
    private VideoDrops _screenDrops;
    private string _screenKind = "";

    /// <summary>mpv's own counts of video frames it dropped and frames it showed late.</summary>
    private readonly record struct VideoDrops(long Dropped, long Delayed);

    /// <summary>Whether full screen moves on the overlay now: the setting is on and the window is in
    /// front, so the topmost overlay never shows over another app.</summary>
    private bool Borderless => App.Settings.BorderlessFullscreen && WindowFocus.IsForeground(WindowHandle);

    private bool ScreenGrowing => _screenMove == ScreenMove.Growing && !_screenGrowCancelled;

    private bool ScreenBusy => _screenMove != ScreenMove.None && !ScreenGrowing;

    /// <summary>Grows the video from <paramref name="from"/> in the window to the whole monitor, then covers
    /// it with the window. A grow sent back lands where <paramref name="back"/> puts the player; one sent
    /// back by Esc lands at <paramref name="escapeTo"/> instead, and <paramref name="afterEscape"/> runs.</summary>
    private void GrowOnScreen(Windows.Foundation.Rect from, double radius, Func<Func<Windows.Foundation.Rect?>> back,
        Func<Func<Windows.Foundation.Rect?>>? escapeTo = null, Action? afterEscape = null)
    {
        if (_surface is null || Root.XamlRoot is not { } xaml)
        {
            ShowFull();
            SetOsFullscreen(true);
            return;
        }
        var id = BeginScreenMove(ScreenMove.Growing);
        var scale = xaml.RasterizationScale;
        var monitor = WindowFullscreen.MonitorRect(WindowHandle);
        var start = OnScreen(from, monitor, scale);
        var whole = ScreenPlacement.Whole(monitor, scale);
        _playerView?.SetChromeHidden(true);
        var overlay = _overlay ??= new FullscreenOverlay();
        overlay.Show(monitor, scale, start, _surface, radius, () =>
        {
            if (id != _screenMoveId) return;
            LogScreenMove(id, "shown");
            KeepScreenFocus();
            RecordScreenFrames("player_grow_screen");
            overlay.Animate(start, whole, () =>
            {
                if (id != _screenMoveId) return;
                LogScreenMove(id, "grown");
                RecordScreenFrames(null);
                if (_screenGrowCancelled)
                {
                    _screenMove = ScreenMove.Shrinking;
                    var escaped = _screenGrowEscaped;
                    ShrinkOnScreen(id, monitor, scale, whole, radius, escaped && escapeTo is not null ? escapeTo : back, escaped ? afterEscape : null);
                    return;
                }
                _screenMove = ScreenMove.Covering;
                ShowFull();
                _playerView?.SetChromeHidden(true);
                SetOsFullscreen(true, () =>
                {
                    if (id != _screenMoveId) return;
                    LogScreenMove(id, "covered");
                    KeepScreenFocus();
                    WhenScreenSettled(id, waitForWindow: true, () => EndScreenMove(id));
                });
            });
        });
    }

    /// <summary>Leaves full screen: the window goes back under the overlay, <paramref name="arrive"/> places
    /// the player and returns how to measure where the video lands (null: nowhere), and it shrinks there.</summary>
    private void LeaveOnScreen(Func<Func<Windows.Foundation.Rect?>> arrive, double radius, Action? done = null)
    {
        if (_surface is null || Root.XamlRoot is not { } xaml || !IsOsFullscreen)
        {
            SetOsFullscreen(false, () =>
            {
                arrive();
                done?.Invoke();
            });
            return;
        }
        var id = BeginScreenMove(ScreenMove.Shrinking);
        var scale = xaml.RasterizationScale;
        var monitor = WindowFullscreen.MonitorRect(WindowHandle);
        var whole = ScreenPlacement.Whole(monitor, scale);
        _playerView?.SetChromeHidden(true);
        var overlay = _overlay ??= new FullscreenOverlay();
        overlay.Show(monitor, scale, whole, _surface, radius, () =>
        {
            if (id != _screenMoveId) return;
            LogScreenMove(id, "shown");
            KeepScreenFocus();
            overlay.YieldToTaskbar();
            SetOsFullscreen(false, () =>
            {
                if (id != _screenMoveId) return;
                LogScreenMove(id, "uncovered");
                KeepScreenFocus();
                ShrinkOnScreen(id, monitor, scale, whole, radius, arrive, done);
            });
        });
    }

    /// <summary>Shrinks the overlay's video onto where <paramref name="arrive"/> put the player, measured
    /// once its layout is on screen, without waiting for XAML to take the window's new size.</summary>
    private void ShrinkOnScreen(int id, ScreenRect monitor, double scale, LayerRect from, double radius, Func<Func<Windows.Foundation.Rect?>> arrive, Action? done)
    {
        if (id != _screenMoveId) return;
        var measure = arrive();
        if (measure() is null)
        {
            EndScreenMove(id);
            done?.Invoke();
            return;
        }
        WhenScreenSettled(id, waitForWindow: false, () =>
        {
            if (measure() is not { } target)
            {
                EndScreenMove(id);
                done?.Invoke();
                return;
            }
            LogScreenMove(id, "shrinking");
            RecordScreenFrames("player_shrink_screen");
            _overlay!.Animate(from, OnScreen(target, monitor, scale), () =>
            {
                if (id != _screenMoveId) return;
                LogScreenMove(id, "shrunk");
                EndScreenMove(id);
                done?.Invoke();
            });
        });
    }

    /// <summary>Places the player where it lands in the window on leaving full screen: back in the detail
    /// page's preview when that page is still shown, else the full window.</summary>
    private Func<Windows.Foundation.Rect?> ArriveInWindow()
    {
        if (_playerMode == PlayerMode.Full && CanReturnToPreview(out var page) && _playback?.Failure is null)
        {
            PlaceInPreview(page);
            return () => PreviewBounds(page);
        }
        return () => WindowBounds;
    }

    /// <summary>Starts a grow from the detail page's preview, a still-loading video growing as the
    /// preview's backdrop; a grow sent back lands in the preview again.</summary>
    private void GrowPreviewOnScreen(DetailPage page)
    {
        _surface!.SetArt(page.BackdropSource, Stretch.UniformToFill);
        _surface.SeeThrough = false;
        GrowOnScreen(PreviewBounds(page), _playerLayer!.CornerRadius.TopLeft, () =>
        {
            if (CanReturnToPreview(out var shown))
            {
                PlaceInPreview(shown);
                return () => PreviewBounds(shown);
            }
            ShowFull();
            return () => WindowBounds;
        });
    }

    /// <summary>Starts a grow from a card's artwork: sent back by the toggle it lands in the full window,
    /// sent back by Esc on the card again, where playback stops.</summary>
    private void GrowCardOnScreen(Windows.Foundation.Rect card, PlayOrigin origin)
    {
        SetPlayerMode(PlayerMode.Full);
        GrowOnScreen(card, Controls.Ui.Resource<CornerRadius>("LwCardRadius").TopLeft,
            back: () =>
            {
                ShowFull();
                return () => WindowBounds;
            },
            escapeTo: () =>
            {
                _leavingCard = true;
                return () => CardReturnBounds(origin);
            },
            afterEscape: () => LandOnCard(origin));
    }

    /// <summary>Starts a grow from the full window; sent back by Esc, the player is then left as Esc leaves it.</summary>
    private void GrowWindowOnScreen() => GrowOnScreen(WindowBounds, 0, () => () => WindowBounds, afterEscape: LeavePlayer);

    /// <summary>Esc during a grow: the video goes back, paused, and the player is left once it has landed.</summary>
    private void EscapeScreenGrow()
    {
        _screenGrowCancelled = true;
        _screenGrowEscaped = true;
        if (_playback?.Player is { } player) player.Pause = true;
    }

    private LayerRect OnScreen(Windows.Foundation.Rect rect, ScreenRect monitor, double scale)
    {
        var inWindow = new LayerRect(rect.X, rect.Y, rect.Width, rect.Height);
        if (Root.FlowDirection == FlowDirection.RightToLeft) inWindow = ScreenPlacement.Mirrored(inWindow, Root.ActualWidth);
        return ScreenPlacement.OnMonitor(inWindow, WindowFullscreen.ContentRect(WindowHandle), monitor, scale);
    }

    /// <summary>Logs the frames of the move's animation under <paramref name="name"/>; null ends the log.</summary>
    private void RecordScreenFrames(string? name)
    {
        _screenFrames?.Dispose();
        _screenFrames = name is null ? null : Services.FrameLog.Start(name);
    }

    private int BeginScreenMove(ScreenMove move)
    {
        StopScreenSettle();
        RecordScreenFrames(null);
        _screenMove = move;
        _screenGrowCancelled = false;
        _screenGrowEscaped = false;
        _screenHold?.Dispose();
        _screenHold = Services.ImageQueue.Hold();
        _screenMoveStarted = Stopwatch.GetTimestamp();
        _screenFocus = WindowFocus.Owner(WindowHandle);
        _screenDrops = CurrentDrops();
        _screenKind = move == ScreenMove.Growing ? "grow" : "shrink";
        var id = ++_screenMoveId;
        LogScreenMove(id, _screenKind);
        return id;
    }

    private void LogScreenMove(int id, string stage)
        => Diagnostics.AppLog.Detail("player", $"event=screen_move id={id} stage={stage} elapsed_ms={Stopwatch.GetElapsedTime(_screenMoveStarted).TotalMilliseconds:0}");

    /// <summary>Gives the keyboard back to the window's content when the window is still in front and the
    /// overlay or the window change left the focus outside it.</summary>
    private void KeepScreenFocus()
    {
        if (WindowFocus.Restore(WindowHandle, _screenFocus)) Diagnostics.AppLog.Info("player", $"event=screen_move id={_screenMoveId} stage=focus outcome=refocused");
    }

    private VideoDrops CurrentDrops()
    {
        if (_playback?.Player is not { } player) return default;
        try
        {
            return new VideoDrops(Count(player.GetPropertyString("frame-drop-count")), Count(player.GetPropertyString("vo-delayed-frame-count")));
        }
        catch (ObjectDisposedException)
        {
            return default;
        }
        static long Count(string? value) => long.TryParse(value, out var count) ? count : 0;
    }

    /// <summary>The window shows the overlay's picture: the overlay goes, the controls come back, and the
    /// move is logged with how many video frames mpv dropped or showed late during it.</summary>
    private void EndScreenMove(int id)
    {
        if (id != _screenMoveId) return;
        var drops = CurrentDrops();
        Diagnostics.AppLog.Info("player", $"event=screen_move id={id} kind={_screenKind} outcome=done elapsed_ms={Stopwatch.GetElapsedTime(_screenMoveStarted).TotalMilliseconds:0} frames_dropped={Math.Max(0, drops.Dropped - _screenDrops.Dropped)} frames_delayed={Math.Max(0, drops.Delayed - _screenDrops.Delayed)}");
        ClearScreenMove();
        KeepScreenFocus();
        _playerView?.SetChromeHidden(false);
        if (_playerMode != PlayerMode.Hidden) _playerView?.Focus(FocusState.Programmatic);
    }

    /// <summary>Drops a move still running (playback stopped, a new play, the window closing): the overlay goes at once.</summary>
    private void AbortScreenMove()
    {
        if (_screenMove == ScreenMove.None && _overlay is not { IsShown: true }) return;
        _screenMoveId++;
        ClearScreenMove();
    }

    private void ClearScreenMove()
    {
        StopScreenSettle();
        RecordScreenFrames(null);
        _screenHold?.Dispose();
        _screenHold = null;
        _screenMove = ScreenMove.None;
        _screenGrowCancelled = false;
        _screenGrowEscaped = false;
        _overlay?.Hide();
    }

    /// <summary>Runs <paramref name="action"/> once the window's layout has been on screen for
    /// <see cref="WindowChange.LayoutFrames"/> frames and, with <paramref name="waitForWindow"/>, no window change is pending.</summary>
    private void WhenScreenSettled(int id, bool waitForWindow, Action action)
    {
        StopScreenSettle();
        var frames = 0;
        _screenSettle = (_, _) =>
        {
            if (id != _screenMoveId)
            {
                StopScreenSettle();
                return;
            }
            if (waitForWindow && _windowChange is not null)
            {
                frames = 0;
                return;
            }
            if (++frames < WindowChange.LayoutFrames) return;
            StopScreenSettle();
            WindowFullscreen.WaitForComposition();
            action();
        };
        CompositionTarget.Rendering += _screenSettle;
    }

    private void StopScreenSettle()
    {
        if (_screenSettle is null) return;
        CompositionTarget.Rendering -= _screenSettle;
        _screenSettle = null;
    }
}
