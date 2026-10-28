namespace LightWeaver.Player;

/// <summary>A rectangle in the window, in DIPs.</summary>
public readonly record struct LayerRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    /// <summary>Finite, with a positive width and height: a rect a transition can start from or land on.</summary>
    public bool IsUsable => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

/// <summary>How the layer's picture fills its rect: fitted by <see cref="Aspect"/> (letterboxed),
/// covering it (<see cref="Covers"/>), or stretched over it when neither is known.</summary>
public readonly record struct LayerContent(double? Aspect, bool Covers = false)
{
    public static readonly LayerContent Stretched = new(null);
}

/// <summary>The compositor transform that shows a layer laid out at one rect as if it were laid out
/// at another: a scale about the layout's centre, a translation of that centre, and a clip in the
/// layer's own coordinates with corners that stay <c>radius</c> on screen.</summary>
public readonly record struct LayerFrame(double ScaleX, double ScaleY, double TranslateX, double TranslateY,
    double ClipLeft, double ClipTop, double ClipRight, double ClipBottom, double RadiusX, double RadiusY)
{
    public double ClipWidth => ClipRight - ClipLeft;
    public double ClipHeight => ClipBottom - ClipTop;

    /// <summary>Values the compositor takes: all finite, a positive scale and clip, and corners from
    /// 0 to half the clip.</summary>
    public bool IsValid => new[] { ScaleX, ScaleY, TranslateX, TranslateY, ClipLeft, ClipTop, ClipRight, ClipBottom, RadiusX, RadiusY }.All(double.IsFinite)
        && ScaleX > 0 && ScaleY > 0 && ClipWidth > 0 && ClipHeight > 0
        && RadiusX >= 0 && RadiusY >= 0 && RadiusX <= ClipWidth / 2 && RadiusY <= ClipHeight / 2;
}

/// <summary>The keyframes of the rest of an animation: <see cref="Times"/>[i], a share of the whole
/// animation, is shown at <see cref="Keys"/>[i] of <see cref="Remaining"/>. The keys run from exactly
/// 0 to exactly 1, strictly increasing.</summary>
public sealed record MotionPlan(IReadOnlyList<double> Times, IReadOnlyList<float> Keys, TimeSpan Remaining);

/// <summary>The player's grow and shrink (design 13): all four edges move together from one rect to
/// another over 180 ms, cubic ease-out, and the last frame is the target exactly.</summary>
public static class LayerMotion
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);

    /// <summary>Keyframes per animation: one every 3 ms of the 180 ms, so the straight lines the
    /// compositor draws between them stay within half a pixel of the curve.</summary>
    public const int Samples = 60;

    /// <summary>The share of the way covered at <paramref name="t"/> (0 to 1) of the time.</summary>
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    /// <summary>The rect at <paramref name="t"/> of the time; the target itself once the time is up.</summary>
    public static LayerRect At(LayerRect from, LayerRect to, double t)
    {
        if (t >= 1) return to;
        var k = Ease(t);
        static double Lerp(double a, double b, double k) => a + (b - a) * k;
        return new(Lerp(from.X, to.X, k), Lerp(from.Y, to.Y, k), Lerp(from.Width, to.Width, k), Lerp(from.Height, to.Height, k));
    }

    /// <summary>The times, as shares of the whole animation, at which the rest of an animation already
    /// <paramref name="startT"/> of the way through is sampled: <paramref name="count"/> + 1 evenly
    /// spaced points from <paramref name="startT"/> to 1.</summary>
    public static IReadOnlyList<double> SampleTimes(double startT, int count = Samples)
    {
        startT = Math.Clamp(startT, 0, 1);
        count = Math.Max(1, count);
        var times = new double[count + 1];
        for (var i = 0; i <= count; i++) times[i] = i == count ? 1 : startT + (1 - startT) * i / count;
        return times;
    }

    /// <summary>Less time left than about one 60 Hz frame: the end state is set at once instead of animated.</summary>
    public static readonly TimeSpan SnapBelow = TimeSpan.FromMilliseconds(16);

    /// <summary>The keyframes for the rest of an animation of <paramref name="duration"/> already
    /// <paramref name="startT"/> of the way through, in <paramref name="count"/> steps. Null when less
    /// than <see cref="SnapBelow"/> is left or the time is not a number: the caller sets the end state.</summary>
    public static MotionPlan? Plan(double startT, TimeSpan duration, int count = Samples)
    {
        if (double.IsNaN(startT)) return null;
        startT = Math.Clamp(startT, 0, 1);
        var remaining = duration * (1 - startT);
        if (remaining < SnapBelow) return null;
        count = Math.Max(1, count);
        var keys = new float[count + 1];
        for (var i = 0; i <= count; i++) keys[i] = i == count ? 1f : (float)i / count;
        return new MotionPlan(SampleTimes(startT, count), keys, remaining);
    }

    /// <summary>How the layer laid out at <paramref name="layout"/> is drawn to look laid out at
    /// <paramref name="shown"/>: <paramref name="content"/> appears the size and place it would have
    /// there, and the clip cuts the layer to <paramref name="shown"/> with corners of
    /// <paramref name="radius"/> on screen, at most half the rect. Identity when both rects are the same.</summary>
    public static LayerFrame Frame(LayerRect shown, LayerRect layout, LayerContent content, double radius)
    {
        var lw = Math.Max(1, layout.Width);
        var lh = Math.Max(1, layout.Height);
        var sw = Math.Max(1, shown.Width);
        var sh = Math.Max(1, shown.Height);
        double sx, sy;
        if (content.Aspect is { } aspect && aspect > 0)
            sx = sy = content.Covers
                ? Math.Max(sw, sh * aspect) / Math.Max(lw, lh * aspect)
                : Math.Min(sw, sh * aspect) / Math.Min(lw, lh * aspect);
        else if (content.Covers)
            sx = sy = Math.Max(sw / lw, sh / lh);
        else
            (sx, sy) = (sw / lw, sh / lh);
        var halfW = sw / sx / 2;
        var halfH = sh / sy / 2;
        var (left, top, right, bottom) = (lw / 2 - halfW, lh / 2 - halfH, lw / 2 + halfW, lh / 2 + halfH);
        var corner = double.IsFinite(radius) ? Math.Max(0, radius) : 0;
        return new LayerFrame(sx, sy, shown.CenterX - layout.CenterX, shown.CenterY - layout.CenterY,
            left, top, right, bottom, Math.Min(corner / sx, (right - left) / 2), Math.Min(corner / sy, (bottom - top) / 2));
    }
}
