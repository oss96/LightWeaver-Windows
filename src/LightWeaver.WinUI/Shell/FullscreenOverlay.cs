using System.Runtime.InteropServices;
using LightWeaver.Player;
using LightWeaver.WinUI.Player;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>The stage of the borderless full screen: a transparent, frameless, topmost window over the
/// monitor that never takes the foreground and mirrors the player's video while it grows or shrinks.</summary>
internal sealed partial class FullscreenOverlay
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long Popup = 0x80000000, Framed = FullscreenStyle.Caption | FullscreenStyle.ThickFrame | FullscreenStyle.SystemMenu | FullscreenStyle.MinimizeBox | FullscreenStyle.MaximizeBox;
    private const long ExToolWindow = 0x00000080, ExTransparent = 0x00000020, ExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;
    private const uint WmEraseBackground = 0x0014, WmNcCalcSize = 0x0083, WmMouseActivate = 0x0021, WmSetFocus = 0x0007;
    private const nint MouseNoActivate = 3;
    private const int DwmWindowCornerPreference = 33, CornerDoNotRound = 1, DwmBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint BlurEnable = 0x1, BlurRegion = 0x2;
    private const int BlackBrush = 4;
    private const int ThreadCurrent = 2, ApartmentSta = 2;
    private const uint GwHwndPrev = 3;
    private const nuint SubclassId = 0x4C58;
    private static readonly nint HwndTopmost = -1;
    private static readonly string[] TaskbarClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private static nint s_systemQueue;

    /// <summary>Frames to wait, at most, for the window to take the monitor's scale and size after a move
    /// across monitors, which rescales it.</summary>
    private const int ScaleFrames = 10;

    private Window? _window;
    private nint _hwnd;
    private Grid? _root;
    private Canvas? _backdrop;
    private Grid? _layer;
    private VideoSurface? _video;
    private LayerTransition? _motion;
    private LayerContent _content;
    private double _radius;
    private EventHandler<object>? _waiting;
    private EventHandler<object>? _overTaskbar;
    private int _generation;

    /// <summary>Whether the window is on screen.</summary>
    public bool IsShown { get; private set; }

    private double Scale => _root?.XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 0;

    /// <summary>Covers <paramref name="monitor"/> with <paramref name="source"/>'s picture drawn at
    /// <paramref name="at"/> (DIPs at <paramref name="scale"/>) with corners of <paramref name="radius"/>,
    /// and runs <paramref name="shown"/> once that is on screen.</summary>
    public void Show(ScreenRect monitor, double scale, LayerRect at, VideoSurface source, double radius, Action shown)
    {
        var generation = ++_generation;
        Create();
        StopWaiting();
        _content = source.Content;
        _radius = radius;
        _video!.Mirror(source);
        Place(at);
        var rect = new Windows.Graphics.RectInt32(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        _window!.AppWindow.MoveAndResize(rect);
        if (!IsShown) _window.AppWindow.Show(false);
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        IsShown = true;
        StayOverTaskbar(true);
        var frames = 0;
        _waiting = (_, _) =>
        {
            if (generation != _generation) return;
            var covers = _window.AppWindow.Size is var size && size.Width == rect.Width && size.Height == rect.Height;
            if (!covers) _window.AppWindow.MoveAndResize(rect);
            if (++frames < WindowChange.LayoutFrames || ((!covers || Math.Abs(Scale - scale) > 0.001) && frames < ScaleFrames)) return;
            StopWaiting();
            WindowFullscreen.WaitForComposition();
            shown();
        };
        CompositionTarget.Rendering += _waiting;
    }

    /// <summary>Moves the picture from <paramref name="from"/> to <paramref name="to"/> over the
    /// player's 180 ms, and keeps it at <paramref name="to"/> once <paramref name="landed"/> runs.</summary>
    public void Animate(LayerRect from, LayerRect to, Action landed)
    {
        if (_layer is null || !IsShown) return;
        StopMotion();
        _layer.Width = to.Width;
        _layer.Height = to.Height;
        var motion = _motion = new LayerTransition(_layer, from, to, _content, _radius, _backdrop);
        motion.Completed += (_, _) =>
        {
            if (ReferenceEquals(_motion, motion)) landed();
        };
        motion.Start();
    }

    /// <summary>Lets the taskbar come over the window from now on: leaving full screen, it comes back
    /// once, over the picture, and stays where it will be when the window is gone.</summary>
    public void YieldToTaskbar() => StayOverTaskbar(false);

    /// <summary>Takes the window off screen, and lets go of the mirrored picture a frame later, so the
    /// window never shows without it.</summary>
    public void Hide()
    {
        var generation = ++_generation;
        StopWaiting();
        StayOverTaskbar(false);
        if (IsShown)
        {
            IsShown = false;
            _window?.AppWindow.Hide();
        }
        _waiting = (_, _) =>
        {
            StopWaiting();
            if (generation != _generation) return;
            StopMotion();
            _video?.StopMirror();
        };
        CompositionTarget.Rendering += _waiting;
    }

    public void Close()
    {
        Hide();
        StopWaiting();
        StopMotion();
        _video?.StopMirror();
        _window?.Close();
        _window = null;
    }

    /// <summary>Keeps the window over the taskbar every frame: the taskbar puts itself back over topmost
    /// windows whenever the foreground changes. Only the taskbar is stepped over, so the toasts of
    /// other apps stay where they are.</summary>
    private void StayOverTaskbar(bool on)
    {
        if (_overTaskbar is not null) CompositionTarget.Rendering -= _overTaskbar;
        _overTaskbar = on ? (_, _) => StepOverTaskbar() : null;
        if (_overTaskbar is not null) CompositionTarget.Rendering += _overTaskbar;
    }

    private void StepOverTaskbar()
    {
        for (var above = GetWindow(_hwnd, GwHwndPrev); above != 0; above = GetWindow(above, GwHwndPrev))
        {
            if (!IsTaskbar(above)) continue;
            var overTaskbar = GetWindow(above, GwHwndPrev);
            SetWindowPos(_hwnd, overTaskbar != 0 ? overTaskbar : HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            return;
        }
    }

    private static unsafe bool IsTaskbar(nint hwnd)
    {
        const int Capacity = 32;
        var name = stackalloc char[Capacity];
        var length = GetClassNameW(hwnd, name, Capacity);
        return length > 0 && TaskbarClasses.Contains(new string(name, 0, length));
    }

    /// <summary>Draws the picture at <paramref name="rect"/> and keeps it there.</summary>
    private void Place(LayerRect rect)
    {
        StopMotion();
        _layer!.Width = rect.Width;
        _layer.Height = rect.Height;
        _motion = new LayerTransition(_layer, rect, rect, _content, _radius, _backdrop);
        _motion.Hold();
    }

    private void StopMotion()
    {
        _motion?.Stop();
        _motion = null;
    }

    private void StopWaiting()
    {
        if (_waiting is null) return;
        CompositionTarget.Rendering -= _waiting;
        _waiting = null;
    }

    private void Create()
    {
        if (_window is not null) return;
        EnsureSystemDispatcherQueue();
        _video = new VideoSurface();
        _layer = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _layer.Children.Add(_video);
        _backdrop = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _root = new Grid { IsHitTestVisible = false };
        _root.Children.Add(_backdrop);
        _root.Children.Add(_layer);
        _window = new Window { Content = _root, SystemBackdrop = new ClearBackdrop(), Title = "LightWeaver" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var app = _window.AppWindow;
        app.IsShownInSwitchers = false;
        if (app.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        MakeFrameless();
        MakeSurfaceTransparent();
        WindowFullscreen.SetTransitionsEnabled(_hwnd, false);
    }

    /// <summary>No frame at all: a popup whose whole rect is client area, with no DWM border line and
    /// square corners.</summary>
    private unsafe void MakeFrameless()
    {
        SetWindowSubclass(_hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&WindowProc, SubclassId, 0);
        SetWindowLongPtrW(_hwnd, GwlStyle, (nint)((GetWindowLongPtrW(_hwnd, GwlStyle) & ~Framed) | Popup));
        SetWindowLongPtrW(_hwnd, GwlExStyle, (nint)(GetWindowLongPtrW(_hwnd, GwlExStyle) | ExToolWindow | ExTransparent | ExNoActivate));
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
        var corner = CornerDoNotRound;
        DwmSetWindowAttribute(_hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
        var border = unchecked((int)DwmColorNone);
        DwmSetWindowAttribute(_hwnd, DwmBorderColor, ref border, sizeof(int));
    }

    /// <summary>DWM composes the window surface by its alpha (blur-behind over an empty region), and
    /// <see cref="WindowProc"/> erases it to transparent black, which Windows otherwise paints opaque.</summary>
    private void MakeSurfaceTransparent()
    {
        var nowhere = CreateRectRgn(-2, -2, -1, -1);
        var blur = new BlurBehind(BlurEnable | BlurRegion, 1, nowhere, 0);
        DwmEnableBlurBehindWindow(_hwnd, ref blur);
        DeleteObject(nowhere);
    }

    /// <summary>The whole rect is client area, the surface is erased transparent, a click never
    /// activates the window, and the keyboard focus goes straight back to where it came from.</summary>
    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcCalcSize && wParam != 0) return 0;
        if (message == WmMouseActivate) return MouseNoActivate;
        if (message == WmSetFocus && wParam != 0 && wParam != hwnd)
        {
            WindowFocus.Give(wParam);
            return 0;
        }
        if (message == WmEraseBackground && GetClientRect(hwnd, out var client))
        {
            FillRect(wParam, ref client, GetStockObject(BlackBrush));
            return 1;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>A system backdrop brush needs the system compositor, and that needs a
    /// Windows.System dispatcher queue on the UI thread, which the Windows App SDK does not create.</summary>
    private static void EnsureSystemDispatcherQueue()
    {
        if (s_systemQueue != 0 || Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new DispatcherQueueOptions(Marshal.SizeOf<DispatcherQueueOptions>(), ThreadCurrent, ApartmentSta);
        if (CreateDispatcherQueueController(options, out var controller) >= 0) s_systemQueue = controller;
    }

    /// <summary>A backdrop of nothing: where XAML draws nothing, the window shows what is behind it.</summary>
    private sealed partial class ClearBackdrop : SystemBackdrop
    {
        private Windows.UI.Composition.Compositor? _compositor;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            _compositor ??= new Windows.UI.Composition.Compositor();
            connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            disconnectedTarget.SystemBackdrop = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DispatcherQueueOptions(int Size, int ThreadType, int ApartmentType);

    [StructLayout(LayoutKind.Sequential)]
    private record struct BlurBehind(uint Flags, int Enable, nint Region, int TransitionOnMaximized);

    [StructLayout(LayoutKind.Sequential)]
    private record struct Rect(int Left, int Top, int Right, int Bottom);

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int length);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, ref Rect rect, nint brush);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
