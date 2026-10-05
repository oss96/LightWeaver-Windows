using System.Text;

namespace LightWeaver.Localization;

public static class TranslationRules
{
    // Argument index, alignment and format stay intact; their order may change.
    public static string[] Placeholders(string value)
    {
        _ = CompositeFormat.Parse(value);
        var result = new List<string>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('{' or '}')) continue;
            if (i + 1 < value.Length && value[i + 1] == value[i]) { i++; continue; }
            if (value[i] != '{') throw new FormatException("Unmatched closing brace.");
            var end = value.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException("Unmatched opening brace.");
            var token = value[(i + 1)..end];
            var colon = token.IndexOf(':');
            var argument = colon < 0 ? token : token[..colon];
            // Spaces around an index or alignment do not change its meaning.
            result.Add(argument.Replace(" ", "", StringComparison.Ordinal)
                + (colon < 0 ? "" : token[colon..]));
            i = end;
        }
        return [.. result.Order(StringComparer.Ordinal)];
    }

    public static string? Validate(string source, string translation)
    {
        if (translation.Length > 16000) return "Translation is too long.";
        if (string.IsNullOrWhiteSpace(translation)) return null;
        string[] expected;
        try { expected = Placeholders(source); }
        catch (FormatException)
        {
            // Plain UI text may contain literal braces, for example a keyboard hint.
            // Only composite format strings impose composite format syntax on translations.
            expected = [];
        }
        try
        {
            var actual = Placeholders(translation);
            if (!expected.SequenceEqual(actual))
                return "Keep every placeholder, including its format, such as {0} or {1:N0}. You can change their order.";
        }
        catch (FormatException)
        {
            if (expected.Length > 0) return "Invalid placeholders or braces. Use {{ and }} for literal braces.";
            if (System.Text.RegularExpressions.Regex.IsMatch(translation, @"(?<!\{)\{[0-9]+(?:[,}:\s]|$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return "This string has no numbered placeholders. Do not add one.";
        }
        return null;
    }

    public static bool IsCurrent(TranslationFile file, string key, CatalogEntry entry)
        => file.Strings.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
            && file.SourceHashes.TryGetValue(key, out var hash) && hash == CatalogJson.Hash(entry.Text)
            && Validate(entry.Text, text) is null;
}
