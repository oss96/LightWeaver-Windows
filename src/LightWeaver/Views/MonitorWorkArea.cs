using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LightWeaver.Views;

/// <summary>
/// The work area (screen minus taskbar) of the monitor a window is on, in that window's DIPs.
/// <see cref="SystemParameters.WorkArea"/> is the PRIMARY monitor only, so anything clamped to it
/// jumps to the primary screen when the app sits on another one.
/// </summary>
internal static class MonitorWorkArea
{
    /// <summary>Empty when the window has no handle yet or the monitor cannot be queried.</summary>
    public static Rect Of(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
            return Rect.Empty;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info))
            return Rect.Empty;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
