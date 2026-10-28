namespace LightWeaver.Theme;

/// <summary>The zoom range, free of WPF so the settings model can bound a saved level in either
/// app.</summary>
public static partial class UiZoom
{
    public const double Min = 0.5;
    public const double Max = 2.0;

    /// <summary>Clamps out-of-range values (an edited settings file) into the ladder's range.</summary>
    public static double Clamp(double scale)
        => double.IsFinite(scale) ? Math.Clamp(scale, Min, Max) : 1.0;

    public static bool IsZoomChord(string? chord)
    {
        if (chord is null) return false;
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && parts.Contains("Ctrl", StringComparer.OrdinalIgnoreCase)
            && parts[..^1].All(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            && parts[^1] is "OemPlus" or "Add" or "OemMinus" or "Subtract" or "D0" or "NumPad0";
    }

    public static int ZoomKey(int virtualKey, bool control, bool alt)
        => !control || alt ? int.MinValue : virtualKey switch
        {
            0xBB or 0x6B => 1,
            0xBD or 0x6D => -1,
            0x30 or 0x60 => 0,
            _ => int.MinValue,
        };
}
