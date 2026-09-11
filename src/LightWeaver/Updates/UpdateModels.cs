namespace LightWeaver.Updates;

/// <summary>The explicit lifecycle of a release check, staged package, and installer launch.</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Installing,
    Error,
}

/// <summary>A stable GitHub release. A missing setup asset is still a real available release.</summary>
public sealed record UpdateInfo(Version Version, Uri? SetupAssetUri, Uri ReleasePageUri, string? AssetName,
    long? ExpectedSize = null, string? PackageSha256 = null, string? DebugSha256 = null)
{
    public string DisplayVersion => Version.ToString(3);
}

public sealed record UpdateStatus(UpdatePhase Phase, UpdateInfo? Update, long BytesDownloaded = 0,
    long? TotalBytes = null, string? Detail = null, DateTimeOffset? LastCheckedUtc = null)
{
    public double? Progress => TotalBytes is > 0
        ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0, 1) : null;
}

internal sealed record UpdateStageMetadata(string Version, string AssetName, string AssetUrl,
    string ReleaseUrl, DateTimeOffset UpdatedUtc, long BytesDownloaded = 0, long? ExpectedSize = null,
    string? PackageSha256 = null, string? DebugSha256 = null);
