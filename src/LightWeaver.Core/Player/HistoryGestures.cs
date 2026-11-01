namespace LightWeaver.Player;

/// <summary>What a back or forward gesture (a mouse side button, Alt+Left or Alt+Right) does.</summary>
public enum HistoryAction
{
    /// <summary>Back or forward in the page history.</summary>
    Navigate,

    /// <summary>Nothing: the full player has no forward, or a grow or shrink is still running.</summary>
    Ignore,

    /// <summary>The mini-player goes back to the full player in the window, as Esc does.</summary>
    LeaveMiniPlayer,

    /// <summary>The full player is left the way its Back button leaves it.</summary>
    LeavePlayer,
}

/// <summary>Picks the <see cref="HistoryAction"/> of the back and forward gestures: the full player
/// takes them, so the page history behind it never moves unseen.</summary>
public static class HistoryGestures
{
    /// <summary>The back gesture.</summary>
    public static HistoryAction Back(bool playerFull, bool miniPlayer, bool transitionRunning)
        => !playerFull ? HistoryAction.Navigate
            : transitionRunning ? HistoryAction.Ignore
            : miniPlayer ? HistoryAction.LeaveMiniPlayer
            : HistoryAction.LeavePlayer;

    /// <summary>The forward gesture.</summary>
    public static HistoryAction Forward(bool playerFull) => playerFull ? HistoryAction.Ignore : HistoryAction.Navigate;
}
