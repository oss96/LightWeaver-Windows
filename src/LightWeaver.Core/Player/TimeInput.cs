using System.Globalization;

namespace LightWeaver.Player;

/// <summary>Parses positions as seconds, minutes:seconds or hours:minutes:seconds.</summary>
public static class TimeInput
{
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            total = total * 60 + value;
        }
        seconds = total;
        return true;
    }

    /// <summary>Where a typed position seeks: inside the file once its duration is known.</summary>
    public static double Target(double seconds, double duration) => duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
}
