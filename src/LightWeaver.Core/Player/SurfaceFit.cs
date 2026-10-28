namespace LightWeaver.Player;

/// <summary>The scale and offset that place a picture mpv drew into a buffer of one size where mpv
/// would draw it on a surface of another: centred and fitted by the video's shape.</summary>
public readonly record struct SurfaceFit(double ScaleX, double ScaleY, double TranslateX, double TranslateY)
{
    /// <summary>The fit of a <paramref name="bufferWidth"/> x <paramref name="bufferHeight"/> buffer on
    /// a <paramref name="width"/> x <paramref name="height"/> surface; without a known
    /// <paramref name="aspect"/> the buffer is stretched over the surface.</summary>
    public static SurfaceFit Of(double width, double height, double bufferWidth, double bufferHeight, double? aspect)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (aspect is not { } shape)
            return new(width / bufferWidth, height / bufferHeight, 0, 0);
        var scale = Math.Min(width, height * shape) / Math.Min(bufferWidth, bufferHeight * shape);
        return new(scale, scale, (width - bufferWidth * scale) / 2, (height - bufferHeight * scale) / 2);
    }
}
