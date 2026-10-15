using System.Windows;
using System.Windows.Media;

namespace LightWeaver.Theme;

/// <summary>
/// App-wide UI zoom (Ctrl+Plus / Ctrl+Minus / Ctrl+wheel, Ctrl+0 resets). One shared
/// <see cref="ScaleTransform"/> is set as the <c>LayoutTransform</c> of every zoomable WPF root, so
/// a zoom step re-lays out the whole UI — cards, artwork, icons and text grow together, and
/// wrapping panels re-flow to the smaller logical width — instead of only the fonts changing.
///
/// The mpv video HWND is never under this transform: it owns its own window, and scaling it
/// is meaningless. Only the browse layer, the in-window chrome above it, the player overlay
/// and the app's own popup windows carry it. Popups, tooltips and context menus are kept at
/// it by <see cref="ZoomPopups"/>.
///
/// While <see cref="LimitToScreen"/> is on (the default) the applied scale stops at the largest
/// step the window's screen can show the whole UI at (<see cref="CapFor"/>). The level the user
/// asked for is kept as <see cref="Preferred"/>, so moving the window to a smaller screen lowers
/// the zoom and moving it back restores it.
/// </summary>
public static class UiZoom
{
    /// <summary>The ladder a step walks, browser-style. 1.0 must stay in it so Ctrl+0 and a
    /// round trip of steps land exactly on 100%.</summary>
    private static readonly double[] Steps = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public const double Min = 0.5;
    public const double Max = 2.0;

    /// <summary>The smallest client area, in layout units, the zoomed UI may get: the client of
    /// the 700x480 minimum window at 100% (MainWindow.xaml MinWidth/MinHeight minus a Windows 11
    /// frame). Zoom never makes the app more cramped than its own smallest window.
    ///
    /// Chosen from the lw-ui-zoom screen survey (UiZoomScreenFixture). Every expected control
    /// stays present, uncut and clickable down to about 632x340, but the screenshot review of
    /// layouts below this size found them cramped: the header title trimmed to one letter, rail
    /// items scrolled out of sight, Settings showing a single row. The survey still asserts that
    /// the cap never exceeds the level where every control passes.</summary>
    public const double MinLayoutWidth = 684;
    public const double MinLayoutHeight = 441;

    /// <summary>The shared transform. Mutated in place, never replaced, so every element that
    /// holds it follows a change without being told.</summary>
    public static ScaleTransform Transform { get; } = new(1.0, 1.0);

    /// <summary>The applied scale: <see cref="Preferred"/>, bounded by <see cref="Cap"/>.</summary>
    public static double Scale => Transform.ScaleX;

    /// <summary>The level the user chose, which is what settings.json keeps.</summary>
    public static double Preferred { get; private set; } = 1.0;

    /// <summary>Largest scale the window's current screen shows the whole UI at (<see cref="CapFor"/>).
    /// <see cref="Max"/> until the window knows its screen.</summary>
    public static double ScreenCap { get; private set; } = Max;

    /// <summary>The "Limit zoom to the screen size" setting. Off, the ladder runs to <see cref="Max"/>
    /// on any screen, and controls can end up cut off or out of reach.</summary>
    public static bool LimitToScreen { get; private set; } = true;

    public static double Cap => LimitToScreen ? ScreenCap : Max;

    /// <summary>True when the last request was a step in that the screen limit, not the ladder,
    /// stopped. The readout says so, or a Ctrl+Plus that does nothing would look broken. A step
    /// out or a reset never reports it, even when it lands on the cap.</summary>
    public static bool AtScreenLimit { get; private set; }

    /// <summary>A frozen copy of the current scale, for a short-lived window. The shared
    /// <see cref="Transform"/> keeps a reference to every element that holds it, so handing it to
    /// a window that closes would keep that window's tree alive for the life of the app.</summary>
    public static ScaleTransform Snapshot()
    {
        var copy = new ScaleTransform(Scale, Scale);
        copy.Freeze();
        return copy;
    }

    /// <summary>Raised after the scale changed, with the new value.</summary>
    public static event Action<double>? Changed;

    /// <summary>Raised after <see cref="Preferred"/> changed, whether or not the applied scale
    /// moved: Ctrl+0 under a 100% cap resets a saved 200% without changing what is on screen.</summary>
    public static event Action? PreferredChanged;

    /// <summary>Raised when <see cref="LimitToScreen"/> is switched, whether or not the scale moved.</summary>
    public static event Action? LimitChanged;

    /// <summary>Raised for every zoom request, including one refused at either end of the ladder,
    /// so the readout can confirm "200%" to a user still pressing Ctrl+Plus.</summary>
    public static event Action? Requested;

    /// <summary>Wheel delta not yet worth a step. A precision touchpad's pinch arrives as a stream
    /// of Ctrl+wheel events with small deltas, as does a high-resolution wheel; one step per event
    /// would jump a gentle pinch straight to either end.</summary>
    private static int _wheelRemainder;

    /// <summary>Whole-number percentage for display ("125").</summary>
    public static int Percent => (int)Math.Round(Scale * 100);

    /// <summary>Clamps out-of-range values (an edited settings file) into the ladder's range.</summary>
    public static double Clamp(double scale)
        => double.IsFinite(scale) ? Math.Clamp(scale, Min, Max) : 1.0;

    /// <summary>Sets the user's level; the applied scale is it, bounded by <see cref="Cap"/>.</summary>
    public static void Set(double scale)
    {
        var preferred = Clamp(scale);
        var changed = Math.Abs(preferred - Preferred) >= 0.001;
        Preferred = preferred;
        Apply();
        if (changed)
            PreferredChanged?.Invoke();
    }

    /// <summary>The window's screen changed (moved, resized taskbar, new resolution or DPI).</summary>
    public static void SetScreenCap(double cap)
    {
        ScreenCap = Math.Clamp(double.IsFinite(cap) ? cap : Max, 1.0, Max);
        Apply();
    }

    public static void SetLimitToScreen(bool limit)
    {
        if (limit == LimitToScreen)
            return;
        LimitToScreen = limit;
        Apply();
        LimitChanged?.Invoke();
    }

    /// <summary>The largest ladder step at which a window client of <paramref name="client"/>
    /// (layout units at 100%) still holds <see cref="MinLayoutWidth"/> x <see cref="MinLayoutHeight"/>.
    /// Never below 100%: a screen too small for the UI at 100% is not made worse by zooming out.</summary>
    public static double CapFor(Size client)
        => Steps.LastOrDefault(s => s >= 1.0 && client.Width >= MinLayoutWidth * s
            && client.Height >= MinLayoutHeight * s, 1.0);

    private static void Apply()
    {
        var scale = Math.Min(Preferred, Cap);
        if (Math.Abs(scale - Scale) < 0.001)
            return;
        Transform.ScaleX = scale;
        Transform.ScaleY = scale;
        Changed?.Invoke(scale);
    }

    public static void StepIn()
    {
        // At the cap a step in changes nothing, and must not raise Preferred past it either:
        // the next step out would otherwise start from a level the user never saw.
        if (Scale < Cap - 0.001)
            Set(Math.Min(Steps.FirstOrDefault(s => s > Scale + 0.001, Max), Cap));
        AtScreenLimit = LimitToScreen && ScreenCap < Max - 0.001 && Scale >= ScreenCap - 0.001;
        Requested?.Invoke();
    }

    public static void StepOut()
    {
        Set(Steps.LastOrDefault(s => s < Scale - 0.001, Min));
        AtScreenLimit = false;
        Requested?.Invoke();
    }

    public static void Reset()
    {
        Set(1.0);
        AtScreenLimit = false;
        Requested?.Invoke();
    }

    /// <summary>Ctrl+wheel: one step per notch (120), whatever size the events arrive in.
    /// Reversing direction discards the remainder, so a pinch that turns back is not owed a step.</summary>
    public static void Wheel(int delta)
    {
        if (delta == 0)
            return;
        if (Math.Sign(delta) != Math.Sign(_wheelRemainder))
            _wheelRemainder = 0;
        _wheelRemainder += delta;
        while (Math.Abs(_wheelRemainder) >= 120)
        {
            if (_wheelRemainder > 0) { _wheelRemainder -= 120; StepIn(); }
            else { _wheelRemainder += 120; StepOut(); }
        }
    }

    /// <summary>A length in window pixels expressed in the zoomed layout's units — what a
    /// window-width breakpoint has to compare against, because the content inside the
    /// transform only gets <c>width / scale</c> to lay out in.</summary>
    public static double ToLayout(double windowLength) => windowLength / Scale;
}
