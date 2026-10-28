using System.Globalization;
using LightWeaver.Localization;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    checks++;
}

var machineCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.CurrentCulture = machineCulture;
// Simulate the operating-system UI culture before the production resolver is first loaded.
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
L.Initialize("de-AT");
var germanIdentity = L.CacheIdentity;
Check(L.Text("hello") == "Hallo", "specific culture uses neutral language pack");
Check(L.Format("count", 1234) == "Anzahl " + 1234.ToString("N0", CultureInfo.GetCultureInfo("de-AT")), "formatting uses selected UI culture");
var cachedAt = new DateTime(2026, 10, 25, 14, 30, 0);
Check(L.Format("cached", cachedAt) == "Offline - zwischengespeicherte Ergebnisse vom " + cachedAt.ToString("g", CultureInfo.GetCultureInfo("de-AT")) + ".", "cached browsing timestamp uses the selected UI culture");
Check(CultureInfo.CurrentCulture.Name == machineCulture.Name, "UI locale does not change machine parsing culture");
Check(L.Text("missing") == "Missing", "missing translation falls back to English");
Check(L.Text("stale") == "Changed source", "outdated source stamp falls back to English");
Check(L.Format("invalid", 3) == "Value 3", "invalid translated placeholders fall back to English");
Check(L.Text("unknown") == "unknown", "unknown key remains diagnosable");
Check(L.Languages.Select(language => language.Code).SequenceEqual(new[] { "en", "de" }), "malformed and mismatched packs excluded from available languages");
L.Initialize("not a culture");
Check(L.Language == "en" && L.Text("hello") == "Hello", "invalid saved locale falls back to English");
L.Initialize("es");
Check(L.Language == "en", "unavailable locale falls back to English");
Check(L.CacheIdentity != germanIdentity, "cached UI projections are scoped by selected language");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
L.Initialize(null);
Check(L.Text("hello") == "Hallo", "system selection uses Windows UI culture");
L.Initialize("en");
var notifications = 0;
System.ComponentModel.PropertyChangedEventHandler observer = (_, args) =>
{
    if (args.PropertyName == nameof(LocalizationSource.Revision)) notifications++;
};
L.Source.PropertyChanged += observer;
L.Initialize("de");
Check(notifications == 1 && L.Source["hello"] == "Hallo", "live language switch notifies existing text sources");
L.Initialize("de");
Check(notifications == 1, "unchanged language does not broadcast a redundant refresh");
L.Initialize("en");
L.Initialize("");
Check(L.Language == "de-DE", "system language uses original OS culture after explicit live switching");
L.Source.PropertyChanged -= observer;
var neutralPack = packsForTest();
neutralPack["sr"] = new TranslationFile
{
    Language = "sr",
    Strings = new(StringComparer.Ordinal) { ["hello"] = "Zdravo" },
    SourceHashes = new(StringComparer.Ordinal) { ["hello"] = CatalogJson.Hash("Hello") },
};
L.Initialize("sr-Latn-RS");
Check(L.Text("hello") == "Zdravo", "script and regional culture traverses every parent language");
L.Initialize("de-DE");
var originalIdentity = L.CacheIdentity;
var packs = packsForTest();
packs["de"].Strings["hello"] = "Guten Tag";
L.Initialize("de-DE");
Check(L.CacheIdentity != originalIdentity && L.Text("hello") == "Guten Tag", "updated language content invalidates cached UI projections");
Console.WriteLine($"Localization runtime: {checks} checks passed.");

static Dictionary<string, TranslationFile> packsForTest() => (Dictionary<string, TranslationFile>)typeof(L)
    .GetField("Translations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

namespace LightWeaver.Diagnostics
{
    // The linked runtime reports pack errors without reading app data or log files.
    internal static class AppLog
    {
        public static void Error(string area, string message) { }
    }
}
