using System.IO;
using System.Text.Json;

namespace LightWeaver.Settings;

/// <summary>JSON persistence for <see cref="AppSettings"/> — defensive load, atomic save.</summary>
public static class SettingsStore
{
    private static readonly string Path = System.IO.Path.Combine(AppPaths.Root, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Schema the shipped defaults describe. Bumped only when a default CHANGES and the
    /// old value has to be moved on existing installs — see <see cref="Migrate"/>.</summary>
    private const int CurrentSettingsVersion = 1;

    /// <summary>Set when the file EXISTS but the OS refused to let us read it. Distinct from a
    /// corrupt file, and the difference decides whether <see cref="Save"/> may overwrite it — see
    /// the comment there. Never reset: a permissions problem does not heal inside one run, and
    /// treating it as transient is what would discard the settings.</summary>
    private static bool _readDeniedByPermissions;

    public static AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(Path))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt/unreadable file: fall back to defaults rather than crash. Logged because
            // to the user this is "all my settings reset themselves", with nothing else to see.
            //
            // UnauthorizedAccessException is NOT an IOException and was missing here until
            // 2026-08-09. File.ReadAllText throws it when the file's ACL denies read — and Load
            // runs from MainWindow's field initializer, so it escaped as a startup crash with no
            // window and no visible cause, in a method whose own summary calls itself a defensive
            // load. (The API also throws it for a directory path, but that case cannot reach here:
            // File.Exists above returns false for a directory, so this reads as file-missing.)
            //
            // Assigned only on the way UP, never cleared: a permissions problem does not heal
            // inside one run, and a plain `= ex is …` would let a hypothetical second Load that hit
            // a JsonException reset the flag and re-enable the overwrite this exists to prevent.
            if (ex is UnauthorizedAccessException)
                _readDeniedByPermissions = true;
            Diagnostics.AppLog.Error("settings",
                $"could not read settings; using defaults error={ex.GetType().Name}");
        }

        // No file, an empty one, or one we could not read: a fresh start on the shipped defaults,
        // which are current by definition. Stamp them so the migration below cannot later fire
        // against values it was never meant to touch.
        if (loaded is null)
        {
            Diagnostics.AppLog.Detail("settings", "event=load outcome=fallback_defaults");
            return new AppSettings { SettingsVersion = CurrentSettingsVersion };
        }

        Diagnostics.AppLog.Detail("settings", FormattableString.Invariant(
            $"event=load outcome=success settings_version={loaded.SettingsVersion}"));
        if (loaded.SettingsVersion < CurrentSettingsVersion)
            Migrate(loaded);
        return loaded;
    }

    /// <summary>Moves an older file's values onto the current defaults, once, and records that it
    /// happened by writing the file back with the new <see cref="AppSettings.SettingsVersion"/>.
    /// </summary>
    private static void Migrate(AppSettings settings)
    {
        if (settings.SettingsVersion < 1)
        {
            // v1: the plain forward jump went 30 s -> 10 s. Only a value that still reads as the
            // OLD default is moved, so anyone who typed their own number keeps it.
            //
            // Two blind spots, both accepted rather than overlooked:
            // - a user who deliberately typed 30 on an older build is indistinguishable from one
            //   who never opened Settings, because Save writes every property. They get moved to
            //   10 once and can type 30 back, which then survives (the marker is set by then).
            // - downgrade-then-upgrade re-runs this, because the older build drops the unknown
            //   SettingsVersion when it saves. One repeat of a one-off correction, not a loop.
            if (settings.SkipForwardSeconds == 30)
                settings.SkipForwardSeconds = 10;
        }

        settings.SettingsVersion = CurrentSettingsVersion;
        // Unguarded on purpose: Save swallows and logs its own IO failures, so this cannot throw
        // out of the field initializer Load runs from. (It used to carry its own try/catch for
        // exactly that reason; that guard moved INTO Save on 2026-08-09 so every call site gets it.)
        //
        // A failed write is survivable and needs no handling here: the marker is already 1 on this
        // in-memory instance, so any later save persists the migrated value AND the marker together.
        // Only the FILE still reads unmigrated, and re-running the migration against it next launch
        // is harmless because the migration is idempotent.
        Save(settings);
    }

    /// <summary>Persists the settings. <b>Never throws</b> — returns false when nothing was
    /// written, and logs why.
    ///
    /// <para>One policy for the whole store, rather than a guard per call site (decided
    /// 2026-08-09). Every caller is a UI event handler or a field initializer:
    /// <c>SettingsView</c>'s save-on-change paths, <c>PlayerViewModel.SelectAudioDevice</c>, and
    /// <see cref="Migrate"/>. An exception out of any of them is an unhandled exception on the
    /// dispatcher — i.e. a crash — and none of them has anything useful to do with one. A save that
    /// silently does not happen is a worse outcome than a save that happens, but a far better one
    /// than losing the session, and the log line is what makes it diagnosable.</para></summary>
    /// <returns>true if the file was written.</returns>
    public static bool Save(AppSettings settings)
    {
        // Refuse to overwrite a file we were not allowed to READ. Those settings are still in
        // there; writing defaults over them would turn a permissions problem into permanent data
        // loss, and the user would have no way to know it happened. A CORRUPT file is deliberately
        // not treated this way — overwriting it is the only way back to a working config, which is
        // why the two cases are distinguished at load rather than lumped together as "unreadable".
        if (_readDeniedByPermissions)
        {
            Diagnostics.AppLog.Error(
                "settings",
                "not overwriting settings: the file exists but could not be read (permissions). " +
                "This session's changes are in memory only.");
            return false;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);
            // Size only, never content: the file holds free-form values (download folder, subtitle
            // font, audio device) that the structural contract keeps out of the log.
            Diagnostics.AppLog.Detail("settings", FormattableString.Invariant(
                $"event=save outcome=success bytes={json.Length}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Error("settings",
                $"could not write settings error={ex.GetType().Name}");
            return false;
        }
    }
}
