namespace LightWeaver.Player;

/// <summary>Where mpv puts text subtitles on a surface that shows its buffer: in the letterbox bands
/// (<see cref="UseMargins"/>) while the surface crops less of them than the subtitle margin, else on
/// the picture, which is always shown whole; <see cref="Position"/> is the mpv sub-pos. An ASS
/// track's own margins are unknown here, so any crop puts it on the picture.</summary>
public readonly record struct SubtitlePlacement(bool UseMargins, double Position)
{
    /// <summary>mpv's default sub-margin-y over its 720-line reference: libass keeps a subtitle this
    /// share of the picture's height away from the band's outer edge.</summary>
    public const double MarginShare = 22.0 / 720;

    /// <summary>On a surface that shows the whole buffer: in the bands, at the user's
    /// <paramref name="basePosition"/>.</summary>
    public static SubtitlePlacement Uncropped(int basePosition) => new(true, Math.Clamp(basePosition, 0, 100));

    /// <summary>The placement on a <paramref name="height"/> tall surface showing a
    /// <paramref name="bufferWidth"/> x <paramref name="bufferHeight"/> buffer, all in physical pixels, under
    /// <paramref name="fit"/>. libass puts a bottom subtitle at sub-pos percent of its distance from
    /// the buffer's top, so the user's <paramref name="basePosition"/> is taken across the shown part.</summary>
    public static SubtitlePlacement For(SurfaceFit fit, double height, double bufferWidth, double bufferHeight, double? aspect, int basePosition, bool authoredMargins = false)
    {
        var resting = Uncropped(basePosition);
        if (bufferHeight <= 0 || fit.ScaleY <= 0) return resting;
        var top = Math.Clamp(-fit.TranslateY / fit.ScaleY / bufferHeight, 0, 1);
        var bottom = Math.Clamp((height - fit.TranslateY) / fit.ScaleY / bufferHeight, 0, 1);
        var picture = aspect is > 0 and var shape ? Math.Min(1, bufferWidth / shape / bufferHeight) : 1;
        var croppedRows = top * bufferHeight;
        if (croppedRows > (authoredMargins ? 0.5 : MarginShare * picture * bufferHeight)) return resting with { UseMargins = false };
        return resting with { Position = Math.Round(100 * top + (bottom - top) * resting.Position, 1) };
    }

    /// <summary>True when the selected subtitle track is ASS or SSA, whose styles set their own margins.</summary>
    public static bool HasAuthoredMargins(IEnumerable<MpvTrack> tracks)
        => tracks.Any(t => t is { Type: "sub", Selected: true, Codec: "ass" or "ssa" });
}
