using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Theme;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace LightWeaver.WinUI.Shell;

/// <summary>Scales app controls and reserves the app-wide zoom gestures.</summary>
public sealed partial class MainWindow
{
    private const string ZoomToastKey = "zoom";
    private static readonly TimeSpan ZoomSaveDelay = TimeSpan.FromMilliseconds(400);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomSave;
    private bool _zoomUnsaved;
    private PointerRoutedEventArgs? _zoomWheelEvent;

    private static void ApplySavedZoom()
    {
        UiZoom.SetLimitToScreen(App.Settings.LimitUiZoomToScreen);
        UiZoom.Set(App.Settings.UiZoom);
    }

    private void InitializeZoom()
    {
        Root.PreviewKeyDown += OnZoomKey;
        foreach (var key in new[] { (VirtualKey)0xBB, (VirtualKey)0xBD, VirtualKey.Add, VirtualKey.Subtract, VirtualKey.Number0, VirtualKey.NumberPad0 })
            foreach (var modifiers in new[] { VirtualKeyModifiers.Control, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift })
                AddAccelerator(key, modifiers, () => ApplyZoomKey((int)key));
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnZoomWheel), handledEventsToo: true);
        UiZoom.Changed += OnZoomChanged;
        UiZoom.PreferredChanged += OnZoomPreferredChanged;
        UiZoom.Requested += ShowZoomReadout;
        UiZoom.LimitChanged += UpdateZoomScreen;
        AppWindow.Changed += (_, e) => { if (e.DidPositionChange || e.DidSizeChange) UpdateZoomScreen(); };
        Root.Loaded += (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => UpdateZoomScreen();
            UpdateZoomScreen();
        };
        Closed += (_, _) =>
        {
            UiZoom.Changed -= OnZoomChanged;
            UiZoom.PreferredChanged -= OnZoomPreferredChanged;
            UiZoom.Requested -= ShowZoomReadout;
            UiZoom.LimitChanged -= UpdateZoomScreen;
            FlushZoomSave();
        };
    }

    private void OnZoomKey(object sender, KeyRoutedEventArgs e)
    {
        if (_nav.CurrentPage is Pages.SettingsPage { IsRecordingShortcut: true }) return;
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Handled || UiZoom.ZoomKey((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Menu)) == int.MinValue) return;
        e.Handled = true;
        ApplyZoomKey((int)e.Key);
    }

    private void ApplyZoomKey(int key)
    {
        if (_miniPlayer) return;
        switch (UiZoom.ZoomKey(key, control: true, alt: false))
        {
            case 1: UiZoom.StepIn(); break;
            case -1: UiZoom.StepOut(); break;
            case 0: UiZoom.Reset(); break;
        }
    }

    private void OnZoomWheel(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_zoomWheelEvent, e) || !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)) return;
        _zoomWheelEvent = e;
        var delta = e.GetCurrentPoint(sender as UIElement ?? Root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        if (!_miniPlayer) UiZoom.Wheel(delta);
    }

    private void OnZoomChanged(double scale)
    {
        Diagnostics.AppLog.Detail("main", $"event=interaction action=zoom percent={UiZoom.Percent}");
        UpdateMinimumSize(grow: true);
        _lastRegions = "";
        UpdateDragRegions();
    }

    private void ShowZoomReadout()
    {
        if (_miniPlayer) return;
        var text = L.Format("UiZoom.Readout", UiZoom.Percent);
        if (UiZoom.AtScreenLimit) text += " \u00B7 " + L.Text("UiZoom.ScreenLimit.5b0d7e3c");
        ToastHost.Show(ToastKind.Info, text, mergeKey: ZoomToastKey);
    }

    private void OnZoomPreferredChanged()
    {
        if (Math.Abs(App.Settings.UiZoom - UiZoom.Preferred) < 0.001) return;
        App.Settings.UiZoom = UiZoom.Preferred;
        _zoomUnsaved = true;
        if (_zoomSave is null)
        {
            _zoomSave = DispatcherQueue.CreateTimer();
            _zoomSave.Interval = ZoomSaveDelay;
            _zoomSave.IsRepeating = false;
            _zoomSave.Tick += (_, _) => FlushZoomSave();
        }
        _zoomSave.Stop();
        _zoomSave.Start();
    }

    private void FlushZoomSave()
    {
        _zoomSave?.Stop();
        if (!_zoomUnsaved) return;
        _zoomUnsaved = false;
        SettingsStore.Save(App.Settings);
    }

    private void UpdateZoomScreen()
    {
        if (Root.XamlRoot is not { RasterizationScale: > 0 } root) return;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        UiZoom.SetScreenCap(UiZoom.CapFor(work.Width / root.RasterizationScale, work.Height / root.RasterizationScale, MinWidthDip, MinHeightDip));
        UpdateMinimumSize(grow: true);
    }

    private (int Width, int Height) MinimumWindow(double scale, RectInt32 work)
    {
        var zoom = UiZoom.LimitToScreen ? UiZoom.Scale : 1;
        return (Math.Min((int)(MinWidthDip * zoom * scale), work.Width), Math.Min((int)(MinHeightDip * zoom * scale), work.Height));
    }

    private void GrowToMinimum(OverlappedPresenter presenter, int width, int height, RectInt32 work)
    {
        if (presenter.State != OverlappedPresenterState.Restored || IsOsFullscreen || _miniPlayer) return;
        var size = AppWindow.Size;
        if (size.Width >= width && size.Height >= height) return;
        var w = Math.Max(size.Width, width);
        var h = Math.Max(size.Height, height);
        var position = AppWindow.Position;
        var x = Math.Clamp(position.X, work.X, Math.Max(work.X, work.X + work.Width - w));
        var y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }
}
