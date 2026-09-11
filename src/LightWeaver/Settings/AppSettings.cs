namespace LightWeaver.Settings;

/// <summary>What the Next/Previous transport controls do (Phase 7 M17) — the SMTC
/// media keys and the overlay next/prev pair follow the same mapping.</summary>
public enum TransportAction
{
    QueueStep,     // next/previous queue entry (with the Up Next fallback on next)
    ChapterStep,   // next/previous chapter
    Seek,          // seek +-TransportSeekSeconds
}

/// <summary>How a discovered signed update is handled.  All policies still check for updates;
/// only package staging and exit-time installation vary.</summary>
public enum UpdatePolicy
{
    ManualInstall,
    AutoDownload,
    AutoDownloadAndInstall,
}

/// <summary>
/// Player settings persisted as JSON. Defaults are the shipped behavior;
/// unknown fields in the file are ignored so downgrades don't break.
/// </summary>
public sealed class AppSettings
{
    // Schema
    /// <summary>Schema marker for one-off default migrations (see <see cref="SettingsStore"/>).
    /// <b>Defaults to 0 on purpose</b> — every file written before the marker existed lacks the
    /// property and therefore deserializes to 0, which is exactly how an unmigrated file is
    /// recognised. Do not "tidy" this to the current version: that silently disables every
    /// migration for the files it exists to catch.</summary>
    public int SettingsVersion { get; set; }

    // Playback
    public bool HardwareDecoding { get; set; } = true;
    /// <summary>Bitrate cap for server playback in Mbps; 0 = unlimited (direct play).
    /// Exceeding sources are transcoded server-side (HLS).</summary>
    public int MaxStreamingBitrateMbps { get; set; }
    private int _videoBufferMiB = 128;
    /// <summary>Network read-ahead packet budget. Values from older or edited files are bounded.</summary>
    public int VideoBufferMiB
    {
        get => _videoBufferMiB;
        set => _videoBufferMiB = Math.Clamp(value, 32, 1024);
    }
    public bool RtxVsrDefault { get; set; }
    public bool RtxHdrDefault { get; set; }
    /// <summary>Max luminance mpv tags RTX Video HDR output with (match the NVIDIA app value).</summary>
    public int RtxHdrMaxLumaNits { get; set; } = 1000;

    // Subtitles
    /// <summary>Scales image subs (and multiplies text subs); font size below is the
    /// primary lever for text subs.</summary>
    public double SubtitleScale { get; set; } = 1.0;
    /// <summary>ISO 639 code (e.g. "eng", "ger"); empty = leave mpv's default selection.</summary>
    public string PreferredSubtitleLanguage { get; set; } = "";
    /// <summary>Font family for text subs; empty = mpv's default (sans-serif).</summary>
    public string SubtitleFontFamily { get; set; } = "";
    /// <summary>mpv sub-font-size (default 38).</summary>
    public int SubtitleFontSize { get; set; } = 38;
    /// <summary>"#RRGGBB"; empty = mpv default (white).</summary>
    public string SubtitleTextColor { get; set; } = "";
    /// <summary>"#RRGGBB"; empty = mpv default (black).</summary>
    public string SubtitleBorderColor { get; set; } = "";
    /// <summary>mpv sub-border-size (default 3).</summary>
    public double SubtitleBorderSize { get; set; } = 3.0;
    /// <summary>mpv sub-shadow-offset (default 0 = no shadow).</summary>
    public double SubtitleShadowOffset { get; set; }
    /// <summary>Resting vertical position (mpv sub-pos percent, 100 = bottom). The
    /// control-bar lift composes against this value.</summary>
    public int SubtitleBasePosition { get; set; } = 100;

    // Audio
    public string PreferredAudioLanguage { get; set; } = "";
    /// <summary>mpv audio-device name ("auto" = system default).</summary>
    public string AudioDevice { get; set; } = "auto";
    /// <summary>Bitstream compressed audio to the device (audio-spdif). Mutually
    /// exclusive with volume normalization (passthrough bypasses filters).</summary>
    public bool AudioPassthrough { get; set; }
    /// <summary>dynaudnorm audio filter.</summary>
    public bool VolumeNormalization { get; set; }

    // External metadata
    /// <summary>OMDB API key (omdbapi.com) for IMDb/Rotten Tomatoes/Metacritic scores on
    /// the detail view. Empty = the external ratings row is silently skipped.</summary>
    public string OmdbApiKey { get; set; } = "";

    // Behavior
    public bool AutoSkipIntro { get; set; }
    public bool AutoSkipCredits { get; set; }
    public int SkipForwardSeconds { get; set; } = 10;
    public int SkipBackwardSeconds { get; set; } = 10;
    /// <summary>The Ctrl+Left / Ctrl+Right large seek, one value for both directions.</summary>
    public int LargeSkipSeconds { get; set; } = 30;
    public bool AutoPlayNextEpisode { get; set; } = true;
    public int AutoPlayCountdownSeconds { get; set; } = 10;

    // Shortcuts (Phase 7 M16): PlayerAction name -> "[Ctrl+][Alt+][Shift+]KeyName"
    // chord ("" = unbound). Missing actions fall back to PlayerActionDispatcher.Defaults
    // (partial/old files stay valid); unknown names are ignored.
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    // Caches (Phase 7 M19; surfaced by the M21 cache UI)
    /// <summary>Image disk-cache cap in MB (LRU eviction).</summary>
    public int ImageCacheMaxMb { get; set; } = 500;
    /// <summary>Maximum items cached per library folder (default 5,000).</summary>
    public int FolderCacheMaxItems { get; set; } = 5000;

    // Downloads (Phase 7 M20)
    /// <summary>Where downloaded media lands; "" = %LOCALAPPDATA%\LightWeaver\downloads.
    /// The index stays at the default location so moving the directory never orphans it.</summary>
    public string DownloadDirectory { get; set; } = "";
    /// <summary>Simultaneous downloads; the rest wait as Queued.</summary>
    public int MaxParallelDownloads { get; set; } = 2;
    /// <summary>Resolution preselected in the download picker ("Original", "1080p", "720p",
    /// "480p"); the picker still shows so the size estimate is always visible.</summary>
    public string DefaultDownloadResolution { get; set; } = "Original";

    // Diagnostics
    /// <summary>Verbose logging: raises mpv's client log level from warn to v (written to
    /// <c>logs\mpv.log</c>) and adds the playback/session events that are otherwise breadcrumbs
    /// only to <c>logs\app.log</c>. Off by default — crash and playback-failure files are written
    /// either way, since those are the evidence you cannot go back and collect.</summary>
    public bool VerboseLogging { get; set; }

    // Transport mapping (Phase 7 M17): what Media Next/Previous (SMTC keys + the
    // overlay next/prev pair) do. Serialized as enum NUMBERS by System.Text.Json.
    public TransportAction MediaNextAction { get; set; } = TransportAction.QueueStep;
    public TransportAction MediaPrevAction { get; set; } = TransportAction.QueueStep;
    /// <summary>Seconds for the Seek transport action.</summary>
    public int TransportSeekSeconds { get; set; } = 10;

    // Updates. Manual remains the default: discovery is automatic, installation is not.
    public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.ManualInstall;
}
