using System.Reflection;
using System.Text.Json;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Imaging;
using LightWeaver.Jellyfin;

/// <summary>
/// Proves the alternate-version picker can name a media source the server did not name. Jellyfin
/// only fills <c>MediaSourceInfo.Name</c> when the library layout gives it one, so the common
/// multi-version item arrives as two unnamed sources that differ in resolution, codec or
/// container — a picker that fell back to the id would list two rows nobody can tell apart.
/// <para>Offscreen by construction: the label rule and the row disambiguator are pure,
/// so this calls them directly. No Application, no window, no mpv and no server.</para>
/// </summary>
internal static class MediaVersionFixture
{
    private static MethodInfo? _derivedLabel;
    private static MethodInfo? _resolutionLabel;
    private static JsonSerializerOptions? _cacheOptions;

    /// <summary>Resolved on first use inside a leg, never in a static initializer, so a rename
    /// arrives as this fixture's own FAIL line instead of a TypeInitializationException thrown
    /// before any leg has reported.</summary>
    private static MethodInfo DerivedLabel => _derivedLabel ??=
        Private(typeof(JellyfinService), "DerivedVersionLabel");

    private static MethodInfo ResolutionLabel => _resolutionLabel ??=
        Private(typeof(JellyfinService), "ResolutionLabel");

    /// <summary>The disk cache's OWN serializer settings, not a fresh
    /// <see cref="JsonSerializerOptions"/>: a naming policy or converter added there is exactly
    /// the change that would break the stored list, so the round trip has to run through it.</summary>
    private static JsonSerializerOptions CacheOptions => _cacheOptions ??=
        (JsonSerializerOptions)(typeof(DiskJsonCache)
            .GetField("Options", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException(
                "DiskJsonCache has no private static Options; the cache's serializer settings "
                + "were renamed, so this fixture is testing nothing."));

    /// <param name="test">The harness's own test recorder.</param>
    public static void Run(Action<string, Action> test)
    {
        test("an unnamed media source labels itself by resolution, codec and container", () =>
        {
            Expect("1080p HEVC (mkv)", Label(Video(1920, 1080, "hevc"), "mkv"));
            Expect("720p H264 (mp4)", Label(Video(1280, 720, "h264"), "mp4"));
        });

        test("a scope-ratio 4K source is 4K, not the 1440p its height says", () =>
        {
            // 3840x1600 is 2.40:1 4K. Height buckets alone call it 1440p, which would sort the
            // remux BELOW a 1080p pillarboxed version in the picker.
            Expect("4K HEVC (mkv)", Label(Video(3840, 1600, "hevc"), "mkv"));
            Expect("4K", Resolution(3840, 2160));
            Expect("1440p", Resolution(2560, 1440));
            Expect("1080p", Resolution(1920, 1080));
            Expect("480p", Resolution(854, 480));
            Expect("360p", Resolution(640, 360));
        });

        test("a source missing dimensions or codec still gets a usable label", () =>
        {
            Expect("mp4", Label(null, "mp4"));
            Expect("HEVC", Label(Video(null, null, "hevc"), null));
            Expect("1080p (mkv)", Label(Video(1920, 1080, null), "mkv"));
            Expect("Unknown version", Label(null, null));
            Expect("", Resolution(null, null));
        });

        test("same-named versions are told apart by size, else bitrate", () =>
        {
            Expect("2 GB", Version(sizeBytes: 2L * 1024 * 1024 * 1024, bitrate: 12_000_000).Detail());
            Expect("12 Mbps", Version(sizeBytes: null, bitrate: 12_000_000).Detail());
            Expect("3.5 Mbps", Version(sizeBytes: null, bitrate: 3_500_000).Detail());
            Expect("", Version(sizeBytes: null, bitrate: null).Detail());
        });

        test("a version list survives the metadata cache's JSON round trip", () =>
        {
            // The list is stored as a List<MediaVersion> under versions:{server}:{item} for an
            // hour, so a property name that does not survive serialization comes back as a
            // default: the picker would lose the size column, or every row would claim to be
            // the default source. MediaVersion is a positional record, which is the case that
            // actually needs proving - the parameter names have to match back on read.
            var original = new List<MediaVersion>
            {
                new("a1b2", "4K HEVC (mkv)", "mkv", 68_000_000, 3840, 2160,
                    24L * 1024 * 1024 * 1024, IsDefault: true),
                new("c3d4", "1080p H264 (mp4)", null, null, null, null, null, IsDefault: false),
            };
            var round = JsonSerializer.Deserialize<List<MediaVersion>>(
                JsonSerializer.Serialize(original, CacheOptions), CacheOptions)
                ?? throw new InvalidOperationException("The cached version list read back as null.");
            if (round.Count != original.Count)
                throw new InvalidOperationException(
                    $"Expected {original.Count} versions back, got {round.Count}.");
            for (var i = 0; i < original.Count; i++)
                if (round[i] != original[i])
                    throw new InvalidOperationException(
                        $"Round trip turned {original[i]} into {round[i]}.");
        });
    }

    private static MethodInfo Private(Type owner, string name)
        => owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{owner.Name} has no private static {name}; it was renamed or its signature "
                + "changed, so this fixture is testing nothing.");

    private static MediaStream Video(int? width, int? height, string? codec)
        => new() { Type = MediaStream_Type.Video, Width = width, Height = height, Codec = codec };

    private static MediaVersion Version(long? sizeBytes, int? bitrate)
        => new("source-id", "Version", "mkv", bitrate, 1920, 1080, sizeBytes, IsDefault: true);

    private static string Label(MediaStream? video, string? container)
        => (string)DerivedLabel.Invoke(null, [video, container])!;

    private static string Resolution(int? width, int? height)
        => (string)ResolutionLabel.Invoke(null, [width, height])!;

    private static void Expect(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected \"{expected}\", got \"{actual}\".");
    }
}
