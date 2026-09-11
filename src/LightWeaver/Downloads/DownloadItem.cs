namespace LightWeaver.Downloads;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// One tracked download (Phase 7 M20). Persisted to the downloads index as JSON;
/// progress fields update in memory only — the store sees status transitions.
/// The transcode knobs (MaxWidth/VideoBitRate/Container) are recorded so an
/// interrupted download resumes with the exact same server URL.
/// </summary>
public sealed record DownloadItem
{
    public required Guid ItemId { get; init; }
    public required string ServerUrl { get; init; }
    public required string Title { get; init; }
    /// <summary>Card-style context line ("Series · S1E5" / year); display only.</summary>
    public string? Subtitle { get; init; }
    public string? PosterUrl { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Display label of the picked quality ("Original", "720p", …).</summary>
    public string Resolution { get; init; } = "Original";
    /// <summary>Media source for single-item downloads; batch items omit it (default source).</summary>
    public string? MediaSourceId { get; init; }
    public string? Container { get; init; }
    /// <summary>Transcode width; null = original-quality download.</summary>
    public int? MaxWidth { get; init; }
    public int? VideoBitRate { get; init; }
    public DownloadStatus Status { get; init; } = DownloadStatus.Queued;
    /// <summary>0..1; estimated for transcoded downloads (server total is unknown up front).</summary>
    public double Progress { get; init; }
    public long BytesDownloaded { get; init; }
    /// <summary>-1 = unknown (e.g. a transcoded stream before headers arrive).</summary>
    public long TotalBytes { get; init; } = -1;
    public long AddedTimestampMs { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsTranscoded => MaxWidth is not null;
}
