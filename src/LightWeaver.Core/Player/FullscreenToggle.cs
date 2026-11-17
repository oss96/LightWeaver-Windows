namespace LightWeaver.Player;

/// <summary>What the full screen toggle does: it goes by where the player is heading, not where it is.</summary>
public enum FullscreenToggle
{
    /// <summary>A grow into full screen is still running: it ends in the window instead.</summary>
    CancelGrow,

    /// <summary>In full screen: leave it.</summary>
    Exit,

    /// <summary>Neither in full screen nor heading there: enter it.</summary>
    Enter,

    /// <summary>A borderless move is past its grow (the window changing under it, or a shrink): the
    /// toggle waits for it to end.</summary>
    Ignore,
}

/// <summary>What Esc does in the player once its flyouts are closed.</summary>
public enum PlayerEscape
{
    /// <summary>A borderless grow into full screen is still running: it goes back to the player.</summary>
    CancelGrow,

    /// <summary>In full screen: leave it.</summary>
    ExitFullscreen,

    /// <summary>In the window: leave the player.</summary>
    Leave,
}

/// <summary>Picks the toggle's <see cref="FullscreenToggle"/> and Esc's <see cref="PlayerEscape"/>.</summary>
public static class FullscreenIntent
{
    /// <summary>The toggle while a grow into full screen runs (<paramref name="growingIntoFullscreen"/>),
    /// while a borderless move is past its grow (<paramref name="screenBusy"/>), or by the window's
    /// state once nothing is heading anywhere.</summary>
    public static FullscreenToggle Toggle(bool growingIntoFullscreen, bool osFullscreen, bool screenBusy = false)
        => growingIntoFullscreen ? FullscreenToggle.CancelGrow
            : screenBusy ? FullscreenToggle.Ignore
            : osFullscreen ? FullscreenToggle.Exit : FullscreenToggle.Enter;

    /// <summary>Esc while a borderless grow runs (<paramref name="growingOnScreen"/>), or by the window's state.</summary>
    public static PlayerEscape Escape(bool growingOnScreen, bool osFullscreen)
        => growingOnScreen ? PlayerEscape.CancelGrow : osFullscreen ? PlayerEscape.ExitFullscreen : PlayerEscape.Leave;
}
