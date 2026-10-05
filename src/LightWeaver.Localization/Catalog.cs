using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightWeaver.Localization;

public sealed record CatalogEntry(string Text, string Context, string[] Sources);

public sealed class StringCatalog
{
    public int Version { get; set; } = 1;
    public string SourceLanguage { get; set; } = "en";
    public SortedDictionary<string, CatalogEntry> Strings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TranslationFile
{
    public string Language { get; set; } = "";
    public SortedDictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> SourceHashes { get; set; } = new(StringComparer.Ordinal);
}

public static class CatalogJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    public static bool TryLanguage(string name, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        try
        {
            var candidate = CultureInfo.GetCultureInfo(name);
            if (candidate.Name.Length == 0 || candidate.Name.Equals("en", StringComparison.OrdinalIgnoreCase)
                || !CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
                    .Any(c => c.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))) return false;
            culture = candidate;
            return true;
        }
        catch (CultureNotFoundException) { return false; }
    }
}
