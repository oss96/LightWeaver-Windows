using System.Runtime.InteropServices;
using LightWeaver.Player;

namespace LightWeaver.WinUI.Shell;

/// <summary>Starts a maximized window's client area at the top of its monitor's work area. The Windows
/// App SDK takes the frame of the main monitor's scale off the top, so on a monitor at another scale
/// the content began a few pixels low, under a light strip, or was cut off at the top.</summary>
internal static partial class MaximizedFrame
{
    private const uint WmNcCalcSize = 0x0083;
    private const uint MonitorDefaultToNearest = 2;
    private const nuint SubclassId = 0x4C58;

    /// <summary>Corrects every later client area calculation of the window <paramref name="hwnd"/>.</summary>
    public static unsafe void Attach(nint hwnd)
        => SetWindowSubclass(hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&WindowProc, SubclassId, 0);

    [UnmanagedCallersOnly]
    private static unsafe nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message != WmNcCalcSize || wParam == 0 || !IsZoomed(hwnd)) return result;
        var client = (Rect*)lParam;
        var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
        if (!GetMonitorInfoW(MonitorFromRect(client, MonitorDefaultToNearest), ref info)) return result;
        var corrected = WindowFrame.MaximizedClient(client->ToScreen(), info.Work.ToScreen());
        client->Top = corrected.Top;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToScreen() => new(Left, Top, Right, Bottom);
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint MonitorFromRect(Rect* rect, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
