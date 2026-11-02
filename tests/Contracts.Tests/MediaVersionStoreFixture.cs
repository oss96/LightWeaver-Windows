using System.IO;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Settings;

/// <summary>
/// Proves the per-item version memory: what it remembers, what it refuses to remember, and that
/// it stays bounded. The store is the ONLY memory of an alternate-version pick — the SDK exposes
/// no primary-version marker to fall back on — so a silent read failure here is a preference
/// that quietly stops working, which is exactly the kind of bug no UI test notices.
/// <para>Offscreen by construction: the store is plain JSON over a file, and the harness points
/// AppPaths at a per-run temp root (LIGHTWEAVER_TEST_APPDATA_ROOT), so this writes the real file
/// through the real code with no Application, no window and no server.</para>
/// </summary>
internal static class MediaVersionStoreFixture
{
    private const string Profile = "0f8fad5bd9cb469fa16570867728950e@https://jellyfin.test";
    private const string OtherProfile = "0f8fad5bd9cb469fa16570867728950e@https://other.test";

    /// <param name="test">The harness's own test recorder.</param>
    public static void Run(Action<string, Action> test)
    {
        test("a remembered version survives the round trip, per profile and per item", () =>
        {
            var movie = Guid.NewGuid();
            var other = Guid.NewGuid();
            MediaVersionStore.Save(Profile, movie, "source-4k");
            MediaVersionStore.Save(Profile, other, "source-1080p");

            Expect("source-4k", MediaVersionStore.Load(Profile, movie));
            Expect("source-1080p", MediaVersionStore.Load(Profile, other));
            // The same item id on another server is a different file set. Leaking a pick across
            // profiles would pin an id the other server has never heard of.
            Expect(null, MediaVersionStore.Load(OtherProfile, movie));
            Expect(null, MediaVersionStore.Load(Profile, Guid.NewGuid()));

            MediaVersionStore.Save(Profile, movie, "source-remux");
            Expect("source-remux", MediaVersionStore.Load(Profile, movie));
        });

        test("a pick for a source the item no longer has is ignored", () =>
        {
            var versions = new List<MediaVersion>
            {
                Version("source-4k"),
                Version("source-1080p"),
            };
            Expect("source-1080p", MediaVersionStore.Resolve("source-1080p", versions));
            // A replaced file is re-scanned under a NEW source id. Passing the dead one to
            // PlaybackInfo asks the server for a source it cannot serve, so it has to fall back
            // to the default instead - silently, because the user never knew the file changed.
            Expect(null, MediaVersionStore.Resolve("source-deleted", versions));
            Expect(null, MediaVersionStore.Resolve(null, versions));
            Expect(null, MediaVersionStore.Resolve("", versions));
            Expect(null, MediaVersionStore.Resolve("source-4k", []));
        });

        // Writes the file by hand, so it runs after the round-trip leg above rather than under it.
        test("a null-valued entry loads as no pick instead of throwing", () =>
        {
            var broken = Guid.NewGuid();
            var kept = Guid.NewGuid();
            var entries = new List<string> { Entry(broken, null), Entry(kept, "source-4k") };
            // Over the cap on purpose: eviction is the second place the entry was dereferenced,
            // and the only one a user reaches without ever touching the file - it runs from Save,
            // which the detail view calls straight out of a version-menu click with nothing
            // catching it. The read below covers the first, where the detail view's catch-all
            // turned the NullReferenceException into a Versions button that never appeared.
            for (var i = 0; i < MediaVersionStore.MaxEntries; i++)
                entries.Add(Entry(Guid.NewGuid(), "filler-" + i));
            File.WriteAllText(Path.Combine(AppPaths.Root, "media-versions.json"),
                "{" + string.Join(",", entries) + "}");

            // "key": null is not a JsonException, so the typed catch in ReadAll never sees it.
            Expect(null, MediaVersionStore.Load(Profile, broken));
            Expect("source-4k", MediaVersionStore.Load(Profile, kept));

            var fresh = Guid.NewGuid();
            MediaVersionStore.Save(Profile, fresh, "source-remux");
            Expect("source-remux", MediaVersionStore.Load(Profile, fresh));
            // The junk entry evicts first: it is the one entry that can never name a source.
            Expect(null, MediaVersionStore.Load(Profile, broken));
        });

        // Last leg on purpose: it fills the file past the cap, so it evicts the entries the
        // legs above it wrote.
        test("the file evicts its oldest picks at the cap", () =>
        {
            var ids = new List<Guid>();
            for (var i = 0; i < MediaVersionStore.MaxEntries + 10; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                MediaVersionStore.Save(Profile, id, "source-" + i);
            }

            // The newest MaxEntries picks are all still there and the older ones are gone. The
            // cap exists so a long watch history cannot turn every later pick into a parse and
            // rewrite of an unbounded file; losing the oldest costs one re-selection.
            for (var i = ids.Count - MediaVersionStore.MaxEntries; i < ids.Count; i++)
                Expect("source-" + i, MediaVersionStore.Load(Profile, ids[i]));
            for (var i = 0; i < ids.Count - MediaVersionStore.MaxEntries; i++)
                Expect(null, MediaVersionStore.Load(Profile, ids[i]));
        });
    }

    /// <summary>One raw entry as the file stores it — a null <paramref name="sourceId"/> writes
    /// the null-VALUED entry the store has to survive, which no call through Save can produce.</summary>
    private static string Entry(Guid itemId, string? sourceId)
        => FormattableString.Invariant($"\"{Profile}|{itemId:N}\":") + (sourceId is null
            ? "null"
            : FormattableString.Invariant(
                $"{{\"SourceId\":\"{sourceId}\",\"ChosenUtc\":\"{DateTimeOffset.UtcNow:O}\"}}"));

    private static MediaVersion Version(string id)
        => new(id, "Version", "mkv", 12_000_000, 1920, 1080, null, IsDefault: false);

    private static void Expect(string? expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected {expected ?? "null"}, got {actual ?? "null"}.");
    }
}
