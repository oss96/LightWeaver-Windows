using System.Globalization;

namespace LightWeaver.Player;

/// <summary>
/// Maps a track's language tag (ISO 639-1/-2, optionally with a region suffix) to a
/// two-letter uppercase badge for the overlay track popups ("deu"/"ger" → "DE").
/// Unknown or undetermined languages get no badge (empty string).
/// </summary>
internal static class LanguageBadge
{
    // ISO 639-2 bibliographic codes .NET cultures don't carry (containers, notably
    // Matroska, commonly store these instead of the terminology codes).
    private static readonly Dictionary<string, string> Bibliographic = new()
    {
        ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu", ["bur"] = "my", ["chi"] = "zh",
        ["cze"] = "cs", ["dut"] = "nl", ["fre"] = "fr", ["geo"] = "ka", ["ger"] = "de",
        ["gre"] = "el", ["ice"] = "is", ["mac"] = "mk", ["mao"] = "mi", ["may"] = "ms",
        ["per"] = "fa", ["rum"] = "ro", ["slo"] = "sk", ["tib"] = "bo", ["wel"] = "cy",
    };

    private static readonly Lazy<Dictionary<string, string>> ThreeToTwo = new(() =>
    {
        var map = new Dictionary<string, string>();
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.ThreeLetterISOLanguageName is { Length: 3 } three
                && culture.TwoLetterISOLanguageName is { Length: 2 } two)
                map.TryAdd(three, two);
        }
        return map;
    });

    /// <summary>Badge for a language tag, or "" when there is nothing sensible to show.</summary>
    public static string For(string? lang)
    {
        if (string.IsNullOrEmpty(lang))
            return "";
        var code = lang.Split('-', '_')[0].Trim().ToLowerInvariant();
        switch (code.Length)
        {
            case 2 when IsLetters(code):
                return code.ToUpperInvariant();
            case 3 when code is not ("und" or "mul" or "zxx" or "mis"):
                if (Bibliographic.TryGetValue(code, out var bib))
                    return bib.ToUpperInvariant();
                if (ThreeToTwo.Value.TryGetValue(code, out var two))
                    return two.ToUpperInvariant();
                return "";
            default:
                return "";
        }
    }

    private static bool IsLetters(string s) => s.All(char.IsAsciiLetterLower);
}
