using System.Runtime.InteropServices;

namespace LightWeaver.WinUI.Player;

/// <summary>Whether the mouse pointer is over a window's XAML content, rather than its title bar,
/// caption buttons, resize borders or another window, each of which is a separate HWND.</summary>
internal static partial class PointerSite
{
    private const string ContentSiteClass = "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const uint RootAncestor = 2;
    private const uint NonClientHitTest = 0x0084;
    private const nint ClientArea = 1;

    /// <summary>The pointer's position over the content of <paramref name="window"/> in physical
    /// pixels, when that window is in the foreground; null anywhere else.</summary>
    public static Windows.Foundation.Point? OverContent(nint window)
    {
        if (window == 0 || GetForegroundWindow() != window || !GetCursorPos(out var point)) return null;
        var under = WindowFromPoint(point);
        if (under == 0 || GetAncestor(under, RootAncestor) != window || !IsContentSite(under) || HitTest(window, point) != ClientArea || !ScreenToClient(under, ref point)) return null;
        return new Windows.Foundation.Point(point.X, point.Y);
    }

    /// <summary>The pointer's position on the screen in physical pixels; null when it cannot be read.</summary>
    public static (int X, int Y)? Cursor() => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    private static nint HitTest(nint window, ScreenPoint point)
        => SendMessage(window, NonClientHitTest, 0, (point.Y << 16) | (point.X & 0xFFFF));

    private static unsafe bool IsContentSite(nint window)
    {
        var name = stackalloc char[64];
        var length = GetClassName(window, name, 64);
        return new ReadOnlySpan<char>(name, length).SequenceEqual(ContentSiteClass);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out ScreenPoint point);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(ScreenPoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref ScreenPoint point);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* name, int capacity);
}
