using System.Windows.Threading;
using LightWeaver.Diagnostics;
using LightWeaver.Jellyfin;

namespace LightWeaver.Player;

/// <summary>The group-owned player operations, independent of the window's item-loading path.
/// All mutations run on the supplied dispatcher; ReadState only reads cached player properties.</summary>
public sealed class SyncPlayPlayerBinding : IDisposable
{
    private readonly MpvPlayer _player;
    private readonly Dispatcher _dispatcher;
    private readonly ISyncPlayReporter _reporter;
    private readonly Func<(SyncPlayState State, TimeSpan Offset)?> _readGroup;
    private readonly Func<DateTime> _utcNow;
    private int _generation = -1;
    private SyncPlayReportContext? _reportContext;
    private SyncPlayAction? _deferred;
    private DispatcherTimer? _due;
    private bool _readyPending;
    private bool _settled;
    private bool _awaitingLoad;
    private bool _startPaused;
    private bool _disposed;
    private DateTime _lastDriftSeekUtc;

    public event Action? StopRequested;

    public SyncPlayPlayerBinding(MpvPlayer player, Dispatcher dispatcher, ISyncPlayReporter reporter,
        Func<(SyncPlayState State, TimeSpan Offset)?> readGroup, Func<DateTime>? utcNow = null)
    {
        _player = player;
        _dispatcher = dispatcher;
        _reporter = reporter;
        _readGroup = readGroup;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        player.FileLoaded += OnFileLoaded;
        player.PlaybackRestarted += OnPlaybackRestarted;
        player.BufferingChanged += OnBufferingChanged;
        player.PositionChanged += OnPositionChanged;
    }

    public SyncPlayPlayerState ReadState() => Owns(_generation)
        ? new(_player.Pause, _player.IsBuffering, _player.TimePos) : SyncPlayPlayerState.Idle;

    public void ArmForLoad(int loadGeneration, bool startPaused, SyncPlayReportContext? expectedContext = null)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Reset();
        var context = expectedContext ?? _reporter.ReportContext;
        if (context is null || context != _reporter.ReportContext || _readGroup() is null) return;
        _reportContext = context;
        _generation = loadGeneration;
        _startPaused = startPaused;
        _readyPending = true;
        _awaitingLoad = true;
    }

    public void Apply(SyncPlayAction action, int loadGeneration)
    {
        _dispatcher.VerifyAccess();
        if (!Owns(loadGeneration))
        {
            AppLog.Detail("syncplay", "event=action outcome=stale");
            return;
        }
        if (action.Kind == SyncPlayActionKind.Noop) return;
        CancelDue();
        if (action.Kind == SyncPlayActionKind.Stop)
        {
            _deferred = null;
            _readyPending = false;
            StopRequested?.Invoke();
            return;
        }
        if (_awaitingLoad || _player.IsLoading)
        {
            _deferred = action;
            return;
        }
        switch (action.Kind)
        {
            case SyncPlayActionKind.SeekAndArmUnpause:
                _player.Pause = true;
                if (action.NeedsSeek) Seek(action.TargetSeconds);
                ArmUnpause(action.DueLocalUtc, loadGeneration);
                break;
            case SyncPlayActionKind.UnpauseNow:
                if (action.NeedsSeek) Seek(action.TargetSeconds);
                _player.Pause = false;
                break;
            case SyncPlayActionKind.Pause:
                _player.Pause = true;
                if (action.NeedsSeek) Seek(action.TargetSeconds);
                break;
            case SyncPlayActionKind.Seek:
                _player.Pause = true;
                Seek(action.TargetSeconds);
                break;
            case SyncPlayActionKind.DriftSeek:
                var delta = action.TargetSeconds - _player.TimePos;
                _lastDriftSeekUtc = _utcNow();
                _player.TimePos = action.TargetSeconds;
                AppLog.Info("syncplay", FormattableString.Invariant(
                    $"event=drift outcome=seek delta_ms={delta * 1000:0}"));
                break;
        }
    }

    public void RefreshReportContext(SyncPlayReportContext context)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _generation != _player.LoadGeneration || _reportContext is not { } previous
            || previous.GroupId != context.GroupId || previous.PlaylistItemId != context.PlaylistItemId
            || context != _reporter.ReportContext || _readGroup() is null) return;
        // A restored membership keeps this file, but old membership reports and commands expire.
        CancelDue();
        _deferred = null;
        _reportContext = context;
        _readyPending = true;
        ReportPendingReady();
    }

    private void Seek(double seconds)
    {
        _readyPending = true;
        _settled = false;
        _player.TimePos = seconds;
    }

    private void ArmUnpause(DateTime dueUtc, int generation)
    {
        var remaining = dueUtc - _utcNow();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        { Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero };
        _due = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_due != timer) return;
            _due = null;
            if (!Owns(generation)) return;
            _player.Pause = false;
            AppLog.Info("syncplay", FormattableString.Invariant(
                $"event=unpause outcome=applied late_ms={(_utcNow() - dueUtc).TotalMilliseconds:0}"));
        };
        timer.Start();
    }

    private bool Owns(int generation) => !_disposed && generation == _generation
        && generation == _player.LoadGeneration && _reportContext is not null
        && _reportContext == _reporter.ReportContext && _readGroup() is not null;

    private void OnFileLoaded(int generation)
    {
        if (Owns(generation) && _startPaused) _player.Pause = true;
    }

    private void OnPlaybackRestarted(int generation)
    {
        if (!Owns(generation)) return;
        _awaitingLoad = false;
        _settled = true;
        if (_deferred is { } action)
        {
            _deferred = null;
            // The incoming file can finish after the scheduled start. Extrapolate from the
            // action's resolution instant, then compare against this file rather than the old one.
            var target = action.TargetTicks;
            if (action.Kind is SyncPlayActionKind.UnpauseNow or SyncPlayActionKind.SeekAndArmUnpause
                && action.DueLocalUtc != default && _utcNow() > action.DueLocalUtc)
                target += (_utcNow() - action.DueLocalUtc).Ticks;
            action = action with
            {
                TargetTicks = target,
                NeedsSeek = Math.Abs(target / (double)TimeSpan.TicksPerSecond - _player.TimePos)
                    > SyncPlayScheduler.SeekThreshold.TotalSeconds,
            };
            Apply(action, generation);
        }
        ReportPendingReady();
    }

    private void ReportPendingReady()
    {
        if (!_readyPending || !_settled || _player.IsBuffering
            || !_player.TryReadPosition(out var position)) return;
        _readyPending = false;
        _ = ReportReadyAsync(_player.GetPropertyString("pause") != "yes", Ticks(position));
    }

    private async Task ReportReadyAsync(bool playing, long ticks)
    {
        try { await _reporter.ReportReadyAsync(playing, ticks, _reportContext); }
        catch (Exception ex) { AppLog.Error("syncplay", $"event=ready outcome=failed exception={ex.GetType().Name}"); }
    }

    private void OnBufferingChanged(bool buffering)
    {
        if (!Owns(_generation) || !_player.TryReadPosition(out var position)) return;
        var released = _reporter.ReportBuffering(buffering, Ticks(position),
            _player.GetPropertyString("pause") != "yes", ready: _settled, expectedContext: _reportContext);
        if (buffering) return;
        if (released && _settled) _readyPending = false;
        else ReportPendingReady();
    }

    private void OnPositionChanged(double position, int generation)
    {
        if (!Owns(generation) || _due is not null || _readyPending || _player.IsLoading
            || _readGroup() is not { } group) return;
        var action = SyncPlayScheduler.ResolveDrift(group.State with
        {
            IsPaused = _player.Pause,
            IsBuffering = _player.IsBuffering,
            LastDriftSeekUtc = _lastDriftSeekUtc,
        }, group.Offset, _utcNow(), position);
        if (action.Kind == SyncPlayActionKind.DriftSeek) Apply(action, generation);
    }

    private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    private void CancelDue()
    {
        _due?.Stop();
        _due = null;
    }

    public void Reset()
    {
        _dispatcher.VerifyAccess();
        CancelDue();
        _generation = -1;
        _reportContext = null;
        _deferred = null;
        _readyPending = false;
        _settled = false;
        _awaitingLoad = false;
        _lastDriftSeekUtc = default;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Reset();
        _disposed = true;
        _player.FileLoaded -= OnFileLoaded;
        _player.PlaybackRestarted -= OnPlaybackRestarted;
        _player.BufferingChanged -= OnBufferingChanged;
        _player.PositionChanged -= OnPositionChanged;
    }
}
