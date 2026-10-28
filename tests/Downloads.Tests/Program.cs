using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

// Store failures are logged under AppPaths; keep those logs out of an app's real profile.
var appData = Path.Combine(Path.GetTempPath(), "lw-downloads-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", appData);
if (!string.Equals(LightWeaver.AppPaths.Root, Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run: app data resolves to {LightWeaver.AppPaths.Root}, not the test folder. "
        + "Only Debug builds honour LIGHTWEAVER_TEST_APPDATA_ROOT; run this test without -c Release.");
    return 1;
}
L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

const long MB = 1024 * 1024;
const long GB = 1024 * MB;
var t0 = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
DownloadItem Item(string title, DownloadStatus status, long added = 0, long done = 0, long total = -1, double progress = 0, string resolution = "Original", string? error = null) => new()
{
    ItemId = Guid.NewGuid(), ServerUrl = "https://tower", Title = title, FilePath = "x.mkv", Status = status, AddedTimestampMs = t0 + added,
    BytesDownloaded = done, TotalBytes = total, Progress = progress, Resolution = resolution, ErrorMessage = error,
};

// --- Sections and badge (design 08, 8a) ----------------------------------------------------------
var running = Item("Harbor Lights", DownloadStatus.Downloading, 10, done: (long)(1.2 * GB), total: (long)(3.4 * GB), progress: 0.36);
var paused = Item("Lantern House", DownloadStatus.Paused, 20, done: 640 * MB, total: (long)(2.1 * GB));
var queued1 = Item("Northbound", DownloadStatus.Queued, 40, total: (long)(2.4 * GB));
var queued2 = Item("Lantern House E4", DownloadStatus.Queued, 30);
var failed = Item("Static Bloom", DownloadStatus.Failed, 50, error: "the server stopped transcoding.");
var olderDone = Item("Glass Season", DownloadStatus.Completed, 0, resolution: "1080p");
var newerDone = Item("Paper Moons", DownloadStatus.Completed, 60);
var sections = DownloadsModel.Sections([newerDone, failed, queued1, olderDone, running, queued2, paused]);
Check(sections.Select(s => s.Section).SequenceEqual([DownloadSection.Downloading, DownloadSection.Queued, DownloadSection.Failed, DownloadSection.Downloaded]), "sections in the fixed order");
Check(sections[0].Items.SequenceEqual([running, paused]), "paused downloads stay under Downloading");
Check(sections[1].Items.SequenceEqual([queued2, queued1]), "the queue in run order");
Check(sections[3].Items.SequenceEqual([newerDone, olderDone]), "downloaded titles newest first");
Check(DownloadsModel.Sections([olderDone]).Single().Section == DownloadSection.Downloaded, "empty sections are left out");
Check(DownloadsModel.BadgeCount([running, paused, queued1, queued2, failed, olderDone]) == 3, "the badge counts running and queued items");
Check(DownloadsModel.BadgeCount([olderDone]) == 0, "no badge when the queue is empty");

// --- Row lines -----------------------------------------------------------------------------------
var line = DownloadsModel.ProgressLine(running, 12.4 * MB, offline: false);
Check(line == "1.2 of 3.4 GB · 12.4 MB/s · 4 min left", "running line: " + line);
Check(DownloadsModel.ProgressLine(running, 12.4 * MB, offline: true) == "Waiting for server · 1.2 of 3.4 GB", "offline, a running download waits for the server");
Check(DownloadsModel.ProgressLine(paused, 0, false) == "Paused · 640 MB of 2.1 GB", "paused line: " + DownloadsModel.ProgressLine(paused, 0, false));
Check(DownloadsModel.ProgressLine(queued1, 0, false) == "Queued · about 2.4 GB", "queued line with an estimate");
Check(DownloadsModel.ProgressLine(queued2, 0, false) == "Queued", "queued line without one");
Check(DownloadsModel.FailedLine(failed) == "Failed: the server stopped transcoding.", "failed line names the reason");
Check(DownloadsModel.FailedLine(Item("x", DownloadStatus.Failed)) == "Failed: unknown error", "a failure without a reason");
var doneLine = DownloadsModel.DoneLine(olderDone, (long)(2.8 * GB), DateTimeOffset.UtcNow);
Check(doneLine == "2.8 GB · 1080p · 2 days ago", "done line: " + doneLine);
Check(DownloadsModel.TimeLeft(TimeSpan.FromSeconds(30)) == "less than a minute left" && DownloadsModel.TimeLeft(TimeSpan.FromMinutes(65)) == "1 h 5 min left", "time left");
var now = DateTimeOffset.UtcNow;
Check(DownloadsModel.Ago(now.AddHours(-3), now) == "today" && DownloadsModel.Ago(now.AddDays(-9), now) == "1 week ago" && DownloadsModel.Ago(now.AddDays(-45), now) == "1 month ago", "ages");

// --- Storage meter -------------------------------------------------------------------------------
var (mine, other) = DownloadsModel.StorageShares(100 * GB, 500 * GB, 100 * GB);
Check(Math.Abs(mine - 0.2) < 1e-9 && Math.Abs(other - 0.6) < 1e-9, "downloads and other data as shares of the disk");
Check(DownloadsModel.StorageShares(1, 0, 0) == (0, 0), "an unknown disk shows an empty meter");

// --- Quality presets (8b) ------------------------------------------------------------------------
Check(DownloadQuality.All.Count == 6 && DownloadQuality.All[0].IsOriginal, "Original first, then five caps");
Check(DownloadQuality.All.Count(q => q.Resolution == "1080p") == 2 && DownloadQuality.ById("1080p-8")!.Note == "8 Mbps", "two 1080p presets told apart by bitrate");
Check(DownloadQuality.Original.Label == "Original file" && DownloadQuality.Original.Note == "no transcoding", "the original's label");
var folder = Path.Combine(Path.GetTempPath(), "lw-dq-" + Guid.NewGuid().ToString("N"));
DownloadQualityStore.FilePath = Path.Combine(folder, "download-quality.json");
try
{
    Check(DownloadQualityStore.Load() == DownloadQualityStore.DefaultId, "1080p · 8 Mbps until chosen");
    DownloadQualityStore.Save(DownloadQuality.AskId);
    Check(DownloadQualityStore.Load() == DownloadQuality.AskId, "Ask for each download is remembered");
    DownloadQualityStore.Save("720p-4");
    Check(DownloadQualityStore.Load() == "720p-4", "a preset is remembered");
    File.WriteAllText(DownloadQualityStore.FilePath, "\"bogus\"");
    Check(DownloadQualityStore.Load() == DownloadQualityStore.DefaultId, "an unknown value reads as the default");
}
finally
{
    Directory.Delete(folder, recursive: true);
}

var original = new DownloadOption("1080p", "src", true, null, null, 3 * GB, "mkv", "Original");
var hour = TimeSpan.FromHours(1).Ticks;
Check(DownloadsModel.Resolve(DownloadQuality.Original, original, 2160, hour) == original, "Original keeps the file");
Check(DownloadsModel.Resolve(DownloadQuality.ById("2160p-40")!, original, 1080, hour) == original, "a cap above the source keeps the original");
var capped = DownloadsModel.Resolve(DownloadQuality.ById("720p-4")!, original, 1080, hour);
Check(!capped.IsOriginal && capped.MaxWidth == 1280 && capped.VideoBitRate == 4_000_000 && capped.EstimatedSizeBytes == 4_000_000L * 3600 / 8 && capped.Container == "mp4", "720p · 4 Mbps transcodes with an estimate");
var small = new DownloadOption("x", "src", true, null, null, 2 * GB, "mkv", "Original");
Check(DownloadsModel.Resolve(DownloadQuality.ById("1080p-20")!, small, 2160, 2 * hour).IsOriginal, "a transcode larger than the file keeps the original");

// --- Queueing ------------------------------------------------------------------------------------
var episode = new MediaItem { Id = Guid.NewGuid(), Name = "Pilot: Part 1/2", Type = BaseItemDto_Type.Episode, SeriesName = "Lantern House", ParentIndexNumber = 1, IndexNumber = 1 };
var built = DownloadsModel.BuildItem(episode, capped, "https://tower", @"C:\dl", includeSourceId: false, DateTimeOffset.UtcNow);
Check(built.Title == "Lantern House" && built.Subtitle == "S1 · E1", "an episode download is named by its show and number");
Check(built.FilePath.EndsWith($"{episode.Id:N}_Pilot_ Part 1_2.mp4", StringComparison.Ordinal), "the file name is safe: " + Path.GetFileName(built.FilePath));
Check(built.MediaSourceId is null && built.MaxWidth == 1280 && built.Resolution == "720p", "batch items use the default source");
var movie = new MediaItem { Id = Guid.NewGuid(), Name = "Harbor Lights", Type = BaseItemDto_Type.Movie, ProductionYear = 2019 };
var whole = DownloadsModel.BuildItem(movie, original, "https://tower", @"C:\dl", includeSourceId: true, DateTimeOffset.UtcNow);
Check(whole.FilePath.EndsWith(".mkv", StringComparison.Ordinal) && whole.MediaSourceId == "src" && whole.Subtitle == "2019" && whole.TotalBytes == 3 * GB, "an original download keeps the container and source");
var hostile = DownloadsModel.BuildItem(movie, original with { Container = @"mp4\..\..\outside.cmd" }, "https://tower", @"C:\dl", includeSourceId: true, DateTimeOffset.UtcNow);
Check(Path.GetDirectoryName(Path.GetFullPath(hostile.FilePath)) == @"C:\dl" && hostile.FilePath.EndsWith(".mkv", StringComparison.Ordinal), "a container naming a path falls back to mkv: " + hostile.FilePath);
Check(DownloadStore.FileExtension("mov,mp4,m4a") == "mov" && DownloadStore.FileExtension(" ts ") == "ts" && DownloadStore.FileExtension(null) == "mkv", "the extension is the container's first token");
Check(DownloadStore.FileExtension("mp4/../x") == "mkv" && DownloadStore.FileExtension("averyverylongname") == "mkv", "an extension with separators or too long is refused");
Check(DownloadManager.IsCanonicalFilePath(@"C:\dl\a_b.mkv") && DownloadManager.IsCanonicalFilePath("D:/Media/a.mkv"), "a plain absolute path is canonical");
Check(!DownloadManager.IsCanonicalFilePath(@"C:\dl\a.mp4\..\..\outside.cmd") && !DownloadManager.IsCanonicalFilePath(@"dl\a.mkv") && !DownloadManager.IsCanonicalFilePath(""), "dot segments and relative paths are not");

MediaItem Ep(int n, bool played) => new() { Id = Guid.NewGuid(), Name = "E" + n, Type = BaseItemDto_Type.Episode, IndexNumber = n, Played = played };
var eps = new[] { Ep(1, true), Ep(2, false), Ep(3, false) };
Check(DownloadsModel.EpisodesToDownload(eps, _ => false).SequenceEqual([eps[1], eps[2]]), "Download all takes the unwatched episodes");
Check(DownloadsModel.EpisodesToDownload([Ep(1, true), Ep(2, true)], _ => false).Count == 2, "every episode when all are watched");
Check(DownloadsModel.EpisodesToDownload(eps, id => id == eps[1].Id).SequenceEqual([eps[2]]), "leaving out those already tracked");

// --- Speed ---------------------------------------------------------------------------------------
var meter = new SpeedMeter();
var id = Guid.NewGuid();
var at = DateTimeOffset.UtcNow;
Check(meter.Sample(id, 0, at) == 0, "no speed from one sample");
Check(Math.Abs(meter.Sample(id, 10 * MB, at.AddSeconds(1)) - 10 * MB) < 1, "speed from two samples");
var smoothed = meter.Sample(id, 30 * MB, at.AddSeconds(2));
Check(smoothed > 10 * MB && smoothed < 20 * MB, "later samples are smoothed");

var english = new DownloadTrack(3, 0, "English", "eng", null, "aac", false, false, true);
var german = new DownloadTrack(9, 1, "German commentary", "deu", "Commentary", "aac", false, false, false);
var textSubtitle = new DownloadTrack(14, 0, "English subtitles", "eng", null, "srt", false, true, false);
var pictureSubtitle = new DownloadTrack(18, 1, "German PGS", "deu", null, "pgs", false, false, false);
var externalSubtitle = new DownloadTrack(25, -1, "English external", "eng", "External", "srt", true, true, false);
var trackSource = original with { AudioTracks = [english, german], SubtitleTracks = [textSubtitle, pictureSubtitle, externalSubtitle], SourceHeight = 1080 };
var trackCap = DownloadsModel.Resolve(DownloadQuality.ById("720p-4")!, trackSource, 1080, hour);
Check(DownloadTracks.DefaultAudio(trackSource, "de") == 2 && DownloadTracks.DefaultSubtitle(trackSource, "en") == 2, "track defaults match language aliases");
var allowed = DownloadTracks.Allow(trackSource, trackCap, 0, 3, false, "de");
Check(!allowed.AudioAllowed[0] && allowed.Audio == 2 && !allowed.SubtitleAllowed[3] && allowed.Subtitle == 0,
    "transcode disables all audio and embedded PGS, resetting unsupported choices");
var selection = DownloadTracks.Select(trackSource, trackCap, DownloadTracks.Allow(trackSource, trackCap, 2, 2, false, "en"), "en");
var trackDownload = DownloadTracks.Build(movie, selection, "https://tower", @"C:\dl", now);
Check(trackDownload.MediaSourceId == "src" && trackDownload.AudioStreamIndex == 9 && trackDownload.PreferredAudioOrdinal == 0
    && trackDownload.PreferredSubtitleFileIndex == 14 && trackDownload.SubtitleFiles.Single().StreamIndex == 14,
    "transcode persists source stream identities and an extracted subtitle sidecar");
Check(trackDownload.PlaybackMetadata == movie && trackDownload.RuntimeTicks == movie.RuntimeTicks, "download stores local playback metadata");
var restoredDownload = System.Text.Json.JsonSerializer.Deserialize<DownloadItem>(System.Text.Json.JsonSerializer.Serialize(trackDownload))!;
Check(restoredDownload.AudioStreamIndex == 9 && restoredDownload.PreferredSubtitleFileIndex == 14
    && restoredDownload.SubtitleFiles.Single().FilePath == trackDownload.SubtitleFiles.Single().FilePath, "track selections survive the download index round trip");
var changedSelection = selection with { Audio = english };
Check(DownloadTracks.Build(movie, changedSelection, "https://tower", @"C:\dl", now).FilePath != trackDownload.FilePath,
    "different track selections never reuse partial bytes");
var nextSource = trackSource with { MediaSourceId = "episode-source", AudioTracks = [german with { Index = 17, TypeOrdinal = 0 }, english with { Index = 2, TypeOrdinal = 1 }], SubtitleTracks = [textSubtitle with { Index = 30 }] };
var nextQuality = DownloadsModel.Resolve(DownloadQuality.ById("720p-4")!, nextSource, 1080, hour);
var resolvedSelection = DownloadTracks.ForTitle(selection, nextQuality);
Check(resolvedSelection?.Audio?.Index == 17 && resolvedSelection.Subtitle?.Index == 30 && resolvedSelection.Quality.MediaSourceId == "episode-source",
    "batch matching follows language and title rather than the previous episode's stream indices");
Check(DownloadTracks.ForTitle(selection, nextQuality with { AudioTracks = [english] }) is null,
    "a batch episode without the explicitly selected audio is skipped");
var forcedSubtitle = textSubtitle with { Index = 41, TypeOrdinal = 1, IsForced = true };
var fullSubtitle = textSubtitle with { Index = 40, TypeOrdinal = 0, IsDefault = true };
var forcedQuality = trackCap with { SubtitleTracks = [fullSubtitle, forcedSubtitle] };
var forcedSelection = selection with { Quality = forcedQuality, Subtitle = forcedSubtitle };
var episodeForced = forcedSubtitle with { Index = 61, TypeOrdinal = 0 };
var episodeFull = fullSubtitle with { Index = 60, TypeOrdinal = 1 };
var forcedEpisode = nextQuality with { SubtitleTracks = [episodeFull, episodeForced] };
Check(DownloadTracks.ForTitle(forcedSelection, forcedEpisode)?.Subtitle == episodeForced,
    "batch forced subtitles match forced with equal language codec and no title, even when full is default and first");
Check(DownloadTracks.ForTitle(forcedSelection with { Subtitle = fullSubtitle }, forcedEpisode)?.Subtitle == episodeFull,
    "batch full subtitles never map to an equally named forced track");
Check(DownloadTracks.ForTitle(forcedSelection, forcedEpisode with { SubtitleTracks = [episodeFull] }) is null,
    "a batch episode with only full subtitles skips a forced selection");
var ambiguousForced = forcedEpisode with { SubtitleTracks = [episodeForced, episodeForced with { Index = 62, TypeOrdinal = 2, IsDefault = true }] };
Check(DownloadTracks.ForTitle(forcedSelection, ambiguousForced) is null,
    "same-identity forced subtitle candidates are ambiguous despite different defaults indices and ordinals");
Check(DownloadTracks.ForTitle(forcedSelection with { Subtitle = fullSubtitle }, forcedEpisode with
    { SubtitleTracks = [episodeFull, episodeFull with { Index = 63, TypeOrdinal = 2, IsDefault = false }] }) is null,
    "same-identity full subtitle candidates are skipped rather than picked by default");
var unknownSubtitle = forcedSubtitle with { Language = null };
Check(DownloadTracks.ForTitle(forcedSelection with { Subtitle = unknownSubtitle }, forcedEpisode with
    { SubtitleTracks = [unknownSubtitle with { Index = 70 }, unknownSubtitle with { Index = 71, TypeOrdinal = 5 }] }) is null,
    "unknown-language candidates cannot use the previous episode's ordinal to break an identity tie");
var forcedDownload = DownloadTracks.Build(movie, forcedSelection, "https://tower", @"C:\dl", now);
var restoredForced = System.Text.Json.JsonSerializer.Deserialize<DownloadItem>(System.Text.Json.JsonSerializer.Serialize(forcedDownload))!;
Check(restoredForced.SubtitleTracks.Single(t => t.Index == forcedSubtitle.Index).IsForced
    && restoredForced.SubtitleFiles.Single().IsForced, "download index preserves forced flags on tracks and subtitle sidecars");
var legacyTrack = System.Text.Json.JsonSerializer.Deserialize<DownloadTrack>("""{"Index":1,"TypeOrdinal":0,"Display":"English","Language":"en","Title":null,"Codec":"srt","IsExternal":false,"IsText":true,"IsDefault":false}""")!;
Check(!legacyTrack.IsForced, "download records without a forced field remain readable");
var mpvTracks = new List<LightWeaver.Player.MpvTrack>
{
    new(5, "audio", null, "en", true, true, false),
    new(19, "audio", null, "de", false, false, false),
    new(8, "sub", null, "en", true, false, false),
    new(24, "sub", null, "de", false, false, false),
};
var originalSelection = selection with { Quality = trackSource, Subtitle = pictureSubtitle };
var originalDownload = DownloadTracks.Build(movie, originalSelection, "https://tower", @"C:\dl", now);
var playback = DownloadTracks.Playback(originalDownload, mpvTracks, "en", "en");
Check(playback.AudioId == 19 && playback.Subtitle == SubtitleStart.Track && playback.SubtitleId == 24,
    "original playback maps embedded ordinals onto actual mpv IDs");
var offDownload = DownloadTracks.Build(movie, originalSelection with { Subtitle = null }, "https://tower", @"C:\dl", now);
Check(DownloadTracks.Playback(offDownload, mpvTracks, "en", "de").Subtitle == SubtitleStart.Off,
    "explicit download None overrides preferred subtitle language");
Check(DownloadTracks.Playback(trackDownload, mpvTracks, "en", "en").Subtitle == SubtitleStart.Off,
    "an absent chosen sidecar never selects an unrelated embedded subtitle");
mpvTracks.Add(new(42, "sub", null, "en", false, false, false, ExternalFilename: trackDownload.SubtitleFiles.Single().FilePath));
Check(DownloadTracks.Playback(trackDownload, mpvTracks, "en", "de").SubtitleId == 42, "a chosen loaded sidecar is selected by its full filename");
var legacyPlayback = DownloadTracks.Playback(whole, mpvTracks, "de", "en");
Check(legacyPlayback.AudioId == 19 && legacyPlayback.SubtitleId == 8, "older downloads keep global language preference behavior");

var unnamed = new List<LightWeaver.Player.MpvTrack>
{
    new(1, "video", null, null, true, true, false),
    new(2, "audio", null, null, true, true, false),
    new(3, "audio", "Director", null, false, false, false),
    new(4, "sub", null, null, false, false, false),
    new(5, "sub", null, null, false, false, false),
    new(6, "sub", null, null, false, false, false, ExternalFilename: @"C:\dl\harbor.sub-25.srt"),
};
var saved = originalDownload with
{
    MaxWidth = null,
    AudioTracks = [english, german],
    SubtitleTracks = [textSubtitle, pictureSubtitle with { IsForced = true }, externalSubtitle],
    SubtitleFiles = [new DownloadSubtitleFile(25, "srt", @"C:\dl\harbor.sub-25.srt", "External", "eng", false)],
};
var named = DownloadTracks.Named(saved, unnamed);
Check(named[1].Lang == "eng" && named[2].Lang == "deu" && named[2].Title == "Director",
    "embedded audio takes the saved language by order and keeps the file's own title");
Check(named[3].Lang == "eng" && named[4].Lang == "deu" && named[4].Forced && named[5].Title == "External" && named[5].Lang == "eng",
    "subtitles take the saved names by order, with the forced flag, and sidecars by path");
Check(named[0] == unnamed[0] && named.Select(t => t.Id).SequenceEqual(unnamed.Select(t => t.Id)), "video and track ids stay as they are");
Check(!named[1].Display.StartsWith("Track", StringComparison.Ordinal), "a named track no longer reads as Track N");
var transcodedNamed = DownloadTracks.Named(saved with { MaxWidth = 1280, AudioStreamIndex = 9 },
    [new(2, "audio", null, null, true, true, false), new(4, "sub", null, null, false, false, false)]);
Check(transcodedNamed[0].Lang == "deu" && transcodedNamed[1].Lang is null,
    "a transcoded file's one audio track is the chosen stream, and its embedded subtitles are not guessed");

var downloading = saved with { Status = DownloadStatus.Downloading, BytesDownloaded = 4 * MB, VideoCompleted = false };
var ready = downloading with { BytesDownloaded = 8 * MB };
Check(!DownloadsModel.CanPlay(downloading) && DownloadsModel.CanPlay(ready), "a running download plays once its 8 MB startup buffer is on disk");
Check(!DownloadsModel.CanPlay(ready with { Status = DownloadStatus.Paused }) && DownloadsModel.CanPlay(ready with { Status = DownloadStatus.Completed }),
    "a paused download does not play, a finished one does");
Check(DownloadsModel.PlayWhileDownloadingHint(downloading) != DownloadsModel.PlayWhileDownloadingHint(ready),
    "the Play hint shows the startup buffer until playback can start");
Check(DownloadsModel.DownloadedFraction(downloading with { TotalBytes = 16 * MB }) == 0.25
    && DownloadsModel.DownloadedFraction(downloading with { TotalBytes = -1, EstimatedTotalBytes = 8 * MB }) == 0.5,
    "the downloaded share comes from the exact size, else the estimate");
Check(DownloadsModel.DownloadedFraction(downloading with { TotalBytes = -1, EstimatedTotalBytes = -1 }) is null
    && DownloadsModel.DownloadedFraction(downloading with { Status = DownloadStatus.Completed }) == 1,
    "an unknown size gives no share, a finished download all of it");
var writing = downloading with { TotalBytes = 100 * MB, BytesDownloaded = 33 * MB };
Check(Math.Abs(DownloadsModel.ReachableSeconds(writing, 1000)!.Value - 200) < 0.001,
    "a seek reaches only as far as the padded bytes on disk, the partial stream's own start check");
Check(DownloadsModel.ReachableSeconds(writing with { BytesDownloaded = 4 * MB }, 1000) == 0
    && DownloadsModel.ReachableSeconds(writing with { Status = DownloadStatus.Completed }, 1000) == 1000
    && DownloadsModel.ReachableSeconds(writing with { TotalBytes = -1, EstimatedTotalBytes = -1 }, 1000) is null,
    "below the startup buffer nothing is reachable, a finished download all of it, an unknown size is unknown");
Check(DownloadsModel.ResumeAfterPartialStream(612, 1452.1, 0) == 612,
    "a partial stream that ran out mid-film goes on from the server where it stopped");
Check(DownloadsModel.ResumeAfterPartialStream(1452.1, 1452.1, 30) == 30 && DownloadsModel.ResumeAfterPartialStream(0, 0, 30) == 30,
    "a stream that ended at once, with mpv at the end of the file or nowhere, starts where the playback began");
Console.WriteLine($"{checks} checks passed.");
if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
return 0;
