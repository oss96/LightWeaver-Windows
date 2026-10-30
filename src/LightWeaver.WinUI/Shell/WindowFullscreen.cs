using System.Runtime.InteropServices;
using LightWeaver.Player;
using Microsoft.UI.Dispatching;

namespace LightWeaver.WinUI.Shell;

/// <summary>Full screen without switching AppWindow presenters: the window keeps its
/// OverlappedPresenter, loses its frame and maximized bit, covers the monitor it is on with its
/// content and is marked full screen with the taskbar; leaving puts the exact style and rect back
/// in one move, maximized again if it was. The presenter switch it replaces left through the window's restored rect (often
/// reaching onto another screen) before maximizing again, under DWM's restore animation.</summary>
internal sealed partial class WindowFullscreen
{
    private const int GwlStyle = -16;
    private const uint SwpNoSize = FullscreenStyle.NoSize, SwpNoMove = FullscreenStyle.NoMove, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;
    private const uint MonitorDefaultToNearest = 2;
    private const uint ShowMinimizedNoActivate = 7, ShowInPlaceNoActivate = 8;
    private const uint WmNcCalcSize = 0x0083, WmStyleChanged = 0x007D, WmWindowPosChanging = 0x0046;
    private const int DwmTransitionsForceDisabled = 3, DwmWindowCornerPreference = 33;
    private const int CornerDefault = 0, CornerDoNotRound = 1;
    private const nuint SubclassId = 0x4C57;
    private const uint ClsctxInprocServer = 1;
    private const int SlotHrInit = 3, SlotMarkFullscreenWindow = 8;
    private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    private static readonly Guid IidTaskbarList2 = new("602d4995-b13a-429b-a66e-1935e44f4317");
    private const string NonRudeProperty = "NonRudeHWND";
    private const string ContentBridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const string CaptionButtonsClass = "ReunionWindowingCaptionControls";
    private const int Hide = 0, ShowNoActivate = 8;

    private static nint s_covering;
    private static WindowFullscreen? s_current;
    private static readonly HashSet<nint> Subclassed = [];

    private readonly nint _hwnd;
    private long _style;
    private ScreenRect _bounds;
    private WindowPlacement _placement;
    private nint _nonRude;
    private bool _entered;
    private bool _waitingForRestore;
    private bool _covering;

    /// <summary>For the window <paramref name="hwnd"/>; nothing changes until <see cref="Enter"/>.</summary>
    public WindowFullscreen(nint hwnd) => _hwnd = hwnd;

    /// <summary>Whether the window was maximized on entering, and is again on leaving; before it has
    /// entered, whether it is maximized now.</summary>
    public bool WasMaximized => ((_entered ? _style : GetWindowLongPtrW(_hwnd, GwlStyle)) & FullscreenStyle.Maximize) != 0;

    /// <summary>The rect of the monitor the window is on, which full screen covers.</summary>
    public ScreenRect Monitor => MonitorRect(_hwnd);

    /// <summary>Whether the window is minimized now.</summary>
    public bool Minimized => IsIconic(_hwnd);

    /// <summary>Blocks until DWM has composed its next frame, so what the window last drew is on screen.</summary>
    public static void WaitForComposition() => DwmFlush();

    /// <summary>Covers the monitor the window is on (the one it overlaps most), from the window's state
    /// now: a window minimized since full screen was asked for stays minimized and covers the monitor
    /// once restored, and one that lost the foreground covers it without coming to the front.</summary>
    public void Enter()
    {
        var entry = FullscreenStyle.Entry(IsIconic(_hwnd), IsForeground);
        _waitingForRestore = entry == FullscreenEntry.OnRestore;
        if (!_waitingForRestore) Apply(entry);
    }

    private bool IsForeground => GetForegroundWindow() == _hwnd;

    private void Apply(FullscreenEntry entry)
    {
        _style = GetWindowLongPtrW(_hwnd, GwlStyle);
        _bounds = WindowRect(_hwnd);
        _placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(_hwnd, ref _placement);
        Subclass(_hwnd);
        s_current = this;
        s_covering = _hwnd;
        SetDwmInt(_hwnd, DwmWindowCornerPreference, CornerDoNotRound);
        SetWindowLongPtrW(_hwnd, GwlStyle, (nint)FullscreenStyle.Borderless(_style));
        Cover(entry == FullscreenEntry.Cover);
        MarkFullscreenLater(_hwnd, true);
        _entered = true;
        _covering = true;
    }

    /// <summary>Back to the style, rect and placement the window had: a maximized window goes straight
    /// back to maximized, a restored or snapped one to where it was; a window minimized meanwhile stays
    /// minimized and restores later the way it was before full screen. Never activates the window;
    /// a window that never covered its monitor is left as it is.</summary>
    public void Exit()
    {
        _waitingForRestore = false;
        _covering = false;
        if (!_entered) return;
        _entered = false;
        s_covering = 0;
        s_current = null;
        MarkFullscreenLater(_hwnd, false);
        if (_nonRude != 0) SetPropW(_hwnd, NonRudeProperty, _nonRude);
        var exit = FullscreenStyle.Exit(IsIconic(_hwnd), _style);
        SetWindowLongPtrW(_hwnd, GwlStyle, (nint)FullscreenStyle.Restored(GetWindowLongPtrW(_hwnd, GwlStyle), _style));
        if (exit == FullscreenExit.SavedPlacement)
        {
            var placement = _placement with { Flags = FullscreenStyle.MinimizedPlacementFlags(_style), ShowCmd = ShowMinimizedNoActivate };
            SetWindowPlacement(_hwnd, ref placement);
            SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged | SwpNoSize | SwpNoMove);
        }
        else
        {
            SetWindowPos(_hwnd, 0, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged);
            if (exit == FullscreenExit.Remaximize) KeepRestoredRect();
        }
        ShowCaptionButtons(_hwnd, true);
        SetDwmInt(_hwnd, DwmWindowCornerPreference, CornerDefault);
    }

    /// <summary>Covers the monitor again when something else moved, reshaped or restyled the window (a
    /// restore from minimized, Win+Up, a move to another screen with Win+Shift+Arrow), and enters
    /// full screen once a window minimized before it could is restored.</summary>
    public void Reassert()
    {
        if (_waitingForRestore)
        {
            if (!IsIconic(_hwnd)) Enter();
            return;
        }
        if (!_covering || IsIconic(_hwnd)) return;
        var style = (long)GetWindowLongPtrW(_hwnd, GwlStyle);
        if (FullscreenStyle.Covers(style, WindowRect(_hwnd), MonitorRect(_hwnd))) return;
        _covering = false;
        SetWindowLongPtrW(_hwnd, GwlStyle, (nint)FullscreenStyle.Borderless(style));
        Cover(IsForeground);
        _covering = true;
    }

    private void Cover(bool toFront)
    {
        AllowFullscreenShell();
        var monitor = MonitorRect(_hwnd);
        var flags = SwpNoOwnerZOrder | SwpFrameChanged | (toFront ? 0 : SwpNoZOrder | SwpNoActivate);
        SetWindowPos(_hwnd, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, flags);
        FillWithContent(_hwnd);
        ShowCaptionButtons(_hwnd, false);
    }

    /// <summary>Hides or shows the Windows App SDK's caption buttons window. Full screen keeps the
    /// caption style, without which that window is destroyed and rebuilt, and UI Automation can then
    /// call the old one's freed provider; so it hides the buttons instead.</summary>
    private static void ShowCaptionButtons(nint hwnd, bool show)
    {
        var buttons = FindWindowExW(hwnd, 0, CaptionButtonsClass, null);
        if (buttons != 0) ShowWindow(buttons, show ? ShowNoActivate : Hide);
    }

    /// <summary>The Windows App SDK marks its windows NonRudeHWND, and the shell then keeps the taskbar
    /// over them however they cover the monitor; full screen takes the mark off and leaving puts it back.</summary>
    private void AllowFullscreenShell()
    {
        var mark = RemovePropW(_hwnd, NonRudeProperty);
        if (mark != 0) _nonRude = mark;
    }

    /// <summary>Moves the XAML content to the whole client area, which <see cref="ContentProc"/> then
    /// keeps while the window covers its monitor.</summary>
    private static void FillWithContent(nint hwnd)
    {
        var content = FindWindowExW(hwnd, 0, ContentBridgeClass, null);
        if (content != 0 && GetClientRect(hwnd, out var client)) SetWindowPos(content, 0, 0, 0, client.Right, client.Bottom, SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate);
    }

    /// <summary>Covering the monitor while not maximized can become the window's restored rect; the
    /// maximized window gets the one it had before full screen back, without moving.</summary>
    private void KeepRestoredRect()
    {
        var now = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(_hwnd, ref now) || now.NormalPosition == _placement.NormalPosition) return;
        var placement = now with { Flags = 0, ShowCmd = ShowInPlaceNoActivate, NormalPosition = _placement.NormalPosition };
        SetWindowPlacement(_hwnd, ref placement);
    }

    /// <summary>Turns DWM's minimize, maximize and restore animations for the window off or on.</summary>
    public static void SetTransitionsEnabled(nint hwnd, bool enabled) => SetDwmInt(hwnd, DwmTransitionsForceDisabled, enabled ? 0 : 1);

    private static ScreenRect MonitorRect(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info);
        return info.Monitor.ToScreen();
    }

    private static ScreenRect WindowRect(nint hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return rect.ToScreen();
    }

    private static void SetDwmInt(nint hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>Marks the window from the dispatcher queue rather than from the window change, which runs
    /// in a XAML render callback: MarkFullscreenWindow waits on the taskbar's thread, a dispatcher timer
    /// that fires meanwhile re-enters XAML there, and XAML fails fast on it.</summary>
    private static void MarkFullscreenLater(nint hwnd, bool fullscreen)
    {
        if (DispatcherQueue.GetForCurrentThread() is { } queue) queue.TryEnqueue(() => MarkFullscreen(hwnd, fullscreen));
        else MarkFullscreen(hwnd, fullscreen);
    }

    /// <summary>ITaskbarList2::MarkFullscreenWindow: the taskbar drops below the window whenever it is
    /// active, rather than relying on its own guess. Raw vtable calls, no built-in COM interop.</summary>
    private static unsafe void MarkFullscreen(nint hwnd, bool fullscreen)
    {
        if (CoCreateInstance(in ClsidTaskbarList, 0, ClsctxInprocServer, in IidTaskbarList2, out var taskbar) < 0) return;
        try
        {
            var vtable = *(nint**)taskbar;
            if (((delegate* unmanaged[Stdcall]<nint, int>)vtable[SlotHrInit])(taskbar) < 0) return;
            ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)vtable[SlotMarkFullscreenWindow])(taskbar, hwnd, fullscreen ? 1 : 0);
        }
        finally
        {
            Marshal.Release(taskbar);
        }
    }

    /// <summary>While the window covers its monitor its whole rect is client area, whatever the
    /// title bar code would reserve for a frame, and the XAML content fills it.</summary>
    private static unsafe void Subclass(nint hwnd)
    {
        if (Subclassed.Add(hwnd)) SetWindowSubclass(hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&WindowProc, SubclassId, 0);
        var content = FindWindowExW(hwnd, 0, ContentBridgeClass, null);
        if (content != 0 && Subclassed.Add(content)) SetWindowSubclass(content, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&ContentProc, SubclassId, 0);
    }

    /// <summary>A style change that keeps the rect (WS_MAXIMIZE or the frame put back) moves nothing,
    /// so it is reasserted here; <see cref="Reassert"/> ignores the change it makes itself.</summary>
    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (hwnd != s_covering) return DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WmNcCalcSize) return 0;
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WmStyleChanged && wParam == GwlStyle) s_current?.Reassert();
        return result;
    }

    /// <summary>XAML places its content a pixel below the top of a window that is not maximized, for a
    /// top border full screen does not have: the row left over shows as a light line and the shorter
    /// content narrows the video. Every move of the content is made one that fills the window.</summary>
    [UnmanagedCallersOnly]
    private static unsafe nint ContentProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmWindowPosChanging && s_covering != 0 && GetParent(hwnd) == s_covering && GetClientRect(s_covering, out var client))
        {
            var position = (WindowPos*)lParam;
            position->X = 0;
            position->Y = 0;
            position->Width = client.Right;
            position->Height = client.Bottom;
            position->Flags = FullscreenStyle.FillingFlags(position->Flags);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public readonly ScreenRect ToScreen() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private record struct WindowPlacement(int Length, uint Flags, uint ShowCmd, Point MinPosition, Point MaxPosition, Rect NormalPosition);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Hwnd;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RemovePropW(nint hwnd, string name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPropW(nint hwnd, string name, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
