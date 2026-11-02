namespace LightWeaver.Theme;

/// <summary>Shared zoom steps, preferences and screen limits.</summary>
public static partial class UiZoom
{
    private static readonly double[] Steps = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public static double Scale { get; private set; } = 1.0;

    public static double Preferred { get; private set; } = 1.0;

    public static double ScreenCap { get; private set; } = Max;

    public static bool LimitToScreen { get; private set; } = true;

    public static double Cap => LimitToScreen ? ScreenCap : Max;

    public static bool AtScreenLimit { get; private set; }

    public static event Action<double>? Changed;

    public static event Action? PreferredChanged;

    public static event Action? LimitChanged;

    public static event Action? Requested;

    private static int _wheelRemainder;

    public static int Percent => (int)Math.Round(Scale * 100);

    public static void Set(double scale)
    {
        var preferred = Clamp(scale);
        var changed = Math.Abs(preferred - Preferred) >= 0.001;
        Preferred = preferred;
        Apply();
        if (changed)
            PreferredChanged?.Invoke();
    }

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

    public static double CapFor(double width, double height, double minWidth, double minHeight)
        => Steps.LastOrDefault(s => s >= 1.0 && width >= minWidth * s && height >= minHeight * s, 1.0);

    static partial void Applied(double scale);

    private static void Apply()
    {
        var scale = Math.Min(Preferred, Cap);
        if (Math.Abs(scale - Scale) < 0.001)
            return;
        Scale = scale;
        Applied(scale);
        Changed?.Invoke(scale);
    }

    public static void StepIn()
    {
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
        _wheelRemainder = 0;
        Set(1.0);
        AtScreenLimit = false;
        Requested?.Invoke();
    }

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

    public static double ToLayout(double windowLength) => windowLength / Scale;
}
