namespace LightWeaver.Player;

/// <summary>A window or monitor rect in physical pixels, as Win32 reports it (right and bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>The rect's size in DIPs at <paramref name="scale"/>, placed at the origin: the layout
    /// a window's content needs to fill a client area of this size.</summary>
    public LayerRect InDips(double scale) => new(0, 0, Width / scale, Height / scale);
}

/// <summary>The window styles of full screen without a presenter switch: the sizing border and the
/// maximized state go, every other bit stays, so leaving full screen puts the exact style back and a
/// maximized window is maximized again.</summary>
public static class FullscreenStyle
{
    public const long Caption = 0x00C00000;
    public const long SystemMenu = 0x00080000;
    public const long ThickFrame = 0x00040000;
    public const long MinimizeBox = 0x00020000;
    public const long MaximizeBox = 0x00010000;
    public const long Maximize = 0x01000000;
    public const long Minimize = 0x20000000;
    public const long Visible = 0x10000000;

    /// <summary>SWP_NOSIZE and SWP_NOMOVE: a SetWindowPos that keeps the size or the position.</summary>
    public const uint NoSize = 0x0001, NoMove = 0x0002;

    /// <summary>SWP_SHOWWINDOW: a SetWindowPos that shows the window.</summary>
    public const uint ShowWindow = 0x0040;

    /// <summary>WPF_RESTORETOMAXIMIZED: a minimized window restores maximized.</summary>
    public const uint RestoreToMaximized = 0x0002;

    /// <summary>The sizing border: the only frame bit full screen takes off. The caption stays, because
    /// without it the Windows App SDK destroys its caption buttons window and builds a new one when it
    /// comes back, and UI Automation can still call the old window's freed provider.</summary>
    public const long Frame = ThickFrame;

    /// <summary>The style of <paramref name="style"/>'s window while it covers its monitor. Windows
    /// keeps the taskbar over a maximized window whatever its rect, so the maximized bit goes too.</summary>
    public static long Borderless(long style) => style & ~(Frame | Maximize);

    /// <summary>The style to put back on leaving: the frame bits the window had before, maximized again
    /// if it was, and minimized if it is now (a minimized window cannot also be maximized).</summary>
    public static long Restored(long current, long before)
    {
        var kept = (current & ~(Frame | Maximize)) | (before & Frame);
        return (current & Minimize) != 0 ? kept : kept | (before & Maximize);
    }

    /// <summary>Whether a window with <paramref name="style"/> at <paramref name="window"/> is full
    /// screen on <paramref name="monitor"/>: no sizing border, neither maximized nor minimized, and
    /// exactly the monitor's rect, not its work area.</summary>
    public static bool Covers(long style, ScreenRect window, ScreenRect monitor)
        => (style & (Frame | Maximize | Minimize)) == 0 && window == monitor;

    /// <summary>How to enter full screen for a window that is <paramref name="minimizedNow"/> or not and
    /// <paramref name="foregroundNow"/> or not when the change is applied, which can be frames after it
    /// was asked for: the state then wins over the state when asked.</summary>
    public static FullscreenEntry Entry(bool minimizedNow, bool foregroundNow)
        => minimizedNow ? FullscreenEntry.OnRestore : foregroundNow ? FullscreenEntry.Cover : FullscreenEntry.CoverBehind;

    /// <summary>How to leave full screen for a window <paramref name="minimizedNow"/> or not whose
    /// style on entering was <paramref name="before"/>.</summary>
    public static FullscreenExit Exit(bool minimizedNow, long before)
        => minimizedNow ? FullscreenExit.SavedPlacement : (before & Maximize) != 0 ? FullscreenExit.Remaximize : FullscreenExit.MoveBack;

    /// <summary>The WINDOWPLACEMENT flags that make a window minimized in full screen restore the way
    /// it was on entering: maximized for a window with <paramref name="before"/>'s maximized bit.</summary>
    public static uint MinimizedPlacementFlags(long before) => (before & Maximize) != 0 ? RestoreToMaximized : 0;

    /// <summary>The SetWindowPos flags of a move of the window's content made to fill the full-screen
    /// window: its position and size always apply, whatever the move kept.</summary>
    public static uint FillingFlags(uint flags) => flags & ~(NoMove | NoSize);

    /// <summary>The SetWindowPos flags of a move of the caption buttons in full screen: they never show,
    /// whatever the Windows App SDK asked for.</summary>
    public static uint HiddenFlags(uint flags) => flags & ~ShowWindow;
}

/// <summary>The client area of a maximized window as Windows lays it out on any monitor.</summary>
public static class WindowFrame
{
    /// <summary>The client area <paramref name="computed"/> for a maximized window, started at the top
    /// of its monitor's <paramref name="work"/> area: the frame taken off the top can be the main
    /// monitor's, wider or narrower than the overhang on a monitor at another scale.</summary>
    public static ScreenRect MaximizedClient(ScreenRect computed, ScreenRect work) => computed with { Top = work.Top };
}
