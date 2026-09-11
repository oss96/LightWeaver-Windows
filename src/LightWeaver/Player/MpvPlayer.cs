using System.Windows.Threading;
using LightWeaver.Player.Native;
using LightWeaver.Settings;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LightWeaver.Player;

public sealed class MpvException(string message) : Exception(message);

/// <summary>One entry of mpv's chapter-list (fallback chapter source for local files).</summary>
public sealed record MpvChapter(double TimeSeconds, string? Title);

/// <summary>One entry of mpv's audio-device-list ("auto", "wasapi/{id}", …).</summary>
public sealed record MpvAudioDevice(string Name, string Description);

/// <summary>One entry of mpv's track-list (video/audio/sub tracks of the current file).</summary>
public sealed record MpvTrack(int Id, string Type, string? Title, string? Lang, bool Selected, bool Default, bool Forced,
    string? Codec = null, int Width = 0, int Height = 0)
{
    public string Display
    {
        get
        {
            var name = Title ?? Lang ?? $"Track {Id}";
            if (Title is not null && Lang is not null)
                name = $"{Title} ({Lang})";
            if (Type == "video")
            {
                var detail = string.Join(" ", new[]
                {
                    Codec,
                    Width > 0 && Height > 0 ? $"{Width}x{Height}" : null,
                }.Where(s => s is not null));
                if (detail.Length > 0)
                    name = $"{name} · {detail}";
            }
            return Forced ? $"{name} [forced]" : name;
        }
    }
}

/// <summary>
/// High-level mpv playback API over the raw <see cref="LibMpv"/> bindings.
///
/// Threading: a dedicated background thread blocks in mpv_wait_event and forwards
/// state to the UI thread via the Dispatcher; all events raise on the UI thread.
/// Position updates are coalesced (latest value only) so high-frequency time-pos
/// changes can't flood the Dispatcher queue.
///
/// Shutdown: Dispose sends "quit", joins the event thread (which exits on
/// MPV_EVENT_SHUTDOWN), then calls mpv_terminate_destroy — never while another
/// thread is inside mpv_wait_event.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    // reply_userdata ids for observed properties
    private const ulong ObsTimePos = 1;
    private const ulong ObsDuration = 2;
    private const ulong ObsPause = 3;
    private const ulong ObsVolume = 4;
    private const ulong ObsMute = 5;
    private const ulong ObsEofReached = 6;
    private const ulong ObsTrackList = 7;
    private const ulong ObsChapterList = 8;
    private const ulong ObsVideoGamma = 9;
    private const ulong ObsPausedForCache = 10;
    private const ulong ObsVideoWidth = 11;
    private const ulong ObsVideoHeight = 12;
    private const ulong ObsOsdMarginTop = 13;
    private const ulong ObsOsdMarginBottom = 14;
    private const ulong ObsOsdHeight = 15;
    private const ulong ObsCurrentAo = 16;

    private readonly nint _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _eventThread;
    private volatile bool _disposed;

    // Latest-value coalescing for time-pos: the event thread overwrites the value;
    // at most one dispatcher callback is pending at a time.
    private readonly Lock _posLock = new();
    private double _pendingPos;
    private bool _posDispatchQueued;

    private bool _pause;
    private double _duration;
    private double _volume = 100;
    private bool _mute;
    private bool _eofRaised;
    private bool _pausedForCache;

    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    /// <summary>mpv paused-for-cache: playback is stalled waiting on the demuxer cache
    /// (network underrun). Raised on the UI thread.</summary>
    public event Action<bool>? BufferingChanged;
    /// <summary>A file finished loading. The payload is the event generation consumed at
    /// MPV_EVENT_START_FILE; compare it against <see cref="LoadGeneration"/> to reject an event for
    /// a superseded request. UI thread.</summary>
    public event Action<int>? FileLoaded;

    private int _loadGeneration;
    private int _eventGeneration;
    private int _authoritativeTracksGeneration;
    private readonly object _loadGenerationQueueLock = new();
    private readonly Queue<int> _pendingLoadGenerations = new();

    /// <summary>The newest load request generation. Event payloads use the separate generation
    /// consumed by MPV_EVENT_START_FILE, never this live request counter.</summary>
    public int LoadGeneration => Volatile.Read(ref _loadGeneration);
#if DEBUG
    /// <summary>Test-only request for the shell to initiate a second local file in this process.</summary>
    public event Action<string>? TestLoadFileRequested;
    private int _testLoadScheduled;
    private int _testStaleTracksInjected;
    private int _testCapturedTracksGeneration;
    private IReadOnlyList<MpvTrack>? _testCapturedTracks;
#endif
    public event Action? EndReached;
    public event Action<string>? LogMessage;

    /// <summary>Raises <see cref="LogMessage"/> and breadcrumbs the same text for the diagnostics
    /// ring. Every app-side message goes through here so the ring has exactly one writer per
    /// source: mpv's own warn/error/fatal lines are breadcrumbed in the event loop (where they
    /// arrive), and these — audio-device fallback, RTX suppression, passthrough give-up, sub-add
    /// failure — are breadcrumbed here. Subscribing to <see cref="LogMessage"/> and breadcrumbing
    /// there instead would double-count everything mpv sends.</summary>
    private void Say(string message)
    {
        Diagnostics.AppLog.Breadcrumb("player", message);
        LogMessage?.Invoke(message);
    }

    // ---- Structural verbose records -----------------------------------------------------
    //
    // These are the APP'S interpretation of what mpv did — a file loaded, the audio chain came
    // back, the RTX chain changed state — and deliberately not a second copy of mpv's own
    // semantics: mpv's full verbose stream already reaches mpv.log through
    // <see cref="Diagnostics.AppLog.MpvLine"/>. What is recorded here is SPARSE by construction:
    // once per file, once per state change, once per user action. The per-tick observations —
    // time-pos, video-params, the osd-dimensions leaves, the pause/volume/mute echoes — are
    // deliberately absent, because one record per tick would bury the ones that mean something
    // and would make an idle, paused player write to disk forever.
    //
    // Threading. mpv's event thread must not do file IO (measured 364 µs per line; that thread
    // delivers the events the loading indicator waits on), so nothing here is written ON it: the
    // event-loop sites record from inside the dispatcher callback they already post, and the ones
    // that do not have such a callback test <see cref="Diagnostics.AppLog.Verbose"/> before they
    // even post. Every call site tests the flag before composing its record, which is why this
    // helper is a bare forward rather than a formatter.

    private static void Detail(string record) => Diagnostics.AppLog.Detail("player", record);

    /// <summary>A player event worth BOTH a breadcrumb (so a later incident file carries it) and a
    /// verbose line. No file gets it twice: <see cref="Say"/> writes only to the incident ring and
    /// the Trace listener, <see cref="Detail"/> only to <c>app.log</c>. Used where the record
    /// replaces a message that used to be prose — the composition is paid for by the ring either
    /// way, so these sites do not test the verbose flag first.</summary>
    private void SayDetail(string record)
    {
        Say(record);
        Detail(record);
    }

    /// <summary>Reduces a free-text mpv string (an <c>mpv_error_string</c> value, an audio-output
    /// name) to a lower_snake token, so a record stays parseable as <c>key=value</c>. mpv's error
    /// table and AO names carry no user data — a device or file name never reaches here.</summary>
    private static string Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_');
        return new string(chars.ToArray());
    }

    /// <summary>What kind of output device a request names, without naming it: <c>auto</c> (mpv's
    /// system-default pseudo-device) or <c>custom</c>. A WASAPI device string identifies a
    /// person's hardware, so the records carry this plus a
    /// <see cref="Diagnostics.AppLog.ShortHash"/> for correlation and never the string itself.</summary>
    private static string DeviceCategory(string? device)
        => string.IsNullOrEmpty(device) || device == "auto" ? "auto" : "custom";

    /// <summary>A file failed to load or aborted with an error (mpv end-file reason
    /// "error"); the payload is mpv's human-readable error string. UI thread.</summary>
    public event Action<string>? PlaybackFailed;

    /// <summary>Audio/sub tracks of the current file; updated whenever mpv's track-list
    /// changes (including selection changes). The payload identifies the load generation whose
    /// list was published. Raised on the UI thread.</summary>
    public IReadOnlyList<MpvTrack> Tracks { get; private set; } = [];
    public event Action<int>? TracksChanged;

    /// <summary>Chapters as seen by mpv (embedded in the file). Raised on the UI thread.</summary>
    public IReadOnlyList<MpvChapter> Chapters { get; private set; } = [];
    public event Action? ChaptersChanged;

    private MpvPlayer(nint ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _dispatcher = dispatcher;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
    }

    /// <summary>Creates and initializes an mpv instance rendering into the given child HWND.</summary>
    /// <exception cref="MpvException">mpv rejected an option or failed to initialize.</exception>
    /// <exception cref="DllNotFoundException">libmpv-2.dll not found next to the exe or on PATH.</exception>
    public static MpvPlayer Create(nint wid, Dispatcher uiDispatcher, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        var ctx = LibMpv.mpv_create();
        if (ctx == nint.Zero)
            throw new MpvException("mpv_create failed");

        try
        {
            // All options — including wid — must be set before mpv_initialize.
            SetOption(ctx, "vo", "gpu-next");
            SetOption(ctx, "hwdec", settings.HardwareDecoding ? "d3d11va" : "no");
            if (Math.Abs(settings.SubtitleScale - 1.0) > 0.001)
                SetOption(ctx, "sub-scale", settings.SubtitleScale.ToString(CultureInfo.InvariantCulture));
            SetOption(ctx, "osc", "no");
            SetOption(ctx, "input-default-bindings", "no");
            SetOption(ctx, "input-vo-keyboard", "no");
            SetOption(ctx, "volume-max", "200");
            SetOption(ctx, "keep-open", "yes");
            // Keep mpv's cache=auto source detection. The byte budget controls network
            // read-ahead; the generous duration ceiling prevents the default time cap
            // from making a larger byte setting ineffective for low-bitrate streams.
            SetOption(ctx, "demuxer-max-bytes", (settings.VideoBufferMiB * 1024L * 1024).ToString(CultureInfo.InvariantCulture));
            SetOption(ctx, "cache-secs", "3600");

            // Letterbox-band subtitle placement (Phase 10 M4). MEASURED, not assumed — the
            // numbers below come from a 2.39:1 clip in a 16:9 render area (mpv reports
            // mt=mb=115, h=900, so the picture ends at y=785):
            //
            //   sub-use-margins=yes  makes the subtitle area the whole render surface instead of
            //     the picture rect, so subtitles are anchored ~51 px above the SURFACE bottom
            //     (measured row 822..849) and therefore land inside the black band whenever the
            //     band is taller than the text plus that margin. This is mpv's default; it is
            //     pinned so a default change or a stray user config cannot silently undo the
            //     placement. With it off, subtitles sit inside the picture (row 707..734) and are
            //     CLIPPED at the picture edge rather than spilling into the band.
            //   sub-ass-force-margins=yes  extends the same treatment to dialogue-style ASS,
            //     which ignores sub-use-margins by default: measured 720..748 (over the picture)
            //     -> 835..863 (in the band). This is the one placement the shipped build got
            //     wrong, and the only behaviour this pair actually changes.
            //
            // The placement is self-gating and needs no geometry input: the anchor is the surface
            // bottom regardless of band height (measured constant ~51 px at band fractions
            // 0.128 / 0.061 / 0.021 / 0), so a band too small to hold the text degrades to exactly
            // the old over-the-picture look instead of moving text somewhere wrong.
            //
            // sub-pos is NOT a lever into the band: values above 100 push the text below the
            // visible surface, where it is clipped and renders nowhere at all (measured: 110, 125
            // and 150 all produced no subtitle). sub-pos stays what it has always been — the
            // 0..100 control-bar lift the overlay composes. \pos-tagged ASS events are unaffected
            // by any of this (measured constant at rows 249..279 across every configuration), so
            // typeset signs stay where the author put them.
            SetOption(ctx, "sub-use-margins", "yes");
            SetOption(ctx, "sub-ass-force-margins", "yes");

            // Diagnostics: LIGHTWEAVER_MPV_LOG=<path> writes mpv's full log to a file.
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_LOG") is { Length: > 0 } logFile)
            {
                SetOption(ctx, "log-file", logFile);
                SetOption(ctx, "msg-level", "all=v");
            }

            // Diagnostics: LIGHTWEAVER_MPV_MUTE=1 starts muted (automated test runs).
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE") == "1")
                SetOption(ctx, "mute", "yes");

            // Diagnostics: LIGHTWEAVER_MPV_NO_AUDIO=1 uses mpv's null audio output, so no audio
            // ENDPOINT is ever opened. mute=yes above is not enough for this: it silences the
            // stream but still initialises the AO, and with passthrough enabled that means a
            // WASAPI *exclusive-mode* open. A test run doing that takes the device away from any
            // other player on the machine — measured 2026-08-05, when automated runs repeatedly
            // knocked the user's own installed instance out of playback. Test launches set this;
            // nothing else does, and audio-dependent suites must not (they would be testing
            // nothing).
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO") == "1")
                SetOption(ctx, "ao", "null");

            unsafe
            {
                long widValue = wid;
                Check(LibMpv.mpv_set_option(ctx, "wid", MpvFormat.Int64, &widValue), "set wid");
            }

            Check(LibMpv.mpv_initialize(ctx), "mpv_initialize");
            // Verbose logging raises the CLIENT log level so the extra detail arrives as events
            // this app can redact before writing (see Diagnostics.AppLog). mpv's own `log-file`
            // would be simpler and is deliberately not used for it: it prints the
            // `http-header-fields` value, i.e. the Jellyfin token, into a file meant for sharing.
            // Only warn/error/fatal are forwarded to the LogMessage event either way, so raising
            // this changes nothing the app reacts to.
            LibMpv.mpv_request_log_messages(ctx, Diagnostics.AppLog.Verbose ? "v" : "warn");

            Check(LibMpv.mpv_observe_property(ctx, ObsTimePos, "time-pos", MpvFormat.Double), "observe time-pos");
            Check(LibMpv.mpv_observe_property(ctx, ObsDuration, "duration", MpvFormat.Double), "observe duration");
            Check(LibMpv.mpv_observe_property(ctx, ObsPause, "pause", MpvFormat.Flag), "observe pause");
            Check(LibMpv.mpv_observe_property(ctx, ObsVolume, "volume", MpvFormat.Double), "observe volume");
            Check(LibMpv.mpv_observe_property(ctx, ObsMute, "mute", MpvFormat.Flag), "observe mute");
            Check(LibMpv.mpv_observe_property(ctx, ObsEofReached, "eof-reached", MpvFormat.Flag), "observe eof-reached");
            Check(LibMpv.mpv_observe_property(ctx, ObsTrackList, "track-list", MpvFormat.String), "observe track-list");
            Check(LibMpv.mpv_observe_property(ctx, ObsChapterList, "chapter-list", MpvFormat.String), "observe chapter-list");
            Check(LibMpv.mpv_observe_property(ctx, ObsVideoGamma, "video-params/gamma", MpvFormat.String), "observe video gamma");
            Check(LibMpv.mpv_observe_property(ctx, ObsPausedForCache, "paused-for-cache", MpvFormat.Flag), "observe paused-for-cache");
            // The audio output actually in use, or nothing at all when init failed. An empty
            // current-ao while an audio track is selected is the signature of the passthrough
            // stall (see EvaluateAudioOutput).
            Check(LibMpv.mpv_observe_property(ctx, ObsCurrentAo, "current-ao", MpvFormat.String), "observe current-ao");
            Check(LibMpv.mpv_observe_property(ctx, ObsVideoWidth, "video-params/w", MpvFormat.Int64), "observe video width");
            Check(LibMpv.mpv_observe_property(ctx, ObsVideoHeight, "video-params/h", MpvFormat.Int64), "observe video height");
            // osd-dimensions/{mt,mb,h}: the letterbox geometry mpv itself computes — the black
            // bands between the render surface and the picture actually drawn in it, plus that
            // surface's height, all in mpv-side pixels.
            //
            // Deliberately NOT derived from video-params/w+h against the window size, and this is
            // not a simplification waiting to happen: osd-dimensions describes the REAL on-screen
            // picture, so it already accounts for anamorphic PAR, cropping, rotation and the RTX
            // `d3d11vpp=scale=2` upscale (ApplyRtxFilterChain) — source-pixel arithmetic misses
            // every one of those and would report a band that is not on screen.
            //
            // These are numeric LEAF sub-fields, observed as plain Int64, so they ride the
            // existing property machinery; mpv_node marshalling stays deferred by design.
            Check(LibMpv.mpv_observe_property(ctx, ObsOsdMarginTop, "osd-dimensions/mt", MpvFormat.Int64), "observe osd margin top");
            Check(LibMpv.mpv_observe_property(ctx, ObsOsdMarginBottom, "osd-dimensions/mb", MpvFormat.Int64), "observe osd margin bottom");
            Check(LibMpv.mpv_observe_property(ctx, ObsOsdHeight, "osd-dimensions/h", MpvFormat.Int64), "observe osd height");
        }
        catch
        {
            LibMpv.mpv_terminate_destroy(ctx);
            throw;
        }

        var player = new MpvPlayer(ctx, uiDispatcher);
        player._eventThread.Start();
        // Style/audio prefs are ordinary properties — applied after init so the same
        // code paths serve startup and live settings changes.
        player.ApplySubtitleStyle(settings);
        player.ApplyAudioOptions(settings);
        return player;
    }

    /// <summary>Updates the read-ahead budget for the current player and subsequent files.</summary>
    public void ApplyVideoBuffer(AppSettings settings)
    {
        CheckAlive();
        var bytes = (settings.VideoBufferMiB * 1024L * 1024).ToString(CultureInfo.InvariantCulture);
        Check(LibMpv.mpv_set_property_string(_ctx, "demuxer-max-bytes", bytes), "set video buffer");
        Check(LibMpv.mpv_set_property_string(_ctx, "cache-secs", "3600"), "set read-ahead duration");
    }

    private static readonly System.Text.RegularExpressions.Regex HexColor =
        new(@"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    /// <summary>Applies the persisted subtitle style (text subs; sub-scale stays the
    /// image-sub lever). Empty strings fall back to mpv's defaults.</summary>
    public void ApplySubtitleStyle(AppSettings settings)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "sub-font",
            settings.SubtitleFontFamily is { Length: > 0 } font ? font : "sans-serif");
        if (settings.SubtitleFontSize is > 0 and <= 300)
            LibMpv.mpv_set_property_string(_ctx, "sub-font-size",
                settings.SubtitleFontSize.ToString(CultureInfo.InvariantCulture));
        LibMpv.mpv_set_property_string(_ctx, "sub-color",
            HexColor.IsMatch(settings.SubtitleTextColor) ? settings.SubtitleTextColor : "#FFFFFF");
        LibMpv.mpv_set_property_string(_ctx, "sub-border-color",
            HexColor.IsMatch(settings.SubtitleBorderColor) ? settings.SubtitleBorderColor : "#000000");
        if (settings.SubtitleBorderSize is >= 0 and <= 20)
            LibMpv.mpv_set_property_string(_ctx, "sub-border-size",
                settings.SubtitleBorderSize.ToString(CultureInfo.InvariantCulture));
        if (Math.Abs(settings.SubtitleShadowOffset) <= 20)
            LibMpv.mpv_set_property_string(_ctx, "sub-shadow-offset",
                settings.SubtitleShadowOffset.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Applies output device, passthrough and normalization. Passthrough and
    /// dynaudnorm are mutually exclusive (bitstreaming bypasses filters) — passthrough
    /// wins if both are somehow set.</summary>
    public void ApplyAudioOptions(AppSettings settings)
    {
        CheckAlive();
        var device = settings.AudioDevice is { Length: > 0 } dev ? dev : "auto";
        // A saved device may have been unplugged since. Fall back to auto for THIS
        // session without touching the saved preference — plugging it back in makes
        // the preference work again. Only validate when mpv can enumerate (list may
        // be empty very early); an unknown device would otherwise kill audio init.
        if (device != "auto" && GetAudioDevices() is { Count: > 0 } known
            && known.All(d => d.Name != device))
        {
            // Structural, not "device 'wasapi/{…}' not present": the saved value is a raw WASAPI
            // endpoint id and names the user's hardware. The hash is enough to tell one absent
            // device from another across a session, which is all this record is read for.
            SayDetail($"event=audio_device outcome=fallback reason=device_absent device_hash={Diagnostics.AppLog.ShortHash(device)}");
            device = "auto";
        }
        LibMpv.mpv_set_property_string(_ctx, "audio-device", device);
        var passthrough = settings.AudioPassthrough;
        // A settings change is a fresh request: re-arm the recovery so picking a device that
        // CAN bitstream is honoured without a relaunch, and so a failure on one file does not
        // permanently silence passthrough for the session.
        _passthroughRequested = passthrough;
        _passthroughGaveUp = false;
        // Same reasoning for the audio-sync repair: a settings change is a fresh request, so one
        // failed resync earlier in the session must not leave the check standing down for good.
        _audioSyncGaveUp = false;
        LibMpv.mpv_set_property_string(_ctx, "audio-spdif",
            passthrough ? "ac3,eac3,dts,dts-hd,truehd" : "");
        var wantNormalization = !passthrough && settings.VolumeNormalization;
        var err = wantNormalization
            ? LibMpv.Command(_ctx, "af", "set", "dynaudnorm")
            : LibMpv.Command(_ctx, "af", "clr", "");
        if (err < 0)
        {
            SayDetail($"event=audio_filter outcome=failure normalization={(wantNormalization ? "on" : "off")} error={Token(LibMpv.ErrorString(err))}");
            // Only worth telling the user about when they ASKED for a filter — a failed `af clr`
            // leaves nothing applied, which is what clearing wanted anyway. The reason this is
            // surfaced at all: LogMessage reaches Trace and nothing else, and a setting that
            // silently does nothing is exactly how the passthrough stall (B31) stayed hidden.
            if (wantNormalization)
                AudioFilterUnavailable?.Invoke("Volume normalization could not be applied — playing unprocessed audio.");
        }
        // One record per APPLY (startup and each settings save), not per file: this is the audio
        // configuration every later audio record has to be read against, and the passthrough stall
        // (B31/B32) is precisely a disagreement between what was asked for here and what came up.
        if (Diagnostics.AppLog.Verbose)
            Detail($"event=audio_options device={DeviceCategory(device)} device_hash={Diagnostics.AppLog.ShortHash(device)} "
                + $"passthrough={(passthrough ? "on" : "off")} normalization={(wantNormalization ? "on" : "off")}");
    }

    // ---- Failed-passthrough recovery ----
    //
    // Bitstreaming is all-or-nothing per device, and mpv handles the "nothing" case badly: when
    // the output rejects the spdif format (a virtual device such as an audio enhancer NEVER
    // accepts exclusive-mode IEC 61937), mpv retries as AC3, fails, walks its AO list, fails
    // again, then re-opens the track with the software decoder — and never brings an audio
    // output back up. Playback then does not start AT ALL: no clock, no audio, no frames, while
    // the demuxer quietly fills its cache. Measured on a DTS-HD title: 18 s with mpv logging
    // nothing whatsoever and 216 MB read. It affects every AC3/E-AC3/DTS/DTS-HD/TrueHD title,
    // i.e. most films, which is why the user's workaround was to seek — a seek rebuilds the
    // audio chain, which then succeeds in PCM.
    //
    // The recovery is to do that rebuild deliberately, without passthrough, and say so.
    // Deliberately per-playback: `AudioPassthrough` stays the user's saved preference, so
    // selecting a device that CAN bitstream makes it work again with no setting to re-find.

    private string? _currentAo;
    private bool _passthroughRequested;
    private bool _passthroughGaveUp;
    private DispatcherTimer? _aoWatchdog;

    // Audio-restart-point drift (B39) — a SEPARATE watchdog from the passthrough one above, on a
    // different signal and a different timescale. See the section in front of ArmAudioSyncCheck.
    private DispatcherTimer? _audioSyncWatchdog;
    private bool _audioSyncRepairing;   // suppresses the AUDIO_RECONFIG our own repair causes
    private bool _audioSyncGaveUp;      // one repair attempt per load generation

    // A restart that never completes (B41) — a THIRD watchdog, on the state the other two exclude:
    // the clock frozen with an output present. See the section in front of ArmPlaybackStallCheck.
    private DispatcherTimer? _playbackStallWatchdog;
    private bool _playbackStallRepairing;  // a repair and its verify are in flight
    private bool _playbackStallGaveUp;     // both repair stages spent for this load generation

    /// <summary>Raised when a requested passthrough could not be initialised and the player
    /// fell back to decoded audio for this playback. The payload is the reason, ready to show.</summary>
    public event Action<string>? PassthroughUnavailable;

    /// <summary>True between a <see cref="LoadFile"/> and mpv reporting it can display frames
    /// (MPV_EVENT_PLAYBACK_RESTART). Drives the loading indicator; raised on the UI thread.</summary>
    public bool IsLoading { get; private set; }
    public event Action<bool>? LoadingChanged;

    private void SetLoading(bool loading)
    {
        if (IsLoading == loading)
            return;
        IsLoading = loading;
        // The transition, never the event: PLAYBACK_RESTART also fires after every seek, and
        // EndFile clears a flag that PlaybackRestart usually cleared already. Both arrive here
        // and only the first of them changes anything, which is what bounds this to one record
        // per direction per load.
        if (Diagnostics.AppLog.Verbose)
            Detail($"event=loading state={(loading ? "start" : "cleared")}");
        LoadingChanged?.Invoke(loading);
    }

    /// <summary>Raised when an audio filter the user asked for could not be applied. Payload is
    /// ready to show. Measured working on this build (mpv reports
    /// <c>dynaudnorm (dynaudnorm.00)</c> in its filter list), so this exists for the case where
    /// a future libmpv lacks the filter — not for a reproduced failure.</summary>
    public event Action<string>? AudioFilterUnavailable;

    /// <summary>The audio output mpv is actually using ("wasapi"), or null when there is none.</summary>
    public string? CurrentAudioOutput => _currentAo;

    private bool HasAudioTrack => Tracks.Any(t => t.Type == "audio");

    /// <summary>
    /// Debounce in front of <see cref="EvaluateAudioOutput"/>. Every signal that an audio output
    /// might be missing (file loaded, audio chain reconfigured, current-ao gone) re-arms this
    /// rather than acting.
    ///
    /// The delay is the whole point, and it was NOT a precaution — the first cut acted on the
    /// instant <c>current-ao</c> went null and consequently fired on ordinary AAC files, which a
    /// control leg caught. mpv clears the property while tearing the previous output down and
    /// sets it again a moment later, so the null is a normal step in a healthy load, not a
    /// failure. What distinguishes the failure is that the null PERSISTS. Measured: a healthy
    /// output appears ~190-300 ms after the file opens, so 1.2 s is a comfortable margin, and it
    /// only ever delays a file that would otherwise never have started at all.
    /// </summary>
    private void ArmAudioOutputCheck(string trigger)
    {
        if (!_passthroughRequested || _passthroughGaveUp)
        {
            UiLog($"AoWatchdog skip trigger={trigger} requested={_passthroughRequested} gaveUp={_passthroughGaveUp}");
            return;
        }
        var generation = LoadGeneration;
        UiLog($"AoWatchdog arm trigger={trigger} gen={generation}");
        _aoWatchdog?.Stop();
        _aoWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _aoWatchdog.Tick += (_, _) =>
        {
            _aoWatchdog?.Stop();
            if (generation == LoadGeneration)
                EvaluateAudioOutput(trigger);
            else
                UiLog($"AoWatchdog stale gen={generation} now={LoadGeneration}");
        };
        _aoWatchdog.Start();
    }

    /// <summary>Drops passthrough for this playback if it is the reason there is no audio
    /// output. Idempotent — several triggers race to call it and only the first does work.</summary>
    private void EvaluateAudioOutput(string trigger)
    {
        if (_disposed || _ctx == nint.Zero)
            return;
        if (!_passthroughRequested || _passthroughGaveUp)
            return;
        // The OBSERVED current-ao cannot be trusted here (B32): mpv never publishes the property
        // going away when playback stops, so the cache can still hold the PREVIOUS file's output
        // ("wasapi") while the new file has none — which made this check conclude "an output
        // exists" and strand the load at Loading… forever. The live read is truthful in exactly
        // that state (measured 2026-08-01: cache "wasapi", live read null), so ask mpv directly
        // and resync the cache while at it.
        var liveAo = GetPropertyString("current-ao");
        _currentAo = liveAo is { Length: > 0 } ? liveAo : null;
        UiLog($"AoWatchdog eval trigger={trigger} liveAo={liveAo ?? "(null)"} "
            + $"hasAudio={HasAudioTrack} loading={IsLoading}");
        if (liveAo is { Length: > 0 })
            return; // an output genuinely exists
        if (!HasAudioTrack)
        {
            // "No audio track" is a transient during a slow open, not only a fact about
            // video-only files: the 1.2 s debounce can beat mpv's track list (B32's second
            // mode, measured stranding the first playback of a session). While the file is
            // still loading, look again instead of standing down for good — a genuinely
            // video-only file resolves when PLAYBACK_RESTART clears IsLoading, which ends
            // the polling. EndFile clears it too, so the loop cannot outlive the load.
            if (IsLoading)
                ArmAudioOutputCheck(trigger);
            return;
        }

        _passthroughGaveUp = true;
        _aoWatchdog?.Stop();
        // The trigger is one of this class's own bounded strings, so it is safe as a token. The
        // user-facing sentence stays what it was (PassthroughFailureMessage) — it NAMES the device,
        // which is the whole point of a toast and exactly why it is not what gets written down.
        SayDetail($"event=passthrough outcome=fallback trigger={Token(trigger)} device={DeviceCategory(GetPropertyString("audio-device"))}");
        LibMpv.mpv_set_property_string(_ctx, "audio-spdif", "");
        ReloadSelectedAudioTrack("passthrough-fallback");
        PassthroughUnavailable?.Invoke(PassthroughFailureMessage());
    }

    /// <summary>
    /// Rebuilds the audio chain in place, keeping the same track.
    ///
    /// Rebuilding by hand rather than trusting a property write to imply it: deselecting and
    /// reselecting the track is the one sequence guaranteed to make mpv re-run audio init, and it
    /// is what the user's manual seek was achieving by accident.
    ///
    /// Deliberately an <c>aid</c> change and NOT a seek. An aid change makes mpv re-select the
    /// demux stream at the current playback position — a per-stream refresh seek — so audio is
    /// repositioned without video being re-decoded and there is no visible hiccup. A seek would
    /// also work; it is literally the <c>issue_refresh_seek</c> mpv fails to issue on a reload
    /// (mpv <c>player/audio.c</c>, gated on <c>audio_status == STATUS_PLAYING</c>), and it is kept
    /// out precisely because it disturbs video.
    /// </summary>
    private void ReloadSelectedAudioTrack(string trigger)
    {
        var aid = GetPropertyString("aid");
        LibMpv.mpv_set_property_string(_ctx, "aid", "no");
        LibMpv.mpv_set_property_string(_ctx, "aid", aid is { Length: > 0 } and not "no" ? aid : "auto");
        // Both callers are recoveries, so this is bounded by construction: once per passthrough
        // fallback and once per load generation for the audio-sync repair.
        if (Diagnostics.AppLog.Verbose)
            Detail($"event=audio_track_reload trigger={Token(trigger)}");
    }

    /// <summary>Names the device that refused, because "passthrough failed" without the device
    /// is not actionable — on a machine whose DEFAULT output is a virtual enhancer, the fix is
    /// to pick a different output, not to change the setting.</summary>
    private string PassthroughFailureMessage()
    {
        var device = GetPropertyString("audio-device");
        var label = device is { Length: > 0 } and not "auto"
            ? GetAudioDevices().FirstOrDefault(d => d.Name == device)?.Description ?? device
            : AudioPassthroughProbe.DefaultDeviceName() ?? "the current audio device";
        return $"{label} can't take passthrough audio — playing decoded audio instead.";
    }

    // ---- Audio-restart-point drift after an output change (B39) ----
    //
    // mpv's reload_audio_output() (player/audio.c) tears the AO down, resets audio_status to
    // SYNCING and calls mp_output_chain_reset_harder(), which DESTROYS every frame buffered inside
    // the audio FILTER chain — while the only compensating rewind anywhere in that path is gated
    // on `audio_status == STATUS_PLAYING`. So a reload arriving while a previous delayed start is
    // still pending discards the buffered audio and issues no seek: the decoder simply carries on
    // from where it had already read, and the audio restart point jumps FORWARD. mpv then sits in
    // `delaying audio start`, silent with video playing normally, with NO bound — it ends only when
    // the video clock catches up, the file ends, or something seeks.
    //
    // Both triggers land in that same function: an in-app `audio-device` write (UPDATE_AUDIO ->
    // mp_option_change_callback) and a Windows default-device change while on `auto`
    // (ao_wasapi_changenotify's OnDefaultDeviceChanged -> ao_request_reload). One physical switch
    // can fire several reloads.
    //
    // The MAGNITUDE comes from dynaudnorm, i.e. only with normalization on. FFmpeg's defaults
    // (af_dynaudnorm.c: framelen 500 ms, gausssize 31) keep ~15.5 s of audio inside the filter and
    // emit nothing until the queue refills, so after a reset the decoder must supply 31 fresh
    // chunks plus one to prime the AO: exactly 32 x 0.5 = 16.000 s of forward jump per
    // uncompensated reload. That is the observed quantum, and the recorded audio-pts pair
    // 101.513 / 181.513 is 80.000 s = 5 x 16.000 s apart, with the .513 fraction as the 0.5 s
    // quantization fingerprint. The defect is mpv's STATUS_PLAYING gate; dynaudnorm is the
    // amplifier, not the cause.
    //
    // The repair is the rewind mpv skipped, done the way that costs no video frame — see
    // ReloadSelectedAudioTrack.

    /// <summary>Raised when audio stopped after the output device changed and the automatic resync
    /// did not bring it back: one repair was attempted for this load and failed. The payload is a
    /// ready-to-show sentence. It names no device — the manual fix (seek, or reselect the audio
    /// track) is the same whichever endpoint was switched to, and a WASAPI endpoint id identifies
    /// a person's hardware.</summary>
    public event Action<string>? AudioResyncFailed;

    private const string AudioResyncFailureMessage =
        "Audio stopped after the output device changed and could not be resynchronised — "
        + "seek or reselect the audio track to restore it.";

    // The check's numbers, all read off the two captures (see ArmAudioSyncCheck for the one behind
    // the delay). The divergence threshold sits well above the 0.2-0.5 s async-queue scale and the
    // 1.334 s worst healthy resync, and an order below the 16 s failure quantum — so nothing
    // healthy reaches it and nothing broken misses it.
    private const int AudioSyncCheckDelayMs = 3000;
    private const double MinVideoAdvanceSeconds = 0.5;
    private const double MaxAudioDivergenceSeconds = 2.0;
    private const double NearEndOfFileSeconds = 10;

    /// <summary>
    /// Debounce in front of <see cref="EvaluateAudioSync"/>, armed by every audio reconfig —
    /// the event a reload_audio_output necessarily produces, whichever trigger caused it.
    ///
    /// 3000 ms, and the number is measured rather than cautious. The healthy control capture
    /// (<c>%TEMP%\lightweaver-tests\audio-device-cycle-mpv.log</c>: 12 real device switches across
    /// 6 endpoints, including sample-rate changes 48k -> 96k -> 44.1k and a wasapi -> openal driver
    /// change) never delayed audio start by more than 1.334 s, and every one of its 151
    /// `delaying audio start` series converged monotonically to zero. The failure quantum is 16 s
    /// per reload. So 3 s clears every healthy resync with better than 2x margin, and still repairs
    /// long before the video clock could catch up on its own.
    /// </summary>
    private void ArmAudioSyncCheck(string trigger)
    {
        if (_disposed || _ctx == nint.Zero)
            return;
        // Passthrough takes precedence: while its recovery is still live, THIS reconfig is part of
        // that recovery, which is about to rebuild the whole chain itself. (ApplyAudioOptions makes
        // passthrough and normalization mutually exclusive, so the amplifier is not even present in
        // that configuration.)
        if (_passthroughRequested && !_passthroughGaveUp)
        {
            UiLog($"AudioSyncWatchdog skip trigger={trigger} reason=passthrough_recovery");
            return;
        }
        if (_audioSyncGaveUp || _audioSyncRepairing)
        {
            UiLog($"AudioSyncWatchdog skip trigger={trigger} gaveUp={_audioSyncGaveUp} repairing={_audioSyncRepairing}");
            return;
        }
        // The stall recovery (B41) rebuilds the audio chain with this same reload, so the reconfig
        // arriving here is its doing. Standing down keeps one stall from earning two repairs; the
        // case is not lost, because if that recovery restarts the video and leaves audio behind,
        // that IS this bug and the next reconfig arms it again.
        if (_playbackStallRepairing)
        {
            UiLog($"AudioSyncWatchdog skip trigger={trigger} reason=playback_stall_recovery");
            return;
        }
        var generation = LoadGeneration;
        // NaN when mpv has no position to give (between files): EvaluateAudioSync treats that as
        // "cannot tell whether video advanced" and stands down, which is the safe direction.
        var timePosAtArm = TryGetPropertyDouble("time-pos", out var pos) ? pos : double.NaN;
        UiLog(string.Format(CultureInfo.InvariantCulture,
            "AudioSyncWatchdog arm trigger={0} gen={1} timePos={2:F3}", trigger, generation, timePosAtArm));
        _audioSyncWatchdog?.Stop();
        _audioSyncWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AudioSyncCheckDelayMs) };
        _audioSyncWatchdog.Tick += (_, _) =>
        {
            _audioSyncWatchdog?.Stop();
            if (generation == LoadGeneration)
                EvaluateAudioSync(trigger, timePosAtArm);
            else
                UiLog($"AudioSyncWatchdog stale gen={generation} now={LoadGeneration}");
        };
        _audioSyncWatchdog.Start();
    }

    /// <summary>
    /// Repairs an audio restart point that jumped past the video clock — at most ONCE per load
    /// generation, and silently: this runs after every audio reconfig, so "nothing to do" is the
    /// normal outcome and the user hears about it only when the repair itself fails.
    ///
    /// The guards run cheapest-first and "not our bug"-first, because every state they exclude
    /// (loading, video-only, no output at all, paused, end-of-file) would otherwise look exactly
    /// like the failure to the oracle below.
    /// </summary>
    private void EvaluateAudioSync(string trigger, double timePosAtArm)
    {
        if (_disposed || _ctx == nint.Zero)
            return;
        if (_audioSyncGaveUp || _audioSyncRepairing || _playbackStallRepairing)
            return;
        // Not this bug: the passthrough recovery owns this reconfig and rebuilds the chain itself.
        if (_passthroughRequested && !_passthroughGaveUp)
            return;
        // A load in progress is a different situation — nothing has played yet, so there is no
        // restart point to be wrong. The reconfig that ends the load re-arms this.
        if (IsLoading)
            return;
        if (!HasAudioTrack)
            return;
        // No output AT ALL is the passthrough stall's signature (B31), not this one; leave it to
        // EvaluateAudioOutput, whose watchdog is already armed on the same reconfig.
        if (GetPropertyString("current-ao") is not { Length: > 0 })
            return;
        // The signature of THIS bug is that video keeps advancing while audio does not, so a video
        // clock that did not move means paused, buffering, or a genuine stall — none of them ours.
        if (!TryGetPropertyDouble("time-pos", out var timePos)
            || !double.IsFinite(timePosAtArm)
            || timePos - timePosAtArm < MinVideoAdvanceSeconds)
            return;
        // Load-bearing, not decoration: audio-pts is ALSO unavailable once audio_status reaches
        // STATUS_EOF, so without this guard every normal end of file would read as stuck and earn a
        // pointless track reload. 10 s is safe because the failure quantum is 16 s — a real stall
        // is still 6 s of silence clear of the end when it is caught here.
        if (!TryGetPropertyDouble("time-remaining", out var timeRemaining)
            || timeRemaining <= NearEndOfFileSeconds)
            return;

        var stuck = IsAudioStuck(timePos, out var audioPtsReadable, out var divergence);
        UiLog(string.Format(CultureInfo.InvariantCulture,
            "AudioSyncWatchdog eval trigger={0} stuck={1} audioPts={2} divergence={3:F3} advanced={4:F3}",
            trigger, stuck, audioPtsReadable ? "readable" : "unavailable", divergence, timePos - timePosAtArm));
        if (!stuck)
            return;

        _audioSyncRepairing = true;
        SayDetail(FormattableString.Invariant(
            $"event=audio_sync outcome=repair trigger={Token(trigger)} audio_pts={(audioPtsReadable ? "readable" : "unavailable")} divergence={divergence:F3}"));
        ReloadSelectedAudioTrack("audio-sync");

        // One verify pass, one give-up, no recursion: the reload produces an AUDIO_RECONFIG of its
        // own, which _audioSyncRepairing swallows until this timer resolves, and _audioSyncGaveUp
        // latches afterwards if it did not work. The generation is re-read here for the same reason
        // the arming timer re-reads it — a load started in between makes the answer meaningless.
        var generation = LoadGeneration;
        var verify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AudioSyncCheckDelayMs) };
        verify.Tick += (_, _) =>
        {
            verify.Stop();
            if (_disposed || _ctx == nint.Zero)
                return;
            if (generation != LoadGeneration)
            {
                // LoadFile already cleared the repairing flag for the new generation.
                UiLog($"AudioSyncWatchdog verify stale gen={generation} now={LoadGeneration}");
                return;
            }
            _audioSyncRepairing = false;
            // A file that ended (or lost its position) inside the verify window cannot answer the
            // question, and answering it wrong would spend the one give-up on nothing — so an
            // inconclusive verify counts as recovered and leaves the check armed for a later
            // reconfig.
            //
            // The video-advance arm is the load-bearing one and it is NOT symmetry with the eval
            // guard for its own sake: mpv only reaches STATUS_PLAYING when it actually starts the
            // output, which it never does while paused, so audio-pts stays unavailable for the
            // WHOLE of a pause. Without this, a user who merely pauses inside the verify window —
            // or who pauses a repair that in fact worked — gets told audio could not be
            // resynchronised, and the one repair for this generation is spent on it. The same arm
            // covers buffering, a genuine video stall, and a backward seek (which fixes the bug
            // anyway). Losing the audio track or the output mid-verify is unanswerable for the same
            // reason: neither is this bug, and neither deserves the toast.
            var inconclusive = !TryGetPropertyDouble("time-pos", out var verifyPos)
                || !TryGetPropertyDouble("time-remaining", out var verifyRemaining)
                || verifyRemaining <= NearEndOfFileSeconds
                || verifyPos - timePos < MinVideoAdvanceSeconds
                || !HasAudioTrack
                || GetPropertyString("current-ao") is not { Length: > 0 };
            if (!inconclusive && IsAudioStuck(verifyPos, out _, out _))
            {
                _audioSyncGaveUp = true;
                SayDetail($"event=audio_sync outcome=failed trigger={Token(trigger)}");
                AudioResyncFailed?.Invoke(AudioResyncFailureMessage);
                return;
            }
            SayDetail($"event=audio_sync outcome=recovered trigger={Token(trigger)} "
                + $"conclusive={(inconclusive ? "no" : "yes")}");
        };
        verify.Start();
    }

    /// <summary>
    /// The stuck oracle: whether audio is behind where the video clock says it should be.
    ///
    /// The PRIMARY signal is the absence of a reading. mpv's <c>mp_property_audio_pts</c>
    /// (<c>player/command.c</c>) returns M_PROPERTY_UNAVAILABLE whenever
    /// <c>audio_status &lt; STATUS_PLAYING</c> — which is exactly the `delaying audio start` state
    /// this looks for. So while stuck, <c>audio-pts</c> is UNREADABLE, and comparing it numerically
    /// against <c>time-pos</c> could never have detected anything. Callers establish the rest of the
    /// context first (an audio track is selected, a live current-ao exists, the video clock
    /// advanced, the file is not near its end), and that context is what makes "no reading" mean
    /// "audio has not started yet" rather than "there is no audio" or "the file ended".
    ///
    /// The numeric divergence is the SECONDARY signal, for when audio-pts does read: an audio
    /// position more than <see cref="MaxAudioDivergenceSeconds"/> AHEAD of the video position is
    /// the forward jump itself, seen after audio finally started at the wrong point.
    /// </summary>
    private bool IsAudioStuck(double timePos, out bool audioPtsReadable, out double divergence)
    {
        divergence = double.NaN;
        // mpv_get_property_string returns NULL on any error, so "no reading" IS "unavailable".
        audioPtsReadable = TryGetPropertyDouble("audio-pts", out var audioPts);
        if (!audioPtsReadable)
            return true;
        divergence = audioPts - timePos;
        return divergence > MaxAudioDivergenceSeconds;
    }

    // ---- A playback restart that never completes (B41) ----
    //
    // Reported 2026-08-28, twice in one sitting, after switching subtitle tracks on a file with 44
    // of them. The user's log (Release 1.0.1) shows the shape exactly: at 15:48:30.506 mpv executes
    // a seek, logs `first video frame after restart shown` at .551 — and then never logs
    // `audio ready`, `playback restart complete` or `starting audio playback` again. The next seek
    // repeats it. Twenty-six seconds of total silence follow, and the user quits. Every healthy
    // restart in the same capture completes in 60-220 ms, so the absence is conclusive.
    //
    // So the stuck state is: one frame shown, audio never reaching mpv's STATUS_PLAYING, and the
    // restart therefore never finishing. It is NOT either of the two states already covered:
    //  - B31/B32 is "no audio output at all" (passthrough refused). Here current-ao is live and
    //    was never reloaded: WASAPI opened once and there is no later `Trying audio driver`.
    //  - B39 is "video keeps playing while audio is stuck". EvaluateAudioSync therefore requires
    //    the video clock to have ADVANCED, and stands down on a frozen one as "a genuine stall —
    //    none of them ours". This is that stall. B39's watchdog also arms only on AUDIO_RECONFIG,
    //    which neither a seek nor a subtitle switch raises, so it could not even look.
    //
    // What is shared with B39 is the amplifier: dynaudnorm is in the filter chain in the capture
    // (`[af] dynaudnorm (dynaudnorm.00)`), and it holds ~15.5 s of audio that a restart has to
    // re-prime. The precise mpv-internal reason audio never re-primes here is NOT established —
    // the capture is verbose but not trace-level, and the app build that produced it predates the
    // player's own detail records. That is why this watchdog writes down what it saw before it
    // acts: the next occurrence should arrive already diagnosed.
    //
    // The repair order is taken from the capture rather than from taste. The user's seeks are the
    // thing that DEMONSTRABLY did not fix it — three of them in a row re-entered the same state —
    // so re-seeking first would repeat the move already proven not to work. Rebuilding the audio
    // chain is the untried one, and it is the same rewind B31 and B39 both use. The re-seek stays
    // as the second stage only because one seek (15:48:26.499) did recover the first incident, so
    // it is not worthless — just not first.

    /// <summary>Raised when playback stopped without resuming and neither repair brought it back:
    /// both stages are spent for this load. The payload is a ready-to-show sentence.</summary>
    public event Action<string>? PlaybackStalled;

    private const string PlaybackStallFailureMessage =
        "Playback stopped and could not be restarted — seek, or reload the item, to resume.";

    /// <summary>How long a stall has to persist before it counts as one. Healthy restarts in the
    /// B41 capture complete in 60-220 ms, so this is ~20x the observed worst case; the margin is
    /// there for a slow remote seek, and buffering is excluded on its own signal anyway.</summary>
    private const int PlaybackStallCheckMs = 5000;

    /// <summary>How long each repair gets to take effect. A restart that works at all shows up in
    /// well under a second, so this is generous; it is the same order as the B39 verify window.</summary>
    private const int PlaybackStallVerifyMs = 3000;

#if DEBUG
    // Diagnostics: LIGHTWEAVER_TEST_STALL_FREEZE makes this watchdog READ the clock as frozen
    // without freezing it. Nothing outside mpv can induce the real state — it is an mpv-internal
    // restart that never finishes — so the choice is between faking the one reading the oracle
    // rests on and shipping the whole state machine untested. Everything else stays real: the
    // player is genuinely playing, and the paused / buffering / loading / audio-track /
    // current-ao / near-EOF guards are all read live and unfaked.
    //
    //   arm  — only the detection is faked, so the repair's verify sees the clock really moving
    //          and takes the recovery path.
    //   all  — the verify's reading is faked too, which drives the escalation to the re-seek and
    //          then the give-up toast.
    private static readonly string? TestStallFreeze =
        Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_STALL_FREEZE");
    private const double TestStallFreezeOffsetSeconds = 3600;
#endif

    /// <summary>
    /// Debounce in front of <see cref="EvaluatePlaybackStall"/>, armed by the three things that
    /// were live when the stall was reported: a seek, a subtitle-track switch (whose demuxer
    /// refresh seek is internal to mpv and raises no event of its own), and leaving pause.
    ///
    /// Arming is deliberately cheap and unconditional — one timer, restarted — because the eval is
    /// where the state is read. Nothing is disarmed on PLAYBACK_RESTART: the un-pause case has no
    /// restart to wait for, and a healthy player fails the eval's first real guard anyway.
    /// </summary>
    private void ArmPlaybackStallCheck(string trigger)
    {
        if (_disposed || _ctx == nint.Zero)
            return;
        // Both other recoveries own states that look like this one from the outside, and both
        // rebuild the chain themselves. Standing down while either is live keeps one stall from
        // earning two repairs.
        if (_passthroughRequested && !_passthroughGaveUp)
            return;
        if (_playbackStallGaveUp || _playbackStallRepairing || _audioSyncRepairing)
            return;
        var generation = LoadGeneration;
        var timePosAtArm = TryGetPropertyDouble("time-pos", out var pos) ? pos : double.NaN;
#if DEBUG
        if (TestStallFreeze is "arm" or "all" && double.IsFinite(timePosAtArm))
            timePosAtArm += TestStallFreezeOffsetSeconds;
#endif
        UiLog(string.Format(CultureInfo.InvariantCulture,
            "PlaybackStallWatchdog arm trigger={0} gen={1} timePos={2:F3}", trigger, generation, timePosAtArm));
        _playbackStallWatchdog?.Stop();
        _playbackStallWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PlaybackStallCheckMs) };
        _playbackStallWatchdog.Tick += (_, _) =>
        {
            _playbackStallWatchdog?.Stop();
            if (generation == LoadGeneration)
                EvaluatePlaybackStall(trigger, timePosAtArm);
            else
                UiLog($"PlaybackStallWatchdog stale gen={generation} now={LoadGeneration}");
        };
        _playbackStallWatchdog.Start();
    }

    /// <summary>
    /// Repairs a playback that stopped and did not come back — at most twice per load generation,
    /// and silently unless both stages fail.
    ///
    /// The guards exclude every legitimate reason for a still clock, cheapest-first: paused,
    /// loading, buffering, at the end of the file, no audio at all, no output at all. What is left
    /// is a player that is supposed to be playing and is not.
    /// </summary>
    private void EvaluatePlaybackStall(string trigger, double timePosAtArm)
    {
        if (_disposed || _ctx == nint.Zero)
            return;
        if (_playbackStallGaveUp || _playbackStallRepairing || _audioSyncRepairing)
            return;
        if (_passthroughRequested && !_passthroughGaveUp)
            return;
        // A load has no restart point to be wrong yet, and a load that never starts is B31/B32's
        // stall, whose watchdog is already armed on it.
        if (IsLoading)
            return;
        // Paused is not stalled. It also makes the state unreadable: mpv only reaches
        // STATUS_PLAYING when it actually starts the output, so audio-pts is unavailable for the
        // WHOLE of a pause and every pause would read as this bug.
        if (_pause)
            return;
        // Waiting on the network is a legitimate stop, and it is the one mpv announces itself.
        if (_pausedForCache)
            return;
        if (!HasAudioTrack)
            return;
        // No output at all is B31's signature, not this one.
        if (GetPropertyString("current-ao") is not { Length: > 0 })
            return;
        // keep-open holds the last frame with the clock still and pause false, which is this bug's
        // shape exactly. The same 10 s guard B39 uses, for the same reason.
        if (!TryGetPropertyDouble("time-remaining", out var timeRemaining)
            || timeRemaining <= NearEndOfFileSeconds)
            return;
        // The oracle: the clock did not move. A position mpv cannot give is not evidence of a
        // stall, and a clock that advanced means playback is running — if audio is silent while it
        // does, that is B39's case and B39's repair.
        if (!TryGetPropertyDouble("time-pos", out var timePos)
            || !double.IsFinite(timePosAtArm)
            || timePos - timePosAtArm >= MinVideoAdvanceSeconds)
            return;

        // Cross-checks, recorded rather than tested. core-idle is mpv's own "nothing is playing"
        // and should be true here; audio-pts should be unreadable, since audio below STATUS_PLAYING
        // is what the capture points at. Neither gates the repair — the state is already
        // established above, and the point of writing them down is that the NEXT report arrives
        // with the two facts this one had to be inferred from.
        var coreIdle = GetPropertyString("core-idle") is "yes";
        var audioPtsReadable = TryGetPropertyDouble("audio-pts", out _);
        UiLog(string.Format(CultureInfo.InvariantCulture,
            "PlaybackStallWatchdog eval trigger={0} timePos={1:F3} coreIdle={2} audioPts={3}",
            trigger, timePos, coreIdle, audioPtsReadable ? "readable" : "unavailable"));
        SayDetail($"event=playback_stall outcome=repair stage=audio_reload trigger={Token(trigger)} "
            + $"core_idle={(coreIdle ? "yes" : "no")} audio_pts={(audioPtsReadable ? "readable" : "unavailable")}");

        _playbackStallRepairing = true;
        ReloadSelectedAudioTrack("playback-stall");
        VerifyPlaybackStallRepair(trigger, timePos, stage: 1);
    }

    /// <summary>
    /// Checks whether a repair took, and escalates once. Stage 1 is the audio-chain rebuild; if the
    /// clock is still frozen, stage 2 re-issues the restart with an exact seek to where playback
    /// already is (visible as at most one re-decoded frame). After stage 2 the user is told, once.
    /// </summary>
    private void VerifyPlaybackStallRepair(string trigger, double timePosAtRepair, int stage)
    {
#if DEBUG
        if (TestStallFreeze is "all" && double.IsFinite(timePosAtRepair))
            timePosAtRepair += TestStallFreezeOffsetSeconds;
#endif
        var generation = LoadGeneration;
        var verify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PlaybackStallVerifyMs) };
        verify.Tick += (_, _) =>
        {
            verify.Stop();
            if (_disposed || _ctx == nint.Zero)
                return;
            if (generation != LoadGeneration)
            {
                // LoadFile already cleared the repairing flag for the new generation.
                UiLog($"PlaybackStallWatchdog verify stale gen={generation} now={LoadGeneration}");
                return;
            }
            // Anything that makes the question unanswerable counts as recovered, and deliberately
            // so: the cost of being wrong here is spending a repair stage, or telling a user that
            // playback failed while they are watching it. A pause inside the verify window is the
            // likely one — the user reaching for the keyboard is exactly what a stall provokes.
            var posReadable = TryGetPropertyDouble("time-pos", out var verifyPos);
            var advanced = posReadable && verifyPos - timePosAtRepair >= MinVideoAdvanceSeconds;
            if (advanced || !posReadable || _pause || _pausedForCache || IsLoading
                || !TryGetPropertyDouble("time-remaining", out var verifyRemaining)
                || verifyRemaining <= NearEndOfFileSeconds)
            {
                _playbackStallRepairing = false;
                UiLog($"PlaybackStallWatchdog verify stage={stage} result=recovered advanced={advanced}");
                SayDetail($"event=playback_stall outcome=recovered stage={stage} "
                    + $"trigger={Token(trigger)} conclusive={(advanced ? "yes" : "no")}");
                return;
            }

            if (stage == 1)
            {
                UiLog("PlaybackStallWatchdog verify stage=1 result=escalate");
                SayDetail($"event=playback_stall outcome=repair stage=reseek trigger={Token(trigger)}");
                // "seek 0 exact" re-issues the restart at the position playback already holds, so
                // it needs no readable time-pos of its own and moves nothing the user would notice.
                LibMpv.Command(_ctx, "seek", "0", "exact");
                VerifyPlaybackStallRepair(trigger, verifyPos, stage: 2);
                return;
            }

            _playbackStallRepairing = false;
            _playbackStallGaveUp = true;
            _playbackStallWatchdog?.Stop();
            UiLog("PlaybackStallWatchdog verify stage=2 result=failed");
            SayDetail($"event=playback_stall outcome=failed trigger={Token(trigger)}");
            PlaybackStalled?.Invoke(PlaybackStallFailureMessage);
        };
        verify.Start();
    }

    /// <summary>The audio outputs mpv can use right now (name + human description).</summary>
    public IReadOnlyList<MpvAudioDevice> GetAudioDevices()
    {
        var json = GetPropertyString("audio-device-list");
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            var list = new List<MpvAudioDevice>();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null)
                    continue;
                var desc = el.TryGetProperty("description", out var d) ? d.GetString() : null;
                list.Add(new MpvAudioDevice(name, desc is { Length: > 0 } ? desc : name));
            }
            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The output device mpv is currently configured to use ("auto" = default).</summary>
    public string CurrentAudioDevice => GetPropertyString("audio-device") is { Length: > 0 } d ? d : "auto";

    /// <summary>Switches the audio output device live (mpv reinits the ao in place).</summary>
    public void SelectAudioDevice(string name)
    {
        CheckAlive();
        var device = name is { Length: > 0 } ? name : "auto";
        LibMpv.mpv_set_property_string(_ctx, "audio-device", device);
        // Never the device string itself: "Speakers (Jane's USB DAC)" and the raw
        // "wasapi/{guid}" endpoint id both describe a person's hardware. The category says which
        // KIND of choice was made and the hash lets two switches in one session be told apart.
        SayDetail($"event=audio_device outcome=applied category={DeviceCategory(device)} device_hash={Diagnostics.AppLog.ShortHash(device)}");
    }

    /// <summary>Raised (UI thread) when the playback speed changes.</summary>
    public event Action<double>? SpeedChanged;

    private double _speed = 1.0;

    /// <summary>Playback speed multiplier (mpv speed); resets to 1 per file.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            CheckAlive();
            var speed = Math.Clamp(value, 0.25, 4.0);
            _speed = speed;
            SetDoubleProperty("speed", speed);
            LibMpv.Command(_ctx, "show-text", $"Speed: {speed.ToString("0.##", CultureInfo.InvariantCulture)}x");
            SpeedChanged?.Invoke(speed);
        }
    }

    public int LoadFile(string pathOrUrl)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        Volatile.Write(ref _authoritativeTracksGeneration, 0);
        _eofRaised = false;
        CheckAlive();
        // Per-load audio-sync state. The repair budget is one attempt per load generation, so a new
        // file gets its own: a give-up on the previous file says nothing about this one, and a
        // repair still awaiting its verify pass belongs to a file that is no longer playing. The
        // watchdog is stopped rather than left to notice the generation moved, so it cannot even
        // read properties belonging to the incoming file.
        _audioSyncGaveUp = false;
        _audioSyncRepairing = false;
        _audioSyncWatchdog?.Stop();
        // Same reasoning for the stall watchdog's two-stage budget: it belongs to the file that
        // stalled, and a verify still in flight is asking about a playback that has ended.
        _playbackStallGaveUp = false;
        _playbackStallRepairing = false;
        _playbackStallWatchdog?.Stop();
        // Speed is a per-viewing choice, not a preference — reset per file.
        if (Math.Abs(_speed - 1.0) > 0.001)
        {
            _speed = 1.0;
            SetDoubleProperty("speed", 1.0);
            SpeedChanged?.Invoke(1.0);
        }
        // Explicit track choices (detail-view preselection or in-player switching)
        // are sticky mpv properties — reset so they never leak into the next file.
        LibMpv.mpv_set_property_string(_ctx, "vid", "auto");
        LibMpv.mpv_set_property_string(_ctx, "aid", "auto");
        LibMpv.mpv_set_property_string(_ctx, "sid", "auto");
#if DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE")))
        {
            UiLog(FormattableString.Invariant(
                $"SubtitleTrackReset event=set_auto request_generation={generation} event_generation={Volatile.Read(ref _eventGeneration)} track_count={Tracks.Count} pending_count={PendingLoadGenerationCount}"));
        }
#endif
        // Reset the track list eagerly: the previous file's tracks would otherwise
        // linger in the UI popups until the new file finishes enumerating.
        Tracks = [];
        TracksChanged?.Invoke(generation);
        // The next file gets its own track/chapter record even if it happens to have the same
        // shape as this one; see DetailTracks/DetailChapters for why they are change-gated.
        _loggedTrackShape = null;
        _loggedChapterCount = -1;
        // keep-open pauses mpv at EOF, and pause is a sticky property — without this
        // the item AFTER a watched-to-the-end file started frozen (M18 finding).
        SetFlagProperty("pause", false);
        // Loading starts HERE, not at FILE_LOADED: opening a network source is most of the wait,
        // and it happens before mpv has any headers to report.
        Post(() => SetLoading(true));
        lock (_loadGenerationQueueLock)
            _pendingLoadGenerations.Enqueue(generation);
        try
        {
            // Queue immediately before the command: START_FILE can arrive as soon as mpv accepts
            // it, and must consume the matching request generation in FIFO order.
            Check(LibMpv.Command(_ctx, "loadfile", pathOrUrl), "loadfile");
        }
        catch
        {
            RemovePendingLoadGeneration(generation);
            throw;
        }
        return generation;
    }

    private void RemovePendingLoadGeneration(int generation)
    {
        lock (_loadGenerationQueueLock)
        {
            if (!_pendingLoadGenerations.Contains(generation))
                return;
            var keep = _pendingLoadGenerations.Where(g => g != generation).ToArray();
            _pendingLoadGenerations.Clear();
            foreach (var item in keep)
                _pendingLoadGenerations.Enqueue(item);
        }
    }

    private int ConsumeStartedLoadGeneration()
    {
        lock (_loadGenerationQueueLock)
        {
            if (_pendingLoadGenerations.Count == 0)
                return Volatile.Read(ref _eventGeneration);
            return _pendingLoadGenerations.Dequeue();
        }
    }

    private int PendingLoadGenerationCount
    {
        get
        {
            lock (_loadGenerationQueueLock)
                return _pendingLoadGenerations.Count;
        }
    }

    /// <summary>Sets the Authorization header for subsequent network loads
    /// (e.g. <c>MediaBrowser Token="…"</c>); null clears it. Keeps tokens out of URLs.</summary>
    public void SetHttpHeaders(string? authorizationValue)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "http-header-fields",
            authorizationValue is null ? "" : $"Authorization: {authorizationValue}");
    }

    public void Stop()
    {
        CheckAlive();
        LibMpv.Command(_ctx, "stop");
    }

    public bool Pause
    {
        get => _pause;
        set => SetFlagProperty("pause", value);
    }

    public double Duration => _duration;

    /// <summary>Current position in seconds; setting seeks (absolute).</summary>
    public double TimePos
    {
        get { lock (_posLock) return _pendingPos; }
        set => SetDoubleProperty("time-pos", value);
    }

    /// <summary>Seeks relative to the current position (negative = backward); mpv clamps.</summary>
    public void SeekRelative(double seconds)
    {
        CheckAlive();
        LibMpv.Command(_ctx, "seek", seconds.ToString(CultureInfo.InvariantCulture), "relative");
    }

    /// <summary>Steps one frame forward. mpv pauses as a side effect of either frame-step command,
    /// which the app never has to mirror: <c>pause</c> is an observed property, so the change comes
    /// back through <see cref="PauseChanged"/> like any other.</summary>
    public void FrameStep()
    {
        CheckAlive();
        LibMpv.Command(_ctx, "frame-step");
    }

    /// <summary>Steps one frame back. mpv implements this as a hi-res seek plus a re-decode, so it
    /// is far more expensive than <see cref="FrameStep"/> and only approximate on long-GOP
    /// content — which is why the binding for it does not auto-repeat.</summary>
    public void FrameBackStep()
    {
        CheckAlive();
        LibMpv.Command(_ctx, "frame-back-step");
    }

    /// <summary>0..200 (volume-max).</summary>
    public double Volume
    {
        get => _volume;
        set => SetDoubleProperty("volume", value);
    }

    public bool Mute
    {
        get => _mute;
        set => SetFlagProperty("mute", value);
    }

    /// <summary>True while mpv reports paused-for-cache (stalled on the network).</summary>
    public bool IsBuffering => _pausedForCache;

    /// <summary>Live-applies subtitle scaling (also persisted for future sessions via settings).</summary>
    public void SetSubtitleScale(double scale)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "sub-scale", scale.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Vertical position of mpv-rendered subtitles in percent of the window
    /// height (mpv sub-pos; 100 = bottom). Lifts subtitles above the overlay control
    /// bar while it is visible. No effect on burned-in subtitles.</summary>
    public void SetSubtitlePosition(int percent)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "sub-pos",
            Math.Clamp(percent, 0, 150).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Live-toggles hardware decoding.</summary>
    public void SetHardwareDecoding(bool enabled)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "hwdec", enabled ? "d3d11va" : "no");
        // A settings action, so once per toggle. hwdec is the first thing a decode failure is read
        // against, and until now the app could switch it with nothing written down anywhere.
        if (Diagnostics.AppLog.Verbose)
            Detail($"event=hwdec state={(enabled ? "on" : "off")} mode={(enabled ? "d3d11va" : "no")}");
    }

    /// <summary>Adds an external subtitle track (call after FileLoaded; mpv drops it on
    /// the next loadfile). The track then shows up via track-list observation.</summary>
    public void AddExternalSubtitle(string url, string? title, string? lang)
    {
        CheckAlive();
        // sub-add <url> [<flags> [<title> [<lang>]]] — trailing args can't be empty strings.
        var result = (title, lang) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => LibMpv.Command(_ctx, "sub-add", url, "auto", title, lang),
            ({ Length: > 0 }, _) => LibMpv.Command(_ctx, "sub-add", url, "auto", title),
            (_, { Length: > 0 }) => LibMpv.Command(_ctx, "sub-add", url, "auto", "External", lang),
            _ => LibMpv.Command(_ctx, "sub-add", url, "auto"),
        };
        // Structural: the URL is a stream URL on the user's server and carries the item behind it.
        // Which subtitle failed is answered by the [detail]/[player] records that bracket this one.
        if (result < 0)
            SayDetail($"event=sub_add outcome=failure error={Token(LibMpv.ErrorString(result))}");
    }

#if DEBUG
    /// <summary>Deterministic B40 late-external test path. The path is consumed but never logged;
    /// markers contain only bounded structural values and the entire hook is absent from Release.</summary>
    public void TestAddLateSubtitle(string path, int generation, int pendingCycles)
    {
        if (generation != LoadGeneration)
        {
            TestLateSubtitleSkip("add_generation_stale", generation, pendingCycles);
            return;
        }
        TestLateSubtitleMarker("add", generation, pendingCycles,
            Tracks.Count(t => t.Type == "sub"));
        AddExternalSubtitle(path, "English SDH", "eng");
        // Force the observed late-add snapshot to start at Off. Sticky restoration must issue its
        // own selection command, and the queued cycle must wait for that command's acknowledgement.
        SelectSubtitleTrack(null);
        if (!RefreshTracks(generation))
            TestLateSubtitleSkip("refresh_stale", generation, pendingCycles);
    }

    public void TestLateSubtitleMarker(
        string eventName, int generation, int pendingCycles, int subtitleCount, int delayMs = 0)
    {
        if (!TestLateSubtitleRequested())
            return;
        UiLog(FormattableString.Invariant(
            $"TestLateSubtitle event={Token(eventName)} generation={generation} pending_cycles={pendingCycles} subtitle_count={subtitleCount} delay_ms={delayMs}"));
    }

    public void TestLateSubtitleSkip(string reason, int generation, int pendingCycles)
    {
        if (!TestLateSubtitleRequested())
            return;
        UiLog(FormattableString.Invariant(
            $"TestLateSubtitle event=skip reason={Token(reason)} generation={generation} pending_cycles={pendingCycles}"));
    }

    private static bool TestLateSubtitleRequested()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE_GENERATION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE_DELAY_MS"));
#endif

    /// <summary>Selects a video track by mpv track id.</summary>
    public void SelectVideoTrack(int id)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "vid", id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Selects an audio track by mpv track id.</summary>
    public void SelectAudioTrack(int id)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "aid", id.ToString());
    }

    /// <summary>Selects a subtitle track by mpv track id; null disables subtitles.</summary>
    public void SelectSubtitleTrack(int? id)
    {
        CheckAlive();
        LibMpv.mpv_set_property_string(_ctx, "sid", id?.ToString() ?? "no");
        // The reported trigger for B41. A subtitle switch makes mpv refresh-seek the demuxer to
        // re-read the new stream, and that seek is internal: it raises no MPV_EVENT_SEEK, so
        // nothing else would arm the check for it.
        ArmPlaybackStallCheck("subtitle-track");
    }

    // The track/chapter shape last written down, so the records follow the CONTENT rather than the
    // property. mpv republishes track-list on every selection change (and a container can publish
    // it in two waves), so an unconditional record would fire several times per file and again on
    // every audio/subtitle switch. LoadFile clears both, so the next file always gets its own.
    private string? _loggedTrackShape;
    private int _loggedChapterCount = -1;

    /// <summary>Records the track COUNTS once per distinct shape. Titles and languages are
    /// deliberately absent: a track title is authored text from the file and routinely names the
    /// release, and the language is a per-file fact a reader can get from the file itself.</summary>
    private void DetailTracks()
    {
        var video = 0;
        var audio = 0;
        var sub = 0;
        foreach (var track in Tracks)
            switch (track.Type)
            {
                case "video": video++; break;
                case "audio": audio++; break;
                case "sub": sub++; break;
            }
        var shape = FormattableString.Invariant($"event=tracks video={video} audio={audio} sub={sub}");
        if (shape == _loggedTrackShape)
            return;
        _loggedTrackShape = shape;
        Detail(shape);
    }

    /// <summary>Records the chapter COUNT once per file. Chapter titles are authored text.</summary>
    private void DetailChapters()
    {
        if (Chapters.Count == _loggedChapterCount)
            return;
        _loggedChapterCount = Chapters.Count;
        Detail(FormattableString.Invariant($"event=chapters count={Chapters.Count}"));
    }

    private static IReadOnlyList<MpvChapter> ParseChapterList(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            var list = new List<MpvChapter>();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new MpvChapter(
                    TimeSeconds: el.TryGetProperty("time", out var t) ? t.GetDouble() : 0,
                    Title: el.TryGetProperty("title", out var ti) ? ti.GetString() : null));
            }
            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<MpvTrack> ParseTrackList(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            var list = new List<MpvTrack>();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not ("video" or "audio" or "sub"))
                    continue;
                list.Add(new MpvTrack(
                    Id: el.GetProperty("id").GetInt32(),
                    Type: type,
                    Title: el.TryGetProperty("title", out var ti) ? ti.GetString() : null,
                    Lang: el.TryGetProperty("lang", out var la) ? la.GetString() : null,
                    Selected: el.TryGetProperty("selected", out var se) && se.GetBoolean(),
                    Default: el.TryGetProperty("default", out var de) && de.GetBoolean(),
                    Forced: el.TryGetProperty("forced", out var fo) && fo.GetBoolean(),
                    Codec: el.TryGetProperty("codec", out var co) ? co.GetString() : null,
                    Width: el.TryGetProperty("demux-w", out var dw) ? dw.GetInt32() : 0,
                    Height: el.TryGetProperty("demux-h", out var dh) ? dh.GetInt32() : 0));
            }
            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads a property as a string (e.g. "hwdec-current"). Null if unavailable.</summary>
    public string? GetPropertyString(string name)
    {
        CheckAlive();
        var p = LibMpv.mpv_get_property_string(_ctx, name);
        if (p == nint.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            LibMpv.mpv_free(p);
        }
    }

    /// <summary>Reads a numeric property through its string form; false when mpv has no value for
    /// it. The distinction between "unavailable" and "zero" is load-bearing — <c>audio-pts</c> is
    /// unavailable precisely while audio is not playing (see <see cref="IsAudioStuck"/>) — so this
    /// never substitutes a default. Invariant culture: mpv formats numbers with a '.' regardless of
    /// the user's locale.</summary>
    private bool TryGetPropertyDouble(string name, out double value)
        => double.TryParse(GetPropertyString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private bool _wantRtxVsr;
    private bool _wantRtxHdr;
    private int _rtxMaxLumaNits = 1000;
    private string? _videoGamma;
    private bool _rtxChainActive;
    private bool _rtxHdrSuppressionLogged;

    /// <summary>
    /// Requests the NVIDIA d3d11vpp filters for RTX VSR and RTX Video HDR.
    /// The chain is (re)applied per file: NVIDIA's video processing supports SDR
    /// input only — on HDR content (PQ/HLG) the filters output a solid green frame
    /// (found live with a Dolby Vision movie), so they are suppressed automatically.
    /// Failures are surfaced via <see cref="LogMessage"/>, not thrown.
    /// </summary>
    public void SetRtxFilters(bool vsr, bool hdr, int maxLumaNits = 1000)
    {
        CheckAlive();
        _wantRtxVsr = vsr;
        _wantRtxHdr = hdr;
        _rtxMaxLumaNits = maxLumaNits;
        ApplyRtxFilterChain();
    }

    /// <summary>Why the RTX video filters are unavailable right now, or null when they
    /// can apply. Static cause: no NVIDIA RTX GPU. Dynamic cause: HDR (PQ/HLG) source —
    /// NVIDIA's video processing is SDR-only. UI toggles disable on non-null.</summary>
    public string? RtxUnavailableReason { get; private set; } = GpuCapabilities.RtxUnavailableReason;

    /// <summary>Raised on the UI thread when <see cref="RtxUnavailableReason"/> changes.</summary>
    public event Action? RtxAvailabilityChanged;

    /// <summary>Whether the VSR / RTX HDR filters are actually applied right now
    /// (requested AND not suppressed) — shown in the live-stats panel.</summary>
    public bool RtxVsrActive { get; private set; }
    public bool RtxHdrActive { get; private set; }

    /// <summary>Source video size from video-params (0 = no video / between files).</summary>
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    /// <summary>
    /// Letterbox geometry as mpv reports it (osd-dimensions): the height of mpv's render
    /// surface and the black bands above/below the drawn picture. 0 = no video / between files.
    ///
    /// **Unit contract: these are mpv-side PIXELS, never WPF DIPs.** The overlay window works in
    /// DIPs, so mixing the two silently scales everything by the display's DPI factor — which is
    /// why <see cref="BottomBandFraction"/> exists and is what consumers should reason with: a
    /// ratio of two mpv pixel values is unit-consistent and DPI-independent by construction, so
    /// it stays valid across monitors, scale factors and window sizes. The raw margins are kept
    /// public because a diagnostic reader wants the actual pixels, not just the ratio.
    /// </summary>
    public int OsdHeight { get; private set; }
    public int OsdMarginTop { get; private set; }
    public int OsdMarginBottom { get; private set; }

    /// <summary>The bottom letterbox band as a fraction of mpv's render-surface height
    /// (0 when there is no band, or no video yet). See the unit contract on <see cref="OsdHeight"/>.</summary>
    public double BottomBandFraction => OsdHeight > 0 ? (double)OsdMarginBottom / OsdHeight : 0;

    /// <summary>Raised on the UI thread when the osd-dimensions geometry above changes —
    /// a window resize, a video reconfig, a filter change, or a new file.</summary>
    public event Action? OsdGeometryChanged;

    /// <summary>The source is natively HDR (PQ/HLG) — independent of the RTX chain.</summary>
    public bool IsHdrContent => _videoGamma is "pq" or "hlg";

    /// <summary>Raised on the UI thread when the OSD status-badge inputs change:
    /// source size, source SDR/HDR-ness, or the RTX filters' actual on/off state.</summary>
    public event Action? VideoStateChanged;

    /// <summary>Recomputes and applies the d3d11vpp chain. **UI thread only** — it mutates the
    /// want/active/reason state and raises <see cref="LogMessage"/>; the gamma observation posts
    /// to the dispatcher rather than calling this from the mpv event thread (B5).</summary>
    private void ApplyRtxFilterChain()
    {
        if (_disposed)
            return;

        // Wait for real video params before applying — RTX defaults are requested
        // before the first file loads, and PQ frames must never hit the filter.
        var known = _videoGamma is not null;
        var hdrContent = _videoGamma is "pq" or "hlg";
        var capableGpu = GpuCapabilities.RtxUnavailableReason is null;
        var vsr = _wantRtxVsr && capableGpu && known && !hdrContent;
        var hdr = _wantRtxHdr && capableGpu && known && !hdrContent;

        var activeChanged = vsr != RtxVsrActive || hdr != RtxHdrActive;
        RtxVsrActive = vsr;
        RtxHdrActive = hdr;
        if (activeChanged)
        {
            // One record per CHANGE of the applied state, not per file: this method runs on every
            // gamma arrival and every toggle, and most of those leave the chain exactly as it was.
            if (Diagnostics.AppLog.Verbose)
                Detail($"event=rtx_chain vsr={(vsr ? "on" : "off")} hdr={(hdr ? "on" : "off")} "
                    + $"requested_vsr={(_wantRtxVsr ? "on" : "off")} requested_hdr={(_wantRtxHdr ? "on" : "off")} "
                    + $"source={(known ? hdrContent ? "hdr" : "sdr" : "unknown")}");
            Post(() => VideoStateChanged?.Invoke());
        }

        var reason = GpuCapabilities.RtxUnavailableReason
            ?? (hdrContent ? "HDR source (NVIDIA video processing is SDR-only)" : null);
        if (reason != RtxUnavailableReason)
        {
            RtxUnavailableReason = reason;
            // The REASON is a sentence written for a tooltip; the record carries the bounded token
            // for the same cause. GPU adapter NAMES are separately accepted in incident headers
            // (AppLog.EnvironmentHeader), so the capability itself is not the sensitive part —
            // free text inside a key=value record is simply not parseable.
            if (Diagnostics.AppLog.Verbose)
                Detail($"event=rtx_availability outcome={(reason is null ? "available" : "unavailable")} "
                    + $"reason={(reason is null ? "none"
                        : GpuCapabilities.RtxUnavailableReason is not null ? GpuCapabilities.RtxUnavailableToken
                        : "hdr_source")}");
            Post(() => RtxAvailabilityChanged?.Invoke());
        }

        if (hdrContent && (_wantRtxVsr || _wantRtxHdr) && !_rtxHdrSuppressionLogged)
        {
            _rtxHdrSuppressionLogged = true;
            Say("RTX filters suppressed: source is HDR (NVIDIA VSR/RTX HDR support SDR input only).");
        }
        if (!hdrContent)
            _rtxHdrSuppressionLogged = false;

        if (!vsr && !hdr && !_rtxChainActive)
            return; // nothing applied, nothing to clear

        var maxLuma = hdr && _rtxMaxLumaNits > 0 ? $",format=max-luma={_rtxMaxLumaNits}" : "";
        // `nvidia-true-hdr=yes`, not the bare `nvidia-true-hdr`. mpv's parser accepts a bare flag
        // suboption as shorthand for `=yes`, so both spellings work and the chain was verified live
        // on an RTX 5070 Ti either way — this is explicitness, not a fix. It was flagged as
        // uncertain by the player review precisely because a reader cannot tell shorthand from a
        // typo, and a typo'd suboption name is rejected as a whole-filter error.
        var err = (vsr, hdr) switch
        {
            (true, true) => LibMpv.Command(_ctx, "vf", "set", $"d3d11vpp=scaling-mode=nvidia:scale=2.0:nvidia-true-hdr=yes{maxLuma}"),
            (true, false) => LibMpv.Command(_ctx, "vf", "set", "d3d11vpp=scale=2:scaling-mode=nvidia"),
            (false, true) => LibMpv.Command(_ctx, "vf", "set", $"d3d11vpp=nvidia-true-hdr=yes{maxLuma}"),
            (false, false) => LibMpv.Command(_ctx, "vf", "clr", ""),
        };
        _rtxChainActive = vsr || hdr;
        if (err < 0)
            SayDetail($"event=rtx_chain outcome=failure vsr={(vsr ? "on" : "off")} hdr={(hdr ? "on" : "off")} error={Token(LibMpv.ErrorString(err))}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (LibMpv.Command(_ctx, "quit") < 0)
            LibMpv.mpv_wakeup(_ctx);
        if (!_eventThread.Join(TimeSpan.FromSeconds(5)))
            LibMpv.mpv_wakeup(_ctx); // last resort; thread is IsBackground so it can't block exit
        LibMpv.mpv_terminate_destroy(_ctx);
    }

    private void EventLoop()
    {
        while (true)
        {
            var evPtr = LibMpv.mpv_wait_event(_ctx, -1);
            MpvEvent ev;
            unsafe { ev = *(MpvEvent*)evPtr; }

            switch (ev.EventId)
            {
                case MpvEventId.Shutdown:
                    return;

                case MpvEventId.StartFile:
                    var startedGeneration = ConsumeStartedLoadGeneration();
                    Volatile.Write(ref _eventGeneration, startedGeneration);
#if DEBUG
                    UiLog(FormattableString.Invariant(
                        $"LoadGeneration event=start_file generation={startedGeneration} live_generation={LoadGeneration} pending_count={PendingLoadGenerationCount}"));
                    InjectStaleTestTracksAfterStart(startedGeneration);
#endif
                    break;

                case MpvEventId.PropertyChange:
                    unsafe { HandlePropertyChange(ev.ReplyUserdata, *(MpvEventProperty*)ev.Data); }
                    break;

                case MpvEventId.FileLoaded:
                    // Event identity comes from START_FILE, never the newest request. A second
                    // load can be requested while this event is waiting on the UI dispatcher.
                    var loadedGeneration = Volatile.Read(ref _eventGeneration);
                    Post(() => DispatchFileLoaded(loadedGeneration));
                    break;

                // A failed bitstream attempt shows up here: mpv reconfigures the audio chain,
                // re-opens the track with the software decoder and comes back with NO audio
                // output at all — at which point playback never starts. Checked on both this
                // event and a watchdog after FILE_LOADED, because which of the two arrives is
                // an mpv-internal detail and the failure mode is a total stall.
                case MpvEventId.AudioReconfig:
                    Post(() =>
                    {
                        // Bounded: mpv reconfigures the audio chain when a file opens, when the
                        // track changes and when an output is rebuilt — never per frame or tick.
                        if (Diagnostics.AppLog.Verbose)
                            Detail($"event=audio_reconfig ao={Token(_currentAo)}");
                        ArmAudioOutputCheck("audio-reconfig");
                        // Two independent failures share this one event: no output at all
                        // (passthrough, above) and an output that came back with its restart point
                        // ahead of the video clock (B39, below). Each watchdog stands down when the
                        // other's case is the live one.
                        ArmAudioSyncCheck("audio-reconfig");
                    });
                    break;

                // "The player is done loading/seeking and is ready to display frames." This is
                // the only honest end-of-loading signal: FILE_LOADED fires when the demuxer has
                // the headers, which is well before anything can be shown, and time-pos can read
                // a non-zero resume position while nothing has been decoded yet.
                // Every seek mpv begins, whether the app asked for it or a key did. Paired with
                // PLAYBACK_RESTART below in the healthy case; the pairing failing to happen is
                // B41, and the check reads the state itself rather than counting events.
                case MpvEventId.Seek:
                    Post(() => ArmPlaybackStallCheck("seek"));
                    break;

                case MpvEventId.PlaybackRestart:
                    Post(() => SetLoading(false));
                    break;

                case MpvEventId.EndFile:
                    // Whatever ended it — error, stop, EOF — nothing is loading any more. Without
                    // this a failed load would leave the indicator spinning forever, which is a
                    // worse lie than the transport bar it replaced.
                    Post(() => SetLoading(false));
                    unsafe
                    {
                        if (ev.Data != nint.Zero)
                        {
                            var end = *(MpvEventEndFile*)ev.Data;
                            var endReason = end.Reason;
                            // One record per file END, whatever ended it. The flag is tested
                            // BEFORE the post so a non-verbose run does not even queue a
                            // dispatcher operation from mpv's event thread.
                            if (Diagnostics.AppLog.Verbose)
                                Post(() => Detail($"event=end_file reason={endReason.ToString().ToLowerInvariant()}"));
                            if (end.Reason == MpvEndFileReason.Error)
                            {
                                var reason = LibMpv.ErrorString(end.Error);
                                Post(() =>
                                {
                                    if (Diagnostics.AppLog.Verbose)
                                        Detail($"event=playback outcome=failure reason={Token(reason)}");
                                    Say($"Playback failed: {reason}");
                                    PlaybackFailed?.Invoke(reason);
                                });
                            }
                        }
                    }
                    break;

                case MpvEventId.LogMessage:
                    unsafe
                    {
                        if (ev.Data != nint.Zero)
                        {
                            var msg = *(MpvEventLogMessage*)ev.Data;
                            var text =
                                $"[{Marshal.PtrToStringUTF8(msg.Prefix)}] {Marshal.PtrToStringUTF8(msg.Text)?.TrimEnd()}";
                            var level = Marshal.PtrToStringUTF8(msg.Level) ?? "info";
                            // Written here, on the event thread, and only while verbose: this is
                            // the bulk stream and it must not cost the UI thread a dispatch each.
                            Diagnostics.AppLog.MpvLine(level, text);
                            // Forward only what the app has always seen. Without this filter,
                            // turning verbose logging on would flood the breadcrumb ring and the
                            // Trace listener with mpv's per-frame chatter — a diagnostics setting
                            // that changes what the app reacts to is a trap.
                            if (level is "warn" or "error" or "fatal")
                            {
                                // Breadcrumbed HERE rather than from the UI-side LogMessage
                                // subscriber: the ring is memory-only and thread-safe, and a
                                // Post() never runs when the UI thread is the one dying — so
                                // mpv's last words before a crash used to be missing from the
                                // crash file that exists to carry them.
                                Diagnostics.AppLog.Breadcrumb("mpv", text);
                                Post(() => LogMessage?.Invoke(text));
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Synchronously replaces the public track snapshot with mpv's authoritative current
    /// <c>track-list</c>. The libmpv client API is thread-safe and serializes synchronous property
    /// reads through the playback core; this is the same established path as
    /// <see cref="GetPropertyString"/>. A generation check on both sides of the read prevents a
    /// snapshot from crossing a concurrent load request.</summary>
    public bool RefreshTracks(int generation)
    {
        CheckAlive();
        if (generation != LoadGeneration)
            return false;
        var tracks = ParseTrackList(GetPropertyString("track-list"));
        if (generation != LoadGeneration)
            return false;
        Volatile.Write(ref _authoritativeTracksGeneration, generation);
        PublishTracks(generation, tracks);
#if DEBUG
        UiLog(FormattableString.Invariant(
            $"TracksRefresh event=authoritative generation={generation} track_count={tracks.Count}"));
        CaptureTestTracks(generation, tracks);
        ScheduleTestLoadAfterTrackRefresh(generation);
#endif
        return true;
    }

    private void DispatchFileLoaded(int generation)
    {
#if DEBUG
        if (TryDelayFileLoadedOnUiThread(generation))
            return;
#endif
        CompleteFileLoaded(generation);
    }

    private void CompleteFileLoaded(int generation)
    {
        if (generation != LoadGeneration)
        {
#if DEBUG
            UiLog(FormattableString.Invariant(
                $"FileLoaded event=drop captured_generation={generation} live_generation={LoadGeneration} pending_count={PendingLoadGenerationCount}"));
#endif
            return;
        }
        // Inside the callback the event already posts, so mpv's thread pays nothing for it.
        if (Diagnostics.AppLog.Verbose)
            Detail(FormattableString.Invariant($"event=file_loaded gen={generation}"));
        FileLoaded?.Invoke(generation);
        ArmAudioOutputCheck("file-loaded");
    }

#if DEBUG
    private bool TryDelayFileLoadedOnUiThread(int generation)
    {
        const int maxDelayMs = 30_000;
        var rawDelay = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_FILE_LOADED_DELAY_MS");
        if (!int.TryParse(rawDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedDelay)
            || requestedDelay <= 0)
            return false;
        var rawGeneration = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_FILE_LOADED_DELAY_GENERATION");
        if (int.TryParse(rawGeneration, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var delayedGeneration) && delayedGeneration != generation)
            return false;

        var delayMs = Math.Min(requestedDelay, maxDelayMs);
        DelayFileLoadedOnUiThread(generation, delayMs);
        return true;
    }

    private async void DelayFileLoadedOnUiThread(int generation, int delayMs)
    {
        UiLog(FormattableString.Invariant(
            $"FileLoadedDelay event=enter generation={generation} delay_ms={delayMs} pending_count={PendingLoadGenerationCount}"));
        await Task.Delay(delayMs);
        UiLog(FormattableString.Invariant(
            $"FileLoadedDelay event=exit generation={generation} delay_ms={delayMs} pending_count={PendingLoadGenerationCount}"));
        CompleteFileLoaded(generation);
    }

    private void CaptureTestTracks(int generation, IReadOnlyList<MpvTrack> tracks)
    {
        if (tracks.Count == 0 || _testCapturedTracks is not null
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE")))
            return;
        _testCapturedTracks = tracks.ToArray();
        Volatile.Write(ref _testCapturedTracksGeneration, generation);
        UiLog(FormattableString.Invariant(
            $"TestTrackInjection event=capture generation={generation} track_count={tracks.Count}"));
    }

    private void InjectStaleTestTracksAfterStart(int startedGeneration)
    {
        var capturedGeneration = Volatile.Read(ref _testCapturedTracksGeneration);
        var tracks = _testCapturedTracks;
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_INJECT_STALE_TRACKS") != "1"
            || tracks is null || startedGeneration <= capturedGeneration
            || Interlocked.Exchange(ref _testStaleTracksInjected, 1) != 0)
            return;
        UiLog(FormattableString.Invariant(
            $"TestTrackInjection event=inject source_generation={capturedGeneration} advisory_generation={startedGeneration} live_generation={LoadGeneration} track_count={tracks.Count}"));
        HandleTrackObservation(tracks, startedGeneration, "test_stale");
    }

    private void ScheduleTestLoadAfterTrackRefresh(int generation)
    {
        var path = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE");
        if (string.IsNullOrWhiteSpace(path) || Interlocked.Exchange(ref _testLoadScheduled, 1) != 0)
            return;
        var triggerPath = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE_TRIGGER");
        const int maxScheduleDelayMs = 12_000;
        const int maxTriggerWaitMs = 30_000;
        var rawDelay = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE_DELAY_MS");
        var requestedDelay = int.TryParse(rawDelay, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var parsed) ? parsed : 6_000;
        var scheduleDelayMs = Math.Clamp(requestedDelay, 1, maxScheduleDelayMs);
        UiLog(FormattableString.Invariant(
            $"TestLoadSchedule event=armed generation={generation} trigger={(string.IsNullOrWhiteSpace(triggerPath) ? "track_refresh" : "signal")} delay_ms={scheduleDelayMs} pending_count={PendingLoadGenerationCount}"));
        _ = Task.Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(triggerPath))
            {
                var deadline = Environment.TickCount64 + maxTriggerWaitMs;
                while (!_disposed && !System.IO.File.Exists(triggerPath)
                       && Environment.TickCount64 < deadline)
                    await Task.Delay(100);
                if (_disposed)
                    return;
                if (!System.IO.File.Exists(triggerPath))
                {
                    UiLog(FormattableString.Invariant(
                        $"TestLoadSchedule event=skip reason=trigger_timeout generation={generation} pending_count={PendingLoadGenerationCount}"));
                    return;
                }
                UiLog(FormattableString.Invariant(
                    $"TestLoadSchedule event=triggered generation={generation} pending_count={PendingLoadGenerationCount}"));
            }
            await Task.Delay(scheduleDelayMs);
            if (_disposed)
                return;
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    return;
                var before = LoadGeneration;
                TestLoadFileRequested?.Invoke(path);
                UiLog(FormattableString.Invariant(
                    $"TestLoadSchedule event=requested from_generation={before} live_generation={LoadGeneration} pending_count={PendingLoadGenerationCount}"));
            });
        });
    }
#endif

    private void PublishTracks(int generation, IReadOnlyList<MpvTrack> tracks)
    {
        Tracks = tracks;
        if (Diagnostics.AppLog.Verbose)
            DetailTracks();
        TracksChanged?.Invoke(generation);
    }

    private void HandleTrackObservation(
        IReadOnlyList<MpvTrack> tracks, int advisoryGeneration, string source)
    {
        // START_FILE attribution is diagnostic only here. The correctness boundary is the current
        // request's authoritative FileLoaded snapshot: before it, no nonempty observed list may
        // escape, even if mpv has already advanced START_FILE and would stamp stale rows as current.
        var requestGeneration = LoadGeneration;
#if DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_NEXT_FILE"))
            || source == "test_stale")
        {
            UiLog(FormattableString.Invariant(
                $"TracksChanged event=capture source={source} advisory_generation={advisoryGeneration} request_generation={requestGeneration} track_count={tracks.Count}"));
        }
#endif
        if (tracks.Count > 0 && Volatile.Read(ref _authoritativeTracksGeneration) != requestGeneration)
        {
#if DEBUG
            UiLog(FormattableString.Invariant(
                $"TracksChanged event=suppress source={source} advisory_generation={advisoryGeneration} request_generation={requestGeneration} track_count={tracks.Count} phase=pre_file_loaded"));
#endif
            return;
        }

        Post(() =>
        {
            if (requestGeneration != LoadGeneration)
            {
#if DEBUG
                UiLog(FormattableString.Invariant(
                    $"TracksChanged event=drop source={source} request_generation={requestGeneration} live_generation={LoadGeneration} track_count={tracks.Count}"));
#endif
                return;
            }
            if (tracks.Count > 0
                && Volatile.Read(ref _authoritativeTracksGeneration) != requestGeneration)
            {
#if DEBUG
                UiLog(FormattableString.Invariant(
                    $"TracksChanged event=suppress source={source} advisory_generation={advisoryGeneration} request_generation={requestGeneration} track_count={tracks.Count} phase=ui_pre_file_loaded"));
#endif
                return;
            }
            PublishTracks(requestGeneration, tracks);
        });
    }

    private unsafe void HandlePropertyChange(ulong userdata, MpvEventProperty prop)
    {
        // Data is null when the property becomes unavailable (e.g. between files).
        if (prop.Data == nint.Zero)
        {
            if (userdata == ObsTimePos)
                PostPosition(0);
            else if (userdata is ObsVideoWidth or ObsVideoHeight)
                Post(() =>
                {
                    if (VideoWidth != 0 || VideoHeight != 0)
                    {
                        VideoWidth = 0;
                        VideoHeight = 0;
                        VideoStateChanged?.Invoke();
                    }
                });
            // Between files mpv drops osd-dimensions too — zero the affected leaf so nothing
            // keeps reporting the previous file's band (the video-params handler above resets
            // for the same reason).
            else if (userdata == ObsOsdMarginTop)
                PostOsdGeometry(mt: 0);
            else if (userdata == ObsOsdMarginBottom)
                PostOsdGeometry(mb: 0);
            else if (userdata == ObsOsdHeight)
                PostOsdGeometry(h: 0);
            return;
        }

        switch (userdata)
        {
            case ObsTimePos when prop.Format == MpvFormat.Double:
                PostPosition(*(double*)prop.Data);
                break;
            case ObsDuration when prop.Format == MpvFormat.Double:
                var duration = *(double*)prop.Data;
                Post(() => { _duration = duration; DurationChanged?.Invoke(duration); });
                break;
            case ObsPause when prop.Format == MpvFormat.Flag:
                var paused = *(int*)prop.Data != 0;
                Post(() =>
                {
                    _pause = paused;
                    PauseChanged?.Invoke(paused);
                    // Leaving pause is the one stall trigger with no event of its own to wait on.
                    // In the B41 capture the user unpaused a stalled player at 15:48:32.150 and got
                    // 26 s of nothing, so "play was pressed and nothing happened" has to be a
                    // question this asks. ArmPlaybackStallCheck is a timer restart, not work.
                    if (!paused)
                        ArmPlaybackStallCheck("unpause");
                });
                break;
            case ObsVolume when prop.Format == MpvFormat.Double:
                var volume = *(double*)prop.Data;
                Post(() => { _volume = volume; VolumeChanged?.Invoke(volume); });
                break;
            case ObsMute when prop.Format == MpvFormat.Flag:
                var mute = *(int*)prop.Data != 0;
                Post(() => { _mute = mute; MuteChanged?.Invoke(mute); });
                break;
            case ObsTrackList when prop.Format == MpvFormat.String:
                // Data is char** for observed string properties.
                var json = Marshal.PtrToStringUTF8(*(nint*)prop.Data);
                var tracks = ParseTrackList(json);
                HandleTrackObservation(tracks, Volatile.Read(ref _eventGeneration), "mpv");
                break;
            case ObsChapterList when prop.Format == MpvFormat.String:
                var chaptersJson = Marshal.PtrToStringUTF8(*(nint*)prop.Data);
                var chapters = ParseChapterList(chaptersJson);
                Post(() =>
                {
                    Chapters = chapters;
                    if (Diagnostics.AppLog.Verbose)
                        DetailChapters();
                    ChaptersChanged?.Invoke();
                });
                break;
            case ObsVideoGamma when prop.Format == MpvFormat.String:
                var gamma = Marshal.PtrToStringUTF8(*(nint*)prop.Data);
                // Posted like every other observation (B5). This used to run ApplyRtxFilterChain
                // straight from the event thread while SetRtxFilters ran it from the UI thread —
                // unsynchronised writes to _videoGamma/_rtxChainActive/RtxVsrActive/RtxHdrActive/
                // RtxUnavailableReason, so interleaving a UI toggle with a per-file gamma arrival
                // could leave _rtxChainActive disagreeing with mpv's actual filter chain. It also
                // raised LogMessage off-thread, against this class's "events raise on the UI
                // thread" contract. Both callers are now on the dispatcher, so no lock is needed.
                Post(() =>
                {
                    if (gamma == _videoGamma)
                        return;
                    _videoGamma = gamma;
                    ApplyRtxFilterChain(); // content SDR/HDR-ness changed — re-evaluate
                    VideoStateChanged?.Invoke();
                });
                break;
            case ObsVideoWidth when prop.Format == MpvFormat.Int64:
                var vw = (int)*(long*)prop.Data;
                Post(() => { if (vw != VideoWidth) { VideoWidth = vw; VideoStateChanged?.Invoke(); } });
                break;
            case ObsVideoHeight when prop.Format == MpvFormat.Int64:
                var vh = (int)*(long*)prop.Data;
                Post(() => { if (vh != VideoHeight) { VideoHeight = vh; VideoStateChanged?.Invoke(); } });
                break;
            case ObsOsdMarginTop when prop.Format == MpvFormat.Int64:
                PostOsdGeometry(mt: (int)*(long*)prop.Data);
                break;
            case ObsOsdMarginBottom when prop.Format == MpvFormat.Int64:
                PostOsdGeometry(mb: (int)*(long*)prop.Data);
                break;
            case ObsOsdHeight when prop.Format == MpvFormat.Int64:
                PostOsdGeometry(h: (int)*(long*)prop.Data);
                break;
            // current-ao arrives as Format.None (not an empty string) whenever there is no
            // audio output — which is precisely the state worth reacting to, so this case is
            // deliberately NOT gated on Format == String the way the others are.
            case ObsCurrentAo:
                var aoName = prop.Format == MpvFormat.String && prop.Data != nint.Zero
                    ? Marshal.PtrToStringUTF8(*(nint*)prop.Data)
                    : null;
                Post(() =>
                {
                    // The TRANSITION only. mpv republishes current-ao around every audio rebuild,
                    // and an output going away and coming back is the signature of the passthrough
                    // stall — an unconditional record would say the same thing on healthy files.
                    // "wasapi" is mpv's OUTPUT DRIVER name, not a device: no hardware is named.
                    if (Diagnostics.AppLog.Verbose && aoName != _currentAo)
                        Detail($"event=audio_output ao={Token(aoName)}");
                    _currentAo = aoName;
                    if (aoName is null)
                        ArmAudioOutputCheck("current-ao cleared");
                    else
                        _aoWatchdog?.Stop();   // an output exists; nothing to recover
                });
                break;
            case ObsPausedForCache when prop.Format == MpvFormat.Flag:
                var buffering = *(int*)prop.Data != 0;
                Post(() =>
                {
                    if (buffering != _pausedForCache)
                    {
                        _pausedForCache = buffering;
                        BufferingChanged?.Invoke(buffering);
                    }
                });
                break;
            case ObsEofReached when prop.Format == MpvFormat.Flag:
                // keep-open=yes: playback holds on the last frame and eof-reached flips true.
                var eof = *(int*)prop.Data != 0;
                Post(() =>
                {
                    if (eof && !_eofRaised)
                    {
                        _eofRaised = true;
                        EndReached?.Invoke();
                    }
                    else if (!eof)
                    {
                        _eofRaised = false;
                    }
                });
                break;
        }
    }

    // Latest-value coalescing for the three osd-dimensions leaves, in the same shape as
    // PostPosition's. mpv sends one property-change event per leaf, microseconds apart, so
    // applying each on its own dispatcher operation publishes geometry that never existed on
    // screen — measured: the new mb (115) against a stale h (1), i.e. a "band fraction" of 115.
    // The event thread updates the pending set; at most one dispatcher callback is in flight and
    // it publishes whatever has arrived by the time it runs.
    private readonly Lock _osdLock = new();
    private int _pendingOsdMt, _pendingOsdMb, _pendingOsdH;
    private bool _osdDispatchQueued;

    /// <summary>
    /// Records one osd-dimensions leaf and publishes the coalesced set on the UI thread, raising
    /// <see cref="OsdGeometryChanged"/> when the geometry actually moved. Callers pass only the
    /// field their property-change event carried; the pending set holds the latest of all three.
    ///
    /// **Posted, never applied on the mpv event thread.** It writes public state and raises an
    /// event, and this class's contract is that both happen on the UI thread — BUGS.md B5 is the
    /// already-fixed bug from breaking exactly that (the gamma observation used to mutate shared
    /// RTX state and raise LogMessage straight from the event thread).
    /// </summary>
    private void PostOsdGeometry(int? mt = null, int? mb = null, int? h = null)
    {
        lock (_osdLock)
        {
            if (mt is { } newTop)
                _pendingOsdMt = newTop;
            if (mb is { } newBottom)
                _pendingOsdMb = newBottom;
            if (h is { } newHeight)
                _pendingOsdH = newHeight;
            if (_osdDispatchQueued)
                return;
            _osdDispatchQueued = true;
        }
        _dispatcher.BeginInvoke(() =>
        {
            int pendingMt, pendingMb, pendingH;
            lock (_osdLock)
            {
                pendingMt = _pendingOsdMt;
                pendingMb = _pendingOsdMb;
                pendingH = _pendingOsdH;
                _osdDispatchQueued = false;   // reset before the disposed check so it can't stick
            }
            if (_disposed)
                return;
            if (pendingMt == OsdMarginTop && pendingMb == OsdMarginBottom && pendingH == OsdHeight)
                return;
            OsdMarginTop = pendingMt;
            OsdMarginBottom = pendingMb;
            OsdHeight = pendingH;
            UiLog(string.Format(CultureInfo.InvariantCulture,
                "OsdGeometry mt={0} mb={1} h={2} band={3:F4}",
                OsdMarginTop, OsdMarginBottom, OsdHeight, BottomBandFraction));
            OsdGeometryChanged?.Invoke();
        });
    }

    // Diagnostics: LIGHTWEAVER_UI_LOG=<path> — the SAME stream OverlayWindow appends its input
    // events to, on purpose: one file then shows both what the app believes the letterbox band
    // is and what the overlay did about it, with no new UI. The appender is duplicated per site
    // exactly as LIGHTWEAVER_SESSION_LOG / LIGHTWEAVER_STARTUP_LOG already are.
    private static readonly string? UiLogPath = Environment.GetEnvironmentVariable("LIGHTWEAVER_UI_LOG");

    private static void UiLog(string msg)
    {
        if (UiLogPath is null)
            return;
        try
        {
            System.IO.File.AppendAllText(UiLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics only — never disturb playback
        }
    }

    private void PostPosition(double pos)
    {
        lock (_posLock)
        {
            _pendingPos = pos;
            if (_posDispatchQueued)
                return;
            _posDispatchQueued = true;
        }
        _dispatcher.BeginInvoke(() =>
        {
            double latest;
            lock (_posLock)
            {
                latest = _pendingPos;
                _posDispatchQueued = false;
            }
            if (!_disposed)
                PositionChanged?.Invoke(latest);
        });
    }

    private void Post(Action action)
        => _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });

    private void SetFlagProperty(string name, bool value)
    {
        CheckAlive();
        unsafe
        {
            var flag = value ? 1 : 0;
            LibMpv.mpv_set_property(_ctx, name, MpvFormat.Flag, &flag);
        }
    }

    private void SetDoubleProperty(string name, double value)
    {
        CheckAlive();
        unsafe
        {
            LibMpv.mpv_set_property(_ctx, name, MpvFormat.Double, &value);
        }
    }

    private static void SetOption(nint ctx, string name, string value)
        => Check(LibMpv.mpv_set_option_string(ctx, name, value), $"set {name}={value}");

    private static void Check(int err, string what)
    {
        if (err < 0)
            throw new MpvException($"{what}: {LibMpv.ErrorString(err)}");
    }

    private void CheckAlive()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
