using System.IO;
using System.Text.Json;
using LightWeaver;
using LightWeaver.Settings;

// Pure settings/policy checks for when the Up Next card appears. No Application, window, mpv or
// Jellyfin connection.
internal static class UpNextContractFixture
{
    public const string Mode = "--up-next-contract";

    public static int Run()
    {
        var failures = 0;
        var checks = 0;
        var user = Guid.NewGuid();
        var show = Guid.NewGuid();
        const string server = "https://jellyfin-fixture.invalid/base";
        var key = Key(server, user, show);
        void Test(string name, Action action)
        {
            checks++;
            try { action(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + ": " + ex.Message); }
        }

        Test("a missing show lead follows changes to the global lead", () =>
        {
            var settings = new AppSettings();
            Equal(30, Resolve(settings));
            Equal(null, UpNextPreferences.GetShowLead(settings, key));
            settings.UpNextLeadSeconds = 45;
            Equal(45, Resolve(settings));
            settings.UpNextLeadSeconds = 300;
            Equal(300, Resolve(settings));
        });
        Test("a show lead wins over the global lead and does not change it", () =>
        {
            var settings = new AppSettings { UpNextLeadSeconds = 17 };
            UpNextPreferences.SetShowLead(settings, key, 90);
            Equal(90, Resolve(settings));
            settings.UpNextLeadSeconds = 5;
            Equal(90, Resolve(settings));
            Equal(5, settings.UpNextLeadSeconds);
            Equal(90, UpNextPreferences.GetShowLead(settings, key));
        });
        Test("removing the show lead resumes inheritance", () =>
        {
            var settings = new AppSettings { UpNextLeadSeconds = 27 };
            UpNextPreferences.SetShowLead(settings, key, 60);
            Equal(60, Resolve(settings));
            UpNextPreferences.SetShowLead(settings, key, null);
            Equal(false, settings.ShowUpNextLeadSeconds.ContainsKey(key));
            Equal(27, Resolve(settings));
        });
        Test("the countdown and auto-play toggle stay global and independent of the lead", () =>
        {
            var settings = new AppSettings { AutoPlayNextEpisode = false, AutoPlayCountdownSeconds = 44 };
            UpNextPreferences.SetShowLead(settings, key, 120);
            Equal(false, settings.AutoPlayNextEpisode);
            Equal(44, settings.AutoPlayCountdownSeconds);
            Equal(30, settings.UpNextLeadSeconds);
        });
        Test("normalized hosts default ports and trailing slashes share a show lead", () =>
        {
            Equal(key, Key("HTTPS://JELLYFIN-FIXTURE.INVALID:443/base/", user, show));
            Equal(Key("http://jellyfin-fixture.invalid", user, show),
                Key("HTTP://JELLYFIN-FIXTURE.INVALID:80/", user, show));
        });
        Test("server scheme port base path user and show remain separate scopes", () =>
        {
            var alternatives = new[]
            {
                Key("http://jellyfin-fixture.invalid/base", user, show),
                Key("https://jellyfin-fixture.invalid:8443/base", user, show),
                Key("https://other-fixture.invalid/base", user, show),
                Key("https://jellyfin-fixture.invalid/other", user, show),
                Key("https://jellyfin-fixture.invalid/Base", user, show),
                Key(server, Guid.NewGuid(), show), Key(server, user, Guid.NewGuid()),
            };
            Equal(false, alternatives.Contains(key));
            Equal(alternatives.Length, alternatives.Distinct().Count());
            var settings = new AppSettings { UpNextLeadSeconds = 33 };
            UpNextPreferences.SetShowLead(settings, key, 200);
            foreach (var otherKey in alternatives)
                Equal(null, UpNextPreferences.GetShowLead(settings, otherKey));
        });
        Test("missing or invalid session identifiers inherit the global lead", () =>
        {
            var settings = new AppSettings { UpNextLeadSeconds = 37 };
            UpNextPreferences.SetShowLead(settings, key, 250);
            foreach (var invalid in new string?[] { null, "", "not a URL", "/base", "file:///C:/base",
                "https://user:synthetic@example.invalid", server + "?query=1", server + "#fragment" })
            {
                Equal(false, UpNextPreferences.TryGetShowKey(invalid, user, show, out var invalidKey));
                Equal("", invalidKey);
                Equal(37, UpNextPreferences.ResolveLeadSeconds(settings, invalid, user, show));
            }
            Equal(false, UpNextPreferences.TryGetShowKey(server, Guid.Empty, show, out _));
            Equal(false, UpNextPreferences.TryGetShowKey(server, user, null, out _));
            Equal(false, UpNextPreferences.TryGetShowKey(server, user, Guid.Empty, out _));
            Equal(37, UpNextPreferences.ResolveLeadSeconds(settings, server, user, null));
            Equal(37, UpNextPreferences.ResolveLeadSeconds(settings, server, Guid.Empty, show));
        });
        Test("each current show resolves its own lead across a mixed-show queue", () =>
        {
            var otherShow = Guid.NewGuid();
            var settings = new AppSettings { UpNextLeadSeconds = 31 };
            UpNextPreferences.SetShowLead(settings, key, 15);
            UpNextPreferences.SetShowLead(settings, Key(server, user, otherShow), 75);
            Equal(15, UpNextPreferences.ResolveLeadSeconds(settings, server, user, show));
            Equal(75, UpNextPreferences.ResolveLeadSeconds(settings, server, user, otherShow));
            Equal(31, UpNextPreferences.ResolveLeadSeconds(settings, server, user, Guid.NewGuid()));
            Equal(31, UpNextPreferences.ResolveLeadSeconds(settings, server, user, null));
        });
        Test("old settings JSON without the new keys keeps a 30-second lead and 10-second countdown", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"AutoPlayNextEpisode\":false}")!;
            Equal(0, settings.ShowUpNextLeadSeconds.Count);
            Equal(30, settings.UpNextLeadSeconds);
            Equal(10, settings.AutoPlayCountdownSeconds);
            Equal(30, Resolve(settings));
            var empty = JsonSerializer.Deserialize<AppSettings>("{}")!;
            Equal(30, Resolve(empty));
            Equal(10, empty.AutoPlayCountdownSeconds);
            // The per-show countdown of an unreleased build is ignored rather than failing the file.
            var earlier = JsonSerializer.Deserialize<AppSettings>("{\"AutoPlayCountdownSeconds\":24,\"ShowAutoPlayOverrides\":{"
                + JsonSerializer.Serialize(key) + ":{\"Mode\":1,\"CountdownSeconds\":12}}}")!;
            Equal(24, earlier.AutoPlayCountdownSeconds);
            Equal(30, Resolve(earlier));
        });
        Test("global and show lead JSON values clamp to 5 through 300 seconds", () =>
        {
            foreach (var (value, expected) in new[] { (long)int.MinValue - 1, -1, 0, 4, 5, 51, 300, 301, int.MaxValue, (long)int.MaxValue + 1 }
                .Select(v => (v, v < 5 ? 5L : v > 300 ? 300L : v)))
            {
                if (value is >= int.MinValue and <= int.MaxValue)
                {
                    var global = JsonSerializer.Deserialize<AppSettings>("{\"UpNextLeadSeconds\":" + value + "}")!;
                    Equal((int)expected, global.UpNextLeadSeconds);
                }
                var json = "{\"ShowUpNextLeadSeconds\":{" + JsonSerializer.Serialize(key) + ":" + value + "}}";
                var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
                Equal((int)expected, UpNextPreferences.GetShowLead(settings, key));
                Equal((int)expected, Resolve(settings));
            }
            // A value placed in memory directly is clamped on read as well.
            var direct = new AppSettings();
            direct.ShowUpNextLeadSeconds[key] = 999;
            Equal(300, UpNextPreferences.GetShowLead(direct, key));
            direct.ShowUpNextLeadSeconds[key] = 1;
            Equal(5, Resolve(direct));
        });
        Test("countdown JSON values still clamp to 3 through 120 seconds", () =>
        {
            foreach (var (value, expected) in new[] { (int.MinValue, 3), (2, 3), (3, 3), (51, 51), (120, 120), (121, 120), (int.MaxValue, 120) })
                Equal(expected, JsonSerializer.Deserialize<AppSettings>("{\"AutoPlayCountdownSeconds\":" + value + "}")!.AutoPlayCountdownSeconds);
        });
        Test("a null map and junk entries inherit without losing other settings", () =>
        {
            var nullMap = JsonSerializer.Deserialize<AppSettings>("{\"ShowUpNextLeadSeconds\":null,\"UpNextLeadSeconds\":42,\"AutoPlayCountdownSeconds\":17}")!;
            Equal(0, nullMap.ShowUpNextLeadSeconds.Count);
            Equal(42, Resolve(nullMap));
            Equal(17, nullMap.AutoPlayCountdownSeconds);
            var otherKey = Key(server, user, Guid.NewGuid());
            foreach (var entry in new[] { "null", "\"60\"", "12.5", "true", "{\"Seconds\":60}", "[60]" })
            {
                var json = "{\"AutoPlayNextEpisode\":false,\"AutoPlayCountdownSeconds\":17,\"UpNextLeadSeconds\":42,"
                    + "\"ShowUpNextLeadSeconds\":{" + JsonSerializer.Serialize(key) + ":" + entry + ","
                    + JsonSerializer.Serialize(otherKey) + ":75},\"SkipForwardSeconds\":23}";
                var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
                Equal(42, Resolve(settings));
                Equal(null, UpNextPreferences.GetShowLead(settings, key));
                Equal(75, UpNextPreferences.GetShowLead(settings, otherKey));
                Equal(false, settings.AutoPlayNextEpisode);
                Equal(17, settings.AutoPlayCountdownSeconds);
                Equal(23, settings.SkipForwardSeconds);
            }
            foreach (var map in new[] { "[]", "[{\"a\":1}]", "\"junk\"", "7" })
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(
                    "{\"ShowUpNextLeadSeconds\":" + map + ",\"UpNextLeadSeconds\":42,\"SkipForwardSeconds\":23}")!;
                Equal(0, settings.ShowUpNextLeadSeconds.Count);
                Equal(42, Resolve(settings));
                Equal(23, settings.SkipForwardSeconds);
            }
        });
        Test("synthetic show leads survive SettingsStore save load and removal", () =>
        {
            var root = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT");
            if (root is null || !Path.GetFileName(root).StartsWith("lw-playback-harness-", StringComparison.Ordinal)
                || !Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || AppPaths.Root != root || File.Exists(Path.Combine(root, "credentials.dat")))
                throw new InvalidOperationException("The settings round trip requires the harness's isolated temporary root.");
            var otherKey = Key(server, user, Guid.NewGuid());
            var settings = new AppSettings { SettingsVersion = 1, AutoPlayNextEpisode = false, AutoPlayCountdownSeconds = 19,
                UpNextLeadSeconds = 50 };
            UpNextPreferences.SetShowLead(settings, key, 75);
            UpNextPreferences.SetShowLead(settings, otherKey, 15);
            Equal(true, SettingsStore.Save(settings));
            var loaded = SettingsStore.Load();
            Equal(75, Resolve(loaded));
            Equal(15, UpNextPreferences.GetShowLead(loaded, otherKey));
            Equal(50, loaded.UpNextLeadSeconds);
            Equal(false, loaded.AutoPlayNextEpisode);
            Equal(19, loaded.AutoPlayCountdownSeconds);
            UpNextPreferences.SetShowLead(loaded, key, null);
            Equal(true, SettingsStore.Save(loaded));
            var reloaded = SettingsStore.Load();
            Equal(50, Resolve(reloaded));
            Equal(false, reloaded.ShowUpNextLeadSeconds.ContainsKey(key));
            Equal(15, UpNextPreferences.GetShowLead(reloaded, otherKey));
            Equal(19, reloaded.AutoPlayCountdownSeconds);
        });

        Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")} ({checks} Up Next contracts, {failures} failures)");
        return failures == 0 ? 0 : 1;
        int Resolve(AppSettings settings) => UpNextPreferences.ResolveLeadSeconds(settings, server, user, show);
    }

    private static string Key(string server, Guid user, Guid show)
        => UpNextPreferences.TryGetShowKey(server, user, show, out var key)
            ? key : throw new InvalidOperationException("Synthetic scope should be valid.");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
