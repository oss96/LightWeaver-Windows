namespace LightWeaver.Player;

/// <summary>
/// Curated language list for the preferred-language settings dropdowns, plus the
/// equivalence match the auto-select and detail-view preseed use. Preferences are
/// stored as ISO 639-2/T codes ("deu"), but containers often carry bibliographic
/// ("ger") or two-letter ("de") tags — Matches treats them as the same language.
/// </summary>
internal static class Languages
{
    public sealed record Option(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>"None" (empty code = no preference) followed by common languages.</summary>
    public static readonly IReadOnlyList<Option> Choices =
    [
        new("", "None"),
        new("eng", "English (eng)"),
        new("deu", "German (deu)"),
        new("jpn", "Japanese (jpn)"),
        new("fra", "French (fra)"),
        new("spa", "Spanish (spa)"),
        new("ita", "Italian (ita)"),
        new("por", "Portuguese (por)"),
        new("nld", "Dutch (nld)"),
        new("pol", "Polish (pol)"),
        new("rus", "Russian (rus)"),
        new("ukr", "Ukrainian (ukr)"),
        new("tur", "Turkish (tur)"),
        new("ara", "Arabic (ara)"),
        new("zho", "Chinese (zho)"),
        new("kor", "Korean (kor)"),
        new("hin", "Hindi (hin)"),
        new("swe", "Swedish (swe)"),
        new("nor", "Norwegian (nor)"),
        new("dan", "Danish (dan)"),
        new("fin", "Finnish (fin)"),
        new("ces", "Czech (ces)"),
        new("hun", "Hungarian (hun)"),
        new("ell", "Greek (ell)"),
        new("heb", "Hebrew (heb)"),
        new("tha", "Thai (tha)"),
        new("vie", "Vietnamese (vie)"),
        new("ind", "Indonesian (ind)"),
        new("ron", "Romanian (ron)"),
    ];

    /// <summary>True when a track's language tag denotes the preferred language.
    /// Canonicalizes both sides via LanguageBadge (two-letter form, bibliographic
    /// twins included); unmappable tags fall back to the old prefix comparison so
    /// hand-typed legacy preferences keep working.</summary>
    public static bool Matches(string? trackLang, string? preference)
    {
        if (trackLang is not { Length: > 0 } || preference is not { Length: > 0 })
            return false;
        var track = LanguageBadge.For(trackLang);
        var pref = LanguageBadge.For(preference);
        if (track.Length > 0 && pref.Length > 0)
            return track == pref;
        return trackLang.StartsWith(preference, StringComparison.OrdinalIgnoreCase);
    }
}
