using System.Globalization;
using LightWeaver.Jellyfin;
using LightWeaver.Settings;

namespace LightWeaver.Localization;

/// <summary>Client-only names for root libraries. Server metadata is never rewritten.</summary>
internal static class LibraryNames
{
    private static AppSettings? _settings;

    public static void Configure(AppSettings settings) => _settings = settings;

    private static string Scope(string server, Guid library)
    {
        var url = Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : server;
        return url.TrimEnd('/') + "|" + library.ToString("N");
    }

    public static string? Override(string server, Guid library, string language)
    {
        if (_settings?.LibraryNameTranslations is not { } names
            || !names.TryGetValue(Scope(server, library), out var languages) || languages is null)
            return null;
        return languages.FirstOrDefault(pair => pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static void Set(string server, Guid library, string language, string text)
    {
        if (_settings is null) return;
        var names = _settings.LibraryNameTranslations ??= new();
        var key = Scope(server, library);
        if (!names.TryGetValue(key, out var languages) || languages is null)
            names[key] = languages = new(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in languages.Keys.Where(code => code.Equals(language, StringComparison.OrdinalIgnoreCase)).ToArray())
            languages.Remove(duplicate);
        if (!string.IsNullOrWhiteSpace(text)) languages[language] = text.Trim();
        if (languages.Count == 0) names.Remove(key);
    }

    public static string Display(MediaItem library)
    {
        if (library.LibraryNameServer is not { } server) return library.Name;
        for (var culture = CultureInfo.GetCultureInfo(L.Language); culture.Name.Length > 0; culture = culture.Parent)
            if (Override(server, library.Id, culture.Name) is { } name && !string.IsNullOrWhiteSpace(name))
                return name;
        return Default(library.Name);
    }

    public static string Default(string original)
    {
        // Recognize conventional names only. A custom name like "Family movies" stays intact.
        var normalized = original.Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        var key = normalized switch
        {
            "movies" or "films" => "LibraryNames.Movies",
            "tvshows" or "televisionshows" or "series" => "LibraryNames.TvShows",
            "music" => "LibraryNames.Music",
            "books" => "LibraryNames.Books",
            "audiobooks" => "LibraryNames.Audiobooks",
            "collections" => "LibraryNames.Collections",
            _ => null,
        };
        var translated = normalized switch
        {
            "movies" or "films" => L.Text("LibraryNames.Movies"),
            "tvshows" or "televisionshows" or "series" => L.Text("LibraryNames.TvShows"),
            "music" => L.Text("LibraryNames.Music"),
            "books" => L.Text("LibraryNames.Books"),
            "audiobooks" => L.Text("LibraryNames.Audiobooks"),
            "collections" => L.Text("LibraryNames.Collections"),
            _ => original,
        };
        // Preserve the user's spelling in English and when a pack has no reviewed translation.
        return key is not null && !translated.Equals(L.SourceText(key), StringComparison.Ordinal) ? translated : original;
    }
}
