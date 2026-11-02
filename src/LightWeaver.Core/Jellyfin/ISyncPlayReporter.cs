namespace LightWeaver.Jellyfin;

public readonly record struct SyncPlayReportContext(long MembershipEpoch, Guid GroupId, Guid? PlaylistItemId);

/// <summary>Player reports shared by the group client and the windowless binding fixture.</summary>
public interface ISyncPlayReporter
{
    SyncPlayReportContext? ReportContext { get; }
    Task ReportReadyAsync(bool isPlaying, long positionTicks, SyncPlayReportContext? expectedContext = null);

    /// <summary>Returns true on a clear edge when the client schedules a releasing Ready.
    /// A short buffer returns false unless it superseded an outstanding release, so a pending
    /// load or seek still owns its Ready when the client has no buffering report to release.</summary>
    bool ReportBuffering(bool buffering, long positionTicks, bool isPlaying, bool ready = true,
        SyncPlayReportContext? expectedContext = null);
}
