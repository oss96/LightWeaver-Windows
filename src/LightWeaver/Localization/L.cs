using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace LightWeaver.Localization;

/// <summary>UI text only. Protocol identifiers, saved enum values and logs stay invariant.</summary>
public static class L
{
    private static readonly Assembly Assembly = typeof(L).Assembly;
    private static readonly StringCatalog Catalog = LoadCatalog();
    private static readonly Dictionary<string, TranslationFile> Translations = LoadTranslations();
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static string _cacheIdentity = "en:" + CatalogJson.Hash(JsonSerializer.Serialize(Catalog, CatalogJson.Options));
    public static string Language => _culture.Name;
    public static LocalizationSource Source { get; } = new();
    /// <summary>Cached UI projections must match both language and embedded text revision.</summary>
    public static string CacheIdentity => _cacheIdentity;
    public static bool IsRightToLeft => _culture.TextInfo.IsRightToLeft;
    public static IReadOnlyList<UiLanguage> Languages =>
        [new("en", "English"), .. Translations.Keys.Order().Select(code => new UiLanguage(code, CultureInfo.GetCultureInfo(code).NativeName))];

    public static void Initialize(string? language)
    {
        var selected = string.IsNullOrEmpty(language) ? SystemUiCulture.Name : language;
        var previousIdentity = _cacheIdentity;
        try
        {
            var candidate = CultureInfo.GetCultureInfo(selected);
            var available = candidate.Name == "en";
            for (var parent = candidate; parent.Name.Length > 0 && !available; parent = parent.Parent)
                available = Translations.ContainsKey(parent.Name);
            _culture = available ? candidate : CultureInfo.GetCultureInfo("en");
        }
        catch (CultureNotFoundException) { _culture = CultureInfo.GetCultureInfo("en"); }
        // Keep CurrentCulture: machine-facing parsing predates UI localization.
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        var packs = new List<TranslationFile>();
        for (var culture = _culture; culture.Name.Length > 0; culture = culture.Parent)
            if (Translations.TryGetValue(culture.Name, out var pack)) packs.Add(pack);
        _cacheIdentity = _culture.Name + ":" + CatalogJson.Hash(
            JsonSerializer.Serialize(new { catalog = Catalog, packs }, CatalogJson.Options));
        if (_cacheIdentity != previousIdentity) Source.Refresh();
    }

    public static string Text(string key)
    {
        if (!Catalog.Strings.TryGetValue(key, out var entry)) return key;
        for (var culture = _culture; culture.Name.Length > 0; culture = culture.Parent)
            if (Translations.TryGetValue(culture.Name, out var file) && TranslationRules.IsCurrent(file, key, entry)) return file.Strings[key];
        return entry.Text;
    }

    public static string Format(string key, params object?[] args)
    {
        try { return string.Format(_culture, Text(key), args); }
        catch (FormatException) { return string.Format(_culture, Catalog.Strings[key].Text, args); }
    }

    internal static string Resolve(string key) => Text(key);
    internal static string SourceText(string key) => Catalog.Strings[key].Text;
    internal static string Resolve(string key, int number) => Format(key, number);

    private static StringCatalog LoadCatalog()
    {
        using var stream = Assembly.GetManifestResourceStream("LightWeaver.Localization.catalog.json")
            ?? throw new InvalidOperationException("The UI string catalog was not embedded.");
        var catalog = JsonSerializer.Deserialize<StringCatalog>(stream, CatalogJson.Options);
        if (catalog is null || catalog.Strings is null || catalog.Strings.Any(pair => pair.Value is null || pair.Value.Text is null))
            throw new InvalidOperationException("The embedded UI string catalog is invalid.");
        return catalog;
    }

    private static Dictionary<string, TranslationFile> LoadTranslations()
    {
        var result = new Dictionary<string, TranslationFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in Assembly.GetManifestResourceNames().Where(n => n.StartsWith("LightWeaver.Localization.locales.", StringComparison.Ordinal)))
        {
            try
            {
                using var stream = Assembly.GetManifestResourceStream(resource)!;
                var file = JsonSerializer.Deserialize<TranslationFile>(stream, CatalogJson.Options);
                var expectedCode = resource["LightWeaver.Localization.locales.".Length..^".json".Length];
                if (file is not null && file.Strings is not null && file.SourceHashes is not null
                    && CatalogJson.TryLanguage(file.Language, out var culture)
                    && culture.Name.Equals(expectedCode, StringComparison.OrdinalIgnoreCase)
                    && file.Strings.All(pair => pair.Value is not null)) result[culture.Name] = file;
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
            {
                Diagnostics.AppLog.Error("localization", $"language pack ignored error={ex.GetType().Name}");
            }
        }
        return result;
    }
}

/// <summary>Shared notification source for live UI text, independent of WPF and app data.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => L.Resolve(key);
    public long Revision { get; private set; }

    internal void Refresh()
    {
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
    }
}

public sealed record UiLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}
