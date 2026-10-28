using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.Jellyfin;
using LightWeaver.Threading;
using System.Reflection;

L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

var vm = new PlayerViewModel();
var changed = new List<string?>();
vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
var settings = new AppSettings { RtxVsrDefault = true, RtxHdrDefault = false };

vm.ApplyRtxDefaults(settings);
Check(vm.RtxVsrEnabled && !vm.RtxHdrEnabled, "the toggles take the saved defaults");
Check(changed.SequenceEqual([nameof(PlayerViewModel.RtxVsrEnabled)]), "only the toggle that changed notifies the bar");

changed.Clear();
vm.ApplyRtxDefaults(settings);
Check(changed.Count == 0, "saving another setting leaves the RTX state, and so the video filters, alone");

settings.RtxVsrDefault = false;
settings.RtxHdrDefault = true;
vm.ApplyRtxDefaults(settings);
Check(!vm.RtxVsrEnabled && vm.RtxHdrEnabled, "a change saved from the bar or the Settings page reaches the player");
Check(changed.Contains(nameof(PlayerViewModel.RtxVsrEnabled)) && changed.Contains(nameof(PlayerViewModel.RtxHdrEnabled)), "both toggles redraw");

Check(vm.RtxAvailable, "without a player nothing rules RTX out yet");

changed.Clear();
settings.RtxHdrMaxLumaNits = 1500;
vm.ApplyRtxDefaults(settings);
Check(changed.Count == 0, "a peak-only change leaves the toggles as they are");

// --- Which RTX change needs a new filter chain -----------------------------------------------------
var hdrAt1000 = new RtxRequest(Vsr: true, Hdr: true, MaxLumaNits: 1000);
Check(hdrAt1000.SameFilterAs(hdrAt1000 with { }), "the same request needs no new chain");
Check(!hdrAt1000.SameFilterAs(hdrAt1000 with { MaxLumaNits = 1500 }), "a peak-only change with RTX HDR on needs a new chain");
Check(new RtxRequest(true, false, 1000).SameFilterAs(new RtxRequest(true, false, 1500)), "the peak does not matter while RTX HDR is off");
Check(!hdrAt1000.SameFilterAs(hdrAt1000 with { Vsr = false }), "turning VSR off needs a new chain");
Check(!hdrAt1000.SameFilterAs(hdrAt1000 with { Hdr = false }), "turning RTX HDR off needs a new chain");

// --- Which settings groups a save re-applies ---------------------------------------------------------
var live = new AppSettings { VolumeNormalization = true };
var applied = LiveSettings.Of(live);
live.RtxVsrDefault = !live.RtxVsrDefault;
live.RtxHdrMaxLumaNits = 1500;
live.MaxStreamingBitrateMbps = 4;
Check(LiveSettings.Of(live).ChangedFrom(applied) == LiveSettingGroups.None, "an RTX or quality save re-applies no audio, subtitle, buffer or decoding option");
live.VolumeNormalization = false;
Check(LiveSettings.Of(live).ChangedFrom(applied) == LiveSettingGroups.Audio, "normalization re-applies audio only");
live = new AppSettings();
applied = LiveSettings.Of(live);
live.AudioPassthrough = true;
Check(LiveSettings.Of(live).ChangedFrom(applied) == LiveSettingGroups.Audio, "passthrough re-applies audio");
live = new AppSettings();
applied = LiveSettings.Of(live);
live.AudioDevice = "wasapi/{device}";
Check(LiveSettings.Of(live).ChangedFrom(applied) == LiveSettingGroups.Audio, "an output device re-applies audio");
live = new AppSettings();
applied = LiveSettings.Of(live);
live.SubtitleTextColor = "#FFFF00";
live.VideoBufferMiB = applied.VideoBufferMiB == 64 ? 128 : 64;
live.HardwareDecoding = !live.HardwareDecoding;
Check(LiveSettings.Of(live).ChangedFrom(applied) == (LiveSettingGroups.SubtitleStyle | LiveSettingGroups.VideoBuffer | LiveSettingGroups.HardwareDecoding),
    "subtitle style, buffer and decoding each re-apply on their own");

// --- A group mpv rejects is not recorded, and is tried again -----------------------------------------
var before = new AppSettings { VolumeNormalization = false };
var start = LiveSettings.Of(before);
var after = new AppSettings { VolumeNormalization = true, HardwareDecoding = !before.HardwareDecoding, SubtitleTextColor = "#FFFF00" };
var attempts = new List<LiveSettingGroups>();
var reported = new List<LiveSettingGroups>();
var result = start.ApplyChanges(LiveSettings.Of(after), group =>
{
    attempts.Add(group);
    if (group == LiveSettingGroups.HardwareDecoding) throw new MpvException("set hwdec: error");
    return group != LiveSettingGroups.Audio;
}, (group, _) => reported.Add(group));
Check(attempts.SequenceEqual([LiveSettingGroups.SubtitleStyle, LiveSettingGroups.Audio, LiveSettingGroups.HardwareDecoding]), "every changed group is attempted, past a failure");
Check(result.SubtitleStyle == LiveSettings.Of(after).SubtitleStyle, "a group that applied is recorded");
Check(result.Audio == start.Audio, "a group whose apply reports failure is not recorded");
Check(result.HardwareDecoding == start.HardwareDecoding && reported.SequenceEqual([LiveSettingGroups.HardwareDecoding]), "a group whose apply throws is reported and not recorded");
attempts.Clear();
var retried = result.ApplyChanges(LiveSettings.Of(after), group => { attempts.Add(group); return true; });
Check(attempts.SequenceEqual([LiveSettingGroups.Audio, LiveSettingGroups.HardwareDecoding]), "the next save tries the failed groups again, and only those");
Check(retried == LiveSettings.Of(after), "once they apply, everything is recorded");

// --- A half-applied group is retried even when the request goes back to the recorded values ---------
var onA = new AppSettings { AudioDevice = "wasapi/{a}", VolumeNormalization = false };
var onB = new AppSettings { AudioDevice = "wasapi/{b}", VolumeNormalization = true };
var recorded = LiveSettings.Of(onA);
var partial = recorded.ApplyChanges(LiveSettings.Of(onB), group => group != LiveSettingGroups.Audio);
Check(partial.Dirty == LiveSettingGroups.Audio, "an audio apply that failed, perhaps after switching to B, marks audio dirty");
attempts.Clear();
var reverted = partial.ApplyChanges(LiveSettings.Of(onA), group => { attempts.Add(group); return true; });
Check(attempts.SequenceEqual([LiveSettingGroups.Audio]), "going back to A and no normalization applies audio again, though it equals the recorded values");
Check(reverted == LiveSettings.Of(onA) && reverted.Dirty == LiveSettingGroups.None, "success records A and clears the dirty mark");
attempts.Clear();
var unrelated = partial.ApplyChanges(LiveSettings.Of(new AppSettings { AudioDevice = "wasapi/{a}", VolumeNormalization = false, SubtitleTextColor = "#FFFF00" }), group => { attempts.Add(group); return group != LiveSettingGroups.Audio; });
Check(attempts.SequenceEqual([LiveSettingGroups.SubtitleStyle, LiveSettingGroups.Audio]) && unrelated.Dirty == LiveSettingGroups.Audio,
    "an unrelated save retries the dirty group, which stays dirty while it fails");

// --- What a link plays as, and what a drop would play ----------------------------------------------
Check(StreamLink.Title(new Uri("https://media.example.com/films/harbor-lights.mp4")) == ("harbor-lights", "media.example.com"), "a file link plays under its name without the extension, the host under it");
Check(StreamLink.Title(new Uri("https://media.example.com/live/channel-4/index.m3u8")) == ("channel-4", "media.example.com"), "a generic file name gives way to the folder above it");
Check(StreamLink.Title(new Uri("https://cdn.example.org/master.m3u8?token=secret")) == ("cdn.example.org", null), "a link that names nothing plays under its host alone, never its query");
Check(StreamLink.Title(new Uri("http://192.0.2.10:8096/live/stream/")) == ("192.0.2.10:8096", null), "every generic folder is skipped, and the port stays with the host");
Check(StreamLink.Title(new Uri("https://media.example.com/films/Harbor%20Lights.mkv")).Title == "Harbor Lights", "the name is unescaped");
var tokenLink = new Uri("https://cdn.example.org/master.m3u8?token=secret");
Check(StreamLink.MediaTitle(new Uri("https://media.example.com/films/harbor-lights.mp4"), "Harbor Lights") == "Harbor Lights", "mpv's media-title replaces the guess");
Check(StreamLink.MediaTitle(new Uri("https://media.example.com/films/harbor-lights.mp4"), "harbor-lights.mp4") is null, "mpv's fallback to the file name is no title");
Check(StreamLink.MediaTitle(tokenLink, "master.m3u8?token=secret") is null && StreamLink.MediaTitle(tokenLink, tokenLink.AbsoluteUri) is null, "a media-title carrying the address or its query is never shown");
Check(StreamLink.MediaTitle(tokenLink, " ") is null, "an empty media-title keeps the guess");
Check(StreamLink.HasCredentials("https://user:pw@media.example.com/a.mp4") && !StreamLink.HasCredentials("rtsp://camera.example.net/live"), "a user name or password gets its own error");
Check(DropPlay.Link(" https://media.example.com/films/harbor-lights.mp4?sig=1#t=5 \r\n") == new DropPlay("https://media.example.com/films/harbor-lights.mp4?sig=1#t=5", "media.example.com/films/harbor-lights.mp4", true),
    "dropped link text plays its full address and shows its host and path only");
Check(DropPlay.Link("https://media.example.com/") is { Name: "media.example.com" }, "a bare host shows as the host");
Check(DropPlay.Link("rtsp://camera.example.net/live") is null && DropPlay.Link("harbor lights") is null && DropPlay.Link("https://user:pw@media.example.com/a.mp4") is null && DropPlay.Link(null) is null,
    "anything that is not a playable link is no drop");
Check(DropPlay.File(@"D:\Videos\Harbor Lights.mkv") == new DropPlay(@"D:\Videos\Harbor Lights.mkv", "Harbor Lights.mkv", false) && DropPlay.File("") is null, "a dropped file shows its file name; one without a path is no drop");
var reads = new DragReads();
var slow = reads.Begin();
reads.End();
Check(!reads.IsCurrent(slow), "a read that finishes after the drag left or dropped does not show the overlay again");
var older = reads.Begin();
var newer = reads.Begin();
Check(!reads.IsCurrent(older) && reads.IsCurrent(newer), "only the newest enter's read applies");

Check(TimeInput.TryParse("83:45", out var typed) && typed == 83 * 60 + 45, "mm:ss may run past 59 minutes");
Check(TimeInput.TryParse(" 1:02:03 ", out typed) && typed == 3723, "hh:mm:ss, with the spaces around it trimmed");
Check(TimeInput.TryParse("90", out typed) && typed == 90, "a bare number is seconds");
Check(!TimeInput.TryParse("1:2:3:4", out _) && !TimeInput.TryParse("", out _) && !TimeInput.TryParse("1:", out _)
    && !TimeInput.TryParse("-5", out _) && !TimeInput.TryParse("1.5", out _) && !TimeInput.TryParse("1 :30", out _), "anything else seeks nowhere");
Check(TimeInput.Target(5000, 3600) == 3600 && TimeInput.Target(30, 0) == 30, "a position past the end seeks to the end; before the duration is known it is taken as typed");
Check(!TimeInput.TryParse("2147483648", out _) && !TimeInput.TryParse("1e3", out _) && !TimeInput.TryParse("NaN", out _), "overflow and floating-point syntax cannot seek");
var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
Check(TimeInput.TryParse("1:30", out typed) && typed == 90 && !TimeInput.TryParse("1,5", out _), "regional number syntax does not change typed times");
System.Globalization.CultureInfo.CurrentCulture = previousCulture;

var upNext = new AppSettings { UpNextLeadSeconds = 45 };
var (server, user, series) = ("https://media.example.com", Guid.NewGuid(), Guid.NewGuid());
var episode = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "E1", Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode, SeriesId = series };
Check(UpNextLead.For(upNext, server, user, episode) == 45, "a show without its own lead follows Settings");
Check(UpNextPreferences.TryGetShowKey(server, user, series, out var showKey), "a show on a server and user has a key");
UpNextPreferences.SetShowLead(upNext, showKey, 90);
Check(UpNextLead.For(upNext, server, user, episode) == 90, "the show's own lead wins");
Check(UpNextLead.For(upNext, "https://other.example.com", user, episode) == 45 && UpNextLead.For(upNext, server, Guid.NewGuid(), episode) == 45,
    "the show's lead belongs to its server and user");
Check(UpNextLead.For(upNext, server, user, new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Film", SeriesId = series }) == 45, "a title that is not an episode follows Settings");
UpNextPreferences.SetShowLead(upNext, showKey, null);
Check(UpNextLead.For(upNext, server, user, episode) == 45, "Global puts the show back on Settings");
UpNextPreferences.SetShowLead(upNext, showKey, 900);
Check(UpNextLead.For(upNext, server, user, episode) == 300, "a show's custom lead is bounded at five minutes");
UpNextPreferences.SetShowLead(upNext, showKey, 0);
Check(UpNextLead.For(upNext, server, user, episode) == 5, "a show's custom lead has a five-second minimum");

var dispatcher = new ManualDispatcher();
UiDispatcher.Current = dispatcher;
var countdown = new PlayerViewModel();
var playedNext = 0;
countdown.UpNextPlayRequested += _ => playedNext++;
typeof(PlayerViewModel).GetField("_durationSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(countdown, 60d);
countdown.SetUpNext(episode, countdownSeconds: 120, leadSeconds: 45);
void Position(double seconds) => typeof(PlayerViewModel).GetMethod("UpdateUpNext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(countdown, [seconds]);
Position(14.9);
Check(!countdown.UpNextVisible, "the custom lead does not show Up Next before its trigger");
Position(15);
Check(countdown.UpNextVisible && countdown.UpNextHasCountdown, "the custom lead shows Up Next at its trigger");
typeof(PlayerViewModel).GetMethod("HandleUpNextEndReached", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(countdown, null);
for (var i = 0; i < 119; i++) dispatcher.Tick();
Check(playedNext == 0 && countdown.UpNextVisible, "EOF preserves the full 120-second countdown");
dispatcher.Tick();
Check(playedNext == 1 && !countdown.UpNextVisible, "the next episode starts after all 120 countdown seconds");
countdown.SetUpNext(episode, countdownSeconds: 120, autoAdvance: false, leadSeconds: 45);
Position(15);
dispatcher.Tick();
Check(countdown.UpNextVisible && !countdown.UpNextHasCountdown && playedNext == 1, "manual mode uses the lead without auto-advancing");

var deletedNext = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Deleted" };
var keptPrevious = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Kept" };
var pruned = new PlayerViewModel();
pruned.SetUpNext(deletedNext);
pruned.SetEpisodeNeighbors(true, keptPrevious, deletedNext);
Check(pruned.ForgetItems(new HashSet<Guid> { deletedNext.Id }) && pruned.UpNextTitle == "" && !pruned.CanGoNext && pruned.CanGoPrevious,
    "a deleted Up Next and next episode are dropped, the previous episode stays");
Check(!pruned.ForgetItems(new HashSet<Guid> { Guid.NewGuid() }), "an unrelated deletion leaves Up Next alone");
var queue = new PlayQueue();
queue.Set([keptPrevious, episode, deletedNext, new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Kept" }], 1);
queue.RemoveAll(new HashSet<Guid> { keptPrevious.Id, deletedNext.Id, episode.Id });
Check(queue.Count == 2 && queue.CurrentIndex == 0 && queue.Current == episode && queue.PeekNext()?.Name == "Kept",
    "deleted queue entries leave, the playing one stays current");
var deletedPlaying = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Playing, then deleted" };
var following = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Following" };
var advancing = new PlayQueue();
advancing.Set([deletedPlaying, following]);
advancing.RemoveAll(new HashSet<Guid> { deletedPlaying.Id });
Check(advancing.Current == deletedPlaying, "the deleted title keeps playing");
advancing.MoveNext();
advancing.RemoveAll(new HashSet<Guid> { deletedPlaying.Id });
Check(advancing.Current == following && advancing.PeekPrevious() is null && advancing.Count == 1,
    "once the next title starts, Previous cannot reach the deleted one");
var growing = new PlayerViewModel();
growing.SetKnownDuration(5400);
Check(growing.DurationSeconds == 5400, "a download still being written shows the title's full length");
growing.SetKnownDuration(60);
Check(growing.DurationSeconds == 5400, "a shorter known length never shortens the timeline");
growing.ResetFileGeometry();
Check(growing.DurationSeconds == 0, "the next file starts without the previous file's length");
var limited = new PlayerViewModel();
var asked = new List<double>();
limited.SeekLimit = target => { asked.Add(target); return target > 100; };
limited.Seek(50);
limited.Seek(200);
limited.JumpForward();
limited.JumpBack();
Check(asked.SequenceEqual([50d, 200d, 10d]), "seeks, chapter-style jumps and forward skips ask the limit; backward skips do not");

static MpvTrack Sub(int id, string? lang, string? title, bool forced = false) => new(id, "sub", title, lang, false, false, forced);
List<MpvTrack> nextFile = [Sub(1, "eng", "Signs", forced: true), Sub(2, "eng", null), Sub(3, "eng", "English (SDH)"), Sub(4, null, "Deutsch")];
Check(new SubtitleCarryOver(false, "en", "English (SDH)", false).Match(nextFile)?.Id == 3, "language and title find SDH, not the first English track");
Check(new SubtitleCarryOver(false, "eng", "Full", false).Match(nextFile)?.Id == 2, "without the title, the same language and forced flag");
Check(new SubtitleCarryOver(false, "eng", "Signs", true).Match(nextFile)?.Id == 1, "a forced track finds the forced one");
Check(new SubtitleCarryOver(false, "ger", "Deutsch", false).Match(nextFile)?.Id == 4, "a track that names no language is found by its title");
Check(new SubtitleCarryOver(false, "jpn", null, false).Match(nextFile) is null, "no counterpart leaves the file's own choice");
Check(SubtitleCarryOver.From(new SubtitleSelectionRequest(3, null, true, null, null, false)).Off, "Off is carried too");
var sdh = new SubtitleCarryOver(false, "eng", "English (SDH)", false);
Check(sdh.Match([Sub(9, "eng", null)])?.Id == 9 && sdh.Match([Sub(9, "eng", null), Sub(17, "eng", "English (SDH)")])?.Id == 17,
    "a late exact external subtitle supersedes the earlier language fallback");
var exactRequest = new SubtitleSelectionRequest(8, 17, false, "eng", "English (SDH)", false);
Check(SubtitleCarryOver.MatchExact(exactRequest, 8, [Sub(17, "eng", "English (SDH)")])?.Id == 17,
    "the current generation keeps its exact chosen track");
Check(SubtitleCarryOver.MatchExact(exactRequest, 9, [Sub(17, "eng", "English (SDH)")]) is null
    && SubtitleCarryOver.MatchExact(exactRequest, 8, [Sub(17, "eng", "English")]) is null
    && SubtitleCarryOver.MatchExact(exactRequest, 8, [Sub(17, "eng", "English (SDH)", forced: true)]) is null,
    "a reused id from another generation or descriptor cannot acknowledge current intent");
List<MediaStreamChoice> sourceSubs =
[
    new(21, 0, "Signs", "eng", "Signs", false, false, true),
    new(33, -1, "English (SDH)", "eng", "English (SDH)", false, true),
    new(40, 1, "English", "eng", "English", false, false),
];
Check(sdh.MatchSource(sourceSubs)?.Index == 33 && new SubtitleCarryOver(false, "eng", "Signs", true).MatchSource(sourceSubs)?.Index == 21,
    "negotiation resolves external and forced subtitles to the new source's stream indices");
Check(new SubtitleCarryOver(true, "eng", "English", false).MatchSource(sourceSubs) is null,
    "Off cannot resolve to an enabled source track");

Console.WriteLine($"PlayerSettings: {checks} checks passed");

sealed class ManualDispatcher : IUiDispatcher
{
    private readonly List<ManualTimer> _timers = [];
    public bool HasThreadAccess => true;
    public void Post(Action action) => action();
    public IUiTimer CreateTimer(TimeSpan interval, Action tick, bool urgent = false)
    {
        var timer = new ManualTimer(interval, tick);
        _timers.Add(timer);
        return timer;
    }
    public void Tick()
    {
        foreach (var timer in _timers.ToList()) timer.Tick();
    }
}

sealed class ManualTimer(TimeSpan interval, Action tick) : IUiTimer
{
    private bool _running;
    public TimeSpan Interval { get; set; } = interval;
    public void Start() => _running = true;
    public void Stop() => _running = false;
    public void Tick() { if (_running) tick(); }
}
