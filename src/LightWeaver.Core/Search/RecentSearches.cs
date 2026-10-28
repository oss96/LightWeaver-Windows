using System.Text.Json;

namespace LightWeaver.Search;

/// <summary>Recent top-bar searches, local to this device and profile (design 04, 4a), newest
/// first, in <c>recent-searches.json</c> keyed by profile. A damaged file reads as empty.</summary>
public static class RecentSearches
{
    public const int Max = 8;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The file, settable so tests can use a temporary folder.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppPaths.Root, "recent-searches.json");

    public static IReadOnlyList<string> Load(string profileKey) => Read().GetValueOrDefault(profileKey) ?? [];

    /// <summary>Puts a search first, replacing an earlier one that differs only in case.</summary>
    public static IReadOnlyList<string> Add(string profileKey, string term)
    {
        term = term.Trim();
        if (term.Length == 0) return Load(profileKey);
        return Change(profileKey, list =>
        {
            list.RemoveAll(t => string.Equals(t, term, StringComparison.CurrentCultureIgnoreCase));
            list.Insert(0, term);
            if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        });
    }

    public static IReadOnlyList<string> Remove(string profileKey, string term)
        => Change(profileKey, list => list.RemoveAll(t => t == term));

    /// <summary>Clear all: no confirmation, the list is only on this device.</summary>
    public static IReadOnlyList<string> Clear(string profileKey) => Change(profileKey, list => list.Clear());

    private static IReadOnlyList<string> Change(string profileKey, Action<List<string>> change)
    {
        var all = Read();
        var list = all.GetValueOrDefault(profileKey) ?? [];
        change(list);
        if (list.Count == 0) all.Remove(profileKey);
        else all[profileKey] = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Error("search", $"event=save_recent outcome=failure error={ex.GetType().Name}");
        }
        return list;
    }

    private static Dictionary<string, List<string>> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
