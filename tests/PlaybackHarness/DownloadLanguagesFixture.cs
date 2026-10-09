using LightWeaver.Localization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Views;

internal static class DownloadLanguagesFixture
{
    private static int _checks;
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lw-download-languages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Case("language plans", () => { Plans(directory); return Task.CompletedTask; });
            await Case("local playback tracks", () => { PlaybackTracks(directory); return Task.CompletedTask; });
            await Case("replacement generation preferences", () => { ReplacementGeneration(); return Task.CompletedTask; });
            await Case("source metadata and URLs", MetadataAndUrls);
            await Case("package completion and retry", () => PackageRetry(directory));
            await Case("cancelled sidecar cleanup", () => CancelPackage(directory));
            await Case("subtitle origin and ownership", () => OriginAndOwnership(directory));
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary directory.");
            Directory.Delete(directory, recursive: true);
        }
        return Result();
    }

    private static DownloadItem Item(string directory, string origin = "http://127.0.0.1") => new()
    {
        ItemId = Guid.NewGuid(), Title = "Synthetic language episode", ServerUrl = origin,
        FilePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mkv"),
        AddedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static DownloadOption[] Options(string source = "source-a")
    {
        DownloadTrack[] audio = [new(3, 0, "English main", "eng", "Main", "aac", false, false, true),
            new(9, 1, "German main", "deu", "Main", "aac", false, false, false)];
        DownloadTrack[] subtitles = [new(5, 0, "French full", "fra", "Full", "subrip", false, true, false),
            new(11, -1, "English external", "eng", "Full", "ass", true, true, false)];
        var original = new DownloadOption("Original", source, true, null, null, 100000, "mkv", "Original")
            { AudioTracks = audio, SubtitleTracks = subtitles };
        return [original, new DownloadOption("720p", source, false, 1280, 4000000, 50000, "mp4", "720p")
            { AudioTracks = audio, SubtitleTracks = subtitles, OriginalOption = original }];
    }

    private static void Plans(string directory)
    {
        var options = Options();
        var audio = options[0].AudioTracks[1];
        var subtitle = options[0].SubtitleTracks[0];
        var item = Item(directory);
        var selection = new DownloadSelection(options[1], audio, Subtitle: subtitle)
            { PlaybackPreferenceTimestampMs = 1000 };
        var selected = DownloadLanguages.ApplyToItem(selection, item);
        Check(selected.IsTranscoded && selected.AudioStreamIndex == 9 && selected.PreferredAudioOrdinal == 0,
            "a smaller video retains explicitly selected German audio rather than the default English track");
        Check(selected.SubtitleFiles is [{ StreamIndex: 5, Codec: "subrip" }]
            && selected.PreferredSubtitleFileIndex == 5 && selected.PreferredSubtitleOrdinal == -1
            && selected.SubtitleFiles[0].FilePath.EndsWith(".srt"),
            "transcoded French text subtitles remain a switchable sidecar with their saved initial selection");
        Check(DownloadLanguages.EffectiveQuality(selection with { Audio = null }).IsOriginal,
            "All audio languages requires Original rather than dropping all but one audio track");
        var original = DownloadLanguages.ApplyToItem(selection with { Quality = options[0] }, item);
        Check(!original.IsTranscoded && original.AudioStreamIndex is null && original.PreferredAudioOrdinal == 1
            && original.PreferredSubtitleOrdinal == 0 && original.SubtitleFiles.Count == 0,
            "Original preserves embedded tracks and stores per-type initial playback ordinals");
        var all = DownloadLanguages.ApplyToItem(selection with { Quality = options[0], Audio = null,
            AllSubtitles = true, Subtitle = null }, item);
        Check(all.SubtitleFiles is [{ StreamIndex: 11, Codec: "ass" }]
            && all.PreferredAudioOrdinal is null && all.PreferredSubtitleOrdinal is null,
            "Original All retains embedded tracks and saves only external subtitle assets");
        var none = DownloadLanguages.ApplyToItem(selection with { Subtitle = null }, item);
        Check(none.SubtitleFiles.Count == 0 && none.PreferredSubtitleOrdinal == -1,
            "None saves no sidecars and explicitly disables initial subtitles");
        var graphic = new DownloadTrack(20, 1, "French PGS", "fra", "Full", "pgssub", false, false, false);
        var graphics = DownloadLanguages.ApplyToItem(selection with { Subtitle = graphic }, item);
        Check(!graphics.IsTranscoded && graphics.SubtitleFiles.Count == 0 && graphics.PreferredSubtitleOrdinal == 1,
            "embedded graphical subtitles force Original and are preserved without lossy text extraction");
        var next = Options("episode-source");
        var nextAudio = audio with { Index = 42, Language = "ger", TypeOrdinal = 2 };
        var nextSubtitle = subtitle with { Index = 77, Language = "fr", TypeOrdinal = 3 };
        var episodeQuality = next[1] with
        {
            AudioTracks = [nextAudio with { Index = 41, Title = "Commentary", IsDefault = true }, nextAudio],
            SubtitleTracks = [nextSubtitle],
        };
        var resolved = DownloadLanguages.ResolveForEpisode(selection, [next[0], episodeQuality]);
        Check(resolved?.Audio?.Index == 42 && resolved.Subtitle?.Index == 77
            && resolved.Quality.MediaSourceId == "episode-source",
            "batch aliases resolve each episode's indices and preserve Main instead of same-language commentary");
        Check(resolved is { PlaybackPreferenceTimestampMs: 1000 }
            && resolved.Quality.Resolution == "720p"
            && DownloadLanguages.ApplyToItem(resolved, item).PlaybackPreferenceTimestampMs == 1000,
            "batch source and quality remapping preserve the original user playback preference timestamp");
        Check(DownloadLanguages.ResolveForEpisode(selection,
            [episodeQuality with { AudioTracks = [options[0].AudioTracks[0]] }]) is null,
            "a batch episode without requested German audio is skipped");
        Check(DownloadLanguages.ResolveForEpisode(selection,
            [episodeQuality with { SubtitleTracks = [] }]) is null,
            "a batch episode without requested French subtitles is skipped");
        Check(DownloadLanguages.ResolveForEpisode(selection,
            [episodeQuality with { AudioTracks = [nextAudio with { Title = "Commentary" }] }]) is null,
            "duplicate-language matching never substitutes commentary for a missing named Main track");
        var automatic = selection with { UseDefaultAudio = true, Subtitle = null };
        var otherDefault = options[0].AudioTracks[0] with { Index = 60, Title = "Stereo", IsDefault = true };
        var automaticEpisode = episodeQuality with { AudioTracks = [otherDefault, nextAudio] };
        Check(DownloadLanguages.ResolveForEpisode(automatic, [automaticEpisode]) is { Audio.Index: 60, Quality.Resolution: "720p" },
            "untouched automatic audio uses each episode's default instead of skipping a different track title");
        Check(DownloadLanguages.ResolveForEpisode(automatic with { PreferredAudioLanguage = "deu" },
            [automaticEpisode]) is { Audio.Index: 42, Quality.Resolution: "720p" },
            "automatic preferred audio maps the preferred language to each episode's own index");
        Check(DownloadLanguages.ResolveForEpisode(automatic with { PreferredAudioLanguage = "jpn" },
            [automaticEpisode]) is { Audio.Index: 60 },
            "an unavailable automatic language preference falls back to that episode's default");
        Check(DownloadLanguages.ResolveForEpisode(automatic, [options[0]]) is null,
            "a missing batch quality is reported instead of silently upgrading to Original");
        Check(DownloadLanguages.ResolveForEpisode(selection,
            [episodeQuality with { SubtitleTracks = [nextSubtitle with { Codec = "pgssub", IsText = false }] }]) is null,
            "a later episode's incompatible graphical subtitles cannot silently upgrade a smaller batch quality");
        Check(selected.FilePath != DownloadLanguages.ApplyToItem(selection with { Audio = options[0].AudioTracks[0] }, item).FilePath
            && selected.FilePath != DownloadLanguages.ApplyToItem(selection with { Quality = Options("source-b")[1] }, item).FilePath
            && selected.FilePath != none.FilePath,
            "different audio, source and subtitle plans cannot append into the same video or asset filenames");
        var roundTrip = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(selected))!;
        Check(roundTrip.AudioStreamIndex == 9 && roundTrip.PreferredSubtitleFileIndex == 5
            && roundTrip.SubtitleFiles.SequenceEqual(selected.SubtitleFiles), "saved language plans survive JSON persistence");
        Check(roundTrip.PlaybackPreferenceTimestampMs == 1000
            && roundTrip.AudioTracks.SequenceEqual(options[1].AudioTracks)
            && roundTrip.SubtitleTracks.SequenceEqual(options[1].SubtitleTracks),
            "saved timestamps and source-specific track manifests survive JSON persistence");
        var legacy = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(new
        {
            item.ItemId, item.Title, item.ServerUrl, item.FilePath, item.AddedTimestampMs,
        }))!;
        Check(!legacy.DownloadLanguagesSelected && legacy.AudioStreamIndex is null
            && legacy.PreferredAudioOrdinal is null && legacy.SubtitleFiles.Count == 0 && !legacy.VideoCompleted,
            "legacy index JSON leaves language behavior unchanged and adds no invented sidecars");
    }

    private static void PlaybackTracks(string directory)
    {
        var item = Package(directory, "http://127.0.0.1", 1) with { PreferredSubtitleFileIndex = 1 };
        LightWeaver.Player.MpvTrack[] tracks =
        [new(1, "sub", "French", "fra", false, false, false),
         new(2, "sub", "French", "fra", false, false, false, ExternalFilename: Path.Combine(directory, "different.srt")),
         new(3, "sub", "French", "fra", false, false, false, ExternalFilename: item.SubtitleFiles[0].FilePath),
         new(4, "sub", "French", "fra", false, false, false)];
        Check(DownloadPlayback.PreferredSubtitleTrack(item, tracks)?.Id == 3,
            "saved sidecar preference selects its exact filename despite duplicate subtitle titles");
        Check(DownloadPlayback.PreferredSubtitleTrack(item with { PreferredSubtitleFileIndex = null,
            PreferredSubtitleOrdinal = 1 }, tracks)?.Id == 4,
            "embedded subtitle ordinals exclude intervening external sidecars");
        Check(DownloadPlayback.PreferredSubtitleTrack(item, tracks.Where(t => t.Id != 3).ToArray()) is null,
            "a missing preferred sidecar never substitutes another same-title subtitle");
        var row = new DownloadRow(item with { VideoCompleted = true, Status = DownloadStatus.Failed,
            ErrorMessage = "Synthetic subtitle failure" });
        Check(row.CanPlay && row.CanResume && row.PlaybackText.Contains("subtitles are still pending")
            && !row.PlaybackText.Contains("Ready offline", StringComparison.OrdinalIgnoreCase),
            "a failed subtitle package exposes playable video and retry without claiming ready offline");

        var selection = new DownloadSelection(Options()[1], Options()[1].AudioTracks[1],
            Subtitle: Options()[1].SubtitleTracks[0]) { PlaybackPreferenceTimestampMs = 2000 };
        var selected = DownloadLanguages.ApplyToItem(selection, Item(directory));
        Check(DownloadPlayback.UseSavedSubtitlePreference(selected, 1000)
            && DownloadPlayback.UseSavedSubtitlePreference(selected, 2000),
            "a new download subtitle choice takes precedence over an older sticky player choice");
        Check(!DownloadPlayback.UseSavedSubtitlePreference(selected, 3000),
            "a newer in-player subtitle choice takes precedence over the saved download choice");
        Check(!DownloadPlayback.UseSavedSubtitlePreference(Item(directory), 0),
            "legacy downloads without an explicit language choice do not override player preferences");
        var localAudio = new LightWeaver.Player.MpvTrack(1, "audio", "German main", "deu", true, false, false);
        var localFrench = new LightWeaver.Player.MpvTrack(2, "sub", "French full", "fra", true, false, false,
            ExternalFilename: selected.SubtitleFiles[0].FilePath);
        LightWeaver.Player.MpvTrack[] localTracks =
        [localAudio, new(3, "sub", "French full", "fra", false, false, false,
            ExternalFilename: Path.Combine(directory, "duplicate-title.srt")), localFrench];
        var fallback = DownloadPlayback.FallbackTracks(selected, localTracks, fileLoaded: true);
        Check(fallback.Audio?.Index == 9 && fallback.Subtitle?.Index == 5 && !fallback.SubtitleOff,
            "transcoded German output track 1 and exact French sidecar map back to source indices 9 and 5");
        var original = DownloadLanguages.ApplyToItem(selection with { Quality = Options()[0] }, Item(directory));
        LightWeaver.Player.MpvTrack[] embeddedTracks =
        [new(1, "audio", "English main", "eng", false, true, false),
         new(2, "audio", "German main", "deu", true, false, false),
         new(3, "sub", "French full", "fra", true, false, false)];
        fallback = DownloadPlayback.FallbackTracks(original, embeddedTracks, fileLoaded: true);
        Check(fallback.Audio?.Index == 9 && fallback.Subtitle?.Index == 5 && !fallback.SubtitleOff,
            "Original local audio ordinal 1 and embedded subtitle ordinal 0 map to source indices 9 and 5");
        fallback = DownloadPlayback.FallbackTracks(selected,
            localTracks.Select(t => t.Type == "sub" ? t with { Selected = false } : t).ToArray(),
            fileLoaded: true, explicitSubtitleOff: true);
        Check(fallback.Subtitle is null && fallback.SubtitleOff,
            "server fallback preserves manual subtitles Off after the local file has loaded");
        fallback = DownloadPlayback.FallbackTracks(selected, [localAudio], fileLoaded: true);
        Check(fallback.Subtitle?.Index == 5 && !fallback.SubtitleOff,
            "loaded video awaiting its French sidecar falls back to source subtitle 5 instead of treating pending subtitles as Off");
        fallback = DownloadPlayback.FallbackTracks(selected, [], fileLoaded: false);
        Check(fallback.Audio?.Index == 9 && fallback.Subtitle?.Index == 5 && !fallback.SubtitleOff,
            "failure before local track loading uses the saved German and French source selections");
        var all = DownloadLanguages.ApplyToItem(selection with { Audio = null, AllSubtitles = true,
            Subtitle = null }, Item(directory));
        fallback = DownloadPlayback.FallbackTracks(all, [], fileLoaded: false);
        Check(fallback.Subtitle is null && !fallback.SubtitleOff,
            "All subtitles before local loading retains automatic server subtitle selection rather than Off");
        fallback = DownloadPlayback.FallbackTracks(all, [localAudio], fileLoaded: true);
        Check(fallback.Subtitle is null && !fallback.SubtitleOff,
            "All subtitles awaiting local subtitle tracks retains automatic selection rather than Off");
        var none = DownloadLanguages.ApplyToItem(selection with { Subtitle = null }, Item(directory));
        Check(DownloadPlayback.FallbackTracks(none, [], fileLoaded: false).SubtitleOff,
            "explicit None remains subtitles Off when local loading fails");
        fallback = DownloadPlayback.FallbackTracks(original, embeddedTracks.Select(t => t.Type == "audio"
            ? t with { Selected = t.Id == 1 } : t).ToArray(), fileLoaded: true);
        Check(fallback.Audio?.Index == 3,
            "Original fallback honors a manual local English selection over the saved German preference");
    }

    private static void ReplacementGeneration()
    {
        // This factory reads only playback fields. Skip Window's constructor so the contract
        // remains windowless while exercising the production generation state.
        var shell = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var factory = typeof(MainWindow).GetMethod("NewSubtitleGenerationState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var audio = new LightWeaver.Player.MpvTrack(1, "audio", "English main", "eng", true, true, false);
        var restore = new MainWindow.QualityTrackSnapshot(audio, null, SubtitleOff: true, null, null);
        var state = factory.Invoke(shell, [2, Task.CompletedTask, restore])!;
        var stateType = state.GetType();
        bool Applied(string name) => (bool)stateType.GetProperty(name)!.GetValue(state)!;
        Check(Applied("DownloadAudioApplied") && Applied("DownloadSubtitleApplied")
            && ReferenceEquals(stateType.GetProperty("RestoreTracks")!.GetValue(state), restore),
            "replacement generation marks restored manual English and subtitle Off as already applied");
        stateType.GetProperty("RestoreTracks")!.SetValue(state, null);
        Check(Applied("DownloadAudioApplied") && Applied("DownloadSubtitleApplied"),
            "settling replacement track restoration retains flags so a later track wave cannot reapply saved German defaults");
        var sourceOnly = new MainWindow.QualityTrackSnapshot(null, null, false,
            new MediaStreamChoice(9, 1, "German main", "deu", "Main", false, false), null);
        state = factory.Invoke(shell, [3, Task.CompletedTask, sourceOnly])!;
        Check(Applied("DownloadAudioApplied") && Applied("DownloadSubtitleApplied"),
            "replacement generation also protects source-only audio restoration from download defaults");
        state = factory.Invoke(shell, [4, Task.CompletedTask, null])!;
        Check(!Applied("DownloadAudioApplied") && !Applied("DownloadSubtitleApplied"),
            "a first playback generation without restoration remains eligible for saved download preferences");
    }

    private static async Task MetadataAndUrls()
    {
        var user = Guid.NewGuid();
        using var server = new Server(async (context, token) =>
        {
            if (context.Request.Url!.AbsolutePath.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase))
                await Reply(context, JsonSerializer.Serialize(new { Id = user, Name = "SYNTHETIC" }), token);
            else
                await Reply(context, """
                    {"MediaSources":[{"Id":"source a","Container":"mkv","Size":100000000000,"RunTimeTicks":27000000000,
                    "MediaStreams":[{"Index":0,"Type":"Video","Width":3840,"Height":2160},
                    {"Index":3,"Type":"Audio","Language":"eng","Title":"Main","Codec":"aac","IsDefault":true},
                    {"Index":9,"Type":"Audio","Language":"deu","Title":"Main","Codec":"aac"},
                    {"Index":5,"Type":"Subtitle","Language":"fra","Codec":"subrip","IsTextSubtitleStream":true},
                    {"Index":11,"Type":"Subtitle","Language":"eng","Codec":"ass","IsExternal":true,"IsTextSubtitleStream":true}]},
                    {"Id":"different-source","Container":"mp4","MediaStreams":[{"Index":0,"Type":"Video","Height":1080},
                    {"Index":3,"Type":"Audio","Language":"jpn","Codec":"aac"}]}]}
                    """, token);
        });
        using var service = new JellyfinService();
        Check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "SYNTHETIC", "synthetic-language-fixture", user)),
            "synthetic language metadata session connects without accessing saved credentials");
        var id = Guid.NewGuid();
        var options = await service.GetDownloadOptionsAsync(id, CancellationToken.None);
        Check(options.Count > 1 && options.All(o => o.MediaSourceId == "source a"
            && o.AudioTracks.Select(a => a.Index).SequenceEqual([3, 9])
            && o.SubtitleTracks.Select(s => s.Index).SequenceEqual([5, 11])),
            "SDK options keep tracks from their selected source and exclude a second source's conflicting indices");
        Check(options[0].SubtitleTracks[0].TypeOrdinal == 0 && options[0].SubtitleTracks[1].TypeOrdinal == -1
            && options[0].SubtitleTracks[1].IsExternal && options[0].SubtitleTracks[1].IsText,
            "SDK metadata distinguishes embedded ordinals, external assets and extractable text");
        var url = new Uri(service.GetDownloadUrl(id, "source a", 1280, 4000000, "mp4", 9));
        var query = Query(url);
        Check(query["audioStreamIndex"] == "9" && query["subtitleStreamIndex"] == "-1"
            && query["mediaSourceId"] == "source a" && query["static"] == "false"
            && !query.ContainsKey("ApiKey") && !query.ContainsKey("api_key"),
            "transcode URL specifies German audio and subtitles-off without putting authorization into its query");
        query = Query(new Uri(service.GetDownloadUrl(id, "source a")));
        Check(query["static"] == "true" && !query.ContainsKey("audioStreamIndex") && !query.ContainsKey("subtitleStreamIndex"),
            "Original URL preserves embedded tracks instead of asking the server to remove tracks");
        var sidecar = new Uri(service.GetDownloadSubtitleUrl(id, "source a", 11, "ass"));
        Check(Uri.UnescapeDataString(sidecar.AbsolutePath).EndsWith("/source a/Subtitles/11/Stream.ass")
            && sidecar.Query.Length == 0, "subtitle URL retains source, stream index and ASS format without query authorization");
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
            parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "", StringComparer.OrdinalIgnoreCase);

    private static DownloadItem Package(string directory, string origin, int subtitles = 2)
    {
        var item = Item(directory, origin) with { MediaSourceId = "package-source", DownloadLanguagesSelected = true };
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(item.FilePath));
        return item with { SubtitleFiles = Enumerable.Range(1, subtitles).Select(index =>
            new DownloadSubtitleFile(index, "srt", $"{stem}.sub-{index}.srt", "Synthetic subtitle", "fra", false)).ToArray() };
    }

    private static DownloadManager Manager(Server server, Func<DownloadItem, DownloadSubtitleFile, string?>? subtitleUrl = null)
        => new(() => 1, _ => server.Origin + "/video", _ => "synthetic-fixture-auth",
            subtitleUrl ?? ((_, subtitle) => server.Origin + "/subtitle/" + subtitle.StreamIndex));

    private static async Task PackageRetry(string directory)
    {
        var firstReached = Signal();
        var firstRelease = Signal();
        var retryReached = Signal();
        var retryRelease = Signal();
        var attempts = 0;
        using var server = new Server(async (context, token) =>
        {
            if (context.Request.Url!.AbsolutePath == "/subtitle/2")
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstReached.TrySetResult(true);
                    await firstRelease.Task.WaitAsync(token);
                    await Reply(context, "synthetic failure", token, 503);
                    return;
                }
                retryReached.TrySetResult(true);
                await retryRelease.Task.WaitAsync(token);
            }
            await Reply(context, context.Request.Url.AbsolutePath == "/video" ? "synthetic video bytes" : "1\n00:00:00,000 --> 00:00:01,000\nBonjour\n", token);
        });
        using var manager = Manager(server);
        var item = Package(directory, server.Origin);
        var completed = new ConcurrentQueue<DownloadItem>();
        manager.Completed += completed.Enqueue;
        try
        {
            manager.Enqueue(item);
            await firstReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = manager.Snapshot().Single(i => i.ItemId == item.ItemId);
            Check(waiting.VideoCompleted && waiting.Status == DownloadStatus.Downloading && !manager.IsDownloaded(item.ItemId)
                && completed.IsEmpty && File.Exists(item.SubtitleFiles[0].FilePath) && !File.Exists(item.SubtitleFiles[1].FilePath),
                "video EOF and one saved subtitle do not complete a package while another subtitle is pending");
            firstRelease.TrySetResult(true);
            await Eventually(() => manager.Snapshot().Any(i => i.ItemId == item.ItemId && i.Status == DownloadStatus.Failed), "subtitle failure");
            Check(manager.GetCompletedDownload(item.ItemId)?.VideoCompleted == true && !manager.IsDownloaded(item.ItemId),
                "subtitle failure retains playable completed video but does not claim a complete offline package");
            manager.Resume(item.ItemId);
            await retryReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(server.Count("/video") == 1 && server.Count("/subtitle/1") == 1 && completed.IsEmpty,
                "retry fetches only the missing subtitle and never redownloads saved video or successful sidecars");
            retryRelease.TrySetResult(true);
            await Eventually(() => completed.Count == 1, "package completed event");
            Check(manager.IsDownloaded(item.ItemId) && item.SubtitleFiles.All(s => File.ReadAllText(s.FilePath).Contains("Bonjour"))
                && item.SubtitleFiles.All(s => !File.Exists(s.FilePath + ".part")),
                "Completed fires only after every subtitle has been atomically saved with its complete contents");
            manager.ClearCompleted();
            Check(!manager.IsTracked(item.ItemId) && File.Exists(item.FilePath) && item.SubtitleFiles.All(s => File.Exists(s.FilePath)),
                "Clear completed drops the package record while keeping video and sidecars on disk");
            manager.Enqueue(completed.Single());
            await Eventually(() => manager.IsDownloaded(item.ItemId), "restored package completion");
            foreach (var subtitle in item.SubtitleFiles) File.WriteAllText(subtitle.FilePath + ".part", "synthetic interrupted sidecar");
            manager.Remove(item.ItemId);
            await Eventually(() => !File.Exists(item.FilePath) && item.SubtitleFiles.All(s =>
                !File.Exists(s.FilePath) && !File.Exists(s.FilePath + ".part")), "owned package deletion");
            Check(!manager.IsTracked(item.ItemId) && !File.Exists(item.FilePath) && item.SubtitleFiles.All(s =>
                !File.Exists(s.FilePath) && !File.Exists(s.FilePath + ".part")),
                "Remove deletes owned video, complete sidecars, partial sidecars and its record");
        }
        finally { firstRelease.TrySetResult(true); retryRelease.TrySetResult(true); manager.Remove(item.ItemId); }
    }

    private static async Task CancelPackage(string directory)
    {
        var started = Signal();
        var release = Signal();
        using var server = new Server(async (context, token) =>
        {
            if (context.Request.Url!.AbsolutePath == "/video")
            {
                await Reply(context, "synthetic video", token);
                return;
            }
            context.Response.ContentLength64 = 1024;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("half"), token);
            await context.Response.OutputStream.FlushAsync(token);
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            context.Response.Abort();
        });
        using var manager = Manager(server);
        var item = Package(directory, server.Origin, 1);
        var subtitle = item.SubtitleFiles[0];
        try
        {
            manager.Enqueue(item);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Eventually(() => File.Exists(subtitle.FilePath + ".part"), "partial subtitle write");
            Check(!File.Exists(subtitle.FilePath) && !manager.IsDownloaded(item.ItemId),
                "interrupted subtitle bodies never appear under the ready sidecar filename");
            manager.Remove(item.ItemId);
            release.TrySetResult(true);
            await Eventually(() => !File.Exists(item.FilePath) && !File.Exists(subtitle.FilePath)
                && !File.Exists(subtitle.FilePath + ".part"), "cancelled package file cleanup");
            Check(!manager.IsTracked(item.ItemId) && !File.Exists(item.FilePath)
                && !File.Exists(subtitle.FilePath) && !File.Exists(subtitle.FilePath + ".part"),
                "cancelling a package removes partial atomic subtitle writes and completed video");
        }
        finally { release.TrySetResult(true); manager.Remove(item.ItemId); }
    }

    private static async Task OriginAndOwnership(string directory)
    {
        using var server = new Server((context, token) => Reply(context, "synthetic video", token));
        using var foreign = new Server((context, token) => Reply(context, "unexpected foreign response", token));
        using var manager = Manager(server, (_, _) => foreign.Origin + "/subtitle/1");
        var item = Package(directory, server.Origin, 1);
        manager.Enqueue(item);
        await Eventually(() => manager.Snapshot().Any(i => i.ItemId == item.ItemId && i.Status == DownloadStatus.Failed), "foreign subtitle rejection");
        Check(foreign.Paths.IsEmpty && !File.Exists(item.SubtitleFiles[0].FilePath),
            "a different subtitle origin is rejected before any HTTP request or ready file is created");
        manager.Remove(item.ItemId);
        await Eventually(() => !File.Exists(item.FilePath), "foreign-origin video cleanup");

        var owned = Package(directory, server.Origin, 1);
        var unrelated = Path.Combine(directory, "unrelated-" + Guid.NewGuid().ToString("N") + ".srt");
        File.WriteAllText(unrelated, "do not delete");
        File.WriteAllText(owned.FilePath, "video");
        File.WriteAllText(owned.SubtitleFiles[0].FilePath, "subtitle");
        owned = owned with { VideoCompleted = true, TotalBytes = 5,
            SubtitleFiles = [owned.SubtitleFiles[0], new(99, "srt", unrelated, null, null, false)] };
        manager.Enqueue(owned);
        await Eventually(() => manager.Snapshot().Any(i => i.ItemId == owned.ItemId && i.Status == DownloadStatus.Failed), "unowned subtitle rejection");
        manager.Remove(owned.ItemId);
        await Eventually(() => !File.Exists(owned.FilePath) && !File.Exists(owned.SubtitleFiles[0].FilePath), "owned subtitle cleanup");
        Check(File.ReadAllText(unrelated) == "do not delete", "asset deletion preserves an unrelated file named by an invalid manifest");
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Eventually(Func<bool> condition, string operation)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(operation);
            await Task.Delay(20);
        }
    }

    private static async Task Case(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) { Check(false, $"{name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task Reply(HttpListenerContext context, string body, CancellationToken token, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, token);
        context.Response.Close();
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _responses = [];
        private readonly Task _loop;
        internal ConcurrentQueue<string> Paths { get; } = new();
        internal string Origin { get; }
        internal int Count(string path) => Paths.Count(p => p == path);

        internal Server(Func<HttpListenerContext, CancellationToken, Task> respond)
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                    catch (Exception) when (_stop.IsCancellationRequested) { return; }
                    Paths.Enqueue(context.Request.Url!.AbsolutePath);
                    _responses.Add(Handle(context));
                }
            });

            async Task Handle(HttpListenerContext context)
            {
                try { await respond(context, _stop.Token); }
                catch (Exception) when (_stop.IsCancellationRequested) { }
                catch (HttpListenerException) { }
                catch (IOException) { }
                finally { context.Response.Abort(); }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            Task.WhenAll(_responses).GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }

    // The modal and its controls run only in the approved guest verification session.
    public static int RunUi()
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Download language UI requires the approved Windows verification guest.");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        var app = UiFixtureApplication.Create();
        var main = new MainWindow { Width = 1280, Height = 760 };
        try
        {
            main.Show();
            main.Activate();
            Pump(150);
            var modal = (ModalHost)main.FindName("Modal");
            var quality = (ListBox)modal.FindName("ChoiceList");
            var confirm = (Button)modal.FindName("ConfirmButton");
            var cancel = (Button)modal.FindName("CancelButton");
            L.Initialize("en");
            var localizedPicker = new DownloadOptionsPicker(Options(), "deu", "fra");
            var localizedChoice = localizedPicker.ChooseAsync(modal, "Localized download", 1);
            Pump(550);
            var localizedItem = DownloadLanguages.ApplyToItem(localizedPicker.Selection, Item(Path.GetTempPath()));
            var originalPath = localizedItem.FilePath;
            foreach (var language in new[] { "de", "ar" })
            {
                L.Initialize(language);
                Pump(550);
                Check(localizedPicker.Children.OfType<TextBlock>().Select(t => t.Text).SequenceEqual(
                    new[] { L.Text("Downloads.Audio"), L.Text("Downloads.Subtitles") })
                    && confirm.Content?.ToString() == L.Text("Downloads.Download")
                    && ((TextBlock)modal.FindName("TitleText")).Text == L.Text("Downloads.Download")
                    && ((ListBoxItem)quality.ItemContainerGenerator.ContainerFromIndex(0)).Content?.ToString() == L.Text("DownloadsView.Original.b0322603")
                    && ((ComboBoxItem)localizedPicker.AudioChoice.Items[0]).Content?.ToString() == L.Text("Downloads.AllAudioLanguages")
                    && ((ComboBoxItem)localizedPicker.SubtitleChoice.Items[0]).Content?.ToString() == L.Text("Downloads.None"),
                    language + " download labels and choices refresh in the open modal");
                Check(quality.SelectedIndex == 1 && localizedPicker.Selection.Audio?.Index == 9
                    && localizedPicker.Selection.Subtitle?.Index == 5 && localizedPicker.Selection.UseDefaultAudio
                    && !ChoiceEnabled(localizedPicker.AudioChoice, 0) && confirm.IsEnabled,
                    language + " refresh preserves 720p, automatic named tracks and unavailable All audio");
                Check(DownloadLanguages.FormatSummary(localizedItem) == L.Format("Downloads.LanguageSummary", "German main", "French full")
                    && localizedItem.FilePath == originalPath && localizedItem.AudioStreamIndex == 9,
                    language + " existing download summary translates without changing path or saved stream indices");
                localizedPicker.AudioChoice.IsDropDownOpen = true;
                Pump(550);
                Capture(main, "localized-" + language);
                localizedPicker.AudioChoice.IsDropDownOpen = false;
            }
            L.Initialize("en");
            Pump(150);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(localizedChoice.GetAwaiter().GetResult() is null, "localized picker cancels without queueing a download");
            var defaults = Options();
            DownloadOption[] tiers = [defaults[0], defaults[1] with { Resolution = "1080p", Label = "1080p", MaxWidth = 1920 },
                defaults[1], defaults[1] with { Resolution = "480p", Label = "480p", MaxWidth = 854 }];
            var picker = new DownloadOptionsPicker(tiers);
            var choice = picker.ChooseAsync(modal, "Default Original download", 0);
            Pump(550);
            Check(quality.SelectedIndex == 0 && picker.Selection.Audio?.Index == 3
                && picker.Selection.UseDefaultAudio && !picker.Selection.AllSubtitles && picker.Selection.Subtitle is null && confirm.IsEnabled,
                "default Original starts with named default audio and None subtitles, not implicit All");
            Check(picker.Children.OfType<TextBlock>().Count() == 2
                && !Elements<TextBlock>(picker).Any(t => AutomationProperties.GetAutomationId(t) == "DownloadLanguageExplanation"),
                "the download picker contains only Audio and Subtitles labels without explanatory subtext");
            for (var tier = 1; tier < tiers.Length; tier++)
            {
                quality.SelectedIndex = tier;
                quality.ScrollIntoView(quality.SelectedItem);
                Pump(150);
                Check(quality.SelectedIndex == tier && picker.Selection.Quality.Resolution == tiers[tier].Resolution
                    && confirm.IsEnabled,
                    $"clicking {tiers[tier].Resolution} from Original keeps the requested quality and enables Download");
            }
            Capture(main, "default-480p");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            var defaultPlan = choice.GetAwaiter().GetResult();
            Check(defaultPlan is { Quality.Resolution: "480p", Audio.Index: 3, AllSubtitles: false, Subtitle: null }
                && DownloadLanguages.ApplyToItem(defaultPlan, Item(Path.GetTempPath())).MaxWidth == 854,
                "confirmation from default Original produces a 480p plan instead of Original");

            picker = new DownloadOptionsPicker(Options(), "deu", "fra");
            choice = picker.ChooseAsync(modal, "Synthetic language episode", 0);
            Pump(550);
            Check(quality.SelectedIndex == 0 && picker.Selection.Audio?.Index == 9 && picker.Selection.Subtitle?.Index == 5,
                "preferred German audio and French subtitles are named even when Original is preselected");
            Check(ChoiceEnabled(picker.AudioChoice, 0) && ChoiceEnabled(picker.SubtitleChoice, 1),
                "Original makes All embedded audio and All text subtitles available");
            picker.AudioChoice.IsDropDownOpen = true;
            Pump(550);
            Capture(main, "audio-options-original");
            picker.AudioChoice.IsDropDownOpen = false;
            picker.SubtitleChoice.IsDropDownOpen = true;
            Pump(550);
            Capture(main, "subtitle-options-original");
            picker.SubtitleChoice.IsDropDownOpen = false;
            quality.SelectedIndex = 1;
            Pump(550);
            Check(AutomationProperties.GetAutomationId(quality) == "ModalChoiceList"
                && quality.SelectedIndex == 1 && picker.AudioChoice.SelectedIndex == 2
                && picker.SubtitleChoice.SelectedIndex == 2,
                "production modal preselects named German audio and French subtitles alongside 720p quality");
            Check(picker.Selection.Audio?.Index == 9 && picker.Selection.Subtitle?.Index == 5,
                "named picker choices retain source-specific audio and subtitle stream indices");
            Check(!ChoiceEnabled(picker.AudioChoice, 0) && ChoiceEnabled(picker.AudioChoice, 1)
                && ChoiceEnabled(picker.AudioChoice, 2) && ChoiceEnabled(picker.SubtitleChoice, 1),
                "720p disables All audio directly while keeping named audio and All text subtitles available");
            Capture(main, "selected");
            picker.AudioChoice.IsDropDownOpen = true;
            Pump(550);
            Check(picker.Selection.UseDefaultAudio,
                "inspecting the audio selector keeps the untouched automatic batch choice");
            Capture(main, "audio-options");
            ClickPopupChoice(main, (ComboBoxItem)picker.AudioChoice.Items[0]);
            Check(picker.AudioChoice.SelectedIndex == 2 && picker.Selection.Audio?.Index == 9
                && picker.Selection.UseDefaultAudio && quality.SelectedIndex == 1 && confirm.IsEnabled,
                "physically clicking disabled All audio keeps automatic named audio and the selected 720p quality");
            picker.AudioChoice.IsDropDownOpen = false;
            picker.AudioChoice.Focus();
            Pump(100);
            Key(main, picker.AudioChoice, 0x24);
            Check(picker.AudioChoice.SelectedIndex == 1 && picker.Selection.Audio?.Index == 3
                && ChoiceEnabled(picker.AudioChoice, picker.AudioChoice.SelectedIndex) && confirm.IsEnabled,
                "Home keyboard navigation skips disabled All audio and selects the first enabled named track");
            Key(main, picker.AudioChoice, 0x26);
            Check(picker.AudioChoice.SelectedIndex == 1 && picker.Selection.Audio?.Index == 3
                && quality.SelectedIndex == 1 && confirm.IsEnabled,
                "Up keyboard navigation cannot select disabled All audio above the first enabled track");
            picker.AudioChoice.SelectedIndex = 2;
            Check(!picker.Selection.UseDefaultAudio,
                "changing the audio selection pins the named choice for batch mapping");
            picker.SubtitleChoice.IsDropDownOpen = true;
            Pump(550);
            Capture(main, "subtitle-options");
            picker.SubtitleChoice.IsDropDownOpen = false;
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            var selected = choice.GetAwaiter().GetResult();
            Check(selected is { Audio.Index: 9, Subtitle.Index: 5, Quality.IsOriginal: false }
                && modal.Visibility == Visibility.Collapsed,
                "confirm returns explicit audio, subtitle and smaller quality selection");

            picker = new DownloadOptionsPicker(Options(), "deu", "fra");
            choice = picker.ChooseAsync(modal, "Synthetic language episode", 1);
            Pump(550);
            picker.AudioChoice.SelectedIndex = 0;
            picker.SubtitleChoice.SelectedIndex = 1;
            Pump(30);
            Check(quality.SelectedIndex == 1 && picker.Selection.Audio?.Index == 9 && picker.Selection.UseDefaultAudio
                && picker.Selection.AllSubtitles && confirm.IsEnabled && !ChoiceEnabled(picker.AudioChoice, 0),
                "a programmatic disabled All audio attempt returns to preferred embedded audio without changing 720p");
            quality.SelectedIndex = 0;
            Check(ChoiceEnabled(picker.AudioChoice, 0) && confirm.IsEnabled,
                "returning to Original re-enables All audio languages");
            picker.AudioChoice.SelectedIndex = 0;
            Check(picker.Selection.Audio is null && !picker.Selection.UseDefaultAudio && picker.Selection.AllSubtitles && confirm.IsEnabled,
                "Original accepts an explicit All audio and All subtitle selection");
            quality.SelectedIndex = 1;
            Check(quality.SelectedIndex == 1 && picker.Selection.Audio?.Index == 9 && picker.Selection.UseDefaultAudio
                && picker.Selection.AllSubtitles && confirm.IsEnabled,
                "changing Original All audio to 720p restores automatic preferred audio and keeps compatible All text subtitles");
            Capture(main, "all-to-720p");
            quality.SelectedIndex = 0;
            picker.AudioChoice.SelectedIndex = 0;
            Capture(main, "all");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is { Audio: null, AllSubtitles: true, Quality.IsOriginal: true },
                "confirmed All language download preserves every selected track through Original quality");

            var graphic = new DownloadTrack(20, 1, "French PGS", "fra", "Full", "pgssub", false, false, false);
            var textSubtitle = Options()[0].SubtitleTracks[1];
            var graphics = Options().Select(o => o with { SubtitleTracks = [graphic, textSubtitle] }).ToArray();
            picker = new DownloadOptionsPicker(graphics, "deu", "fra");
            choice = picker.ChooseAsync(modal, "Embedded graphical subtitles", 0);
            Pump(550);
            Check(picker.Selection.Subtitle?.Index == 20 && ChoiceEnabled(picker.SubtitleChoice, 1)
                && ChoiceEnabled(picker.SubtitleChoice, 2) && ChoiceEnabled(picker.SubtitleChoice, 3) && confirm.IsEnabled,
                "Original enables embedded graphical subtitles alongside All and separate text subtitles");
            picker.SubtitleChoice.IsDropDownOpen = true;
            Pump(550);
            Capture(main, "graphic-options-original");
            picker.SubtitleChoice.IsDropDownOpen = false;
            quality.SelectedIndex = 1;
            Check(quality.SelectedIndex == 1 && picker.SubtitleChoice.SelectedIndex == 0
                && picker.Selection.Subtitle is null && !picker.Selection.AllSubtitles && confirm.IsEnabled,
                "changing Original graphical subtitles to 720p restores None while keeping quality and Download usable");
            Check(ChoiceEnabled(picker.SubtitleChoice, 0) && !ChoiceEnabled(picker.SubtitleChoice, 1)
                && !ChoiceEnabled(picker.SubtitleChoice, 2) && ChoiceEnabled(picker.SubtitleChoice, 3),
                "720p disables graphical subtitles and incompatible All directly while keeping None and text subtitles available");
            picker.SubtitleChoice.IsDropDownOpen = true;
            Pump(550);
            Capture(main, "graphic-options-720p");
            ClickPopupChoice(main, (ComboBoxItem)picker.SubtitleChoice.Items[2]);
            Check(picker.SubtitleChoice.SelectedIndex == 0 && picker.Selection.Subtitle is null
                && !picker.Selection.AllSubtitles && quality.SelectedIndex == 1 && confirm.IsEnabled,
                "physically clicking disabled graphical subtitles keeps None and the selected 720p quality");
            picker.SubtitleChoice.IsDropDownOpen = false;
            picker.SubtitleChoice.SelectedIndex = 2;
            Check(picker.SubtitleChoice.SelectedIndex == 0 && quality.SelectedIndex == 1 && confirm.IsEnabled,
                "programmatic selection of a disabled graphical subtitle repairs to None without changing 720p");
            picker.SubtitleChoice.SelectedIndex = 1;
            Check(picker.SubtitleChoice.SelectedIndex == 0 && !picker.Selection.AllSubtitles && confirm.IsEnabled,
                "programmatic incompatible All subtitle selection repairs to None");
            picker.SubtitleChoice.SelectedIndex = 3;
            Check(picker.Selection.Subtitle?.Index == 11 && confirm.IsEnabled,
                "a compatible text subtitle remains selectable when graphical choices are unavailable");
            quality.SelectedIndex = 0;
            Check(ChoiceEnabled(picker.SubtitleChoice, 1) && ChoiceEnabled(picker.SubtitleChoice, 2),
                "returning to Original re-enables All and graphical subtitles");
            picker.SubtitleChoice.SelectedIndex = 1;
            quality.SelectedIndex = 1;
            Check(quality.SelectedIndex == 1 && picker.SubtitleChoice.SelectedIndex == 0 && confirm.IsEnabled,
                "changing Original All graphical subtitles to 720p repairs the subtitle choice to None");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is null, "graphical subtitle test cancels without enqueueing");

            picker = new DownloadOptionsPicker(graphics, "deu", "fra");
            choice = picker.ChooseAsync(modal, "Preferred graphical subtitles with smaller quality", 1);
            Pump(550);
            Check(quality.SelectedIndex == 1 && picker.Selection.Subtitle is null
                && picker.SubtitleChoice.SelectedIndex == 0 && !ChoiceEnabled(picker.SubtitleChoice, 2) && confirm.IsEnabled,
                "an initial 720p preset normalizes an incompatible preferred graphical subtitle to None");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is null, "preferred graphical subtitle test cancels without enqueueing");

            var externalAudio = Options()[0].AudioTracks[0] with
                { Index = 25, TypeOrdinal = -1, Display = "Japanese external", Language = "jpn", IsExternal = true, IsDefault = false };
            var external = Options().Select(o => o with { AudioTracks = [.. o.AudioTracks, externalAudio] }).ToArray();
            picker = new DownloadOptionsPicker(external, "jpn");
            choice = picker.ChooseAsync(modal, "Unsupported external audio", 0);
            Pump(550);
            Check(!ChoiceEnabled(picker.AudioChoice, 0) && !ChoiceEnabled(picker.AudioChoice, 3)
                && picker.Selection.Audio?.Index == 3 && confirm.IsEnabled,
                "Original disables external audio and All containing it while falling back to supported embedded audio");
            picker.AudioChoice.SelectedIndex = 3;
            Check(picker.Selection.Audio?.Index == 3 && picker.Selection.UseDefaultAudio && confirm.IsEnabled,
                "programmatic external audio selection returns to automatic embedded audio");
            quality.SelectedIndex = 1;
            Check(!ChoiceEnabled(picker.AudioChoice, 0) && !ChoiceEnabled(picker.AudioChoice, 3)
                && quality.SelectedIndex == 1 && confirm.IsEnabled,
                "external audio remains disabled when switching to a smaller quality");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is null, "external audio test cancels without enqueueing");

            main.Width = 700;
            main.Height = 480;
            picker = new DownloadOptionsPicker(Options(), "deu", "fra");
            choice = picker.ChooseAsync(modal, "Synthetic language episode with a long title that wraps in the download dialog", 1);
            Pump(550);
            picker.SubtitleChoice.SelectedIndex = 0;
            var scroll = Elements<ScrollViewer>(modal).First(s => s.Content is StackPanel);
            scroll.ScrollToBottom();
            Pump(100);
            var card = (Border)modal.FindName("Card");
            Check(Inside(card, modal) && Inside(confirm, modal) && Inside(cancel, modal)
                && confirm.IsVisible && cancel.IsVisible && confirm.ActualHeight > 0 && cancel.ActualHeight > 0,
                "700 by 480 modal keeps its card and confirm/cancel controls visible inside the viewport");
            Check(picker.SubtitleChoice.SelectedIndex == 0 && !picker.Selection.AllSubtitles
                && picker.Selection.Subtitle is null && picker.AudioChoice.ActualWidth <= card.ActualWidth,
                "None disables initial subtitles and narrow language controls fit the modal width");
            Capture(main, "narrow");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is { Subtitle: null, AllSubtitles: false },
                "None is retained after confirmation");

            var empty = Options()[0] with { AudioTracks = [], SubtitleTracks = [] };
            picker = new DownloadOptionsPicker([empty]);
            choice = picker.ChooseAsync(modal, "Synthetic video without language tracks", 0);
            Pump(550);
            Check(!picker.AudioChoice.IsEnabled && !picker.SubtitleChoice.IsEnabled && confirm.IsEnabled,
                "a video without audio or subtitle tracks disables empty selectors and remains downloadable");
            Capture(main, "no-tracks");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            Check(choice.GetAwaiter().GetResult() is null && modal.Visibility == Visibility.Collapsed,
                "Cancel dismisses the language modal without returning a download plan");
            return Result();
        }
        finally { main.Close(); app.Shutdown(); }
    }

    private static bool ChoiceEnabled(ComboBox combo, int index)
        => combo.Items[index] is ComboBoxItem { IsEnabled: true };

    private static bool Inside(FrameworkElement element, FrameworkElement viewport)
    {
        var point = element.TranslatePoint(new Point(), viewport);
        return point.X >= -1 && point.Y >= -1
            && point.X + element.ActualWidth <= viewport.ActualWidth + 1
            && point.Y + element.ActualHeight <= viewport.ActualHeight + 1;
    }

    private static IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var descendant in Elements<T>(VisualTreeHelper.GetChild(parent, i))) yield return descendant;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string state)
    {
        window.UpdateLayout();
        Pump(200);
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(handle, out var owner);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (owner != Environment.ProcessId || foreground != owner || !GetWindowRect(handle, out var rect))
            throw new InvalidOperationException("Capture requires the visible tested process in the foreground.");
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        var directory = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"download-language-{state}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void AssertForeground(Window window)
    {
        GetWindowThreadProcessId(new WindowInteropHelper(window).Handle, out var owner);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (owner != Environment.ProcessId || foreground != owner)
            throw new InvalidOperationException("Native input requires the tested process in the foreground.");
    }

    private static void ClickPopupChoice(Window window, ComboBoxItem row)
    {
        AssertForeground(window);
        var expectedHandle = PresentationSource.FromVisual(row) is HwndSource source ? source.Handle : nint.Zero;
        if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            throw new InvalidOperationException("Native click requires a visible tested dropdown row.");
        var point = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
        var target = GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2);
        GetWindowThreadProcessId(target, out var owner);
        if (expectedHandle == nint.Zero || target != expectedHandle || owner != Environment.ProcessId)
            throw new InvalidOperationException("Native click target is not the fixture's expected popup window.");
        var x = (int)((point.X - GetSystemMetrics(76)) * 65535.0 / (GetSystemMetrics(78) - 1));
        var y = (int)((point.Y - GetSystemMetrics(77)) * 65535.0 / (GetSystemMetrics(79) - 1));
        const uint moveAbsolute = 0x0001 | 0x8000 | 0x4000;
        Send([new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute } } },
              new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0002 } } },
              new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0004 } } }]);
        Pump(200);
    }

    private static void Key(Window window, ComboBox combo, ushort virtualKey)
    {
        AssertForeground(window);
        if (!combo.IsKeyboardFocusWithin)
            throw new InvalidOperationException("Native keys require focus in the tested language selector.");
        Send([new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey } } },
              new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey, Flags = 2 } } }]);
        Pump(100);
    }

    private static void Send(NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new InvalidOperationException("Native input was not delivered.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public NativeMouse Mouse;
        [FieldOffset(0)] public NativeKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nint Extra; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    }

    private static int Result()
    {
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks} download language checks, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }
}
