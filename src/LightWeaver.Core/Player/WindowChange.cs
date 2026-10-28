namespace LightWeaver.Player;

/// <summary>The rules of a window change made after its layout (entering and leaving full screen):
/// the content is laid out at the new size first, the window changes two frames later, and the
/// content keeps its explicit size until XAML reports the window's new size.</summary>
public static class WindowChange
{
    /// <summary>Frames the new layout is given to reach the screen before the window changes.</summary>
    public const int LayoutFrames = 2;

    /// <summary>How long after the window changed the content waits for XAML to take the new size.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether content held at <paramref name="target"/> can be let go: XAML's window is that
    /// size, to a DIP, or <see cref="CatchUp"/> has passed <paramref name="sinceChange"/>.</summary>
    public static bool CanRelease(double xamlWidth, double xamlHeight, LayerRect target, TimeSpan sinceChange)
        => (Math.Abs(xamlWidth - target.Width) < 1 && Math.Abs(xamlHeight - target.Height) < 1) || sinceChange >= CatchUp;

    /// <summary>Whether the title bar's caption-button inset is one the top bar takes: not while the
    /// window is full screen, which has no caption buttons, and never zero, which is what the title
    /// bar reports in the frames after full screen before it measures its buttons again.</summary>
    public static bool TakesCaptionInset(bool fullscreen, double inset) => !fullscreen && inset > 0;
}
