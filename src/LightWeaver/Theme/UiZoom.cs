using System.Windows;
using System.Windows.Media;

namespace LightWeaver.Theme;

/// <summary>Applies the shared app zoom to WPF controls.</summary>
public static partial class UiZoom
{
    public const double MinLayoutWidth = 684;
    public const double MinLayoutHeight = 441;

    public static ScaleTransform Transform { get; } = new(1.0, 1.0);

    public static ScaleTransform Snapshot()
    {
        var copy = new ScaleTransform(Scale, Scale);
        copy.Freeze();
        return copy;
    }

    public static double CapFor(Size client) => CapFor(client.Width, client.Height, MinLayoutWidth, MinLayoutHeight);

    static partial void Applied(double scale)
    {
        Transform.ScaleX = scale;
        Transform.ScaleY = scale;
    }
}
