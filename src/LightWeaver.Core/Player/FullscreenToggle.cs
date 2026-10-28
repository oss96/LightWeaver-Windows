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
}

/// <summary>Picks the toggle's <see cref="FullscreenToggle"/>.</summary>
public static class FullscreenIntent
{
    /// <summary>The toggle while a grow into full screen runs (<paramref name="growingIntoFullscreen"/>),
    /// or by the window's state once nothing is heading there.</summary>
    public static FullscreenToggle Toggle(bool growingIntoFullscreen, bool osFullscreen)
        => growingIntoFullscreen ? FullscreenToggle.CancelGrow : osFullscreen ? FullscreenToggle.Exit : FullscreenToggle.Enter;
}
