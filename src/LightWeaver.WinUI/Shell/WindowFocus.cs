using System.Runtime.InteropServices;

namespace LightWeaver.WinUI.Shell;

/// <summary>The Win32 keyboard focus of the main window: only its XAML content reads keys, so focus on
/// the bare top-level window counts as lost.</summary>
internal static partial class WindowFocus
{
    /// <summary>Whether <paramref name="window"/> is the foreground window.</summary>
    public static bool IsForeground(nint window) => GetForegroundWindow() == window;

    /// <summary>The window in <paramref name="window"/>'s content that has the keyboard focus now, or 0.</summary>
    public static nint Owner(nint window)
    {
        var focus = GetFocus();
        return Within(window, focus) ? focus : 0;
    }

    /// <summary>Gives the keyboard to <paramref name="owner"/> (or the content) when <paramref name="window"/>
    /// is in front and the focus is outside its content; true when it had to. Never changes the foreground.</summary>
    public static bool Restore(nint window, nint owner)
    {
        if (!IsForeground(window) || Within(window, GetFocus())) return false;
        Give(owner != 0 && IsWindow(owner) && Within(window, owner) ? owner : WindowFullscreen.ContentWindow(window) is var content and not 0 ? content : window);
        return true;
    }

    /// <summary>Gives the keyboard focus to <paramref name="target"/>, a window of this thread.</summary>
    public static void Give(nint target) => SetFocus(target);

    private static bool Within(nint window, nint other) => other != 0 && other != window && IsChild(window, other);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);
}
