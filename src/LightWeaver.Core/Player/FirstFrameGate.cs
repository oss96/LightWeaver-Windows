namespace LightWeaver.Player;

/// <summary>Keeps the video hidden from the moment a new title is asked for until mpv has shown
/// the first frame of a load made after that moment, so no frame of the previous file is seen.</summary>
public sealed class FirstFrameGate
{
    private int? _heldAt;

    /// <summary>Whether the video is hidden, waiting for a first frame.</summary>
    public bool IsHeld => _heldAt is not null;

    /// <summary>Hides the video until a load newer than <paramref name="liveGeneration"/> shows a frame.</summary>
    public void Hold(int liveGeneration) => _heldAt = liveGeneration;

    /// <summary>mpv restarted playback of load <paramref name="eventGeneration"/>; true (and open)
    /// when it is the load being waited for and still the newest one.</summary>
    public bool TryRelease(int eventGeneration, int liveGeneration)
    {
        if (_heldAt is not { } heldAt || eventGeneration <= heldAt || eventGeneration != liveGeneration) return false;
        _heldAt = null;
        return true;
    }

    public void Clear() => _heldAt = null;
}
