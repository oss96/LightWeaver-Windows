namespace LightWeaver.Player;

/// <summary>Places rects of the window's content on the overlay of the borderless full screen, a
/// window that covers the same monitor at the same scale.</summary>
public static class ScreenPlacement
{
    /// <summary><paramref name="rect"/>, in DIPs of content whose top-left is at <paramref name="content"/>
    /// on screen, in DIPs of a window covering <paramref name="monitor"/>; both at <paramref name="scale"/>.</summary>
    public static LayerRect OnMonitor(LayerRect rect, ScreenRect content, ScreenRect monitor, double scale)
        => rect with { X = rect.X + (content.Left - monitor.Left) / scale, Y = rect.Y + (content.Top - monitor.Top) / scale };

    /// <summary><paramref name="rect"/> measured from the right edge of right-to-left content
    /// <paramref name="width"/> wide, as measured from its left edge.</summary>
    public static LayerRect Mirrored(LayerRect rect, double width) => rect with { X = width - rect.X - rect.Width };

    /// <summary>The whole of <paramref name="monitor"/> in DIPs of a window covering it at <paramref name="scale"/>.</summary>
    public static LayerRect Whole(ScreenRect monitor, double scale) => monitor.InDips(scale);
}
