using System.Globalization;
using System.Text.Json;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Downloads;

/// <summary>A download quality preset (design 08, 8b): the original file or a transcode cap.
/// Presets with the same resolution differ in bitrate, which is why the bitrate is shown too.</summary>
public sealed record DownloadQuality(string Id, string Resolution, int? MaxWidth, int? MaxHeight, int? VideoBitRate)
{
    public const string AskId = "ask";

    public static readonly DownloadQuality Original = new("original", "Original", null, null, null);

    public static readonly IReadOnlyList<DownloadQuality> All =
    [
        Original,
        new("2160p-40", "4K", 3840, 2160, 40_000_000),
        new("1080p-20", "1080p", 1920, 1080, 20_000_000),
        new("1080p-8", "1080p", 1920, 1080, 8_000_000),
        new("720p-4", "720p", 1280, 720, 4_000_000),
        new("480p-1.5", "480p", 854, 480, 1_500_000),
    ];

    public bool IsOriginal => MaxWidth is null;

    /// <summary>"Original file" or "1080p".</summary>
    public string Label => IsOriginal ? L.Text("Downloads.OriginalFile") : Resolution;

    /// <summary>"no transcoding" or "8 Mbps".</summary>
    public string Note => VideoBitRate is { } bps
        ? L.Format("Downloads.Mbps", (bps / 1_000_000.0).ToString("0.#", CultureInfo.CurrentUICulture))
        : L.Text("Downloads.NoTranscoding");

    public static DownloadQuality? ById(string? id) => All.FirstOrDefault(q => q.Id == id);
}

/// <summary>Remembers the quality for new downloads in <c>download-quality.json</c>, or
/// <see cref="DownloadQuality.AskId"/> to ask each time. A damaged file reads as 1080p · 8 Mbps.</summary>
public static class DownloadQualityStore
{
    public const string DefaultId = "1080p-8";

    /// <summary>The file, settable so tests can use a temporary folder.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppPaths.Root, "download-quality.json");

    public static string Load()
    {
        try
        {
            var id = File.Exists(FilePath) ? JsonSerializer.Deserialize<string>(File.ReadAllText(FilePath)) : null;
            return id == DownloadQuality.AskId || DownloadQuality.ById(id) is not null ? id! : DefaultId;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return DefaultId;
        }
    }

    public static void Save(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Error("downloads", $"event=save_quality outcome=failure error={ex.GetType().Name}");
        }
    }
}

/// <summary>The sections of the Downloads page, in their fixed order.</summary>
public enum DownloadSection { Downloading, Queued, Failed, Downloaded }

/// <summary>The Downloads page's rules (design 08): sections, row lines, the badge count, the
/// storage meter and turning a title and a preset into a download.</summary>
public static class DownloadsModel
{
    public static DownloadSection SectionOf(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Paused => DownloadSection.Downloading,
        DownloadStatus.Queued => DownloadSection.Queued,
        DownloadStatus.Failed => DownloadSection.Failed,
        _ => DownloadSection.Downloaded,
    };

    /// <summary>Non-empty sections in order; the queue in run order, downloads newest first.</summary>
    public static IReadOnlyList<(DownloadSection Section, IReadOnlyList<DownloadItem> Items)> Sections(IEnumerable<DownloadItem> items)
    {
        var all = items.ToList();
        return [.. Enum.GetValues<DownloadSection>()
            .Select(s => (s, (IReadOnlyList<DownloadItem>)(s == DownloadSection.Downloaded
                ? [.. all.Where(i => SectionOf(i) == s).OrderByDescending(i => i.AddedTimestampMs)]
                : [.. all.Where(i => SectionOf(i) == s).OrderBy(i => i.AddedTimestampMs)])))
            .Where(s => s.Item2.Count > 0)];
    }

    /// <summary>The Downloads badge: running and queued items; gone when the queue is empty.</summary>
    public static int BadgeCount(IEnumerable<DownloadItem> items) => items.Count(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);

    /// <summary>The line under a running, paused or queued download.</summary>
    public static string ProgressLine(DownloadItem item, double bytesPerSecond, bool offline)
    {
        var done = DownloadManager.FormatBytes(item.BytesDownloaded);
        var total = item.TotalBytes > 0 ? DownloadManager.FormatBytes(item.TotalBytes) : null;
        var amount = total is null ? done : L.Format("Downloads.OfTotal", SameUnit(done, total) ? done[..done.LastIndexOf(' ')] : done, total);
        switch (item.Status)
        {
            case DownloadStatus.Queued:
                return total is null ? L.Text("Downloads.QueuedStatus") : L.Format("Downloads.QueuedAbout", total);
            case DownloadStatus.Paused:
                return L.Format("Downloads.PausedAt", amount);
        }
        if (offline) return L.Format("Downloads.WaitingForServer", amount);
        if (bytesPerSecond <= 0) return amount;
        var speed = L.Format("Downloads.Speed", DownloadManager.FormatBytes((long)bytesPerSecond));
        if (item.TotalBytes <= 0) return $"{amount} · {speed}";
        var left = TimeSpan.FromSeconds(Math.Max(0, item.TotalBytes - item.BytesDownloaded) / bytesPerSecond);
        return $"{amount} · {speed} · {TimeLeft(left)}";
    }

    /// <summary>"1.2 of 3.4 GB" rather than "1.2 GB of 3.4 GB" when both share a unit.</summary>
    private static bool SameUnit(string a, string b) => a.LastIndexOf(' ') is > 0 and var i && b.EndsWith(a[i..], StringComparison.Ordinal);

    /// <summary>"3 min left", "1 h 5 min left", or "less than a minute left".</summary>
    /// <summary>Whether a download plays now: finished, or running with the startup buffer on disk.</summary>
    public static bool CanPlay(DownloadItem item)
        => item.Status == DownloadStatus.Completed || item.VideoCompleted
            || item.Status == DownloadStatus.Downloading && item.BytesDownloaded >= DownloadManager.MinPartialStreamBytes;

    /// <summary>How much of a download is on disk, 0 to 1, from its exact size or else its
    /// estimate; null when neither is known yet.</summary>
    public static double? DownloadedFraction(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed || item.VideoCompleted) return 1;
        var total = item.TotalBytes > 0 ? item.TotalBytes : item.EstimatedTotalBytes;
        return total > 0 ? Math.Clamp((double)item.BytesDownloaded / total, 0, 1) : null;
    }

    /// <summary>How far into a title of <paramref name="runtimeSeconds"/> a seek can safely go on
    /// a download still being written: the bytes for that point, padded as the partial stream's
    /// own start check pads them, must be on disk. Null when the size is not known yet.</summary>
    public static double? ReachableSeconds(DownloadItem item, double runtimeSeconds)
    {
        if (item.Status == DownloadStatus.Completed || item.VideoCompleted) return runtimeSeconds;
        var total = item.TotalBytes > 0 ? item.TotalBytes : item.EstimatedTotalBytes;
        if (total <= 0 || runtimeSeconds <= 0) return null;
        var usable = Math.Max(0, item.BytesDownloaded - DownloadManager.MinPartialStreamBytes) / 1.25;
        return Math.Clamp(usable / total, 0, 1) * runtimeSeconds;
    }

    /// <summary>Where the server stream picks up after a partial stream gave out: where playback
    /// had got to, or where it started when mpv puts the position at the end of the file, which a
    /// download still being written cannot really have reached.</summary>
    public static double ResumeAfterPartialStream(double position, double fileEnd, double startedAt)
        => position > 0 && (fileEnd <= 0 || position < fileEnd - 1) ? position : startedAt;

    /// <summary>What Play does on a running download, or how much of the startup buffer has arrived.</summary>
    public static string PlayWhileDownloadingHint(DownloadItem item) => CanPlay(item)
        ? L.Text("Downloads.PartialPlayTooltip")
        : L.Format("Downloads.PreparingPlayback", DownloadManager.FormatBytes(item.BytesDownloaded), DownloadManager.FormatBytes(DownloadManager.MinPartialStreamBytes));

    public static string TimeLeft(TimeSpan left)
    {
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        if (left.TotalSeconds < 60) return L.Text("Downloads.UnderAMinute");
        return minutes >= 60 ? L.Format("Downloads.HoursMinutesLeft", minutes / 60, minutes % 60) : L.Format("Downloads.MinutesLeft", minutes);
    }

    /// <summary>"Failed: the server stopped transcoding."</summary>
    public static string FailedLine(DownloadItem item) => L.Format("Downloads.FailedBecause", item.ErrorMessage is { Length: > 0 } reason ? reason : L.Text("Downloads.UnknownError"));

    /// <summary>"2.8 GB · 1080p · 2 days ago" for a finished download.</summary>
    public static string DoneLine(DownloadItem item, long fileBytes, DateTimeOffset now)
    {
        var size = DownloadManager.FormatBytes(fileBytes > 0 ? fileBytes : item.TotalBytes);
        var quality = item.Resolution == "Original" ? L.Text("Downloads.Original") : item.Resolution;
        var parts = new[] { size, quality, Ago(DateTimeOffset.FromUnixTimeMilliseconds(item.AddedTimestampMs), now) };
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now - then).TotalDays);
        return days switch
        {
            < 1 => L.Text("Downloads.Today"),
            1 => L.Text("Downloads.Yesterday"),
            < 7 => L.Format("Downloads.DaysAgo", days),
            < 14 => L.Text("Downloads.WeekAgo"),
            < 30 => L.Format("Downloads.WeeksAgo", days / 7),
            < 60 => L.Text("Downloads.MonthAgo"),
            _ => L.Format("Downloads.MonthsAgo", days / 30),
        };
    }

    /// <summary>The storage meter: LightWeaver's share and the rest of the used space, as
    /// fractions of the disk.</summary>
    public static (double Downloads, double Other) StorageShares(long downloadBytes, long diskTotal, long diskFree)
    {
        if (diskTotal <= 0) return (0, 0);
        var downloads = Math.Clamp((double)downloadBytes / diskTotal, 0, 1);
        var used = Math.Clamp((double)(diskTotal - diskFree) / diskTotal, 0, 1);
        return (downloads, Math.Max(0, used - downloads));
    }

    /// <summary>The option a preset turns into for one title: the original file when asked for, or
    /// when the source is no larger than the cap; else a transcode with an estimated size, which
    /// keeps the source's tracks and the original to fall back on.</summary>
    public static DownloadOption Resolve(DownloadQuality quality, DownloadOption original, int? sourceHeight, long? runtimeTicks)
    {
        if (quality.IsOriginal || sourceHeight is { } h && quality.MaxHeight is { } cap && h <= cap) return original;
        long? estimate = runtimeTicks is > 0 ? (long)quality.VideoBitRate!.Value * (runtimeTicks.Value / TimeSpan.TicksPerSecond) / 8 : null;
        if (estimate is { } e && original.EstimatedSizeBytes is { } size && e >= size) return original;
        return new DownloadOption(quality.Label, original.MediaSourceId, IsOriginal: false, quality.MaxWidth, quality.VideoBitRate, estimate, "mp4", quality.Resolution)
        {
            AudioTracks = original.AudioTracks,
            SubtitleTracks = original.SubtitleTracks,
            OriginalOption = original,
        };
    }

    /// <summary>The download record for a title (file name from its id and name).</summary>
    public static DownloadItem BuildItem(MediaItem item, DownloadOption option, string serverUrl, string directory, bool includeSourceId, DateTimeOffset now)
    {
        var extension = DownloadStore.FileExtension(option.IsOriginal ? option.Container : "mp4");
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(item.Name.Select(c => invalid.Contains(c) ? '_' : c));
        if (safe.Length > 100) safe = safe[..100];
        return new DownloadItem
        {
            ItemId = item.Id,
            ServerUrl = serverUrl,
            Title = item.Type == BaseItemDto_Type.Episode && item.SeriesName is { Length: > 0 } show ? show : item.Name,
            Subtitle = item.Type == BaseItemDto_Type.Episode ? Details.DetailModel.EpisodeTag(item) : item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
            PosterUrl = item.ThumbUrl ?? item.PosterUrl,
            FilePath = Path.Combine(directory, FormattableString.Invariant($"{item.Id:N}_{safe}.{extension}")),
            Resolution = option.Resolution,
            MediaSourceId = includeSourceId ? option.MediaSourceId : null,
            Container = extension,
            MaxWidth = option.MaxWidth,
            VideoBitRate = option.VideoBitRate,
            TotalBytes = option.EstimatedSizeBytes ?? -1,
            RuntimeTicks = item.RuntimeTicks,
            PlaybackMetadata = item,
            AddedTimestampMs = now.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>Which episodes "Download all" queues: the unwatched ones, or every one when all
    /// are watched, leaving out those already downloaded or queued.</summary>
    public static IReadOnlyList<MediaItem> EpisodesToDownload(IEnumerable<MediaItem> episodes, Func<Guid, bool> tracked)
    {
        var playable = episodes.Where(e => e.IsPlayable).ToList();
        var unwatched = playable.Where(e => !e.Played).ToList();
        return [.. (unwatched.Count > 0 ? unwatched : playable).Where(e => !tracked(e.Id))];
    }
}

/// <summary>Download speed per item from successive byte counts, smoothed so the line does not jump.</summary>
public sealed class SpeedMeter
{
    private readonly Dictionary<Guid, (long Bytes, DateTimeOffset At, double Speed)> _last = [];

    public double Sample(Guid itemId, long bytes, DateTimeOffset at)
    {
        if (!_last.TryGetValue(itemId, out var last) || bytes < last.Bytes)
        {
            _last[itemId] = (bytes, at, 0);
            return 0;
        }
        var seconds = (at - last.At).TotalSeconds;
        if (seconds < 0.5) return last.Speed;
        var now = (bytes - last.Bytes) / seconds;
        var speed = last.Speed <= 0 ? now : last.Speed * 0.7 + now * 0.3;
        _last[itemId] = (bytes, at, speed);
        return speed;
    }

    public void Forget(Guid itemId) => _last.Remove(itemId);
}
