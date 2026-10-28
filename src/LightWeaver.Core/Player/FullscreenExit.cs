namespace LightWeaver.Player;

/// <summary>How a window leaves full screen, by the state it had on entering and the one it has now.</summary>
public enum FullscreenExit
{
    /// <summary>Not minimized, and restored or snapped on entering: back to the exact rect it had, in one move.</summary>
    MoveBack,

    /// <summary>Not minimized, and maximized on entering: maximized again at the exact rect it had, in
    /// one move, with the restored rect it had before full screen.</summary>
    Remaximize,

    /// <summary>Minimized: covering the monitor became its restored rect, so the placement saved on
    /// entering goes back while it stays minimized, and it restores maximized if it was.</summary>
    SavedPlacement,
}

/// <summary>How a window enters full screen, by its state when the change is applied.</summary>
public enum FullscreenEntry
{
    /// <summary>In the foreground: covers the monitor at the top of the z-order.</summary>
    Cover,

    /// <summary>Another window took the foreground meanwhile: covers the monitor where it is in the
    /// z-order, without activating it.</summary>
    CoverBehind,

    /// <summary>Minimized meanwhile: stays minimized, and covers the monitor once it is restored.</summary>
    OnRestore,
}
