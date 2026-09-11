using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LightWeaver.Theme;

/// <summary>
/// Turns a bound <c>ActualHeight</c> into the <see cref="CornerRadius"/> that makes an element
/// FULLY ROUND — a stadium when it is wider than tall, a circle when it is square.
/// <para>
/// Why this exists (Phase 10 M5, decided by measurement): a large constant such as
/// <c>CornerRadius="999"</c> does NOT produce a stadium in WPF. <c>Border</c> resolves corner
/// overlap per EDGE, so the horizontal and vertical arc radii are clamped independently —
/// x to width/2 and y to height/2 — and the corners become quarter-ellipses that meet in the
/// middle. Measured on the login screen's 510x60 primary button at radius 999: the top edge
/// curved continuously (only 24% of columns at the flat minimum, 28 px of sag at the ends)
/// instead of the 88% a 510x60 stadium requires.
/// </para>
/// <para>
/// Exactly <c>height/2</c> is the one value that makes both clamps bind at the same radius, so
/// the corners stay circular and the silhouette is a true stadium. Since the shared templates
/// serve consumers from 28 px to 44 px tall, that value cannot be a static token — hence the
/// binding. This stays inside the token/template layer: no Path, Geometry or per-element clip.
/// </para>
/// </summary>
public sealed class HalfHeightRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var h = System.Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture);
        if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0d) return new CornerRadius(0d);
        return new CornerRadius(h / 2d);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
