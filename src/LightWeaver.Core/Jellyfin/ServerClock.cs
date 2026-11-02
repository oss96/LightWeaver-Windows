using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Diagnostics;

namespace LightWeaver.Jellyfin;

/// <summary>The two instants a <c>GET /GetUtcTime</c> answer carries.</summary>
public sealed record ServerTimeSample(DateTimeOffset RequestReceptionTime, DateTimeOffset ResponseTransmissionTime);

/// <summary>One clock comparison: how far ahead the server is, and what the round trip cost.</summary>
/// <param name="Offset">Server clock minus local clock.</param>
/// <param name="Delay">Round-trip time with the server's own processing time taken out.</param>
public readonly record struct ClockMeasurement(TimeSpan Offset, TimeSpan Delay)
{
    /// <summary>Half the round trip — what the server is told, and what it sizes a group's pre-roll
    /// off.</summary>
    public TimeSpan Ping => Delay / 2;
}

/// <summary>
/// Keeps this client's idea of the server's clock. SyncPlay's whole protocol is instants in SERVER
/// time — a command says "be at this position at this instant" — so every one of them has to be
/// converted before it means anything locally, and the conversion is only as good as this.
///
/// <para>NTP's exchange, which is what the endpoint is shaped for: <c>t0</c> local before the
/// request, <c>t1</c> and <c>t2</c> the server's reception and transmission, <c>t3</c> local on
/// arrival. <c>offset = ((t1-t0) + (t2-t3)) / 2</c> and <c>delay = (t3-t0) - (t2-t1)</c>. The offset
/// is only exactly right when the two legs of the round trip took the same time, so the measurement
/// to believe is the one with the SMALLEST delay — the least room for the paths to have been
/// asymmetric. Averaging instead would let one congested sample poison every good one, which is why
/// NTP does not average either.</para>
/// </summary>
public sealed class ServerClock : IDisposable
{
    /// <summary>How many measurements the window keeps. Eight at a minute apart is a working
    /// afternoon's worth of history and still small enough that a genuinely better sample displaces
    /// a stale champion within a few minutes.</summary>
    private const int WindowSize = 8;

    /// <summary>Measurements taken at <see cref="GreedyInterval"/> before settling. The first
    /// samples are the ones a join is about to be judged against, and a single one is as likely to
    /// be the congested one as not.</summary>
    private const int GreedyMeasurements = 3;

    /// <summary>The server clamps the ping it is told into this range, so a client that reported
    /// outside it would be sizing the group's pre-roll off a number the server discarded.</summary>
    private const long MaxPingMs = 10_000;

    private static readonly TimeSpan GreedyInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettledInterval = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling on the interval while nothing can be measured. The poll interval is chosen
    /// off the number of measurements TAKEN, which a failing server never advances, so without a
    /// ladder a server that is away — or a reverse proxy that does not route <c>/GetUtcTime</c> —
    /// is asked once a second for as long as the session lasts. The ladder doubles from
    /// <see cref="GreedyInterval"/> so a single blip still costs a second, and the same shape as
    /// <c>SessionSocket</c>'s reconnect backoff.</summary>
    private static readonly TimeSpan MaxFaultInterval = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<ServerTimeSample?>> _fetchServerTime;
    private readonly Func<long, CancellationToken, Task> _reportPing;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>One measurement at a time. <see cref="ForceUpdateAsync"/> is called from the socket
    /// and group paths while the timer is running its own, and two in flight would interleave their
    /// four timestamps into two meaningless ones.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A <see cref="System.Threading.Timer"/> rather than a <c>DispatcherTimer</c>, the
    /// same choice <c>SessionSocket._keepAlive</c> makes and for the same reason: this has to keep
    /// ticking while the UI thread is busy. A dispatcher tick is starved by exactly the work that
    /// makes playback drift, so the offset would go stale at the moment it is most needed.</summary>
    private readonly System.Threading.Timer _timer;

    private readonly ClockMeasurement[] _window = new ClockMeasurement[WindowSize];
    private int _written;
    private ClockMeasurement _best;
    private volatile bool _isReady;
    private bool _disposed;

    /// <summary>Consecutive attempts that produced no usable measurement, which is what the poll
    /// interval backs off on. Written only under the measurement gate and read in
    /// <see cref="Rearm"/> immediately after it, so the worst a stale read can cost is one poll at
    /// the wrong interval.</summary>
    private int _faults;

    /// <summary>Whether the current run of faults has already been logged at <c>Info</c>. Reset on
    /// a measurement that lands, so each outage leaves one breadcrumb rather than one per tick.</summary>
    private bool _faultLogged;

    /// <summary>Both calls are injected so the arithmetic can be driven from hand-built timestamps
    /// with no server at all; <paramref name="utcNow"/> is injected for the same reason, since
    /// <c>t0</c> and <c>t3</c> are read from it. <see cref="ForSession"/> is the production
    /// wiring.</summary>
    public ServerClock(Func<CancellationToken, Task<ServerTimeSample?>> fetchServerTime,
        Func<long, CancellationToken, Task> reportPing, Func<DateTime>? utcNow = null)
    {
        _fetchServerTime = fetchServerTime;
        _reportPing = reportPing;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timer = new System.Threading.Timer(_ => _ = MeasureAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The production wiring: the SDK's <c>GET /GetUtcTime</c> and
    /// <c>POST /SyncPlay/Ping</c>, both against the session that owns the group.</summary>
    public static ServerClock ForSession(JellyfinService service) => new(
        async cancellation =>
        {
            var utc = await service.Client.GetUtcTime.GetAsync(cancellationToken: cancellation)
                .ConfigureAwait(false);
            return utc is { RequestReceptionTime: { } received, ResponseTransmissionTime: { } sent }
                ? new ServerTimeSample(received, sent)
                : null;
        },
        (ping, cancellation) => service.Client.SyncPlay.Ping.PostAsync(new PingRequestDto { Ping = ping },
            cancellationToken: cancellation));

    /// <summary>Server clock minus local clock, from the measurement with the smallest delay.
    ///
    /// <para>Read with NO barrier, and deliberately so: measurements land on a thread-pool thread
    /// and this is read on the UI thread, so what a caller gets is the champion as of some recent
    /// moment, not as of now — and <see cref="Offset"/> and <see cref="PingMs"/> are two reads of a
    /// 16-byte struct that is not written atomically, so they can come from different measurements.
    /// Neither costs anything real here: a measurement replaces the offset by at most the
    /// difference between two round trips, the protocol's own tolerance is 500 ms, and the server
    /// discards a client's timing outright past 2000 ms. A caller that needs the two to agree, or
    /// needs to see a <see cref="Resync"/> it just asked for, must await
    /// <see cref="ForceUpdateAsync"/> — there is no happens-before to lean on otherwise.</para></summary>
    public TimeSpan Offset => _best.Offset;

    /// <summary>Half the best round trip, in milliseconds, clamped the way the server clamps it.
    /// Unbarriered; see <see cref="Offset"/>.</summary>
    public long PingMs => Clamp(_best.Ping);

    /// <summary>Whether anything has been measured yet. Before this, <see cref="Offset"/> is zero —
    /// which is the right guess and still only a guess.</summary>
    public bool IsReady => _isReady;

    /// <summary>A server instant as a local one.</summary>
    public DateTime ToLocal(DateTime serverUtc) => serverUtc - Offset;

    /// <summary>A local instant as a server one.</summary>
    public DateTime ToServer(DateTime localUtc) => localUtc + Offset;

    /// <summary>Starts the polling loop with a measurement now.</summary>
    public void Start() => Resync();

    /// <summary>Back into greedy polling, with a measurement now. Called when the socket reconnects
    /// and when a group is joined: both are moments where the offset is about to be acted on and
    /// may have been sitting unmeasured for a minute, and a machine that has been asleep can come
    /// back with a clock that moved.</summary>
    public void Resync()
    {
        if (_disposed)
            return;
        // Deliberately not taken under the measurement gate: the count only decides how eagerly to
        // poll and which ring slot is written next, so the worst a measurement in flight can do is
        // leave the window one sample shorter than it looks. The offset stays whatever was last
        // measured until a new one lands, which is the right answer while the new one is in flight.
        Interlocked.Exchange(ref _written, 0);
        _ = MeasureAsync();
    }

    /// <summary>One measurement, awaited. Also re-arms the timer, so the caller does not end up
    /// racing the loop it just ran a leg of.</summary>
    public Task ForceUpdateAsync() => MeasureAsync();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        _timer.Dispose();
        // The token source and the gate are left undisposed on purpose: a measurement may still be
        // unwinding through both, and disposing them under it turns a clean shutdown into an
        // ObjectDisposedException on a thread nobody is watching. Same rule as SessionSocket.
    }

    /// <summary>The arithmetic, with no clock and no network in it — every timestamp is an
    /// argument, so the one thing that decides whether two clients agree can be asserted
    /// directly.</summary>
    /// <param name="t0">Local, before the request went out.</param>
    /// <param name="t1">Server, when it received the request.</param>
    /// <param name="t2">Server, when it sent the answer.</param>
    /// <param name="t3">Local, when the answer arrived.</param>
    public static ClockMeasurement Measure(DateTime t0, DateTime t1, DateTime t2, DateTime t3)
        => new(((t1 - t0) + (t2 - t3)) / 2, (t3 - t0) - (t2 - t1));

    private async Task MeasureAsync()
    {
        if (_disposed)
            return;
        try
        {
            await _gate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            var t0 = _utcNow();
            ServerTimeSample? sample = null;
            string? fault = null;
            try
            {
                sample = await _fetchServerTime(_cancellation.Token).ConfigureAwait(false);
                if (sample is null)
                    // Answered, but with no instants in it. That is what a 200 carrying something
                    // other than a UtcTimeResponse looks like from here — a reverse proxy that
                    // serves the web client for an unrouted path, above all.
                    fault = "event=clock outcome=failure error=empty_response";
            }
            catch (Exception ex)
            {
                // By type, never by message: an SDK failure carries the request URL.
                fault = $"event=clock outcome=failure error={ex.GetType().Name}";
            }
            var t3 = _utcNow();
            if (sample is not null && !Record(Measure(t0, sample.RequestReceptionTime.UtcDateTime,
                    sample.ResponseTransmissionTime.UtcDateTime, t3)))
                fault = "event=clock outcome=rejected reason=negative_delay";
            if (fault is null)
            {
                _faults = 0;
                _faultLogged = false;
            }
            else
            {
                _faults++;
                LogFault(fault);
            }
            if (_isReady)
                await ReportPingAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Rearm();
        }
    }

    /// <summary>Files a measurement and re-picks the champion, or refuses it. The window is a ring,
    /// so the ninth measurement displaces the first — a delay that was the smallest an hour ago is
    /// not evidence about the network now.
    ///
    /// <para>A NEGATIVE delay is refused outright. <c>(t3-t0) - (t2-t1)</c> can only go below zero
    /// when one of the two clocks moved during the exchange, which is the local clock stepping
    /// backwards — and the selection rule makes that the worst possible thing to file: smallest
    /// delay wins, so a stepped sample necessarily becomes the champion, its fabricated offset is
    /// what every command is then converted with, its ping reports as zero to a server that sizes
    /// the whole group's pre-roll off the highest one, and none of it leaves until the ring evicts
    /// it eight measurements — half an hour of settled polling — later. The common form of this
    /// self-heals because a machine that wakes up also reconnects, and <see cref="Resync"/> empties
    /// the window; a step without a reconnect has nothing to heal it.</para></summary>
    /// <returns>Whether the measurement was filed.</returns>
    private bool Record(ClockMeasurement measurement)
    {
        if (measurement.Delay < TimeSpan.Zero)
            return false;
        var written = Interlocked.Increment(ref _written);
        _window[(written - 1) % WindowSize] = measurement;
        var best = _window[0];
        var count = Math.Min(written, WindowSize);
        for (var i = 1; i < count; i++)
            if (_window[i].Delay < best.Delay)
                best = _window[i];
        _best = best;
        _isReady = true;
        AppLog.Detail("syncplay", FormattableString.Invariant(
            $"event=clock outcome=measured offset_ms={(long)measurement.Offset.TotalMilliseconds} delay_ms={(long)measurement.Delay.TotalMilliseconds} best_ping_ms={PingMs}"));
        return true;
    }

    /// <summary>First fault of a run to the breadcrumb ring, the rest verbose-only — the same shape
    /// as <c>SessionSocket.LogHandlerFault</c>, and for the same reason: the ring holds 500 entries
    /// in total, so a failure that repeats on a timer must not be what evicts the events an incident
    /// is read for. It cannot be Detail throughout, which is what it was: a wrong or missing offset
    /// is already a silent failure end to end — the server treats a timing discrepancy over 2000 ms
    /// as zero and says so only in its own log — so a client whose clock never measured has to leave
    /// something behind with verbose off.</summary>
    private void LogFault(string message)
    {
        // Nothing at Info once shutdown has started: the cancellation that ends an in-flight
        // measurement is not an incident.
        if (_faultLogged || _disposed)
            AppLog.Detail("syncplay", message);
        else
            AppLog.Info("syncplay", message);
        _faultLogged = true;
    }

    /// <summary>Tells the server the best ping, not the newest one: the server sizes the group's
    /// pre-roll off the highest ping among its members, so a client that reported one congested
    /// sample would slow every other member down until its next measurement.</summary>
    private async Task ReportPingAsync()
    {
        try
        {
            await _reportPing(PingMs, _cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best effort by contract. The server keeps the last ping it was told and falls back to
            // its own 500 ms default, so a failed report degrades the pre-roll rather than the
            // session, and it must not surface on a timer thread.
            AppLog.Detail("syncplay", $"event=ping outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>One-shot rather than periodic, re-armed after each measurement: a periodic timer
    /// whose tick outlives its interval queues the next one on top of it, and the gate above would
    /// then hold a thread-pool thread per tick.
    ///
    /// <para>A run of faults backs off and takes precedence over the greedy/settled choice, which is
    /// made on the number of measurements TAKEN and so never advances while they are all
    /// failing.</para></summary>
    private void Rearm()
    {
        if (_disposed)
            return;
        var interval = _faults > 0 ? FaultInterval(_faults)
            : Volatile.Read(ref _written) < GreedyMeasurements ? GreedyInterval : SettledInterval;
        try
        {
            _timer.Change(interval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced Dispose. Nothing to re-arm.
        }
    }

    /// <summary>The backoff ladder: one second doubled per consecutive fault, up to
    /// <see cref="MaxFaultInterval"/>. Ten faults is about eight minutes, after which a server that
    /// stays away costs one request every five.</summary>
    private static TimeSpan FaultInterval(int faults)
        => TimeSpan.FromMilliseconds(Math.Min(
            GreedyInterval.TotalMilliseconds * Math.Pow(2, Math.Min(faults, 20) - 1),
            MaxFaultInterval.TotalMilliseconds));

    private static long Clamp(TimeSpan ping)
        => Math.Clamp((long)ping.TotalMilliseconds, 0, MaxPingMs);
}
