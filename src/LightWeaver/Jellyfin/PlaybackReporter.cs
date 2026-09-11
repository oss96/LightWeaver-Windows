using System.Windows.Threading;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Reports playback state to the server: start, progress every 5 s, stop.
/// All calls are fire-safe — reporting must never disturb playback — and the
/// stop report is sent fire-and-forget so it survives view teardown (a lesson
/// from the predecessor, which lost stop reports to scope cancellation).
/// Position ticks are 100 ns units: seconds × 10 000 000.
/// </summary>
public sealed class PlaybackReporter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Func<JellyfinApiClient> _clientProvider;
    private readonly Func<(double PositionSeconds, bool IsPaused)> _snapshot;
    private readonly DispatcherTimer _timer;

    private Guid _itemId;
    private string? _playSessionId;
    private string? _mediaSourceId;
    private bool _transcode;
    // Captured per Start: reports keep hitting the server session that STARTED the
    // playback even if the user warm-switches profiles mid-play.
    private JellyfinApiClient? _client;

    public PlaybackReporter(Func<JellyfinApiClient> clientProvider, Func<(double, bool)> playbackSnapshot)
    {
        _clientProvider = clientProvider;
        _snapshot = playbackSnapshot;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await ReportProgressAsync();
    }

    public bool IsActive => _playSessionId is not null;

    /// <summary>Starts a reporting session. The server-issued PlaySessionId and
    /// MediaSourceId from playback negotiation tie the reports to the server's own
    /// session (transcode teardown etc.); without them (local negotiation failure)
    /// a client-minted session id keeps reporting working like before.</summary>
    public void Start(Guid itemId, long positionTicks, string? playSessionId = null,
        string? mediaSourceId = null, bool transcode = false)
    {
        _itemId = itemId;
        _mediaSourceId = mediaSourceId;
        _transcode = transcode;
        _client = _clientProvider();
        _playSessionId = playSessionId ?? Guid.NewGuid().ToString("N");
        var sessionId = _playSessionId;
        Diagnostics.AppLog.Detail("playback-report", FormattableString.Invariant(
            $"event=report op=start outcome=sent item={_itemId:N} play_method={(_transcode ? "transcode" : "direct_play")} server_session={(playSessionId is null ? "false" : "true")} position_ticks={positionTicks}"));
        _ = Guard("start", () => _client!.Sessions.Playing.PostAsync(new PlaybackStartInfo
        {
            ItemId = _itemId,
            MediaSourceId = _mediaSourceId,
            PositionTicks = positionTicks,
            CanSeek = true,
            IsPaused = false,
            IsMuted = false,
            PlayMethod = _transcode
                ? PlaybackStartInfo_PlayMethod.Transcode
                : PlaybackStartInfo_PlayMethod.DirectPlay,
            PlaySessionId = sessionId,
        }));
        _timer.Start();
    }

    private async Task ReportProgressAsync()
    {
        if (_playSessionId is null || _client is null)
            return;
        var (seconds, paused) = _snapshot();
        // No success record here on purpose: this fires every 5 s for the whole runtime of every
        // item, and a per-tick line would bury the events an incident is read for. Only a FAILED
        // progress report is worth a line, which Guard writes.
        await Guard("progress", () => _client.Sessions.Playing.Progress.PostAsync(new PlaybackProgressInfo
        {
            ItemId = _itemId,
            MediaSourceId = _mediaSourceId,
            PositionTicks = ToTicks(seconds),
            CanSeek = true,
            IsPaused = paused,
            PlayMethod = _transcode
                ? PlaybackProgressInfo_PlayMethod.Transcode
                : PlaybackProgressInfo_PlayMethod.DirectPlay,
            PlaySessionId = _playSessionId,
        }));
    }

    /// <summary>
    /// Safe to call fire-and-forget during teardown/app close; the returned task
    /// completes when the server has the stop report (or it failed), so callers
    /// that re-read UserData can await it to avoid a stale resume position.
    /// </summary>
    public Task Stop()
    {
        if (_playSessionId is null || _client is null)
            return Task.CompletedTask;
        _timer.Stop();
        var (seconds, _) = _snapshot();
        var info = new PlaybackStopInfo
        {
            ItemId = _itemId,
            MediaSourceId = _mediaSourceId,
            PositionTicks = ToTicks(seconds),
            Failed = false,
            PlaySessionId = _playSessionId,
        };
        _playSessionId = null;
        var client = _client;   // a new Start() mustn't retarget this in-flight stop
        Diagnostics.AppLog.Detail("playback-report", FormattableString.Invariant(
            $"event=report op=stop outcome=sent item={_itemId:N} position_ticks={info.PositionTicks}"));
        return Guard("stop", () => client.Sessions.Playing.Stopped.PostAsync(info));
    }

    private static long ToTicks(double seconds) => (long)(seconds * 10_000_000);

    /// <summary>Reporting stays best-effort — a failed report must never disturb playback — but it
    /// is no longer SILENT. A server that rejects progress reports (revoked token, reaped session)
    /// previously showed up only as watched state that mysteriously never advanced, with nothing
    /// anywhere admitting the reports were being dropped. The exception's TYPE is recorded, not its
    /// message: an SDK response failure can carry the request URL.</summary>
    private static async Task Guard(string op, Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("playback-report",
                $"event=report op={op} outcome=failure error={ex.GetType().Name}");
        }
    }
}
