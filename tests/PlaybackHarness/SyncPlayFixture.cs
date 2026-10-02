using System.Text.Json;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

// The pure half of SyncPlay: the clock offset the whole protocol is expressed in, and the mapping
// from a group command to what the local player does about it. Neither needs mpv, a window, a
// socket or the second client that would otherwise be the only way to see any of this work — which
// is the entire reason ServerClock's two HTTP calls are injected and SyncPlayScheduler is static.
//
// The one leg that does want a server uses the same hand-rolled TcpListener the live-session
// fixture drives, so the production ServerClock issues its real GET /GetUtcTime and
// POST /SyncPlay/Ping through the real SDK.
internal static class SyncPlayFixture
{
    private static async Task CheckReadyLifetimeAsync(Action<bool, string> check)
    {
        const string key = "sync-ready-lifetime";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST",
            "synthetic-fixture-token", server.UserId)).ConfigureAwait(false), "Ready lifetime fixture connects");
        using var live = new LiveSessionService(k => k == key ? service : null);
        using var client = new SyncPlayClient(live, k => k == key ? service : null);
        live.StartProfile(key);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var group = Guid.NewGuid();
        var item = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow;
        await client.JoinAsync(key, group).ConfigureAwait(false);
        await connection.SendAsync(GroupJoinedFrame(group, "Readiness", "Paused", "test")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(5)).ConfigureAwait(false), "Ready lifetime group joins");
        await connection.SendAsync(QueueFrame(group, stamp, item)).ConfigureAwait(false);
        check(await SettlesAsync(() => client.PlaylistItemId == item, TimeSpan.FromSeconds(5)).ConfigureAwait(false), "Ready lifetime queue arrives");

        async Task<TaskCompletionSource> HoldBuffer(TaskCompletionSource? previous = null)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BufferingResponseGate = gate.Task;
            var count = server.CountOf("/SyncPlay/Buffering");
            client.ReportBuffering(true, TimeSpan.TicksPerSecond, false);
            if (previous is not null)
            {
                await Task.Delay(400).ConfigureAwait(false);
                check(server.CountOf("/SyncPlay/Buffering") == count,
                    "a new buffering cycle waits for the preceding Buffering response");
                previous.SetResult();
            }
            check(await SettlesAsync(() => server.CountOf("/SyncPlay/Buffering") > count,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false), "Buffering request is held in flight");
            return gate;
        }

        var held = await HoldBuffer().ConfigureAwait(false);
        var observedAt = DateTime.UtcNow + (client.Clock?.Offset ?? TimeSpan.Zero);
        var ready = client.ReportReadyAsync(false, 2 * TimeSpan.TicksPerSecond);
        var observedBy = DateTime.UtcNow + (client.Clock?.Offset ?? TimeSpan.Zero);
        check(!ready.IsCompleted && server.CountOf("/SyncPlay/Ready") == 0,
            "Ready waits for in-flight Buffering response");
        client.ReportBuffering(false, 2 * TimeSpan.TicksPerSecond, false, ready: false);
        await Task.Delay(400).ConfigureAwait(false);
        held.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1, "settled seek releases Buffering exactly once");
        var when = When(LastRequest(server, "/SyncPlay/Ready").Body);
        check(when >= observedAt.AddMilliseconds(-2) && when <= observedBy.AddMilliseconds(2),
            "held Ready retains the timestamp of its position observation");

        held = await HoldBuffer().ConfigureAwait(false);
        var outgoingContext = client.ReportContext;
        ready = client.ReportReadyAsync(false, 3 * TimeSpan.TicksPerSecond);
        client.ReportBuffering(false, 3 * TimeSpan.TicksPerSecond, false, ready: false);
        item = Guid.NewGuid();
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(1), item)).ConfigureAwait(false);
        check(await SettlesAsync(() => client.PlaylistItemId == item, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "playlist changes during held Buffering");
        held.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1, "old entry Ready is suppressed after playlist changes");
        var bufferCount = server.CountOf("/SyncPlay/Buffering");
        await client.ReportReadyAsync(false, 30 * TimeSpan.TicksPerSecond, outgoingContext).ConfigureAwait(false);
        client.ReportBuffering(true, 30 * TimeSpan.TicksPerSecond, false, expectedContext: outgoingContext);
        await Task.Delay(400).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1 && server.CountOf("/SyncPlay/Buffering") == bufferCount,
            "stale outgoing entry context cannot start Ready or Buffering for the new entry");

        held = await HoldBuffer().ConfigureAwait(false);
        outgoingContext = client.ReportContext;
        ready = client.ReportReadyAsync(false, 4 * TimeSpan.TicksPerSecond);
        client.ReportBuffering(false, 4 * TimeSpan.TicksPerSecond, false, ready: false);
        await client.LeaveAsync().ConfigureAwait(false);
        await client.JoinAsync(key, group).ConfigureAwait(false);
        await connection.SendAsync(GroupJoinedFrame(group, "Readiness", "Paused", "test")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "same group rejoins while Buffering response is held");
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(2), item)).ConfigureAwait(false);
        check(await SettlesAsync(() => client.PlaylistItemId == item, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "same item is restored in the new membership");
        held.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1, "old membership Ready cannot release the rejoined group");
        bufferCount = server.CountOf("/SyncPlay/Buffering");
        await client.ReportReadyAsync(false, 40 * TimeSpan.TicksPerSecond, outgoingContext).ConfigureAwait(false);
        client.ReportBuffering(true, 40 * TimeSpan.TicksPerSecond, false, expectedContext: outgoingContext);
        await Task.Delay(400).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1 && server.CountOf("/SyncPlay/Buffering") == bufferCount,
            "stale membership context cannot report against the same rejoined group and item");

        held = await HoldBuffer().ConfigureAwait(false);
        ready = client.ReportReadyAsync(false, 5 * TimeSpan.TicksPerSecond);
        client.ReportBuffering(false, 5 * TimeSpan.TicksPerSecond, false, ready: false);
        var nextBuffer = await HoldBuffer(held).ConfigureAwait(false);
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 1,
            "an earlier buffer's delayed Ready cannot release a new buffering cycle");
        ready = client.ReportReadyAsync(false, 6 * TimeSpan.TicksPerSecond);
        client.ReportBuffering(false, 6 * TimeSpan.TicksPerSecond, false, ready: false);
        nextBuffer.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 2,
            "the current buffering cycle releases once its own response completes");

        held = await HoldBuffer().ConfigureAwait(false);
        check(client.ReportBuffering(false, 7 * TimeSpan.TicksPerSecond, false),
            "a buffer clear schedules its releasing Ready");
        var buffers = server.CountOf("/SyncPlay/Buffering");
        client.ReportBuffering(true, 8 * TimeSpan.TicksPerSecond, false);
        check(client.ReportBuffering(false, 9 * TimeSpan.TicksPerSecond, false),
            "a superseding brief buffer retains the outstanding Ready obligation");
        // Repeated clear notifications must not queue duplicate releases while HTTP is held.
        client.ReportBuffering(false, 10 * TimeSpan.TicksPerSecond, false);
        held.SetResult();
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Ready") == 3,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "a brief superseding buffer still releases the group after the held response");
        await Task.Delay(400).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Buffering") == buffers && server.CountOf("/SyncPlay/Ready") == 3,
            "a brief superseding buffer sends no new Buffering and exactly one current Ready");
        check(Field(LastRequest(server, "/SyncPlay/Ready").Body, "PositionTicks").GetInt64()
            == 10 * TimeSpan.TicksPerSecond,
            "the replacement Ready carries the latest clear's position");
        await client.ReportReadyAsync(false, 11 * TimeSpan.TicksPerSecond, client.ReportContext).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 4
            && Field(LastRequest(server, "/SyncPlay/Ready").Body, "PlaylistItemId").GetGuid() == item,
            "current report context remains accepted after the stale reports are rejected");

        held = await HoldBuffer().ConfigureAwait(false);
        await client.LeaveAsync().ConfigureAwait(false);
        await client.JoinAsync(key, group).ConfigureAwait(false);
        await connection.SendAsync(GroupJoinedFrame(group, "Readiness", "Paused", "test")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "membership restores before an old Buffering response returns");
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(3), item)).ConfigureAwait(false);
        check(await SettlesAsync(() => client.PlaylistItemId == item, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "same playing item restores before the old response returns");
        ready = client.ReportReadyAsync(false, 12 * TimeSpan.TicksPerSecond, client.ReportContext);
        check(!ready.IsCompleted && server.CountOf("/SyncPlay/Ready") == 4,
            "restored membership Ready cannot overtake an old Buffering response");
        held.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Ready") == 5
            && Field(LastRequest(server, "/SyncPlay/Ready").Body, "PositionTicks").GetInt64() == 12 * TimeSpan.TicksPerSecond,
            "restored membership reports its own position after old Buffering completes");
    }

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            count++;
            Console.WriteLine("PASS: " + name);
        }

        try
        {
            CheckClockArithmetic(Check);
            await CheckMeasurementSelectionAsync(Check).ConfigureAwait(false);
            await CheckPingClampAsync(Check).ConfigureAwait(false);
            CheckCommandResolution(Check);
            CheckStaleAndDuplicate(Check);
            CheckDrift(Check);
            CheckTransportGate(Check);
            await CheckReadyLifetimeAsync(Check).ConfigureAwait(false);
            await CheckServerClockTransportAsync(Check).ConfigureAwait(false);
            await CheckGroupLifecycleAsync(Check).ConfigureAwait(false);
            await CheckLocalTransportAsync(Check).ConfigureAwait(false);
            await CheckQueueContractsAsync(Check).ConfigureAwait(false);
            await CheckRestoreRaceAsync(Check).ConfigureAwait(false);
            await CheckJoinRefusalAsync(Check).ConfigureAwait(false);
            await CheckSessionLossAsync(Check).ConfigureAwait(false);
            Console.WriteLine($"RESULT: PASS ({count} sync play assertions)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: sync play: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static readonly DateTime Base = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static void CheckTransportGate(Action<bool, string> check)
    {
        foreach (var kind in Enum.GetValues<TransportKind>())
            check(SyncPlayGate.Resolve(new(kind), false, false, 20, 100).Outcome == GateOutcome.Apply,
                "ungrouped " + kind + " applies locally");
        GateDecision Resolve(TransportKind kind, double seconds = 0, bool paused = false,
            double position = 20, double duration = 100, Guid? id = null)
            => SyncPlayGate.Resolve(new(kind, seconds, id), true, paused, position, duration);
        foreach (var (kind, verb) in new[]
        {
            (TransportKind.Pause, "pause"), (TransportKind.Unpause, "unpause"),
            (TransportKind.Stop, "stop"), (TransportKind.QueueNext, "next"),
            (TransportKind.QueuePrevious, "previous"),
        })
            check(Resolve(kind) == new GateDecision(GateOutcome.Post, verb),
                "grouped " + kind + " posts " + verb);
        check(Resolve(TransportKind.TogglePause, paused: true).Verb == "unpause"
            && Resolve(TransportKind.TogglePause).Verb == "pause",
            "group pause toggle requests the opposite of the current pause state");
        check(Resolve(TransportKind.SeekAbsolute, 30) == new GateDecision(GateOutcome.Post, "seek", 30 * TimeSpan.TicksPerSecond)
            && Resolve(TransportKind.SeekRelative, 15) == new GateDecision(GateOutcome.Post, "seek", 35 * TimeSpan.TicksPerSecond),
            "group seeks carry absolute ticks including relative position conversion");
        foreach (var kind in new[] { TransportKind.SeekAbsolute, TransportKind.SeekRelative })
        {
            check(Resolve(kind, -200).PositionTicks == 0
                && Resolve(kind, 200).PositionTicks == 100 * TimeSpan.TicksPerSecond,
                kind + " clamps group position at both ends");
            foreach (var duration in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
                check(Resolve(kind, 1, duration: duration) == new GateDecision(GateOutcome.Drop, Reason: "no_duration"),
                    kind + " drops missing or invalid duration " + duration);
            foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                check(Resolve(kind, seconds) == new GateDecision(GateOutcome.Drop, Reason: "invalid_position"),
                    kind + " drops invalid requested position " + seconds);
        }
        check(Resolve(TransportKind.SeekRelative, 1, position: double.NaN).Reason == "invalid_position",
            "relative seek drops an invalid current position");
        check(Resolve(TransportKind.FrameStep) == new GateDecision(GateOutcome.Drop, Reason: "frame_step")
            && Resolve(TransportKind.Speed) == new GateDecision(GateOutcome.Drop, Reason: "speed"),
            "group frame stepping and speed requests are dropped");
        var entry = Guid.NewGuid();
        foreach (var (kind, verb) in new[] { (TransportKind.QueueJump, "select"), (TransportKind.QueueRemove, "remove") })
        {
            check(Resolve(kind, id: entry) == new GateDecision(GateOutcome.Post, verb, PlaylistItemId: entry),
                kind + " preserves its server playlist entry id");
            check(Resolve(kind) == new GateDecision(GateOutcome.Drop, Reason: "queue")
                && Resolve(kind, id: Guid.Empty) == new GateDecision(GateOutcome.Drop, Reason: "queue"),
                kind + " drops missing or empty playlist entry ids");
        }
    }

    private static async Task CheckQueueContractsAsync(Action<bool, string> check)
    {
        const string key = "sync-queue-contracts";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST",
            "synthetic-fixture-token", server.UserId)).ConfigureAwait(false), "queue contract fixture connects");
        using var live = new LiveSessionService(k => k == key ? service : null);
        using var client = new SyncPlayClient(live, k => k == key ? service : null);
        live.StartProfile(key);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entry = Guid.NewGuid();
        check(!await client.SetNewQueueAsync([first]).ConfigureAwait(false)
            && !await client.NextItemAsync().ConfigureAwait(false)
            && !await client.PreviousItemAsync().ConfigureAwait(false)
            && !await client.SetPlaylistItemAsync(entry).ConfigureAwait(false)
            && !await client.RemovePlaylistItemAsync(entry).ConfigureAwait(false),
            "queue mutations require confirmed membership");
        check(!await client.CreateAsync(key, "  ").ConfigureAwait(false)
            && server.CountOf("/SyncPlay/New") == 0, "blank group name sends no create request");
        check(await client.CreateAsync(key, "  Contract night  ").ConfigureAwait(false), "create request is accepted");
        var create = LastRequest(server, "/SyncPlay/New");
        check(create.Method == "POST" && Field(create.Body, "GroupName").GetString() == "Contract night",
            "New endpoint carries the trimmed group name");
        check(!client.IsInGroup, "create HTTP success still waits for GroupJoined");
        var group = Guid.NewGuid();
        await connection.SendAsync(GroupJoinedFrame(group, "Contract night", "Paused", "test")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "created group confirms through the socket");
        await connection.SendAsync(QueueFrame(group, DateTimeOffset.UtcNow, entry)).ConfigureAwait(false);
        check(await SettlesAsync(() => client.PlaylistItemId == entry, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "queue contract receives a server playlist entry");

        check(!await client.SetNewQueueAsync([]).ConfigureAwait(false)
            && !await client.SetNewQueueAsync([Guid.Empty]).ConfigureAwait(false)
            && !await client.SetNewQueueAsync([first], -1).ConfigureAwait(false)
            && !await client.SetNewQueueAsync([first], 1).ConfigureAwait(false)
            && server.CountOf("/SyncPlay/SetNewQueue") == 0,
            "invalid replacement queues send no request");
        check(await client.SetNewQueueAsync([first, second], 1, 123456789).ConfigureAwait(false),
            "replacement queue request is accepted");
        var queue = LastRequest(server, "/SyncPlay/SetNewQueue");
        check(queue.Method == "POST"
            && Field(queue.Body, "PlayingQueue").EnumerateArray().Select(i => i.GetGuid()).SequenceEqual([first, second])
            && Field(queue.Body, "PlayingItemPosition").GetInt32() == 1
            && Field(queue.Body, "StartPositionTicks").GetInt64() == 123456789,
            "SetNewQueue carries ordered library ids, playing index and start ticks");
        await client.SetNewQueueAsync([second], 0, -5).ConfigureAwait(false);
        check(Field(LastRequest(server, "/SyncPlay/SetNewQueue").Body, "StartPositionTicks").GetInt64() == 0,
            "replacement queue clamps negative start ticks");

        check(!await client.NextItemAsync(Guid.NewGuid()).ConfigureAwait(false)
            && server.CountOf("/SyncPlay/NextItem") == 0, "next rejects an outgoing playlist entry");
        check(await client.NextItemAsync(entry).ConfigureAwait(false), "next queue request is accepted");
        check(await client.PreviousItemAsync().ConfigureAwait(false), "previous queue request is accepted");
        var selected = Guid.NewGuid();
        check(await client.SetPlaylistItemAsync(selected).ConfigureAwait(false), "select queue request is accepted");
        check(await client.RemovePlaylistItemAsync(selected).ConfigureAwait(false), "remove queue request is accepted");
        foreach (var (route, id) in new[] { ("NextItem", entry), ("PreviousItem", entry), ("SetPlaylistItem", selected) })
        {
            var request = LastRequest(server, "/SyncPlay/" + route);
            check(request.Method == "POST" && Field(request.Body, "PlaylistItemId").GetGuid() == id,
                route + " endpoint carries the server playlist entry id");
        }
        var remove = LastRequest(server, "/SyncPlay/RemoveFromPlaylist");
        check(remove.Method == "POST"
            && Field(remove.Body, "PlaylistItemIds").EnumerateArray().Select(i => i.GetGuid()).SequenceEqual([selected])
            && Field(remove.Body, "ClearPlayingItem").GetBoolean()
            && !Field(remove.Body, "ClearPlaylist").GetBoolean(),
            "RemoveFromPlaylist removes the selected entry with clear-playing and preserves the rest");
        check(client.PlaylistItemId == entry, "queue POST success does not optimistically change the playing entry");
        check(!await client.SetPlaylistItemAsync(Guid.Empty).ConfigureAwait(false)
            && !await client.RemovePlaylistItemAsync(Guid.Empty).ConfigureAwait(false)
            && server.CountOf("/SyncPlay/SetPlaylistItem") == 1
            && server.CountOf("/SyncPlay/RemoveFromPlaylist") == 1,
            "empty playlist entry sends no select or remove request");
    }

    // The four-timestamp exchange, hand-built. Every number below is chosen so the answer can be
    // read off the scenario rather than off the formula.
    private static void CheckClockArithmetic(Action<bool, string> check)
    {
        // Server 30 s ahead, 100 ms each way, 50 ms of server-side processing in between.
        var t0 = Base;
        var t1 = t0 + TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(100);
        var t2 = t1 + TimeSpan.FromMilliseconds(50);
        var t3 = t0 + TimeSpan.FromMilliseconds(250);
        var symmetric = ServerClock.Measure(t0, t1, t2, t3);
        check(symmetric.Offset == TimeSpan.FromSeconds(30),
            "a symmetric round trip recovers the clock offset exactly");
        check(symmetric.Delay == TimeSpan.FromMilliseconds(200),
            "the delay takes the server's own processing time out of the round trip");
        check(symmetric.Ping == TimeSpan.FromMilliseconds(100), "the ping is half the delay");

        // The same exchange with the clocks IDENTICAL but the paths lopsided: 300 ms out, 100 ms
        // back. The offset is only exactly right when the two legs cost the same, so this one
        // reports 100 ms of skew that does not exist — and its delay is four times the symmetric
        // case's, which is how the window below knows not to believe it.
        var asymmetric = ServerClock.Measure(t0, t0 + TimeSpan.FromMilliseconds(300),
            t0 + TimeSpan.FromMilliseconds(300), t0 + TimeSpan.FromMilliseconds(400));
        check(asymmetric.Offset == TimeSpan.FromMilliseconds(100),
            "an asymmetric path reports half the difference between its legs as offset");
        check(asymmetric.Delay == TimeSpan.FromMilliseconds(400)
            && asymmetric.Delay > symmetric.Delay && asymmetric.Offset != symmetric.Offset,
            "the asymmetric measurement disagrees about the offset and says so in its delay");
    }

    // Nine measurements through the real ServerClock, so the ring and the selection rule are both
    // driven rather than reasoned about. The first is the best measurement anyone ever takes, the
    // last is middling, and the window only holds eight.
    private static async Task CheckMeasurementSelectionAsync(Action<bool, string> check)
    {
        (long DelayMs, int SkewSeconds)[] script =
        [
            (100, 11), (200, 22), (500, 33), (600, 44),
            (700, 55), (800, 66), (900, 77), (1100, 88), (950, 99),
        ];
        using var driver = new ScriptedClock(script);
        check(!driver.Clock.IsReady && driver.Clock.Offset == TimeSpan.Zero,
            "an unmeasured clock reports itself unready with no offset");

        for (var i = 0; i < 8; i++)
            await driver.Clock.ForceUpdateAsync().ConfigureAwait(false);
        check(driver.Clock.IsReady, "the clock is ready once it has measured");
        check(driver.Clock.Offset == TimeSpan.FromSeconds(11),
            "the smallest delay wins the window, not the most recent measurement");
        check(driver.Clock.PingMs == 50, "the reported ping is half the winning delay");

        // The ninth displaces the first. If the ring kept it, the offset would not move; if it
        // picked by recency, the offset would be the ninth's.
        await driver.Clock.ForceUpdateAsync().ConfigureAwait(false);
        check(driver.Clock.Offset == TimeSpan.FromSeconds(22),
            "the ninth measurement evicts the first and the second-smallest delay takes over");
        check(driver.Clock.PingMs == 100 && driver.Pings[^1] == 100,
            "the server is told the winning ping, not the newest one");
        // ServerClock re-arms its own one-shot timer after every measurement, so a stall longer
        // than the greedy interval anywhere above lets a real tick land between two scripted legs.
        // Next() cannot be corrupted by one any more — it hands out nothing once the script is
        // spent — but a tick that consumed a leg the driver wanted would make the assertions above
        // fail for a reason that has nothing to do with the selection rule, so the count is
        // asserted rather than left to be inferred from a confusing failure.
        //
        // ATTEMPTS, not legs handed out. A spent script answers null without spending anything, so
        // a count of what left the queue saturates at the script's own length and would equal it
        // however many extra callers arrived — which is the one thing this leg exists to notice.
        check(driver.Attempts == script.Length,
            "the nine scripted measurements are exactly the nine that were taken");
    }

    private static async Task CheckPingClampAsync(Action<bool, string> check)
    {
        // 24 s of round trip is a twelve-second ping. The server clamps what it is told to 10 s and
        // sizes the group's pre-roll off the highest ping in the group, so reporting the raw number
        // would only be reporting something the server threw away.
        using (var slow = new ScriptedClock([(24_000, 0)]))
        {
            await slow.Clock.ForceUpdateAsync().ConfigureAwait(false);
            check(slow.Clock.PingMs == 10_000 && slow.Pings is [10_000],
                "a twelve-second ping is reported at the server's ten-second ceiling");
        }

        // A round trip that took less than the server spent inside it — i.e. the local clock
        // stepped backwards mid-exchange. It is not a measurement, and filing it would be worse
        // than losing it: smallest delay wins, so a negative one necessarily becomes the champion.
        using (var backwards = new ScriptedClock([(-400, 0)]))
        {
            await backwards.Clock.ForceUpdateAsync().ConfigureAwait(false);
            check(!backwards.Clock.IsReady && backwards.Clock.Offset == TimeSpan.Zero
                && backwards.Pings.Count == 0,
                "a negative delay is refused rather than filed as a measurement");
        }

        // The case that does the damage: one good measurement, then a step. Unrefused, the step's
        // fabricated one-second offset would win on delay and hold the champion through every good
        // measurement after it until the ring evicted it — eight of them, half an hour at the
        // settled interval — while reporting a ping of zero to a server that sizes the group's
        // pre-roll off the highest one it is told.
        using var stepped = new ScriptedClock([(200, 7), (-1900, 1000), (400, 9)]);
        for (var i = 0; i < 3; i++)
            await stepped.Clock.ForceUpdateAsync().ConfigureAwait(false);
        check(stepped.Clock.Offset == TimeSpan.FromSeconds(7) && stepped.Clock.PingMs == 100,
            "a stepped measurement does not take the champion from the good ones around it");
    }

    private static void CheckCommandResolution(Action<bool, string> check)
    {
        var item = Guid.NewGuid();
        var offset = TimeSpan.FromSeconds(30);
        var localNow = Base.AddMinutes(10);
        var serverNow = localNow + offset;
        var state = new SyncPlayState(Base, item, IsPaused: true);
        const long tenMinutes = 600 * TimeSpan.TicksPerSecond;

        // The pre-roll case, which is the normal one: the server dates an Unpause
        // now + max(highestPing * 2, 500 ms) exactly so every member can get into position first.
        var future = Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(2), tenMinutes, item, serverNow);
        var armed = SyncPlayScheduler.Resolve(future, state, offset, localNow, 0);
        check(armed.Kind == SyncPlayActionKind.SeekAndArmUnpause
            && armed.DueLocalUtc == localNow.AddSeconds(2)
            && armed.TargetTicks == tenMinutes && armed.NeedsSeek,
            "an Unpause dated in the future seeks now and arms the unpause for its own instant");
        check(SyncPlayScheduler.Resolve(future, state, offset, localNow, 600.2).NeedsSeek == false,
            "a player already within the threshold is armed without a seek");

        // Late, by three seconds. The group did not wait, so the position to land on is where it
        // has got to, not where it started.
        var late = Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(-3), tenMinutes, item, serverNow);
        var caught = SyncPlayScheduler.Resolve(late, state, offset, localNow, 0);
        check(caught.Kind == SyncPlayActionKind.UnpauseNow
            && caught.TargetTicks == tenMinutes + 3 * TimeSpan.TicksPerSecond
            && caught.DueLocalUtc == localNow && caught.NeedsSeek,
            "an Unpause whose instant has passed targets the position the group has reached");

        var paused = SyncPlayScheduler.Resolve(
            Command(SyncPlayCommandTypes.Pause, serverNow, 900 * TimeSpan.TicksPerSecond, item, serverNow),
            state with { IsPaused = false }, offset, localNow, 600);
        // One action, not two. A Seek followed by a Pause would let mpv run past the group's stop
        // point in the gap between them, so the pause carries its own target and the caller has
        // nothing left to order wrongly.
        check(paused.Kind == SyncPlayActionKind.Pause
            && paused.TargetTicks == 900 * TimeSpan.TicksPerSecond && paused.NeedsSeek,
            "a Pause carries the group's stop point as its own seek target");
        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Pause, serverNow, 900 * TimeSpan.TicksPerSecond, item, serverNow),
                state with { IsPaused = false }, offset, localNow, 900).NeedsSeek == false,
            "a Pause at the position already reached does not seek");

        // Dated in the past like the Unpause above, and deliberately NOT extrapolated: a group
        // seeking is a group that has stopped, so the position it named is still the position.
        var seek = SyncPlayScheduler.Resolve(
            Command(SyncPlayCommandTypes.Seek, serverNow.AddSeconds(-3), 120 * TimeSpan.TicksPerSecond, item, serverNow),
            state, offset, localNow, 600);
        check(seek.Kind == SyncPlayActionKind.Seek
            && seek.TargetTicks == 120 * TimeSpan.TicksPerSecond && seek.NeedsSeek,
            "a Seek moves to exactly the position it names and stays paused");

        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Stop, serverNow, 0, item, serverNow), state, offset, localNow, 600)
            .Kind == SyncPlayActionKind.Stop, "a Stop resolves to a stop");

        check(SyncPlayScheduler.Resolve(
                Command("Rewind", serverNow, tenMinutes, item, serverNow), state, offset, localNow, 0)
            .Kind == SyncPlayActionKind.Noop, "a command this client does not know resolves to nothing");
        check(SyncPlayScheduler.Resolve(null, state, offset, localNow, 0).Kind == SyncPlayActionKind.Noop,
            "an absent command resolves to nothing");
    }

    private static void CheckStaleAndDuplicate(Action<bool, string> check)
    {
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        var offset = TimeSpan.FromSeconds(30);
        var localNow = Base.AddMinutes(10);
        var serverNow = localNow + offset;
        var joined = Base.AddMinutes(5);
        var state = new SyncPlayState(joined, item, IsPaused: true);
        const long tenMinutes = 600 * TimeSpan.TicksPerSecond;

        // The server replays a group's current command to a session that joins, so the one that
        // started the group half an hour ago arrives looking perfectly current.
        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(-2), tenMinutes, item, joined.AddSeconds(-1)),
                state, offset, localNow, 0).Kind == SyncPlayActionKind.Noop,
            "a command emitted before this client joined is dropped");
        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(-2), tenMinutes, item, joined.AddSeconds(1)),
                state, offset, localNow, 0).Kind == SyncPlayActionKind.UnpauseNow,
            "a command emitted after this client joined is acted on");

        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Unpause, serverNow, tenMinutes, other, serverNow),
                state, offset, localNow, 0).Kind == SyncPlayActionKind.Noop,
            "a command for another queue entry is dropped");
        check(SyncPlayScheduler.Resolve(
                Command(SyncPlayCommandTypes.Stop, serverNow, 0, other, serverNow),
                state, offset, localNow, 0).Kind == SyncPlayActionKind.Stop,
            "a Stop for another queue entry is still a stop");

        // The server re-sends when a client looks lost. Three seconds of extrapolation puts the
        // group at 603 s, which is exactly where this client already is.
        var repeated = Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(-3), tenMinutes, item, serverNow);
        var applied = state with { LastApplied = repeated, IsPaused = false };
        check(SyncPlayScheduler.Resolve(repeated, applied, offset, localNow, 603).Kind == SyncPlayActionKind.Noop,
            "a repeated command is dropped when the local state already agrees with it");
        check(SyncPlayScheduler.Resolve(repeated, applied with { IsPaused = true }, offset, localNow, 603)
            .Kind == SyncPlayActionKind.UnpauseNow,
            "a repeated command is re-applied to a client that is paused when the group is not");
        var divergent = SyncPlayScheduler.Resolve(repeated, applied, offset, localNow, 601);
        check(divergent.Kind == SyncPlayActionKind.UnpauseNow && divergent.NeedsSeek
            && divergent.TargetTicks == tenMinutes + 3 * TimeSpan.TicksPerSecond,
            "a repeated command is re-applied to a client that is playing from the wrong place");

        // A Stop is exempt from the duplicate rule, and this is the leg that says so: the position
        // matches and the client is paused, so every other verb would be suppressed here. The other
        // three have an observable local state to disagree with; "stopped" has none, and a stop
        // dropped as a duplicate leaves this client playing on alone — which is the one failure the
        // whole exemption exists to prevent.
        var stop = Command(SyncPlayCommandTypes.Stop, serverNow, tenMinutes, item, serverNow);
        check(SyncPlayScheduler.Resolve(stop, state with { LastApplied = stop }, offset, localNow, 600)
            .Kind == SyncPlayActionKind.Stop,
            "a repeated Stop is acted on again rather than dropped as a duplicate");
    }

    private static void CheckDrift(Action<bool, string> check)
    {
        var item = Guid.NewGuid();
        var offset = TimeSpan.FromSeconds(30);
        var localNow = Base.AddMinutes(10);
        var serverNow = localNow + offset;
        // Unpaused ten minutes in, ten seconds ago: the group is at 610 s.
        var unpause = Command(SyncPlayCommandTypes.Unpause, serverNow.AddSeconds(-10),
            600 * TimeSpan.TicksPerSecond, item, serverNow.AddSeconds(-10));
        var playing = new SyncPlayState(Base, item, IsPaused: false, LastApplied: unpause);

        check(SyncPlayScheduler.ResolveDrift(playing, offset, localNow, 610.2).Kind == SyncPlayActionKind.Noop,
            "drift under the threshold is left alone");
        var corrected = SyncPlayScheduler.ResolveDrift(playing, offset, localNow, 612);
        // DriftSeek, not Seek: a drift correction happens while the group is PLAYING and has to
        // leave it playing, whereas Seek's contract is "seek and stay paused" — the group goes to
        // Waiting on a real seek and the server sends its own Unpause once every member is ready.
        // One kind for both would pause this client on its first correction and, since the drift
        // rule does not look at the transport state, keep correcting it forever afterwards.
        check(corrected.Kind == SyncPlayActionKind.DriftSeek && corrected.NeedsSeek
            && corrected.TargetTicks == 610 * TimeSpan.TicksPerSecond,
            "drift over the threshold seeks to where the group has got to, without touching pause");
        check(SyncPlayScheduler.ResolveDrift(playing, offset, localNow, 608).Kind == SyncPlayActionKind.DriftSeek,
            "drift is corrected in both directions");

        check(SyncPlayScheduler.ResolveDrift(
                playing with { LastDriftSeekUtc = localNow.AddMilliseconds(-1400) }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.Noop, "a second correction inside 1500 ms is suppressed");
        check(SyncPlayScheduler.ResolveDrift(
                playing with { LastDriftSeekUtc = localNow.AddMilliseconds(-1600) }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.DriftSeek, "a correction is allowed again once 1500 ms have passed");
        check(SyncPlayScheduler.ResolveDrift(playing with { IsBuffering = true }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.Noop, "a buffering player is not corrected");
        check(SyncPlayScheduler.ResolveDrift(playing with { PlaylistItemId = Guid.NewGuid() }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.Noop, "drift against another queue entry is not corrected");
        check(SyncPlayScheduler.ResolveDrift(
                playing with { LastApplied = unpause with { Command = SyncPlayCommandTypes.Pause } },
                offset, localNow, 612).Kind == SyncPlayActionKind.Noop,
            "a group that is not playing is not corrected");
        check(SyncPlayScheduler.ResolveDrift(playing with { LastApplied = null }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.Noop, "a group with no command applied yet is not corrected");
        check(SyncPlayScheduler.ResolveDrift(playing with { IsPaused = true }, offset, localNow, 612)
            .Kind == SyncPlayActionKind.Noop, "a paused player is not corrected");

        // THE PRE-ROLL. Every leg above dates its Unpause in the past, which is why none of them
        // could see this: the server dates a resume When = now + max(highestPing * 2, 500 ms) and
        // sends it immediately, so on EVERY group resume there is a window of at least half a
        // second in which the command is applied, the player is parked at exactly the group's start
        // position, and the group has not started. Every suppression above is satisfied in it — the
        // last command is an Unpause, for this entry, on a player that is playing and not
        // buffering — so without the When guard the elapsed term is negative and this reports drift
        // that does not exist, ordering a seek BACKWARDS by up to the whole pre-roll and re-arming
        // every 1500 ms. Landing it costs a demuxer flush, a new server-side session under
        // transcode, and a group that starts up to the pre-roll behind.
        var preRoll = Command(SyncPlayCommandTypes.Unpause, serverNow.AddMilliseconds(800),
            600 * TimeSpan.TicksPerSecond, item, serverNow);
        check(SyncPlayScheduler.ResolveDrift(playing with { LastApplied = preRoll }, offset, localNow, 600)
            .Kind == SyncPlayActionKind.Noop,
            "a group that has not reached its start instant has not drifted");
        // And the instant itself is not late either: at When exactly, nothing has elapsed. The
        // start position is a second AHEAD of the player here, which is what makes this leg about
        // the boundary rather than about the guard in general — against the pre-roll's own 600 s
        // the elapsed term is zero and the player is already on the target, so the answer would be
        // Noop whether the comparison were <= or <. Offset by a second, only the inclusive
        // comparison suppresses: an exclusive one would call this a second of drift and seek.
        var boundary = preRoll with { PositionTicks = 601 * TimeSpan.TicksPerSecond };
        check(SyncPlayScheduler.ResolveDrift(playing with { LastApplied = boundary }, offset,
                localNow.AddMilliseconds(800), 600).Kind == SyncPlayActionKind.Noop,
            "a group exactly at its start instant has not drifted");
        // The guard releases the moment the group is genuinely running: a second past the start
        // instant the group is at 601 s and a player still sitting at 600 s is really behind.
        var released = SyncPlayScheduler.ResolveDrift(playing with { LastApplied = preRoll }, offset,
            localNow.AddMilliseconds(1800), 600);
        check(released.Kind == SyncPlayActionKind.DriftSeek
            && released.TargetTicks == 601 * TimeSpan.TicksPerSecond,
            "once the start instant has passed the same command corrects drift normally");
    }

    // The production ServerClock against the hand-rolled server: the real SDK request builders, the
    // real UtcTimeResponse, the real ping body. The server's clock is deliberately five minutes
    // ahead, so a leg that stopped reading the answer would report an offset of nothing.
    private static async Task CheckServerClockTransportAsync(Action<bool, string> check)
    {
        var skew = TimeSpan.FromMinutes(5);
        using var server = new LiveSessionFixture.FakeJellyfinServer { ClockSkew = skew };
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the production service accepts the fixture session");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var clock = ServerClock.ForSession(service);
        await clock.ForceUpdateAsync().ConfigureAwait(false);

        var time = await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(time.Method == "GET" && time.Path.EndsWith("/GetUtcTime", StringComparison.Ordinal),
            "the clock reads the server time from GetUtcTime");
        check(clock.IsReady && Math.Abs((clock.Offset - skew).TotalSeconds) < 5,
            "the measured offset is the server's actual skew");
        check(clock.ToLocal(clock.ToServer(Base)) == Base,
            "converting to server time and back is a round trip");

        var ping = await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(ping.Method == "POST" && ping.Path.EndsWith("/SyncPlay/Ping", StringComparison.Ordinal),
            "every measurement is followed by a SyncPlay ping");
        using var body = JsonDocument.Parse(ping.Body);
        var reported = body.RootElement.EnumerateObject()
            .First(property => property.Name.Equals("Ping", StringComparison.OrdinalIgnoreCase)).Value.GetInt64();
        check(reported == clock.PingMs && reported is >= 0 and <= 10_000,
            "the ping the server is told is the one the clock measured");
    }

    // The group lifecycle end to end, over the wire: the production SyncPlayClient, the production
    // LiveSessionService and SessionSocket, the production ServerClock and the real SDK request
    // builders, against the same hand-rolled Jellyfin the live-session fixture drives. Nothing here
    // is mocked, so every assertion about a request body is an assertion about what the SDK
    // actually put on the wire.
    //
    // The server's clock is five minutes ahead. That is not decoration: Ready and Buffering carry a
    // When the server measures itself against, and past a 2000 ms discrepancy it silently treats
    // the elapsed time as zero and logs that this client is "not time syncing properly" — nothing
    // reaches the client at all. A When left in local time would degrade every member's sync with
    // no error anywhere, so the skew is what makes that visible here.
    private static async Task CheckGroupLifecycleAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-group-profile";
        var skew = TimeSpan.FromMinutes(5);
        using var server = new LiveSessionFixture.FakeJellyfinServer { ClockSkew = skew };
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the group fixture session connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // The session resolver is a delegate the caller supplies, and the client calls it on every
        // REST path BEFORE it takes its own lock — which makes it the one place a fixture can
        // stand inside a window that is otherwise microseconds wide. Both switches are one-shot
        // and both are off except for the leg that arms them.
        var starve = 0;
        var hold = 0;
        var entered = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        JellyfinService? Resolve(string key)
        {
            if (key != profileKey)
                return null;
            if (Interlocked.Exchange(ref starve, 0) == 1)
                return null;
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                entered.Release();
                proceed.Wait();
            }
            return service;
        }

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        using var client = new SyncPlayClient(live, Resolve);
        var events = new Recorder(client);
        live.StartProfile(profileKey);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        check(await client.RefreshAccessAsync(profileKey).ConfigureAwait(false)
                == SyncPlayAccessLevel.CreateAndJoinGroups,
            "the SyncPlay level is read off the user's policy");

        var listed = await client.ListGroupsAsync(profileKey).ConfigureAwait(false);
        check(LastRequest(server, "/SyncPlay/List").Method == "GET", "the group list is a GET on SyncPlay/List");
        check(listed is [{ GroupName: "Movie night", Participants.Count: 2 }]
            && listed[0].GroupId == server.ListedGroupId,
            "the group list carries the server's group, name and participants");

        var group = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        // Both started before either is awaited, which is what a double-clicked group row is: the
        // second call has to be answered by the guard rather than posted. Two POSTs would be two
        // GroupJoined raises and, at the player binding, the same file loaded twice.
        var joining = client.JoinAsync(profileKey, group);
        var doubled = await client.JoinAsync(profileKey, group).ConfigureAwait(false);
        var accepted = await joining.ConfigureAwait(false);
        // The REQUEST first and the client's answer second, each with its own name, and both
        // printed when either is wrong. A bare `accepted && !doubled` cannot tell "the POST never
        // left" from "the POST left and the client did not like what came back" — this leg went
        // red once on an untouched join path and the harness said nothing about which it had been.
        var posts = server.CountOf("/SyncPlay/Join");
        if (posts != 1 || !accepted || doubled)
            Console.WriteLine(FormattableString.Invariant(
                $"DIAG: join posts={posts} accepted={accepted} doubled={doubled}"));
        check(posts == 1, "a double-clicked join posts once, not twice");
        check(accepted && !doubled,
            "the join POST is accepted and a second join while it is in flight is refused");
        var join = LastRequest(server, "/SyncPlay/Join");
        check(join.Method == "POST", "the join is a POST on SyncPlay/Join");
        check(Field(join.Body, "GroupId").GetGuid() == group, "the join body names the group being joined");
        // The join FAILS on two of the nine updates, so nothing may treat an accepted POST as
        // membership: a client that did would echo a playlist item id it was never given.
        check(!client.IsInGroup && client.GroupId is null,
            "an accepted join POST is not a membership until the server confirms it");
        var ping = LastRequest(server, "/SyncPlay/Ping");
        check(ping.Method == "POST" && Field(ping.Body, "Ping").GetInt64() is >= 0 and <= 10_000,
            "joining measures the clock and reports the ping");

        // Settled on the event rather than on the flag: membership is taken under the lock and the
        // event raised after it, so waiting for IsInGroup would race the raise.
        await connection.SendAsync(GroupJoinedFrame(group, "Movie night", "Paused", "alice", "bob")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Joined.Count == 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && events.Joined[0].GroupName == "Movie night" && client.IsInGroup,
            "GroupJoined is what puts the client in the group, and it is raised once");
        check(client.GroupId == group && client.GroupName == "Movie night"
            && client.Participants is ["alice", "bob"] && client.GroupState == "Paused",
            "the group carries its name, its participants and its state");

        // The monotonic queue guard. The three updates go out in one burst with the NEWEST last, so
        // the assertion that only two were applied is what says the older and the equal one were
        // dropped rather than merely slow — a leg that waited for them not to arrive could only
        // ever prove it with a timeout.
        var stamp = new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);
        await connection.SendAsync(QueueFrame(group, stamp, first)).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Queues.Count == 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "a PlayQueue update is applied");
        check(client.PlaylistItemId == first,
            "the playing entry's server-minted PlaylistItemId is what the client holds");
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(-5), Guid.NewGuid())).ConfigureAwait(false);
        await connection.SendAsync(QueueFrame(group, stamp, Guid.NewGuid())).ConfigureAwait(false);
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(5), second)).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Queues.Count == 2, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && events.Queues[1].LastUpdate == stamp.AddSeconds(5),
            "a queue update older than or equal to the applied one is dropped and a newer one is not");
        check(client.PlaylistItemId == second,
            "the newer update moved the playing entry and the dropped ones did not");

        // The playlist item id in the OTHER form the server may write it in. It is a String
        // server-side while the Ready and Buffering bodies that echo it are Guid, and
        // System.Text.Json reads only the 36-character dashed form into a Guid? — it throws on the
        // dashless one, which drops the WHOLE PlayQueue payload rather than the field. Everything
        // downstream then fails silently: the playlist item id stays null, every Ready and
        // Buffering echoes Guid.Empty, the server rejects them, and the group waits out its 30 s
        // timeout on every resume.
        await connection.SendAsync(QueueFrame(group, stamp.AddSeconds(10), third, dashless: true)).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Queues.Count == 3, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && client.PlaylistItemId == third,
            "a dashless playlist item id is applied rather than dropping the queue update");

        // A command over the socket, resolved against the group the client holds. Three seconds
        // late, so the position to land on is where the group has got to.
        var serverNow = DateTime.UtcNow + skew;
        await connection.SendAsync(LiveSessionFixture.SyncCommand(group, third, SyncPlayCommandTypes.Unpause,
            Iso(serverNow.AddSeconds(-3)), Iso(serverNow), 600 * TimeSpan.TicksPerSecond)).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Actions.Count == 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "a group command reaches the player as an action");
        var action = events.Actions[0];
        check(action.Kind == SyncPlayActionKind.UnpauseNow && action.NeedsSeek
            && Math.Abs(action.TargetSeconds - 603) < 2,
            "the action targets the position the group has reached, not the one the command named");
        check(client.LastApplied is { Command: SyncPlayCommandTypes.Unpause },
            "the applied command is kept, which is what the drift rule is measured against");

        var readyAt = DateTime.UtcNow;
        await client.ReportReadyAsync(isPlaying: true, 600 * TimeSpan.TicksPerSecond).ConfigureAwait(false);
        var ready = LastRequest(server, "/SyncPlay/Ready");
        check(ready.Method == "POST", "readiness is a POST on SyncPlay/Ready");
        check(Field(ready.Body, "PlaylistItemId").GetGuid() == third
            && Field(ready.Body, "PositionTicks").GetInt64() == 600 * TimeSpan.TicksPerSecond
            && Field(ready.Body, "IsPlaying").GetBoolean(),
            "the Ready body echoes the current playlist item, the position and the transport state");
        var readyWhen = When(ready.Body);
        check(Math.Abs((readyWhen - (readyAt + skew)).TotalSeconds) < 10
            && readyWhen - readyAt > TimeSpan.FromMinutes(1),
            "Ready's When is this client's instant converted to SERVER time, not local now");

        // The debounce. A Buffering report pauses the WHOLE group, so a cache blip that clears
        // inside the window must tell the group nothing at all — and must not send the Ready that
        // would release a group nothing ever stopped.
        var buffers = server.CountOf("/SyncPlay/Buffering");
        var readies = server.CountOf("/SyncPlay/Ready");
        client.ReportBuffering(true, 600 * TimeSpan.TicksPerSecond, isPlaying: true);
        await Task.Delay(60).ConfigureAwait(false);
        client.ReportBuffering(false, 600 * TimeSpan.TicksPerSecond, isPlaying: true);
        await Task.Delay(600).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Buffering") == buffers && server.CountOf("/SyncPlay/Ready") == readies,
            "a buffer shorter than the debounce window tells the group nothing");

        // Signalled REPEATEDLY, the way a poll-driven caller signals a stall that is still going.
        // The window is armed on the edge for exactly this: re-arming on each repeat would reset it
        // every time and the group would never be told about a stall that never ends.
        var stalledAt = DateTime.UtcNow;
        using var repeating = new CancellationTokenSource();
        var repeats = Task.Run(async () =>
        {
            while (!repeating.IsCancellationRequested)
            {
                client.ReportBuffering(true, 900 * TimeSpan.TicksPerSecond, isPlaying: true);
                await Task.Delay(100).ConfigureAwait(false);
            }
        });
        // Asserted WHILE the repeats are still arriving, which is what makes this about the edge
        // and not about the debounce: a window re-armed on every repeat would never expire for as
        // long as the stall is being signalled, and the group would never be told about a stall
        // that never ends. A window that only expires once the signalling stops would still pass a
        // leg that waited for the loop to finish.
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Buffering") == buffers + 1,
                TimeSpan.FromSeconds(3)).ConfigureAwait(false),
            "a buffer that outlasts the window is reported to the group, however often it is signalled");
        await repeating.CancelAsync().ConfigureAwait(false);
        await repeats.ConfigureAwait(false);
        await Task.Delay(600).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Buffering") == buffers + 1,
            "a stall that continues is reported exactly once, not on a timer");
        var buffering = LastRequest(server, "/SyncPlay/Buffering");
        check(buffering.Method == "POST"
            && Field(buffering.Body, "PlaylistItemId").GetGuid() == third
            && Field(buffering.Body, "PositionTicks").GetInt64() == 900 * TimeSpan.TicksPerSecond,
            "the Buffering body echoes the current playlist item and the position it stalled at");
        check(When(buffering.Body) - stalledAt > TimeSpan.FromMinutes(1),
            "Buffering's When is in server time too");
        client.ReportBuffering(false, 900 * TimeSpan.TicksPerSecond, isPlaying: false);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Ready") == readies + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "clearing a reported buffer sends the Ready that releases the group");

        // THE FIRING WINDOW. The debounce timer is let go before anything can cancel it, so a
        // buffer that clears in the moment between the callback being dispatched and the callback
        // taking the lock is a real interleaving: the clear takes the lock first, sees that no
        // Buffering has gone out and sends no Ready, and a callback that did not look again would
        // then report a buffer that is already over. That parks the WHOLE group in Waiting with
        // nothing left to release it — this client is edge-driven and the edge has been spent — so
        // what ends it is the server's 30 s group wait, for every member.
        //
        // Driven through the session resolver, which the callback calls before it takes the lock.
        var windowBuffers = server.CountOf("/SyncPlay/Buffering");
        var windowReadies = server.CountOf("/SyncPlay/Ready");
        Interlocked.Exchange(ref hold, 1);
        client.ReportBuffering(true, 1200 * TimeSpan.TicksPerSecond, isPlaying: true);
        check(await entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the debounce callback reaches the session lookup, which is inside the firing window");
        client.ReportBuffering(false, 1200 * TimeSpan.TicksPerSecond, isPlaying: true);
        proceed.Release();
        await Task.Delay(600).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Buffering") == windowBuffers
            && server.CountOf("/SyncPlay/Ready") == windowReadies,
            "a buffer that clears inside the firing window tells the group nothing, either way");
        // And the client is not left thinking it has a report outstanding: the next real stall has
        // to arm the window again and the Ready after it has to be the one that releases the group.
        client.ReportBuffering(true, 1300 * TimeSpan.TicksPerSecond, isPlaying: true);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Buffering") == windowBuffers + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "a stall after the cancelled one is still reported");
        client.ReportBuffering(false, 1300 * TimeSpan.TicksPerSecond, isPlaying: true);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Ready") == windowReadies + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "and clearing it releases the group exactly once");

        // The other half of the same flag: a callback that gives up — here because the session it
        // would post through has gone — has to close the window behind it. The window is armed on
        // the EDGE, so a pending flag left standing means no later stall can arm it at all until
        // the buffer has cleared in between, and a poll-driven caller signalling a stall that never
        // ends never produces that clear.
        var starvedBuffers = server.CountOf("/SyncPlay/Buffering");
        Interlocked.Exchange(ref starve, 1);
        client.ReportBuffering(true, 1400 * TimeSpan.TicksPerSecond, isPlaying: true);
        await Task.Delay(600).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Buffering") == starvedBuffers,
            "a stall the session lookup cannot answer for reports nothing");
        // No clear in between, deliberately: this is the stall continuing, not a new one.
        client.ReportBuffering(true, 1400 * TimeSpan.TicksPerSecond, isPlaying: true);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Buffering") == starvedBuffers + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the abandoned window is closed behind it, so the stall that follows can still arm one");
        client.ReportBuffering(false, 1400 * TimeSpan.TicksPerSecond, isPlaying: true);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Ready") == windowReadies + 2,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "clearing that one releases the group too");

        // A subscriber that throws costs one event, not the client. These are raised on the
        // socket's receive loop, so an escaping handler exception would unwind the loop and take
        // the reconnect ladder — and with it the re-join below — down for good. The socket's own
        // Raise is the outer half of that and the client's is the inner half; this leg covers the
        // path end to end, and the GroupJoined leg further down is the one that can only pass if
        // the client has a guard of its own.
        client.StateChanged += _ => throw new InvalidOperationException("subscriber fault");
        var states = events.States.Count;
        await connection.SendAsync(LiveSessionFixture.GroupUpdate(group, "StateUpdate",
            """{"State":"Playing","Reason":"Unpause"}""")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.States.Count == states + 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "an update still reaches the subscribers ahead of one that throws");
        await connection.SendAsync(LiveSessionFixture.GroupUpdate(group, "StateUpdate",
            """{"State":"Paused","Reason":"Pause"}""")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.States.Count == states + 2 && client.GroupState == "Paused",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "a subscriber exception costs one event, not the client");

        // Re-join IS the resynchronisation primitive: the server holds the membership against the
        // SessionInfo for 60 s after the socket drops and has an explicit restore branch for a join
        // naming a group the session is already in. One per OUTAGE — a reconnect storm must not be
        // one POST per attempt, and a socket that dies and returns inside a single POST round trip
        // must not be one POST for two outages either: the second reconnect is a live socket
        // holding a membership that nothing has resynchronised, and an idle client sends nothing
        // that would ever notice.
        var joins = server.CountOf("/SyncPlay/Join");
        // The server's clock STEPS an hour during the outage, and its /GetUtcTime slows down. Both
        // halves are load-bearing: the step is what a machine that slept through the outage comes
        // back to, and the slow answer is what makes the fresh measurement LOSE on delay. The clock
        // believes the smallest delay it holds, so a client that files a new sample into its
        // pre-outage window keeps the pre-outage champion — and then every instant it sends is an
        // hour wrong for the seven or eight minutes the ring takes to evict it, with nothing
        // reporting anything: past 2000 ms the server treats the elapsed time as zero and says so
        // only in its own log.
        var stepped = TimeSpan.FromHours(1);
        server.ClockSkew = stepped;
        server.ClockDelay = TimeSpan.FromMilliseconds(750);
        // And the restore's join is held open for eight seconds, which is what puts the SECOND
        // outage inside the first restore rather than after it: the reconnect ladder backs off four
        // seconds before the second attempt, so against an instant join the first restore would
        // simply be over by then and the guard would never be under test at all.
        server.JoinDelay = TimeSpan.FromSeconds(8);
        // Where this outage's requests start, so the ORDER can be asserted rather than a count. A
        // count of /GetUtcTime is satisfied by the clock's own poll loop whatever the re-join did,
        // which is the one thing this has to be able to tell apart.
        var before = server.Requests.Count;
        var resumed = await DropAndResumeAsync(server, connection).ConfigureAwait(false);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == joins + 1,
                TimeSpan.FromSeconds(60)).ConfigureAwait(false),
            "the first outage's restore reaches the server and is left in flight");
        // The second outage, landing while that POST is still open.
        resumed = await DropAndResumeAsync(server, resumed).ConfigureAwait(false);
        // At LEAST two, so that a client which re-joined once per attempt fails on the count below
        // by name rather than by timing out here.
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") >= joins + 2,
                TimeSpan.FromSeconds(60)).ConfigureAwait(false),
            "a reconnect that lands while a restore is in flight is re-run, not dropped");
        server.JoinDelay = TimeSpan.Zero;
        // Long enough for BOTH held joins to have been answered. A third re-join would show up in
        // the count below — and a restore still in flight would be a POST landing in the middle of
        // a later leg, which counts requests for a living.
        await Task.Delay(TimeSpan.FromSeconds(9)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Join") == joins + 2,
            "two outages are two re-joins, one per outage and not one per reconnect attempt");
        check(Field(LastRequest(server, "/SyncPlay/Join").Body, "GroupId").GetGuid() == group,
            "the re-join names the group already held");
        // The ORDER, which is what "before it asks for the group back" means and what no count can
        // see: between losing the socket and asking for the group back this client measured the
        // clock and reported the ping it measured, and did nothing else.
        var outage = server.Requests.Skip(before).ToList();
        var restore = outage.FindIndex(request =>
            request.Path.EndsWith("/SyncPlay/Join", StringComparison.OrdinalIgnoreCase));
        check(restore > 0
            && outage.Take(restore).All(request =>
                request.Path.EndsWith("/GetUtcTime", StringComparison.OrdinalIgnoreCase)
                || request.Path.EndsWith("/SyncPlay/Ping", StringComparison.OrdinalIgnoreCase))
            && outage.Take(restore).Any(request =>
                request.Path.EndsWith("/GetUtcTime", StringComparison.OrdinalIgnoreCase)),
            "the re-join measures the clock before it asks for the group back, with nothing between");
        check(client.Clock is { } measured && Math.Abs((measured.Offset - stepped).TotalSeconds) < 5,
            "the restore measures into an EMPTIED window, so a clock that stepped during the outage "
                + "is believed over the samples from before it");
        server.ClockDelay = TimeSpan.Zero;

        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(group, "UserJoined", "\"carol\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Notices.Count == 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && events.Notices[0] is { Kind: SyncPlayNoticeKind.UserJoined, Text: "carol" }
            && client.Participants.Contains("carol"),
            "UserJoined is a notice and joins the participant list");
        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(group, "UserLeft", "\"alice\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Notices.Count == 2, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && events.Notices[1] is { Kind: SyncPlayNoticeKind.UserLeft, Text: "alice" }
            && !client.Participants.Contains("alice"),
            "UserLeft is a notice and leaves the participant list");

        // Settled on the EVENT, not on the flag: the membership is dropped under the lock and
        // GroupLeft is raised after it, so a leg that waited for IsInGroup to go false would race
        // the raise and read the count one event early.
        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(group, "GroupLeft", $"\"{group:D}\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Left.Count == 1, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && events.Left[0] == group && !client.IsInGroup
            && client.GroupName is null && client.PlaylistItemId is null,
            "GroupLeft clears the membership and everything hanging off it");

        await BackInGroupAsync(check, client, resumed, profileKey, group, "NotInGroup").ConfigureAwait(false);
        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(group, "NotInGroup", $"\"{group:D}\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Left.Count == 2, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && !client.IsInGroup,
            "NotInGroup clears the membership too");

        // The client's OWN Raise guard, which the leg above cannot see: the socket swallows
        // anything that escapes a handler, so a client with no guard of its own still looks healthy
        // from outside. What it actually loses is the work it does AFTER the raise — a join raises
        // GroupJoined and then raises the group's state — so a throwing GroupJoined subscriber
        // would silently cost every other subscriber the state that came with the join.
        client.GroupJoined += _ => throw new InvalidOperationException("subscriber fault");
        var statesBeforeJoin = events.States.Count;
        await BackInGroupAsync(check, client, resumed, profileKey, group, "leave").ConfigureAwait(false);
        check(await SettlesAsync(() => events.States.Count == statesBeforeJoin + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "a throwing GroupJoined subscriber does not cost the state raised after it");
        var leaves = server.CountOf("/SyncPlay/Leave");
        await client.LeaveAsync().ConfigureAwait(false);
        var leave = LastRequest(server, "/SyncPlay/Leave");
        check(server.CountOf("/SyncPlay/Leave") == leaves + 1 && leave.Method == "POST" && leave.Body.Length == 0,
            "leaving posts an empty-bodied SyncPlay/Leave");
        check(!client.IsInGroup && events.Left.Count == 3, "leaving drops the membership locally as well");

        // Denial has no socket message at all in 12.0 — it is a bare 403 — so a run of them is the
        // only thing that can end a membership the server has already stopped honouring.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "denial").ConfigureAwait(false);
        server.SyncPlayStatus = 403;
        for (var i = 0; i < 3; i++)
            await client.ReportReadyAsync(isPlaying: false, 0).ConfigureAwait(false);
        check(!client.IsInGroup && events.Left.Count == 4
            && events.Notices[^1] is { Kind: SyncPlayNoticeKind.AccessDenied },
            "three consecutive refusals end the membership with a notice");
        server.SyncPlayStatus = 204;

        // A socket that comes back AFTER the server's 60 s WebSocketLostTimeout: the membership is
        // gone, so the restore join is refused. Sitting in a group the server has forgotten is the
        // silent half of this failure — the pill would still be up and every command would be aimed
        // at nothing — so the honest answer is to leave locally and say why.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "expired re-join").ConfigureAwait(false);
        server.SyncPlayStatus = 403;
        resumed = await DropAndResumeAsync(server, resumed).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Left.Count == 5, TimeSpan.FromSeconds(20)).ConfigureAwait(false)
            && !client.IsInGroup
            && events.Notices[^1] is { Kind: SyncPlayNoticeKind.RejoinFailed },
            "a re-join the server refuses leaves the group locally, with a notice");
        server.SyncPlayStatus = 204;

        // A LEAVE that lands while a restore is in flight. The restore's POST re-joins this session
        // server-side a round trip after the user left, and the server has no way to know: the
        // membership is keyed to the session, so this client would be back in the group as a member
        // that never reports Ready — which is the 30 s group wait, for everyone, on every resume.
        // The join is held open for two seconds so the leave necessarily lands inside it.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "leave race").ConfigureAwait(false);
        var raceJoins = server.CountOf("/SyncPlay/Join");
        var raceLeaves = server.CountOf("/SyncPlay/Leave");
        var raceNotices = events.Notices.Count;
        server.JoinDelay = TimeSpan.FromSeconds(2);
        resumed = await DropAndResumeAsync(server, resumed).ConfigureAwait(false);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == raceJoins + 1,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "the restore's join reaches the server");
        await client.LeaveAsync().ConfigureAwait(false);
        check(!client.IsInGroup, "the leave takes effect locally while the restore is still in flight");
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Leave") == raceLeaves + 2,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "a restore that landed after the leave is undone with a second Leave");
        server.JoinDelay = TimeSpan.Zero;
        check(!client.IsInGroup && events.Notices.Count == raceNotices,
            "and the client stays out of the group, with nothing to tell the user about");

        // A refused RESTORE. The server answers the restore's Join 204 whether it means to honour
        // it or not, so a group destroyed during the outage is only ever announced afterwards, on
        // the socket — with GroupDoesNotExist carrying an EMPTY group id, which is what the server
        // sends when the group it would name is gone. Treating that as a notice alone would keep
        // the membership, the group id, the playlist item id and a polling clock for a group that
        // does not exist, and per this client's own rule an idle member sends nothing, so the
        // NotInGroup that might eventually correct it may never come.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "refused restore").ConfigureAwait(false);
        var refusedJoins = server.CountOf("/SyncPlay/Join");
        var refusedLeft = events.Left.Count;
        resumed = await DropAndResumeAsync(server, resumed).ConfigureAwait(false);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == refusedJoins + 1,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false)
            && client.IsInGroup,
            "the restore is posted, answered 204, and leaves the membership standing");
        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(Guid.Empty, "GroupDoesNotExist",
            $"\"{Guid.Empty:D}\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Left.Count == refusedLeft + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && !client.IsInGroup && client.GroupId is null && client.Clock is null
            && events.Notices[^1] is { Kind: SyncPlayNoticeKind.GroupDoesNotExist },
            "a GroupDoesNotExist against a held membership ends it rather than only saying so");

        // The other refusal, naming the group actually held rather than an empty id — the shape a
        // library this user may no longer see produces.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "library refusal").ConfigureAwait(false);
        var deniedLeft = events.Left.Count;
        await resumed.SendAsync(LiveSessionFixture.GroupUpdate(group, "LibraryAccessDenied",
            $"\"{group:D}\"")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.Left.Count == deniedLeft + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && !client.IsInGroup
            && events.Notices[^1] is { Kind: SyncPlayNoticeKind.LibraryAccessDenied },
            "a LibraryAccessDenied naming the group held ends the membership too");

        // Last, because it takes the socket with it: the owning session going away. Every REST call
        // resolves the session by profile key, so once that lookup answers null they are all silent
        // no-ops — a membership kept here would be a group pill, and a group id echoed at nothing,
        // for the life of the process.
        await BackInGroupAsync(check, client, resumed, profileKey, group, "session close").ConfigureAwait(false);
        var closedLeft = events.Left.Count;
        live.StopProfile(profileKey);
        check(await SettlesAsync(() => events.Left.Count == closedLeft + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            && !client.IsInGroup,
            "closing the owning profile's session drops the membership");
    }

    // Local transport inside a group is not local at all: the four calls below ASK the group, and
    // the player moves on the echo that comes back. Its own server and its own client because half
    // of what is asserted is SILENCE — that a call made outside a group reaches the network not at
    // all — and a leg running after the lifecycle fixture's joins and leaves could not tell a
    // request this client suppressed from one an earlier leg had already made.
    private static async Task CheckLocalTransportAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-transport-profile";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the transport fixture session connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        using var client = new SyncPlayClient(live, key => key == profileKey ? service : null);
        var events = new Recorder(client);
        live.StartProfile(profileKey);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        const long seekTicks = 742 * TimeSpan.TicksPerSecond;
        // All four, in one order, so every leg below compares the same four answers against the
        // same four route counts.
        async Task<bool[]> PostAllAsync(long ticks) =>
        [
            await client.PauseAsync().ConfigureAwait(false),
            await client.UnpauseAsync().ConfigureAwait(false),
            await client.SeekAsync(ticks).ConfigureAwait(false),
            await client.StopAsync().ConfigureAwait(false),
        ];
        int[] Counts() =>
        [
            server.CountOf("/SyncPlay/Pause"),
            server.CountOf("/SyncPlay/Unpause"),
            server.CountOf("/SyncPlay/Seek"),
            server.CountOf("/SyncPlay/Stop"),
        ];

        // Outside a group there is nothing to ask, and BOTH halves of that matter: this server
        // answers every SyncPlay route 204, so a method that posted anyway would still hand back
        // false and only the request count can tell "suppressed" from "sent and shrugged off".
        check(await PostAllAsync(seekTicks).ConfigureAwait(false) is [false, false, false, false],
            "outside a group all four transport calls answer false");
        check(Counts() is [0, 0, 0, 0], "and none of them reaches the server at all");

        // The window a guard copied from LeaveAsync would get wrong: the join POST has been
        // accepted, GroupJoined has not arrived, so there IS a profile key to post through and
        // there is still no membership. A transport call sent here is aimed at a group this client
        // has not been admitted to.
        var group = Guid.NewGuid();
        check(await client.JoinAsync(profileKey, group).ConfigureAwait(false) && !client.IsInGroup,
            "the transport fixture's join POST is accepted and is not yet a membership");
        check(await PostAllAsync(seekTicks).ConfigureAwait(false) is [false, false, false, false],
            "a transport call between the join POST and its confirmation answers false");
        check(Counts() is [0, 0, 0, 0], "and reaches the server no more than one outside a group does");

        await connection.SendAsync(GroupJoinedFrame(group, "Movie night", "Paused", "bob")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the client is in the group before the transport leg");

        check(await PostAllAsync(seekTicks).ConfigureAwait(false) is [true, true, true, true],
            "in a group all four transport calls are accepted");
        // Per ROUTE rather than in total: a pause posted to Unpause is four requests either way,
        // and the group would resume where the user asked it to stop.
        check(Counts() is [1, 1, 1, 1], "each transport call hits its own route exactly once");
        check(LastRequest(server, "/SyncPlay/Pause") is { Method: "POST", Body.Length: 0 }
            && LastRequest(server, "/SyncPlay/Unpause") is { Method: "POST", Body.Length: 0 }
            && LastRequest(server, "/SyncPlay/Stop") is { Method: "POST", Body.Length: 0 },
            "pause, unpause and stop are empty-bodied POSTs");
        var seek = LastRequest(server, "/SyncPlay/Seek");
        check(seek.Method == "POST" && Field(seek.Body, "PositionTicks").GetInt64() == seekTicks,
            "the seek body carries the position it was asked to seek to");
        // A second target, because one assertion against one number cannot tell a body that
        // carries the argument from one that carries a constant that happens to match it.
        check(await client.SeekAsync(seekTicks * 2).ConfigureAwait(false), "a second seek is accepted");
        check(Field(LastRequest(server, "/SyncPlay/Seek").Body, "PositionTicks").GetInt64() == seekTicks * 2,
            "and its body carries the new position rather than the first one");

        // The 403 rule, which is the reason all four go through the same PostAsync as the join, the
        // leave and the Ready. Denial has no socket message behind it, and a transport POST is
        // exactly the traffic that discovers a membership the server has stopped honouring.
        server.SyncPlayStatus = 403;
        var pauses = server.CountOf("/SyncPlay/Pause");
        var refused = new List<bool>();
        for (var i = 0; i < 3; i++)
            refused.Add(await client.PauseAsync().ConfigureAwait(false));
        check(refused is [false, false, false], "each refused pause answers false");
        check(server.CountOf("/SyncPlay/Pause") == pauses + 3,
            "all three reached the server, so the run of denials is a run and not one call counted thrice");
        check(!client.IsInGroup && events.Left is [var left] && left == group
            && events.Notices is [{ Kind: SyncPlayNoticeKind.AccessDenied }],
            "three consecutive refused pauses end the membership with a notice");
        server.SyncPlayStatus = 204;

        // And with the membership gone the next one is silent again, which is what "the transport
        // calls are inside the rule" has to mean on the far side of it.
        var afterDenial = server.CountOf("/SyncPlay/Pause");
        check(!await client.PauseAsync().ConfigureAwait(false)
            && server.CountOf("/SyncPlay/Pause") == afterDenial,
            "a pause after the denials ended the membership reaches the server not at all");
    }

    // A restore that spans a LEAVE AND A FRESH JOIN, which is the one window where the re-join can
    // act on a group this client no longer holds. Its own server and its own client because both
    // halves need the reconnect ladder near the bottom of its range: the guard under test is the
    // re-run a reconnect is owed when it lands INSIDE a restore, and against a ladder already at
    // its 30 s ceiling the restore would be long over before the second reconnect arrived, so the
    // leg would pass without ever reaching what it names.
    private static async Task CheckRestoreRaceAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-restore-race-profile";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the restore-race fixture session connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        using var client = new SyncPlayClient(live, key => key == profileKey ? service : null);
        var events = new Recorder(client);
        live.StartProfile(profileKey);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var held = Guid.NewGuid();
        var joined = Guid.NewGuid();
        var pendingGroup = Guid.NewGuid();
        await BackInGroupAsync(check, client, connection, profileKey, held, "restore race").ConfigureAwait(false);

        // Every join from here is held open, so everything below happens INSIDE one restore. Ten
        // seconds covers the second reconnect, which the ladder puts four seconds after the drop
        // that causes it, with margin for the leave and the join that follow it.
        server.JoinDelay = TimeSpan.FromSeconds(10);
        var joins = server.CountOf("/SyncPlay/Join");
        connection = await DropAndResumeAsync(server, connection).ConfigureAwait(false);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == joins + 1,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "the held group's restore is posted and left in flight");

        // The second outage, landing while that POST is open: the reconnect it ends is owed a
        // re-run, because the socket the restore was started for is already gone.
        connection = await DropAndResumeAsync(server, connection).ConfigureAwait(false);
        // Waited for on the CLIENT side, not on the accept. Connected is raised just before the
        // receive loop starts, so a frame that has reached a subscriber proves the reconnect has
        // been seen — and everything below depends on it having been seen while the group was
        // still held, since a leave that got in first would leave nothing for it to re-run.
        var states = events.States.Count;
        await connection.SendAsync(LiveSessionFixture.GroupUpdate(held, "StateUpdate",
            """{"State":"Paused","Reason":"Pause"}""")).ConfigureAwait(false);
        check(await SettlesAsync(() => events.States.Count == states + 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the second reconnect is seen by the client before the group changes under it");

        // And now the user leaves that group and joins another one, still inside the same restore.
        // The join is not awaited: its POST is held by the same delay, and it is the socket's
        // GroupJoined that makes it a membership anyway.
        await client.LeaveAsync().ConfigureAwait(false);
        var joining = client.JoinAsync(profileKey, joined);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == joins + 2,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "the new group's join is posted while the old group's restore is still open");
        await connection.SendAsync(GroupJoinedFrame(joined, "Second night", "Paused", "bob")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.GroupId == joined, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the client holds the new group before the restore comes back");

        // THE RE-RUN, and the whole point of the leg. What it has to restore is the group this
        // client holds NOW. Posting the captured one would ask the server to leave the new group
        // and re-join the old, and the GroupJoined answering that names a group nothing here is
        // waiting for — so it would be dropped, leaving this client in no group at all and the
        // server holding it in the one the user left, with nothing that would ever notice.
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == joins + 3,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "the reconnect that landed inside the restore is re-run once the restore returns");
        check(Field(LastRequest(server, "/SyncPlay/Join").Body, "GroupId").GetGuid() == joined,
            "the re-run restores the group held NOW, not the one captured when the socket came back");
        check(client.GroupId == joined && client.IsInGroup && events.Left.Count == 1,
            "and the client is still in the group it joined, with only its own leave behind it");

        // The other half of the same window. A restore that lands after a leave is undone with a
        // corrective Leave — the server has just put this session back in a group the user left —
        // but a join that has been POSTED AND NOT YET CONFIRMED has to stop it: membership starts
        // at GroupJoined, so that gap reads exactly like "left, and nothing replaced it", and the
        // corrective Leave would land after the new join and take the user straight back out of
        // the group they had just joined, silently.
        // The first join has to have returned before another can be posted at all — one join at a
        // time is the client's own guard against a double-clicked group row — and its POST is held
        // by the same delay as everything else here. It landed before the re-run's did, so waiting
        // for it still leaves the re-run's restore open.
        check(await joining.ConfigureAwait(false), "the new group's join POST was accepted");
        var leaves = server.CountOf("/SyncPlay/Leave");
        await client.LeaveAsync().ConfigureAwait(false);
        var pendingJoin = client.JoinAsync(profileKey, pendingGroup);
        check(await SettlesAsync(() => server.CountOf("/SyncPlay/Join") == joins + 4,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false),
            "the pending group's join is posted while the re-run's restore is still open");
        // No GroupJoined for it, deliberately: the pending join is the state under test. The wait
        // covers the re-run's restore returning — held by the same ten seconds — and anything it
        // decided to send afterwards reaching the server.
        await Task.Delay(TimeSpan.FromSeconds(13)).ConfigureAwait(false);
        check(server.CountOf("/SyncPlay/Leave") == leaves + 1,
            "a restore landing on a join that is not yet confirmed posts no corrective Leave");
        server.JoinDelay = TimeSpan.Zero;
        await pendingJoin.ConfigureAwait(false);

        // Last, because it closes the socket: ProfileStopped is the one event LiveSessionService
        // raises itself instead of re-raising out of a socket handler, so it is the one with no
        // Raise guard behind it — and it goes off on the UI THREAD, inside MainWindow's
        // SessionClosed handler. A subscriber that throws has to cost its own handler and nothing
        // else: unwinding out of StopProfile would leave a logout half done, with this socket
        // disposed and any other warm profile's never stopped.
        live.ProfileStopped += _ => throw new InvalidOperationException("subscriber fault");
        var escaped = false;
        try
        {
            live.StopProfile(profileKey);
        }
        catch (InvalidOperationException)
        {
            escaped = true;
        }
        check(!escaped, "a throwing ProfileStopped subscriber does not escape StopProfile");
    }

    // The other way a session ends: the token is refused, which stops the socket for good and is
    // the only notice anything gets. Its own server, because the socket that establishes the group
    // has to be accepted before the reconnect that carries the refusal is not.
    private static async Task CheckSessionLossAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-rejected-profile";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the rejected-session fixture connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        using var client = new SyncPlayClient(live, key => key == profileKey ? service : null);
        var events = new Recorder(client);
        live.StartProfile(profileKey);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var group = Guid.NewGuid();
        await client.JoinAsync(profileKey, group).ConfigureAwait(false);
        await connection.SendAsync(GroupJoinedFrame(group, "Movie night", "Paused", "bob")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the client is in the group before its token is revoked");

        // Revoked mid-session, which is what the socket finds out on its next upgrade: a 401, and
        // nothing reconnects after it. The membership has to go with it — there is no session left
        // to leave the group through, and every call it would make is now a no-op.
        server.RejectUpgrades = true;
        connection.Abort();
        check(await SettlesAsync(() => events.Left.Count == 1, TimeSpan.FromSeconds(20)).ConfigureAwait(false)
            && !client.IsInGroup && events.Left[0] == group,
            "a refused token drops the membership that was riding on it");
    }

    /// <summary>Drops the socket and hands back the connection that replaces it.
    ///
    /// <para>The waits are long because the production reconnect ladder is: it doubles on every
    /// drop that did not last a minute — 2, 4, 8, 16 and then 30 seconds, which is its ceiling —
    /// and this fixture drops the socket five times. A wait sized for the first rung would fail
    /// the fifth leg for a reason that has nothing to do with what it asserts.</para></summary>
    private static async Task<LiveSessionFixture.ServerConnection> DropAndResumeAsync(
        LiveSessionFixture.FakeJellyfinServer server, LiveSessionFixture.ServerConnection connection)
    {
        connection.Abort();
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return await server.NextConnectionAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    /// <summary>Joins and waits for the server's confirmation. Three legs above need the client
    /// back in the group first and none of them is about the join itself.</summary>
    private static async Task BackInGroupAsync(Action<bool, string> check, SyncPlayClient client,
        LiveSessionFixture.ServerConnection connection, string profileKey, Guid group, string what)
    {
        await client.JoinAsync(profileKey, group).ConfigureAwait(false);
        await connection.SendAsync(GroupJoinedFrame(group, "Movie night", "Paused", "bob")).ConfigureAwait(false);
        check(await SettlesAsync(() => client.IsInGroup, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            $"the client is back in the group before the {what} leg");
    }

    // The two updates that mean the join FAILED. Its own server and its own client, because what is
    // being asserted is the ABSENCE of a membership, and a leg running after a successful join
    // elsewhere could not tell "never joined" from "joined and then left".
    private static async Task CheckJoinRefusalAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-refusal-profile";
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the refusal fixture session connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        using var client = new SyncPlayClient(live, key => key == profileKey ? service : null);
        var events = new Recorder(client);
        live.StartProfile(profileKey);
        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        foreach (var (type, kind) in new[]
        {
            ("GroupDoesNotExist", SyncPlayNoticeKind.GroupDoesNotExist),
            ("LibraryAccessDenied", SyncPlayNoticeKind.LibraryAccessDenied),
        })
        {
            var group = Guid.NewGuid();
            check(await client.JoinAsync(profileKey, group).ConfigureAwait(false),
                $"the {type} join POST is accepted");
            await connection.SendAsync(LiveSessionFixture.GroupUpdate(group, type, $"\"{group:D}\"")).ConfigureAwait(false);
            check(await SettlesAsync(() => events.Notices.Any(notice => notice.Kind == kind),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false),
                $"{type} surfaces as a notice");
            // Settled first, then asserted: the notice above proves the frame has been handled, so
            // an in-group reading here is a membership that outlived its own refusal rather than
            // one that simply had not arrived yet.
            check(!client.IsInGroup && client.GroupId is null
                && events.Joined.Count == 0 && events.Left.Count == 0,
                $"{type} leaves no membership and no GroupJoined behind");
        }
    }

    /// <summary>Every event the client raises, recorded. They arrive on the socket's receive loop
    /// and are read from the fixture's own thread, so each list is guarded — an unsynchronised
    /// <c>List&lt;T&gt;</c> read while another thread appends is how a leg fails for a reason that
    /// has nothing to do with what it is testing.</summary>
    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<SyncPlayGroupInfo> _joined = [];
        private readonly List<Guid> _left = [];
        private readonly List<SyncPlayQueueUpdate> _queues = [];
        private readonly List<SyncPlayStateUpdate> _states = [];
        private readonly List<SyncPlayNotice> _notices = [];
        private readonly List<SyncPlayAction> _actions = [];

        public Recorder(SyncPlayClient client)
        {
            client.GroupJoined += info => Add(_joined, info);
            client.GroupLeft += id => Add(_left, id);
            client.QueueChanged += queue => Add(_queues, queue);
            client.StateChanged += state => Add(_states, state);
            client.Notice += notice => Add(_notices, notice);
            client.ActionRequired += action => Add(_actions, action);
        }

        public IReadOnlyList<SyncPlayGroupInfo> Joined => Snapshot(_joined);
        public IReadOnlyList<Guid> Left => Snapshot(_left);
        public IReadOnlyList<SyncPlayQueueUpdate> Queues => Snapshot(_queues);
        public IReadOnlyList<SyncPlayStateUpdate> States => Snapshot(_states);
        public IReadOnlyList<SyncPlayNotice> Notices => Snapshot(_notices);
        public IReadOnlyList<SyncPlayAction> Actions => Snapshot(_actions);

        private void Add<T>(List<T> target, T value)
        {
            lock (_gate)
                target.Add(value);
        }

        private List<T> Snapshot<T>(List<T> source)
        {
            lock (_gate)
                return [.. source];
        }
    }

    /// <summary>A <c>GroupJoined</c> payload as the server writes it.</summary>
    private static string GroupJoinedFrame(Guid group, string name, string state, params string[] participants)
    {
        var names = string.Join(",", participants.Select(participant => $"\"{participant}\""));
        return LiveSessionFixture.GroupUpdate(group, "GroupJoined",
            $$"""{"GroupId":"{{group:N}}","GroupName":"{{name}}","State":"{{state}}","Participants":[{{names}}],"LastUpdatedAt":"2026-09-16T20:00:00.0000000Z"}""");
    }

    /// <summary>A <c>PlayQueue</c> payload with one entry playing. Library ids use Jellyfin's
    /// compact format; <paramref name="dashless"/> exercises both spellings of the string
    /// playlist entry id.</summary>
    private static string QueueFrame(Guid group, DateTimeOffset lastUpdate, Guid playlistItemId,
        bool dashless = false)
        => LiveSessionFixture.GroupUpdate(group, "PlayQueue",
            $$"""{"Reason":"NewPlaylist","LastUpdate":"{{Iso(lastUpdate.UtcDateTime)}}","Playlist":[{"ItemId":"{{Guid.NewGuid():N}}","PlaylistItemId":"{{(dashless ? playlistItemId.ToString("N") : playlistItemId.ToString("D"))}}"}],"PlayingItemIndex":0,"StartPositionTicks":0,"IsPlaying":false,"ShuffleMode":"Sorted","RepeatMode":"RepeatNone"}""");

    private static string Iso(DateTime utc)
        => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One field of a request body, matched case-insensitively: Kiota writes camelCase
    /// while the server reads PascalCase, and which of the two this SDK emits is not what any leg
    /// here is about.</summary>
    private static JsonElement Field(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject()
            .First(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value.Clone();
    }

    /// <summary>The <c>When</c> a request carries, as a UTC instant.</summary>
    private static DateTime When(string body)
        => DateTimeOffset.Parse(Field(body, "When").GetString() ?? "",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime;

    private static LiveSessionFixture.HttpCapture LastRequest(
        LiveSessionFixture.FakeJellyfinServer server, string pathSuffix)
        => server.Requests.LastOrDefault(request =>
                request.Path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"no request reached {pathSuffix}");

    private static async Task<bool> SettlesAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>A command as the server would have sent it.</summary>
    private static SyncPlayCommandMessage Command(string command, DateTime when, long positionTicks,
        Guid playlistItemId, DateTime emittedAt)
        => new(Guid.NewGuid(), playlistItemId, when, positionTicks, command, emittedAt);

    /// <summary>A <see cref="ServerClock"/> whose four timestamps are all scripted: the fetch
    /// advances the fixture's own clock by the round trip it is pretending to have taken, so
    /// <c>t0</c> and <c>t3</c> are as controlled as the two the server sends.</summary>
    private sealed class ScriptedClock : IDisposable
    {
        private readonly Queue<(long DelayMs, int SkewSeconds)> _script;
        private readonly List<long> _pings = [];
        private DateTime _now = Base;

        public ScriptedClock(IEnumerable<(long DelayMs, int SkewSeconds)> script)
        {
            _script = new Queue<(long, int)>(script);
            Clock = new ServerClock(_ => Task.FromResult(Next()), (ping, _) =>
            {
                _pings.Add(ping);
                return Task.CompletedTask;
            }, () => _now);
        }

        public ServerClock Clock { get; }

        public IReadOnlyList<long> Pings => _pings;

        /// <summary>How many times the clock has asked for a measurement. The driver asserts this
        /// rather than the number of legs handed out, because the only other thing that can ask is
        /// a real timer tick — and one arriving after the script was spent takes nothing, so a
        /// count of legs handed out could not see it.</summary>
        public int Attempts { get; private set; }

        public void Dispose() => Clock.Dispose();

        /// <summary>Symmetric legs, so the measured offset is exactly the scripted skew and the
        /// measured delay is exactly the scripted one — the selection rule is what is under test
        /// here, not the arithmetic, which the hand-built legs above own.
        ///
        /// <para>Nothing once the script is spent, rather than an empty-queue throw. The real
        /// ServerClock re-arms its timer after every measurement, including the ones driven from
        /// here, so a tick can arrive between two scripted legs; throwing into it would unwind
        /// inside MeasureAsync's swallowing catch and the corruption would surface as an
        /// unexplained selection failure several legs later. A null is "no answer from the server",
        /// which the clock already handles and which changes nothing it has already measured.</para></summary>
        private ServerTimeSample? Next()
        {
            Attempts++;
            if (_script.Count == 0)
                return null;
            var leg = _script.Dequeue();
            var t0 = _now;
            var t1 = t0 + TimeSpan.FromMilliseconds(leg.DelayMs / 2.0) + TimeSpan.FromSeconds(leg.SkewSeconds);
            _now = t0 + TimeSpan.FromMilliseconds(leg.DelayMs);
            return new ServerTimeSample(t1, t1);
        }
    }
}
