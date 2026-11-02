using LightWeaver.Localization;
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
    public static IReadOnlyList<Option> Choices =>
    [
        new("", L.Text("Languages.None.9a0dbd36")),
        new("eng", L.Text("Languages.EnglishEng.88a1521d")),
        new("deu", L.Text("Languages.GermanDeu.da9ad6a6")),
        new("jpn", L.Text("Languages.JapaneseJpn.fc9df014")),
        new("fra", L.Text("Languages.FrenchFra.d4348786")),
        new("spa", L.Text("Languages.SpanishSpa.58b3d5f6")),
        new("ita", L.Text("Languages.ItalianIta.065e69bd")),
        new("por", L.Text("Languages.PortuguesePor.9d7efad5")),
        new("nld", L.Text("Languages.DutchNld.a573043f")),
        new("pol", L.Text("Languages.PolishPol.472d4009")),
        new("rus", L.Text("Languages.RussianRus.6600cfa3")),
        new("ukr", L.Text("Languages.UkrainianUkr.68f307d8")),
        new("tur", L.Text("Languages.TurkishTur.e55cdf2d")),
        new("ara", L.Text("Languages.ArabicAra.912229fb")),
        new("zho", L.Text("Languages.ChineseZho.094b43f6")),
        new("kor", L.Text("Languages.KoreanKor.c60636e0")),
        new("hin", L.Text("Languages.HindiHin.5af93b54")),
        new("swe", L.Text("Languages.SwedishSwe.e931a51d")),
        new("nor", L.Text("Languages.NorwegianNor.0f8a47d7")),
        new("dan", L.Text("Languages.DanishDan.c4b1fe89")),
        new("fin", L.Text("Languages.FinnishFin.b0dd47e0")),
        new("ces", L.Text("Languages.CzechCes.7b8e68b3")),
        new("hun", L.Text("Languages.HungarianHun.de895eca")),
        new("ell", L.Text("Languages.GreekEll.a13487aa")),
        new("heb", L.Text("Languages.HebrewHeb.c46022b1")),
        new("tha", L.Text("Languages.ThaiTha.fee6aea0")),
        new("vie", L.Text("Languages.VietnameseVie.ad7c7e7d")),
        new("ind", L.Text("Languages.IndonesianInd.4e58498f")),
        new("ron", L.Text("Languages.RomanianRon.ce6d326b")),
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
