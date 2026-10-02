using System.IO;
using System.Text.Json;

namespace LightWeaver.Settings;

/// <summary>One item's remembered alternate version. <see cref="ChosenUtc"/> is not display
/// data: it is the only thing that can order an eviction, since every entry is otherwise
/// equally valid.</summary>
public sealed record MediaVersionPick(string SourceId, DateTimeOffset ChosenUtc);

/// <summary>
/// Remembers the alternate version (media source) last chosen per item:
/// <c>media-versions.json</c> holds one flat dictionary keyed by
/// <c>{profileKey}|{itemId:N}</c>, with the profile key coming from
/// <see cref="HomeLayoutStore.ProfileKey"/> — a source id names one server's files, so the
/// same item id on another server must not inherit it.
/// <para>This is the whole memory of the choice: the SDK exposes no primary-version marker on
/// MediaSourceInfo (checked against 2025.10.21 and 2026.9.8-unstable), so there is no server
/// default to fall back on beyond "whatever the server listed first".</para>
/// <para>Modeled on <see cref="HomeLayoutStore"/>: defensive load, atomic tmp-then-move save.</para>
/// </summary>
public static class MediaVersionStore
{
    /// <summary>How many picks the file keeps. Unlike the Home layout — five rails per profile,
    /// bounded by construction — this file grows by one entry per item ever played from a
    /// non-default source, so without a cap a library-sized watch history would be replayed
    /// through a full parse and rewrite on every pick. The oldest go first; losing a pick costs
    /// one re-selection, which is why the cap can be this blunt.</summary>
    public const int MaxEntries = 400;

    private static readonly string Path = System.IO.Path.Combine(AppPaths.Root, "media-versions.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The remembered source id for an item, or null when there is none. The caller
    /// still has to put it through <see cref="Resolve"/> against the item's CURRENT sources.</summary>
    public static string? Load(string profileKey, Guid itemId)
        // The ENTRY is pattern-checked, not only the id: neither `"key": null` nor a null string
        // in a positional record is a JsonException, whatever the declared types say, so both
        // come through ReadAll's typed catch intact. A null entry then threw NullReferenceException
        // on the first property read, which the detail view's catch-all swallowed — the Versions
        // button silently never appeared. Length, not just non-null, because an empty id would be
        // pinned to negotiation as if it named a source.
        => ReadAll().GetValueOrDefault(Key(profileKey, itemId)) is { SourceId: { Length: > 0 } id }
            ? id
            : null;

    /// <summary>Records a pick, evicting the oldest entries once the file is over
    /// <see cref="MaxEntries"/>.</summary>
    public static void Save(string profileKey, Guid itemId, string sourceId)
    {
        var all = ReadAll();
        all[Key(profileKey, itemId)] = new MediaVersionPick(sourceId, Stamp());
        var evicted = Prune(all);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            var json = JsonSerializer.Serialize(all, Options);
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);
            Diagnostics.AppLog.Detail("media-version", FormattableString.Invariant(
                $"event=save outcome=success session={Diagnostics.AppLog.ShortHash(profileKey)} item={itemId:N} entries={all.Count} evicted={evicted}"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A pick that cannot be written is a preference lost, never a playback stopped — the
            // switch itself already happened by the time this runs.
            Diagnostics.AppLog.Error("media-version",
                $"event=save outcome=failure session={Diagnostics.AppLog.ShortHash(profileKey)} item={itemId:N} error={ex.GetType().Name}");
        }
    }

    /// <summary>The remembered id, but only if the item still HAS that source. Files get
    /// replaced and re-scanned, which mints new source ids; handing a dead one to PlaybackInfo
    /// makes the server answer for a source that no longer exists, so a stale pick falls back
    /// to the default silently — the user gets the item playing, not an error about a file they
    /// never knew changed.</summary>
    public static string? Resolve(string? rememberedId,
        IReadOnlyList<Jellyfin.MediaVersion> versions)
        => rememberedId is { Length: > 0 } id
            && versions.Any(v => string.Equals(v.Id, id, StringComparison.Ordinal))
            ? id
            : null;

    /// <summary>The composite key. Never parsed back apart — a server URL may contain anything,
    /// so this is an opaque identity, not a record with fields.</summary>
    private static string Key(string profileKey, Guid itemId)
        => FormattableString.Invariant($"{profileKey}|{itemId:N}");

    /// <summary>The time to stamp a pick with, forced past the last one this process issued.
    /// <see cref="DateTimeOffset.UtcNow"/> only advances every ~15 ms on Windows, so a burst of
    /// picks shares one value and the eviction order among them falls to the key tie-break —
    /// which is a GUID, so the entry dropped at the cap could be the pick just made.</summary>
    private static DateTimeOffset Stamp()
    {
        var now = DateTimeOffset.UtcNow;
        _lastStamp = now > _lastStamp ? now : _lastStamp.AddTicks(1);
        return _lastStamp;
    }

    private static DateTimeOffset _lastStamp;

    /// <summary>Drops the oldest entries until the dictionary fits the cap; returns how many
    /// went. Ties are broken by key so entries written by an OLDER build — before the stamps
    /// above were monotonic — still evict deterministically.</summary>
    private static int Prune(Dictionary<string, MediaVersionPick> all)
    {
        if (all.Count <= MaxEntries)
            return 0;
        // A null entry (see Load) sorts oldest so the junk is dropped before any real pick — and,
        // more to the point, its ChosenUtc is never read: this runs from Save, which the detail
        // view calls straight out of a menu click with nothing catching it, so the dereference
        // here would take the app down rather than be swallowed the way Load's was.
        var doomed = all.OrderBy(e => e.Value is { } pick ? pick.ChosenUtc : DateTimeOffset.MinValue)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(all.Count - MaxEntries).Select(e => e.Key).ToList();
        foreach (var key in doomed)
            all.Remove(key);
        return doomed.Count;
    }

    private static Dictionary<string, MediaVersionPick> ReadAll()
    {
        try
        {
            if (File.Exists(Path)
                && JsonSerializer.Deserialize<Dictionary<string, MediaVersionPick>>(
                    File.ReadAllText(Path), Options) is { } all)
                return all;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // corrupt/unreadable file: no remembered picks rather than a failed detail view
            Diagnostics.AppLog.Error("media-version",
                $"event=load outcome=unreadable error={ex.GetType().Name}");
        }
        return [];
    }
}
