using System.IO;
using System.Text.Json;

namespace LightWeaver.Downloads;

/// <summary>
/// JSON index of tracked downloads at %LOCALAPPDATA%\LightWeaver\downloads\index.json
/// (media files live beside it, or in the configured download directory). Same
/// defensive-load / atomic tmp-then-move durability as <see cref="Settings.SettingsStore"/>.
/// The index location is fixed — moving the media directory must not orphan the metadata.
/// </summary>
public sealed class DownloadStore
{
    public static string DefaultDirectory { get; } =
        Path.Combine(AppPaths.Root, "downloads");

    private static readonly string IndexPath = Path.Combine(DefaultDirectory, "index.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Status as a name ("Downloading"), not an enum ordinal — the index is
        // hand-inspectable and the verify suite asserts on it.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();

    public List<DownloadItem> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    var loaded = JsonSerializer.Deserialize<List<DownloadItem>>(
                        File.ReadAllText(IndexPath), Options) ?? [];
                    Diagnostics.AppLog.Detail("downloads-store", FormattableString.Invariant(
                        $"event=load outcome=success count={loaded.Count}"));
                    return loaded;
                }
                Diagnostics.AppLog.Detail("downloads-store", "event=load outcome=absent count=0");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // corrupt/unreadable index: start empty rather than crash. To the user this is
                // "my downloads list emptied itself", which is worth a line even when verbose is
                // off — with the exception TYPE only, since the message names the index path.
                Diagnostics.AppLog.Error("downloads-store",
                    $"event=load outcome=unreadable error={ex.GetType().Name}");
            }
            return [];
        }
    }

    public void AddOrUpdate(DownloadItem item)
    {
        lock (_lock)
        {
            var items = LoadAllUnsafe();
            var index = items.FindIndex(i => i.ItemId == item.ItemId);
            if (index >= 0)
                items[index] = item;
            else
                items.Add(item);
            SaveUnsafe(items);
        }
    }

    public void Remove(Guid itemId)
    {
        lock (_lock)
            SaveUnsafe(LoadAllUnsafe().Where(i => i.ItemId != itemId).ToList());
    }

    public void Save(List<DownloadItem> items)
    {
        lock (_lock)
            SaveUnsafe(items);
    }

    private List<DownloadItem> LoadAllUnsafe()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<List<DownloadItem>>(
                    File.ReadAllText(IndexPath), Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Same class as LoadAll's, on the read-modify-write path: a status transition against
            // an unreadable index silently REPLACES it, so it is the more destructive of the two.
            Diagnostics.AppLog.Error("downloads-store",
                $"event=reload outcome=unreadable error={ex.GetType().Name}");
        }
        return [];
    }

    private static void SaveUnsafe(List<DownloadItem> items)
    {
        try
        {
            Directory.CreateDirectory(DefaultDirectory);
            var tmp = IndexPath + ".tmp";
            var json = JsonSerializer.Serialize(items, Options);
            File.WriteAllText(tmp, json);
            File.Move(tmp, IndexPath, overwrite: true);
            Diagnostics.AppLog.Detail("downloads-store", FormattableString.Invariant(
                $"event=save outcome=success count={items.Count} bytes={json.Length}"));
        }
        catch (IOException ex)
        {
            // index write is best-effort; in-memory state stays authoritative — but a session whose
            // transitions never reach disk loses every download on restart, and that used to leave
            // no trace at all. Type only: the message names the index path.
            Diagnostics.AppLog.Error("downloads-store",
                $"event=save outcome=failure error={ex.GetType().Name}");
        }
    }
}
