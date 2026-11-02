namespace LightWeaver.Player;

/// <summary>The RTX video filters asked of a player: Super Resolution, RTX Video HDR and the HDR peak.</summary>
public readonly record struct RtxRequest(bool Vsr, bool Hdr, int MaxLumaNits)
{
    /// <summary>True when both ask mpv for the same filter chain; the peak counts only while HDR is on.</summary>
    public bool SameFilterAs(RtxRequest other)
        => Vsr == other.Vsr && Hdr == other.Hdr && (!Hdr || MaxLumaNits == other.MaxLumaNits);
}
