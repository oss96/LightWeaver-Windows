using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightWeaver.Settings;

/// <summary>The Home rails, in default order (Phase 7 M11). <see cref="Libraries"/> is the 1.x
/// app's only and <see cref="Downloads"/> v2's only.</summary>
public enum HomeSectionId { Resume, NextUp, Latest, Favorites, Libraries, Downloads }

/// <summary>One rail's layout preference: its position is its list index.</summary>
public sealed record HomeSectionPref(HomeSectionId Id, bool Enabled);

/// <summary>
/// Per-profile Home layout persistence (Phase 7 M11): <c>home-layout.json</c> holds a
/// dictionary keyed by <c>{UserId:N}@{ServerUrl}</c> so every server/user profile keeps
/// its own rail order and toggles. The global player settings.json stays untouched.
/// Modeled on <see cref="SettingsStore"/>: defensive load, atomic tmp-then-move save.
/// </summary>
public static class HomeLayoutStore
{
    private static readonly string Path = System.IO.Path.Combine(AppPaths.Root, "home-layout.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },   // readable ids, reorder-diffable file
    };

    /// <summary>The per-profile key. It is a persisted key component in TWO places: this file's
    /// dictionary, and — as <see cref="ViewModels.AppViewModel.SessionKey"/> /
    /// <c>ActiveSessionKey</c> — every browse cache key (<c>browse:{profile}:…</c>, including
    /// <see cref="Jellyfin.BrowseFolderCache.Key"/> and its profile index). The server URL is
    /// used exactly as stored, so changing how a URL is normalised (trailing slash, scheme,
    /// case, port) silently misses every saved layout and every cached folder: no error, just
    /// cold caches and a reset Home.</summary>
    public static string ProfileKey(Guid userId, string? serverUrl) => $"{userId:N}@{serverUrl}";

    /// <summary>The server URL of a <see cref="ProfileKey"/>; null for anything else or an empty URL.</summary>
    public static string? ServerOf(string? profileKey)
        => profileKey is { Length: > 33 } key && key[32] == '@' && Guid.TryParseExact(key[..32], "N", out _) ? key[33..] : null;

    /// <summary>Whether a section is on before the user chooses: every rail but Downloads.</summary>
    public static bool EnabledByDefault(HomeSectionId id) => id != HomeSectionId.Downloads;

    /// <summary>Every section in default order, each on or off by <see cref="EnabledByDefault"/>.</summary>
    public static List<HomeSectionPref> Defaults()
        => [.. Enum.GetValues<HomeSectionId>().Select(id => new HomeSectionPref(id, EnabledByDefault(id)))];

    /// <summary>The saved layout for the profile, reconciled (port of Velly's
    /// HomeSectionsStore.load): duplicates and unknown ids are dropped, sections the
    /// file doesn't know yet are appended at the end, on or off by
    /// <see cref="EnabledByDefault"/>, so both a stale file and a fresh profile yield
    /// every section.</summary>
    public static List<HomeSectionPref> Load(string profileKey)
    {
        List<HomeSectionPref>? saved = null;
        try
        {
            if (File.Exists(Path)
                && JsonSerializer.Deserialize<Dictionary<string, List<HomeSectionPref>>>(
                    File.ReadAllText(Path), Options) is { } all)
                all.TryGetValue(profileKey, out saved);
            // The profile key is a user id plus a server URL, so it is hashed rather than printed
            // (see AppLog.ShortHash) — the same token the session records carry, so a layout line
            // and a session line can be tied to the same profile.
            Diagnostics.AppLog.Detail("home-layout", FormattableString.Invariant(
                $"event=load outcome={(saved is null ? "defaults" : "success")} session={Diagnostics.AppLog.ShortHash(profileKey)} sections={saved?.Count ?? 0}"));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // corrupt/unreadable file: default layout rather than crash
            Diagnostics.AppLog.Error("home-layout",
                $"event=load outcome=unreadable session={Diagnostics.AppLog.ShortHash(profileKey)} error={ex.GetType().Name}");
        }
        return Reconcile(saved);
    }

    /// <summary>Saves the profile's layout; a section the layout leaves out keeps its saved entry,
    /// after the given ones, so each app can save the sections it shows.</summary>
    public static void Save(string profileKey, List<HomeSectionPref> layout)
    {
        Dictionary<string, List<HomeSectionPref>> all = [];
        try
        {
            if (File.Exists(Path)
                && JsonSerializer.Deserialize<Dictionary<string, List<HomeSectionPref>>>(
                    File.ReadAllText(Path), Options) is { } existing)
                all = existing;   // keep other profiles' layouts
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // unreadable: rewrite with just this profile
        }
        var kept = all.TryGetValue(profileKey, out var saved)
            ? saved.Where(p => Enum.IsDefined(p.Id) && layout.All(l => l.Id != p.Id))
            : [];
        layout = [.. layout, .. kept];
        all[profileKey] = layout;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        var json = JsonSerializer.Serialize(all, Options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
        // profiles= is the number of profiles the file holds, which is why the count is worth
        // having: it is how "my other account's layout was wiped" becomes visible.
        Diagnostics.AppLog.Detail("home-layout", FormattableString.Invariant(
            $"event=save outcome=success session={Diagnostics.AppLog.ShortHash(profileKey)} sections={layout.Count} profiles={all.Count} bytes={json.Length}"));
    }

    private static List<HomeSectionPref> Reconcile(List<HomeSectionPref>? saved)
    {
        var result = new List<HomeSectionPref>();
        if (saved is not null)
            foreach (var pref in saved)
                if (Enum.IsDefined(pref.Id) && result.All(p => p.Id != pref.Id))
                    result.Add(pref);
        foreach (var id in Enum.GetValues<HomeSectionId>())
            if (result.All(p => p.Id != id))
                result.Add(new HomeSectionPref(id, EnabledByDefault(id)));
        return result;
    }
}
