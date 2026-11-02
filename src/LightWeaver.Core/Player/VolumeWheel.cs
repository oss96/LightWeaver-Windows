namespace LightWeaver.Player;

/// <summary>Turns wheel deltas into volume steps, one per 120-unit notch, so a touchpad's small
/// deltas add up to a notch instead of each moving the volume.</summary>
public sealed class VolumeWheel
{
    private const int Notch = 120;
    private int _remainder;

    /// <summary>Adds a wheel delta and returns the notches it completes: positive up, negative down.</summary>
    public int Steps(int delta)
    {
        if (delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_remainder)) _remainder = 0;
        _remainder += delta;
        var steps = _remainder / Notch;
        _remainder -= steps * Notch;
        return steps;
    }
}
