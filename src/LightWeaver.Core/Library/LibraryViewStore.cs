using System.Text.Json;

namespace LightWeaver.Library;

/// <summary>A library's display options (design 02, 2c): poster size step and title visibility.</summary>
public sealed record LibraryView(int SizeStep = LibraryViewStore.DefaultStep, bool ShowTitles = true);

/// <summary>Remembers display options per library in <c>library-views.json</c>, keyed by
/// <c>{profileKey}|{libraryId:N}</c>. Defensive load and an atomic save, like the other small
/// stores; a damaged file reads as defaults.</summary>
public static class LibraryViewStore
{
    /// <summary>The slider snaps to four poster widths (design 02, 2c note 2).</summary>
    public static readonly IReadOnlyList<int> PosterWidths = [120, 150, 185, 230];
    public const int DefaultStep = 1;
    private const int MaxEntries = 200;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The file, settable so tests can use a temporary folder.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppPaths.Root, "library-views.json");

    public static LibraryView Load(string profileKey, Guid libraryId)
        => Read().TryGetValue(Key(profileKey, libraryId), out var view) ? view with { SizeStep = Math.Clamp(view.SizeStep, 0, PosterWidths.Count - 1) } : new LibraryView();

    public static void Save(string profileKey, Guid libraryId, LibraryView view)
    {
        var all = Read();
        all.Remove(Key(profileKey, libraryId));
        all[Key(profileKey, libraryId)] = view;
        while (all.Count > MaxEntries) all.Remove(all.Keys.First());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Error("library", $"event=save_view outcome=failure error={ex.GetType().Name}");
        }
    }

    private static string Key(string profileKey, Guid libraryId) => $"{profileKey}|{libraryId:N}";

    private static Dictionary<string, LibraryView> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, LibraryView>>(File.ReadAllText(FilePath), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
