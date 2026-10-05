using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LightWeaver.Jellyfin;

/// <summary>
/// Two real LightWeaver clients in one SyncPlay group, against one synthetic server.
///
/// <para>This process is the controller. It hosts <see cref="SyncPlayTwoClientServer"/> and starts
/// two copies of this same harness executable in <see cref="SyncPlayTwoClientWorker.Mode"/>, each
/// with its own temporary app-data root — so each persists its own device id, signs in through the
/// normal login call as the same synthetic user, is issued its own token and opens its own session
/// socket. The controller drives them over named pipes and asserts on three things: what the
/// SERVER received from which session, what each client's real player reports, and what each
/// client's windows DISPLAY — the status beside the SyncPlay icon, the flyout's group and
/// participants, the overlay's title block and the queue popup's current-entry mark, read off the
/// visual tree. Every step is also captured from the screen, both clients, at the worker's capture size, into
/// <see cref="CaptureDirectory"/> as <c>syncplay-two-client-NN-step-A.png</c> / <c>…-B.png</c>.</para>
///
/// <para>The library is one series of three episodes, each its own visibly different clip, so a
/// capture shows which entry is playing. Starting the first from its detail view queues all three
/// into the group, and the queue is then walked from both clients through the overlay: Next,
/// Previous, a jump from the queue popup, a removal from it, and Back, which stops the whole group
/// and keeps both members in it.</para>
///
/// <para>Guest-only (<c>--sync-play-two-client</c> opens two windows). The server half has a
/// windowless self-test, <c>--sync-play-two-client-server</c>, which drives the same server with raw
/// HTTP and WebSocket clients and can run anywhere.</para>
///
/// <para>Bounded throughout: every wait has a deadline and the whole run has one, so a client that
/// never answers is a named failure rather than a hung guest. Teardown asks each worker to close
/// its window first and kills only the two processes this run started, by the handle captured at
/// launch, if that fails.</para>
/// </summary>
internal static class SyncPlayTwoClientFixture
{
    public const string Mode = "--sync-play-two-client";
    public const string ServerMode = "--sync-play-two-client-server";

    /// <summary>Set to <c>1</c> to run the workers' players with no video output, for a guest that
    /// cannot render it. The captures then show the UI over an empty video area.</summary>
    public const string NoVideoSwitch = "LIGHTWEAVER_TWO_CLIENT_NO_VIDEO";

    /// <summary>Worker logs.</summary>
    public static readonly string OutputDirectory =
        Path.Combine(Path.GetTempPath(), "lightweaver-tests", "syncplay-two-client");

    /// <summary>Every step's captures, as <c>syncplay-two-client-NN-step-A.png</c>. Flat in the
    /// evidence directory rather than under <see cref="OutputDirectory"/>: the operator's
    /// fetch-guest-evidence.ps1 lists that one directory and nothing below it.</summary>
    public static readonly string CaptureDirectory = Path.Combine(Path.GetTempPath(), "lightweaver-tests");

    public const string CapturePrefix = "syncplay-two-client-";

    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleDeadline = TimeSpan.FromSeconds(20);
    // About three minutes on a guest: some thirty captured steps, six of them loading a new entry.
    private static readonly TimeSpan TotalDeadline = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan CloseDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Check 5: a group seek lands both players this close to the target.</summary>
    private const double SeekTolerance = 0.25;

    /// <summary>Check 6: after a scheduled resume, the two positions compared at one instant.</summary>
    private const double ResumeDisagreement = 0.75;

    /// <summary>A pause stops each client where it is and seeks only past the scheduler's 400 ms
    /// threshold, so after a pause echo a client may sit up to that far from the server's stop
    /// point, plus a frame and the echo's delivery skew.</summary>
    private const double PauseTolerance = 0.6;

    /// <summary>How still a paused player must stay while the echo is held.</summary>
    private const double StillTolerance = 0.15;

    private const string RunPrefix = "lw-two-client-";

    private const string GroupName = "Two-client regression";

    /// <summary>One lavfi source per episode, each unmistakable at a glance: the moving test card,
    /// HD colour bars, and the older test pattern with its frame counter.</summary>
    private static readonly string[] ClipSources = ["testsrc2", "smptehdbars", "testsrc"];

    private static readonly Regex CaptureName = new(@"^syncplay-two-client-\d\d-.+-[AB]\.png$");

    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var run = new Scenario();
        var passed = false;
        try
        {
            await run.ExecuteAsync().ConfigureAwait(false);
            passed = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: sync play two-client: {ex.GetType().Name}: {ex.Message}");
        }
        var cleaned = await run.TeardownAsync().ConfigureAwait(false);
        var ok = passed && cleaned;
        Console.WriteLine(ok
            ? $"RESULT: PASS ({run.Assertions} sync play two-client assertions)"
            : $"RESULT: FAIL ({run.Assertions} sync play two-client assertions passed before the failure)");
        return ok ? 0 : 1;
    }

    // ---- the scenario ---------------------------------------------------------------------

    private sealed class Scenario
    {
        private readonly CancellationTokenSource _total = new(TotalDeadline);
        private SyncPlayTwoClientServer? _server;
        private Worker? _a;
        private Worker? _b;
        private string? _runDir;
        private Guid _group;
        private int _captures;

        public int Assertions { get; private set; }

        private SyncPlayTwoClientServer Server => _server!;
        private Worker A => _a!;
        private Worker B => _b!;

        private void Check(bool valid, string name, Func<string>? detail = null)
        {
            if (!valid)
                throw new InvalidOperationException(detail is null ? name : $"{name} [{detail()}]");
            Assertions++;
            Console.WriteLine("PASS: " + name);
        }

        public async Task ExecuteAsync()
        {
            await StartAsync().ConfigureAwait(false);
            await CheckIndependentClientsAsync().ConfigureAwait(false);
            await CheckGroupDiscoveryAsync().ConfigureAwait(false);
            await ShowStepAsync("joined-idle", Browsing(Status("Idle")), Browsing(Status("Idle"))).ConfigureAwait(false);
            await CheckFlyoutsAsync("joined-idle-flyout", "Idle").ConfigureAwait(false);
            await CheckGroupItemStartAsync().ConfigureAwait(false);
            await ShowGroupStepAsync("queue-started", "Paused").ConfigureAwait(false);
            await CheckQueuesAsync("queue-started-queue").ConfigureAwait(false);

            // Checks 4-6, on the first entry. The order keeps the clip's 30 s clear of EOF: two
            // paused seeks, then playback from 4 s for a handful of short legs.
            await EchoStepAsync("A seeks to 12 s while paused", "a-seeks-12", A, "seek", "12", "Seek", Kind.Seek, 12)
                .ConfigureAwait(false);
            await EchoStepAsync("B seeks to 4 s while paused", "b-seeks-4", B, "seek", "4", "Seek", Kind.Seek, 4)
                .ConfigureAwait(false);
            await EchoStepAsync("A resumes", "a-resumes", A, "toggle-pause", null, "Unpause", Kind.Resume)
                .ConfigureAwait(false);
            await EchoStepAsync("B pauses", "b-pauses", B, "toggle-pause", null, "Pause", Kind.Pause).ConfigureAwait(false);
            await EchoStepAsync("B resumes", "b-resumes", B, "toggle-pause", null, "Unpause", Kind.Resume)
                .ConfigureAwait(false);
            await EchoStepAsync("A pauses", "a-pauses", A, "toggle-pause", null, "Pause", Kind.Pause).ConfigureAwait(false);
            await EchoStepAsync("A resumes again", "a-resumes-again", A, "toggle-pause", null, "Unpause", Kind.Resume)
                .ConfigureAwait(false);

            // The queue, from both clients and in both directions. Each move restarts an entry at
            // zero and resumes the group, so no entry gets near its end.
            await MoveStepAsync("A presses Next", "a-next", A, "next", null, "NextItem", 1).ConfigureAwait(false);
            await MoveStepAsync("B presses Next", "b-next", B, "next", null, "NextItem", 2).ConfigureAwait(false);
            await MoveStepAsync("A presses Previous", "a-previous", A, "previous", null, "PreviousItem", 1)
                .ConfigureAwait(false);
            await MoveStepAsync("B presses Previous", "b-previous", B, "previous", null, "PreviousItem", 0)
                .ConfigureAwait(false);
            await MoveStepAsync("A picks the third entry in the queue popup", "a-selects-third", A, "queue-select", "2",
                "SetPlaylistItem", 2).ConfigureAwait(false);
            await RemoveStepAsync("B removes the second entry in the queue popup", "b-removes-second", B, 1)
                .ConfigureAwait(false);
            await StopStepAsync("A presses Back", "group-stopped", A).ConfigureAwait(false);

            // Something playing again, so the leave below is a leave WHILE playing.
            var entry = await CheckRestartAsync("restarted").ConfigureAwait(false);
            await EchoStepAsync("A resumes the new queue", "restarted-playing", A, "toggle-pause", null, "Unpause",
                Kind.Resume).ConfigureAwait(false);

            await CheckLeaveWhilePlayingAsync(entry).ConfigureAwait(false);
        }

        // ---- startup ----------------------------------------------------------------------

        private async Task StartAsync()
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "PlaybackHarness.exe");
            Check(File.Exists(exe), "the Debug harness executable the workers run is present",
                () => "missing " + exe);
            Check(LibMpvLoads(), "libmpv-2.dll loads for the workers' players",
                () => "libmpv-2.dll is neither next to the harness nor on PATH");
            var clips = new List<string>();
            foreach (var source in ClipSources)
            {
                var clip = BuildClip(source, out var clipFailure);
                Check(clip is not null, $"ffmpeg built the 30 s {source} clip", () => clipFailure ?? "");
                clips.Add(clip!);
            }

            _server = new SyncPlayTwoClientServer(clips);
            _server.Diagnostic += line => Console.WriteLine(line);
            _runDir = Path.Combine(Path.GetTempPath(), RunPrefix + Guid.NewGuid().ToString("N"));
            var logs = OutputDirectory;
            Directory.CreateDirectory(logs);
            // Last run's captures go, so every image in the directory is this run's.
            Directory.CreateDirectory(CaptureDirectory);
            foreach (var stale in Directory.EnumerateFiles(CaptureDirectory, CapturePrefix + "*.png")
                         .Where(f => CaptureName.IsMatch(Path.GetFileName(f))))
                File.Delete(stale);
            var rootA = Path.Combine(_runDir, "A");
            var rootB = Path.Combine(_runDir, "B");
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            // Side by side on a 1080p guest, so neither shows up in the other's captures; each
            // worker keeps itself on the work area and takes the foreground for its own captures.
            _a = Worker.Launch(exe, "A", rootA, 0, 0, logs);
            _b = Worker.Launch(exe, "B", rootB, 960, 0, logs);
            Console.WriteLine($"two-client: workers A pid={_a.Pid} and B pid={_b.Pid}; logs in {logs}");
            await Task.WhenAll(_a.ConnectAsync(StartupDeadline, _total.Token),
                _b.ConnectAsync(StartupDeadline, _total.Token)).ConfigureAwait(false);
        }

        // ---- check 1 ----------------------------------------------------------------------

        private async Task CheckIndependentClientsAsync()
        {
            var helloA = A.Hello!;
            var helloB = B.Hello!;
            Check(helloA.Pid == A.Pid && helloB.Pid == B.Pid && A.Pid != B.Pid
                && A.Pid != Environment.ProcessId && B.Pid != Environment.ProcessId
                && !A.Process.HasExited && !B.Process.HasExited,
                "two live worker processes with distinct PIDs",
                () => $"A={helloA.Pid}/{A.Pid} B={helloB.Pid}/{B.Pid} controller={Environment.ProcessId}");
            Check(SamePath(helloA.AppRoot, A.Root) && SamePath(helloB.AppRoot, B.Root)
                && !SamePath(A.Root, B.Root),
                "each worker resolved its own temporary app-data root",
                () => $"A={helloA.AppRoot} B={helloB.AppRoot}");
            Check(helloA.MainWindows == 1 && helloB.MainWindows == 1,
                "each worker shows exactly one production MainWindow",
                () => $"A={helloA.MainWindows} B={helloB.MainWindows}");

            var logins = await Task.WhenAll(
                A.SendAsync("login", Server.Origin, CommandDeadline, _total.Token),
                B.SendAsync("login", Server.Origin, CommandDeadline, _total.Token)).ConfigureAwait(false);
            Check(logins.All(r => r.Ok && r.Result is null),
                "both workers sign in through AppViewModel.LoginAsync",
                () => string.Join("; ", logins.Select(r => r.Error ?? r.Result ?? "ok")));
            var (a, b) = await WaitBothAsync((x, y) => x.State == "Browse" && y.State == "Browse"
                && x.SessionKey is not null && y.SessionKey is not null, StartupDeadline,
                "both shells reach Browse after sign-in").ConfigureAwait(false);

            Check(Guid.TryParse(a.DeviceId, out _) && Guid.TryParse(b.DeviceId, out _)
                && !string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase)
                && PersistedDevice(A.Root) == a.DeviceId && PersistedDevice(B.Root) == b.DeviceId,
                "each worker persisted a device id of its own under its own root",
                () => $"A={a.DeviceId} B={b.DeviceId}");
            A.Token = Server.Http.TokenForDevice(a.DeviceId);
            B.Token = Server.Http.TokenForDevice(b.DeviceId);
            Check(A.Token is not null && B.Token is not null && A.Token != B.Token,
                "the server issued each device a token of its own",
                () => $"A has token={A.Token is not null} B has token={B.Token is not null}");
            Check(a.UserId == Server.UserId && b.UserId == Server.UserId,
                "both workers are signed in as the same synthetic user");

            await UntilAsync(() => Socket(A.Token!) is not null && Socket(B.Token!) is not null, StartupDeadline,
                "both session sockets open").ConfigureAwait(false);
            var socketA = Socket(A.Token!)!;
            var socketB = Socket(B.Token!)!;
            Check(!ReferenceEquals(socketA, socketB) && socketA.Token == A.Token && socketB.Token == B.Token,
                "each worker holds its own session socket, opened with its own token");
        }

        private LiveSessionFixture.ServerConnection? Socket(string token)
            => Server.Http.Connections.LastOrDefault(c => c.IsOpen && c.Token == token);

        // ---- check 2 ----------------------------------------------------------------------

        private async Task CheckGroupDiscoveryAsync()
        {
            const string name = GroupName;
            var created = await A.SendAsync("create", name, CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(created is { Ok: true, Result: "True" }, "A creates a group through the flyout's create call",
                () => created.Error ?? created.Result ?? "");
            var a = await WaitAsync(A, s => s.InGroup && s.GroupId is not null, SettleDeadline,
                "A holds the created group").ConfigureAwait(false);
            _group = a.GroupId!.Value;
            Check(Server.View(_group) is { Members: [var only] } && only == A.Token,
                "the server group A created has A's session as its only member");

            var listed = await B.SendAsync("list", null, CommandDeadline, _total.Token).ConfigureAwait(false);
            var groups = listed.Ok && listed.Result is { } json
                ? JsonSerializer.Deserialize<List<SyncPlayGroupSummary>>(json) ?? [] : [];
            Check(groups.Any(g => g.GroupId == _group && g.GroupName == name),
                "B discovers A's group through List", () => listed.Error ?? listed.Result ?? "");

            var mark = Server.SyncRequestCount;
            var joined = await B.SendAsync("join", _group.ToString(), CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(joined is { Ok: true, Result: "True" }, "B joins it through the flyout's join call",
                () => joined.Error ?? joined.Result ?? "");
            var b = await WaitAsync(B, s => s.InGroup && s.GroupId == _group, SettleDeadline,
                "B holds A's group").ConfigureAwait(false);
            Check(Server.CommandRequests(B.Token!, mark) is [{ Route: "Join" } join]
                && SyncPlayTwoClientServer.BodyString(join, "GroupId") is { } posted
                && Guid.TryParse(posted, out var postedId) && postedId == _group
                && Server.CommandRequests(A.Token!, mark).Count == 0,
                "B's join is one Join naming A's group, and A sent nothing");
            Check(Server.View(_group) is { Members: { Count: 2 } members }
                && members.Contains(A.Token!) && members.Contains(B.Token!),
                "the server holds two independent sessions in that group");
            Check(b.GroupId == a.GroupId, "B is in the same group A created");
            // The same user twice is one display name. Asserted so the reason the membership checks
            // above go to the server is written down, not because the names are the point.
            a = await A.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(a.Participants.Count == 1 && b.Participants.Count == 1,
                "the participant names dedupe to one, so membership is asserted per server session",
                () => $"A=[{string.Join(",", a.Participants)}] B=[{string.Join(",", b.Participants)}]");
        }

        // ---- check 3 ----------------------------------------------------------------------

        private async Task<Guid> CheckGroupItemStartAsync()
        {
            var first = Server.Episodes[0];
            var item = first.Id;
            var mark = Server.SyncRequestCount;
            var played = await A.SendAsync("play", item.ToString(), CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(played.Ok, "A starts the first episode through the detail view's Play handler", () => played.Error ?? "");
            var (a, b) = await WaitBothAsync((x, y) => Server.View(_group) is { State: "Paused", PlaylistItemId: { } minted }
                    && Loaded(x, item, minted) && Loaded(y, item, minted),
                StartupDeadline, "both clients load the group's first entry and the group settles paused").ConfigureAwait(false);
            var view = Server.View(_group)!;
            var entry = view.PlaylistItemId!.Value;

            Check(Server.CommandRequests(A.Token!, mark) is [{ Route: "SetNewQueue" } queue]
                && SyncPlayTwoClientServer.BodyItems(queue, "PlayingQueue").SequenceEqual(Server.Episodes.Select(e => e.Id))
                && (SyncPlayTwoClientServer.BodyLong(queue, "PlayingItemPosition") ?? 0) == 0
                && Server.CommandRequests(B.Token!, mark).Count == 0,
                "A's start reaches the group as exactly one SetNewQueue of the series' three episodes from the first, and B sent nothing",
                () => Routes(mark));
            Check(view.Queue.Count == 3 && view.Queue.Select(e => e.Entry).Distinct().Count() == 3 && view.PlayingIndex == 0
                && view.Queue[0].Entry == entry,
                "the server minted a PlaylistItemId of its own for each of the three entries");
            Check(a.PlaylistItemId == entry && b.PlaylistItemId == entry && a.PlayingItemId == item
                && b.PlayingItemId == item,
                "both clients are on the server-minted PlaylistItemId for the same episode");
            var negotiated = Server.Http.Requests
                .Where(r => r.Method == "POST" && r.Path.EndsWith($"/Items/{item:D}/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Token).ToHashSet();
            Check(negotiated.Contains(A.Token) && negotiated.Contains(B.Token),
                "both clients negotiated playback of that episode with the server");
            CheckStreamed(first, a, b, "both players streamed the first episode over HTTP, each with its own token");
            CheckReady(mark, entry, "both real players reported Ready for that entry");
            Check(a.Paused && b.Paused && a.Position < 1 && b.Position < 1,
                "the group's first entry sits paused at its start on both clients",
                () => $"A paused={a.Paused} {a.Position:0.000}s B paused={b.Paused} {b.Position:0.000}s");
            return entry;
        }

        private static bool Loaded(TwoClientSnapshot s, Guid item, Guid entry)
            => s.State == "Playing" && s.PlaylistItemId == entry && s.PlayingItemId == item
                && s.HasPosition && !s.Loading && s.Duration > 0 && s.Paused;

        /// <summary>On an entry with its clip loaded off the server, paused or not.</summary>
        private static bool OnEntry(TwoClientSnapshot s, SyncPlayTwoClientServer.Episode episode, Guid entry)
            => s.State == "Playing" && s.PlaylistItemId == entry && s.PlayingItemId == episode.Id
                && s.HasPosition && !s.Loading && s.Duration > 0
                && s.Path?.Contains($"/Videos/{episode.Id:D}/stream", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>Both players fetched this episode's stream, with their own tokens, and have it
        /// loaded.</summary>
        private void CheckStreamed(SyncPlayTwoClientServer.Episode episode, TwoClientSnapshot a, TwoClientSnapshot b,
            string name)
        {
            var streamed = Server.Http.MediaServes
                .Where(s => s.Method == "GET" && s.Status is 200 or 206
                    && s.Path.Contains(episode.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Token).ToHashSet();
            var stream = $"/Videos/{episode.Id:D}/stream";
            Check(streamed.Contains(A.Token) && streamed.Contains(B.Token)
                && a.Path?.Contains(stream, StringComparison.OrdinalIgnoreCase) == true
                && b.Path?.Contains(stream, StringComparison.OrdinalIgnoreCase) == true, name,
                () => $"A path={a.Path} B path={b.Path} streamed by A={streamed.Contains(A.Token)} B={streamed.Contains(B.Token)}");
        }

        private void CheckReady(int mark, Guid entry, string name)
        {
            var ready = Server.SyncRequestsSince(mark).Where(r => r.Route == "Ready"
                && Guid.TryParse(SyncPlayTwoClientServer.BodyString(r, "PlaylistItemId"), out var id) && id == entry)
                .Select(r => r.Token).ToHashSet();
            Check(ready.Contains(A.Token!) && ready.Contains(B.Token!), name, () => Routes(mark));
        }

        // ---- checks 4, 5 and 6 ------------------------------------------------------------

        private enum Kind { Pause, Resume, Seek }

        private async Task EchoStepAsync(string label, string step, Worker initiator, string op, string? arg,
            string route, Kind kind, double target = 0)
        {
            var peer = ReferenceEquals(initiator, A) ? B : A;
            await UntilAsync(() => Server.View(_group) is { State: not "Waiting" } && Server.HeldCount == 0,
                SettleDeadline, $"{label}: the group is settled first").ConfigureAwait(false);
            var (preA, preB) = await BothAsync().ConfigureAwait(false);
            var requests = Server.SyncRequestCount;
            var frames = Server.FrameCount;
            var emitted = Server.EmittedCount;

            Server.HoldEcho();
            try
            {
                var reply = await initiator.SendAsync(op, arg, CommandDeadline, _total.Token).ConfigureAwait(false);
                Check(reply.Ok, $"{label}: {initiator.Role} runs the player view model's {op}",
                    () => reply.Error ?? "");
                await UntilAsync(() => Server.CommandRequests(initiator.Token!, requests).Any(r => r.Route == route)
                        && Server.HeldCount > 0, CommandDeadline,
                    $"{label}: the server receives {route} from {initiator.Role}").ConfigureAwait(false);
                var (a1, b1) = await BothAsync().ConfigureAwait(false);
                await Task.Delay(400, _total.Token).ConfigureAwait(false);
                var (a2, b2) = await BothAsync().ConfigureAwait(false);
                Check(Unchanged(preA, a1, a2) && Unchanged(preB, b1, b2)
                    && Server.FramesTo(A.Token!, frames).Count == 0 && Server.FramesTo(B.Token!, frames).Count == 0,
                    $"{label}: with the echo held, neither client's player moved",
                    () => $"A {Describe(preA)} -> {Describe(a1)} -> {Describe(a2)}; "
                        + $"B {Describe(preB)} -> {Describe(b1)} -> {Describe(b2)}");
                Check(ExactlyOne(initiator, peer, requests, route, kind == Kind.Seek ? Ticks(target) : null),
                    $"{label}: exactly one {route} from {initiator.Role}, no group request from {peer.Role}",
                    () => Routes(requests));
            }
            finally
            {
                Server.ReleaseEcho();
            }

            switch (kind)
            {
                case Kind.Seek:
                    {
                        var (a, b) = await WaitBothAsync((x, y) => Server.View(_group) is { State: "Paused" }
                                && Near(x, target, SeekTolerance) && Near(y, target, SeekTolerance),
                            SettleDeadline, $"{label}: both clients converge on {target:0} s", fail: false)
                            .ConfigureAwait(false);
                        Check(Near(a, target, SeekTolerance) && Near(b, target, SeekTolerance),
                            $"{label}: both land within {SeekTolerance * 1000:0} ms of {target:0} s, still paused",
                            () => $"A {Describe(a)} B {Describe(b)}");
                        break;
                    }
                case Kind.Pause:
                    {
                        var (a, b) = await WaitBothAsync((x, y) => Server.View(_group) is { State: "Paused" }
                                && x is { Paused: true, Loading: false } && y is { Paused: true, Loading: false },
                            SettleDeadline, $"{label}: both clients pause on the echo").ConfigureAwait(false);
                        await Task.Delay(300, _total.Token).ConfigureAwait(false);
                        (a, b) = await BothAsync().ConfigureAwait(false);
                        var stop = Server.EmittedSince(emitted).LastOrDefault(c => c.Command == "Pause");
                        var at = stop is null ? double.NaN : stop.PositionTicks / (double)TimeSpan.TicksPerSecond;
                        Check(stop is not null && a.Paused && b.Paused
                            && Math.Abs(a.Position - at) <= PauseTolerance && Math.Abs(b.Position - at) <= PauseTolerance
                            && Math.Abs(a.Position - b.Position) <= PauseTolerance,
                            $"{label}: both pause within {PauseTolerance * 1000:0} ms of the group's stop point",
                            () => $"stop={at:0.000}s A {Describe(a)} B {Describe(b)}");
                        break;
                    }
                case Kind.Resume:
                    await CheckScheduledResumeAsync(label, emitted).ConfigureAwait(false);
                    break;
            }
            Check(Server.CommandRequests(peer.Token!, requests).Count == 0
                && Server.CommandRequests(initiator.Token!, requests).Count == 1,
                $"{label}: after the echo, still one request from {initiator.Role} and none from {peer.Role}",
                () => Routes(requests));
            await ShowGroupStepAsync(step, kind == Kind.Resume ? "Playing" : "Paused").ConfigureAwait(false);
        }

        /// <summary>Check 6. Positions are compared at one instant: each sample is moved to the
        /// later of the two capture instants at playback rate 1, so the IPC round trip between
        /// asking A and asking B is not mistaken for disagreement.</summary>
        private async Task CheckScheduledResumeAsync(string label, int emitted)
        {
            var start = Server.EmittedSince(emitted).LastOrDefault(c => c.Command == "Unpause");
            Check(start is not null && start.Recipients.Contains(A.Token!) && start.Recipients.Contains(B.Token!)
                && start.When - start.EmittedAt >= SyncPlayTwoClientServer.DefaultPing - TimeSpan.FromMilliseconds(1),
                $"{label}: the server scheduled the resume for both, at least 500 ms ahead",
                () => start is null ? "no Unpause emitted" : $"lead={(start.When - start.EmittedAt).TotalMilliseconds:0} ms");
            await WaitBothAsync((x, y) => x is { Paused: false, HasPosition: true } && y is { Paused: false, HasPosition: true },
                SettleDeadline, $"{label}: both clients start playing").ConfigureAwait(false);
            var samples = new List<(TwoClientSnapshot A, TwoClientSnapshot B)>();
            for (var i = 0; i < 4; i++)
            {
                samples.Add(await BothAsync().ConfigureAwait(false));
                await Task.Delay(250, _total.Token).ConfigureAwait(false);
            }
            var worst = samples.Max(pair => Disagreement(pair.A, pair.B));
            Check(worst < ResumeDisagreement,
                $"{label}: timestamp-adjusted disagreement stays under {ResumeDisagreement * 1000:0} ms",
                () => $"worst={worst * 1000:0} ms");
            var first = samples[0];
            var last = samples[^1];
            Check(samples.All(p => !p.A.Paused && !p.B.Paused)
                && last.A.Position - first.A.Position > 0.4 && last.B.Position - first.B.Position > 0.4,
                $"{label}: both clients advance after the scheduled start",
                () => $"A {first.A.Position:0.000}->{last.A.Position:0.000} B {first.B.Position:0.000}->{last.B.Position:0.000}");
            var schedule = start!;
            var off = samples.SelectMany(p => new[] { p.A, p.B }).Max(s => ScheduleOffset(s, schedule));
            Check(off < ResumeDisagreement,
                $"{label}: each client is where the group's schedule puts it",
                () => $"worst={off * 1000:0} ms from position+elapsed-since-When");
        }

        private bool ExactlyOne(Worker initiator, Worker peer, int mark, string route, long? ticks)
        {
            var mine = Server.CommandRequests(initiator.Token!, mark);
            if (mine is not [{ } request] || request.Route != route
                || Server.CommandRequests(peer.Token!, mark).Count != 0)
                return false;
            return ticks is { } expected
                ? SyncPlayTwoClientServer.BodyLong(request, "PositionTicks") == expected
                : string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim() == "{}";
        }

        // ---- moving within the queue -------------------------------------------------------

        /// <summary>Next, Previous or a queue-popup jump from one client, while the group plays.
        /// Behind the barrier: the initiator's request is exactly one <paramref name="route"/>
        /// naming the entry it believes is playing (or, for a jump, the one picked), the peer sent
        /// nothing, and neither player nor overlay moved off the outgoing entry. After it: both
        /// clients on the server's entry for <paramref name="toIndex"/>, that episode's clip
        /// streamed and loaded, Ready from both, and the group's scheduled resume played in step.</summary>
        private async Task MoveStepAsync(string label, string step, Worker initiator, string op, string? arg,
            string route, int toIndex)
        {
            var peer = ReferenceEquals(initiator, A) ? B : A;
            await UntilAsync(() => Server.View(_group) is { State: "Playing" } && Server.HeldCount == 0,
                SettleDeadline, $"{label}: the group is playing first").ConfigureAwait(false);
            var view = Server.View(_group)!;
            var from = view.PlaylistItemId!.Value;
            var leaving = EpisodeOf(view.Queue[view.PlayingIndex].Item);
            var to = view.Queue[toIndex].Entry;
            var arriving = EpisodeOf(view.Queue[toIndex].Item);
            var (preA, preB) = await BothAsync().ConfigureAwait(false);
            var requests = Server.SyncRequestCount;
            var frames = Server.FrameCount;
            var emitted = Server.EmittedCount;

            Server.HoldEcho();
            try
            {
                var reply = await initiator.SendAsync(op, arg, CommandDeadline, _total.Token).ConfigureAwait(false);
                Check(reply.Ok, $"{label}: {initiator.Role} presses it in the overlay", () => reply.Error ?? "");
                await UntilAsync(() => Server.CommandRequests(initiator.Token!, requests).Any(r => r.Route == route)
                        && Server.HeldCount > 0, CommandDeadline,
                    $"{label}: the server receives {route} from {initiator.Role}").ConfigureAwait(false);
                var (a1, b1) = await BothAsync().ConfigureAwait(false);
                var (uiA, uiB) = await BothUiAsync().ConfigureAwait(false);
                await Task.Delay(400, _total.Token).ConfigureAwait(false);
                var (a2, b2) = await BothAsync().ConfigureAwait(false);
                Check(Unchanged(preA, a1, a2) && Unchanged(preB, b1, b2) && a2.PlaylistItemId == from && b2.PlaylistItemId == from
                    && Server.FramesTo(A.Token!, frames).Count == 0 && Server.FramesTo(B.Token!, frames).Count == 0,
                    $"{label}: with the echo held, both players stay on {leaving.Name} and keep playing",
                    () => $"A {Describe(preA)} -> {Describe(a1)} -> {Describe(a2)}; "
                        + $"B {Describe(preB)} -> {Describe(b1)} -> {Describe(b2)}");
                Check(uiA.Subtitle == leaving.Subtitle && uiB.Subtitle == leaving.Subtitle,
                    $"{label}: with the echo held, both overlays still show {leaving.Subtitle}",
                    () => $"A [{uiA.Subtitle}] B [{uiB.Subtitle}]");
                var named = route == "SetPlaylistItem" ? to : from;
                Check(Server.CommandRequests(initiator.Token!, requests) is [{ } request] && request.Route == route
                    && Guid.TryParse(SyncPlayTwoClientServer.BodyString(request, "PlaylistItemId"), out var echoed)
                    && echoed == named && Server.CommandRequests(peer.Token!, requests).Count == 0,
                    $"{label}: exactly one {route} from {initiator.Role} naming the {(route == "SetPlaylistItem" ? "picked" : "playing")} "
                        + $"PlaylistItemId, no group request from {peer.Role}",
                    () => Routes(requests) + " body=" + string.Join(" | ",
                        Server.CommandRequests(initiator.Token!, requests).Select(r => r.Body)));
            }
            finally
            {
                Server.ReleaseEcho();
            }

            var (a, b) = await WaitBothAsync((x, y) => OnEntry(x, arriving, to) && OnEntry(y, arriving, to),
                SettleDeadline, $"{label}: both clients load {arriving.Name}").ConfigureAwait(false);
            // A player is loaded a moment before it says so: the group resumes only once both
            // Readies are in, and everything below reads what that resume left behind.
            await UntilAsync(() => Server.View(_group) is { State: "Playing", PlaylistItemId: { } resumed } && resumed == to,
                SettleDeadline, $"{label}: the group resumes on the new entry").ConfigureAwait(false);
            Check(Server.View(_group) is { PlaylistItemId: { } now } moved && now == to
                && moved.Queue.Select(e => e.Entry).SequenceEqual(view.Queue.Select(e => e.Entry)),
                $"{label}: the server moved the group to entry {toIndex + 1} and kept every entry's id");
            CheckStreamed(arriving, a, b, $"{label}: both players streamed and loaded {arriving.Name}, each with its own token");
            CheckReady(requests, to, $"{label}: both real players reported Ready for the new entry");
            await CheckScheduledResumeAsync(label, emitted).ConfigureAwait(false);
            Check(Server.CommandRequests(peer.Token!, requests).Count == 0
                && Server.CommandRequests(initiator.Token!, requests).Count == 1,
                $"{label}: after the echo, still one request from {initiator.Role} and none from {peer.Role}",
                () => Routes(requests));
            await ShowGroupStepAsync(step, "Playing").ConfigureAwait(false);
            await CheckQueuesAsync(step + "-queue").ConfigureAwait(false);
        }

        /// <summary>A queue-popup removal of an entry that is not playing. Behind the barrier
        /// nothing changes on either client; after it both list the shorter queue, and playback
        /// went on untouched — same file, same load, still playing.</summary>
        private async Task RemoveStepAsync(string label, string step, Worker initiator, int index)
        {
            var peer = ReferenceEquals(initiator, A) ? B : A;
            await UntilAsync(() => Server.View(_group) is { State: "Playing" } && Server.HeldCount == 0,
                SettleDeadline, $"{label}: the group is playing first").ConfigureAwait(false);
            var view = Server.View(_group)!;
            var removed = view.Queue[index];
            var playing = view.PlaylistItemId!.Value;
            var (preA, preB) = await BothAsync().ConfigureAwait(false);
            var requests = Server.SyncRequestCount;
            var frames = Server.FrameCount;

            Server.HoldEcho();
            try
            {
                var reply = await initiator.SendAsync("queue-remove", index.ToString(CultureInfo.InvariantCulture),
                    CommandDeadline, _total.Token).ConfigureAwait(false);
                Check(reply.Ok, $"{label}: {initiator.Role} presses the row's remove button", () => reply.Error ?? "");
                await UntilAsync(() => Server.CommandRequests(initiator.Token!, requests).Any(r => r.Route == "RemoveFromPlaylist")
                        && Server.HeldCount > 0, CommandDeadline,
                    $"{label}: the server receives RemoveFromPlaylist from {initiator.Role}").ConfigureAwait(false);
                var (a1, b1) = await BothAsync().ConfigureAwait(false);
                await Task.Delay(400, _total.Token).ConfigureAwait(false);
                var (a2, b2) = await BothAsync().ConfigureAwait(false);
                Check(Unchanged(preA, a1, a2) && Unchanged(preB, b1, b2) && a2.Queue?.Count == 3 && b2.Queue?.Count == 3
                    && Server.FramesTo(A.Token!, frames).Count == 0 && Server.FramesTo(B.Token!, frames).Count == 0,
                    $"{label}: with the echo held, both clients still list three entries and play on",
                    () => $"A [{string.Join(", ", a2.Queue ?? [])}] B [{string.Join(", ", b2.Queue ?? [])}]");
                Check(Server.CommandRequests(initiator.Token!, requests) is [{ Route: "RemoveFromPlaylist" } request]
                    && SyncPlayTwoClientServer.BodyItems(request, "PlaylistItemIds") is [var named] && named == removed.Entry
                    && SyncPlayTwoClientServer.BodyBool(request, "ClearPlaylist") != true
                    && Server.CommandRequests(peer.Token!, requests).Count == 0,
                    $"{label}: exactly one RemoveFromPlaylist from {initiator.Role} naming only that entry, nothing from {peer.Role}",
                    () => Routes(requests) + " body=" + string.Join(" | ",
                        Server.CommandRequests(initiator.Token!, requests).Select(r => r.Body)));
            }
            finally
            {
                Server.ReleaseEcho();
            }

            var expected = view.Queue.Where(e => e != removed)
                .Select(e => (e.Entry == playing ? "*" : "") + EpisodeOf(e.Item).QueueDisplay).ToList();
            var (a, b) = await WaitBothAsync((x, y) => x.Queue?.SequenceEqual(expected) == true
                    && y.Queue?.SequenceEqual(expected) == true,
                SettleDeadline, $"{label}: both clients list the shorter queue").ConfigureAwait(false);
            Check(a is { Paused: false } && b is { Paused: false } && a.LoadGeneration == preA.LoadGeneration
                && b.LoadGeneration == preB.LoadGeneration && a.PlaylistItemId == playing && b.PlaylistItemId == playing,
                $"{label}: removing it did not interrupt or reload what both are playing",
                () => $"A {Describe(preA)} -> {Describe(a)}; B {Describe(preB)} -> {Describe(b)}");
            Check(Server.View(_group) is { State: "Playing" } after && after.PlaylistItemId == playing
                && after.Queue.SequenceEqual(view.Queue.Where(e => e != removed)),
                $"{label}: the server's queue lost that entry and kept playing the same one");
            Check(Server.CommandRequests(peer.Token!, requests).Count == 0
                && Server.CommandRequests(initiator.Token!, requests).Count == 1,
                $"{label}: after the echo, still one request from {initiator.Role} and none from {peer.Role}",
                () => Routes(requests));
            await ShowGroupStepAsync(step, "Playing").ConfigureAwait(false);
            await CheckQueuesAsync(step + "-queue").ConfigureAwait(false);
        }

        /// <summary>Back in a group is a group Stop: it stops EVERY member and leaves them all in
        /// the group. Behind the barrier neither player stops.</summary>
        private async Task StopStepAsync(string label, string step, Worker initiator)
        {
            var peer = ReferenceEquals(initiator, A) ? B : A;
            await UntilAsync(() => Server.View(_group) is { State: "Playing" } && Server.HeldCount == 0,
                SettleDeadline, $"{label}: the group is playing first").ConfigureAwait(false);
            var (preA, preB) = await BothAsync().ConfigureAwait(false);
            var requests = Server.SyncRequestCount;
            var frames = Server.FrameCount;
            var emitted = Server.EmittedCount;

            Server.HoldEcho();
            try
            {
                var reply = await initiator.SendAsync("back", null, CommandDeadline, _total.Token).ConfigureAwait(false);
                Check(reply.Ok, $"{label}: {initiator.Role} presses the overlay's Back", () => reply.Error ?? "");
                await UntilAsync(() => Server.CommandRequests(initiator.Token!, requests).Any(r => r.Route == "Stop")
                        && Server.HeldCount > 0, CommandDeadline,
                    $"{label}: the server receives Stop from {initiator.Role}").ConfigureAwait(false);
                var (a1, b1) = await BothAsync().ConfigureAwait(false);
                await Task.Delay(400, _total.Token).ConfigureAwait(false);
                var (a2, b2) = await BothAsync().ConfigureAwait(false);
                Check(Unchanged(preA, a1, a2) && Unchanged(preB, b1, b2) && a2.State == "Playing" && b2.State == "Playing"
                    && Server.FramesTo(A.Token!, frames).Count == 0 && Server.FramesTo(B.Token!, frames).Count == 0,
                    $"{label}: with the echo held, neither player stopped",
                    () => $"A {Describe(a2)} B {Describe(b2)}");
                Check(ExactlyOne(initiator, peer, requests, "Stop", null),
                    $"{label}: exactly one Stop from {initiator.Role}, no group request from {peer.Role}",
                    () => Routes(requests));
            }
            finally
            {
                Server.ReleaseEcho();
            }

            var (a, b) = await WaitBothAsync((x, y) => x is { State: "Browse", PlayingItemId: null }
                    && y is { State: "Browse", PlayingItemId: null },
                SettleDeadline, $"{label}: both clients stop and return to browsing").ConfigureAwait(false);
            Check(Server.EmittedSince(emitted).Any(c => c.Command == "Stop" && c.Recipients.Contains(A.Token!)
                    && c.Recipients.Contains(B.Token!)),
                $"{label}: the server sent the group's Stop to both members");
            Check(a is { InGroup: true } && b is { InGroup: true } && a.GroupId == _group && b.GroupId == _group
                && Server.View(_group) is { State: "Idle", Members: { Count: 2 } members }
                && members.Contains(A.Token!) && members.Contains(B.Token!),
                $"{label}: both stay members of the group, which is idle",
                () => $"A {Describe(a)} B {Describe(b)} group={Server.View(_group)?.State}");
            Check(Server.CommandRequests(peer.Token!, requests).Count == 0
                && Server.CommandRequests(initiator.Token!, requests).Count == 1,
                $"{label}: after the echo, still one request from {initiator.Role} and none from {peer.Role}",
                () => Routes(requests));
            await ShowStepAsync(step, Browsing(Status("Idle")), Browsing(Status("Idle"))).ConfigureAwait(false);
            await CheckFlyoutsAsync(step + "-flyout", "Idle").ConfigureAwait(false);
        }

        /// <summary>A starts the first episode again from its detail view: a NEW group queue, with
        /// entry ids of its own, which both clients load paused.</summary>
        private async Task<Guid> CheckRestartAsync(string step)
        {
            var old = Server.View(_group)!.Queue.Select(e => e.Entry).ToHashSet();
            var first = Server.Episodes[0];
            var mark = Server.SyncRequestCount;
            var played = await A.SendAsync("play", first.Id.ToString(), CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(played.Ok, "A starts the first episode again through the detail view's Play handler", () => played.Error ?? "");
            var (a, b) = await WaitBothAsync((x, y) => Server.View(_group) is { State: "Paused", PlaylistItemId: { } minted }
                    && !old.Contains(minted) && Loaded(x, first.Id, minted) && Loaded(y, first.Id, minted),
                StartupDeadline, "both clients load the new queue's first entry, paused").ConfigureAwait(false);
            var entry = Server.View(_group)!.PlaylistItemId!.Value;
            Check(Server.CommandRequests(A.Token!, mark) is [{ Route: "SetNewQueue" } queue]
                && SyncPlayTwoClientServer.BodyItems(queue, "PlayingQueue").SequenceEqual(Server.Episodes.Select(e => e.Id))
                && Server.CommandRequests(B.Token!, mark).Count == 0,
                "the restart is exactly one SetNewQueue of the three episodes from A, nothing from B", () => Routes(mark));
            Check(Server.View(_group)!.Queue.All(e => !old.Contains(e.Entry)),
                "the new queue's entries all have newly minted ids");
            CheckReady(mark, entry, "both real players reported Ready for the restarted entry");
            await ShowGroupStepAsync(step, "Paused").ConfigureAwait(false);
            return entry;
        }

        private SyncPlayTwoClientServer.Episode EpisodeOf(Guid item) => Server.Episodes.First(e => e.Id == item);

        // ---- what the windows show ---------------------------------------------------------

        private static string Status(string state) => $"{state} · 1";

        /// <summary>What a step expects one client to display. <paramref name="Status"/> is the
        /// label beside the SyncPlay icon that is on screen, empty when there is none;
        /// <paramref name="Playing"/> null means the browse shell, otherwise the overlay showing that
        /// episode. <paramref name="AnyCount"/> compares the status up to its participant count.</summary>
        private sealed record Shown(string Status, SyncPlayTwoClientServer.Episode? Playing, string? Badge = null,
            bool AnyCount = false);

        private static Shown Browsing(string status) => new(status, null);

        /// <summary>Both clients on the group's current entry, showing the group in that state.</summary>
        private Task ShowGroupStepAsync(string step, string state)
        {
            var view = Server.View(_group)!;
            var episode = EpisodeOf(view.Queue[view.PlayingIndex].Item);
            var expected = new Shown(Status(state), episode, $"{view.PlayingIndex + 1}/{view.Queue.Count}");
            return ShowStepAsync(step, expected, expected);
        }

        private bool Matches(TwoClientUi ui, Shown expected, bool inGroup)
        {
            var status = expected.AnyCount
                ? ui.Status.StartsWith(expected.Status[..(expected.Status.LastIndexOf('·') + 1)], StringComparison.Ordinal)
                : ui.Status == expected.Status;
            var button = ui.OverlayVisible ? ui.OverlayButtonName : ui.ShellButtonName;
            var name = !inGroup ? button == "SyncPlay groups"
                : expected.AnyCount ? button.StartsWith($"SyncPlay · {GroupName} · ", StringComparison.Ordinal)
                : button == $"SyncPlay · {GroupName} · {expected.Status}";
            var screen = expected.Playing is { } episode
                ? ui is { OverlayVisible: true, AppState: "Playing" } && ui.Title == SyncPlayTwoClientServer.SeriesName
                    && ui.Subtitle == episode.Subtitle && (expected.Badge is null || ui.QueueBadge == expected.Badge)
                : ui is { OverlayVisible: false, AppState: "Browse" };
            return status && name && screen;
        }

        private static string Describe(Shown expected)
            => $"status [{expected.Status}]{(expected.AnyCount ? " (any count)" : "")}, "
                + (expected.Playing is { } episode ? $"overlay showing {episode.Subtitle}"
                    + (expected.Badge is { } badge ? $" at {badge}" : "") : "the browse shell");

        private static string DescribeUi(TwoClientUi ui)
            => $"state={ui.AppState} overlay={ui.OverlayVisible} status=[{ui.Status}] shell=[{ui.ShellStatus}|{ui.ShellButtonName}] "
                + $"overlay=[{ui.OverlayStatus}|{ui.OverlayButtonName}] title=[{ui.Title}] subtitle=[{ui.Subtitle}] badge=[{ui.QueueBadge}]";

        /// <summary>Waits for both clients to display what the step expects, asserts it, and
        /// captures both.</summary>
        private async Task ShowStepAsync(string step, Shown a, Shown b)
        {
            var inA = a.Status.Length > 0;
            var inB = b.Status.Length > 0;
            var deadline = DateTime.UtcNow + SettleDeadline;
            var (uiA, uiB) = await BothUiAsync().ConfigureAwait(false);
            while (!(Matches(uiA, a, inA) && Matches(uiB, b, inB)) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, _total.Token).ConfigureAwait(false);
                (uiA, uiB) = await BothUiAsync().ConfigureAwait(false);
            }
            Check(Matches(uiA, a, inA), $"{step}: A displays {Describe(a)}", () => DescribeUi(uiA));
            Check(Matches(uiB, b, inB), $"{step}: B displays {Describe(b)}", () => DescribeUi(uiB));
            await CaptureBothAsync(step).ConfigureAwait(false);
        }

        private async Task<(TwoClientUi A, TwoClientUi B)> BothUiAsync()
        {
            var a = UiAsync(A);
            var b = UiAsync(B);
            return (await a.ConfigureAwait(false), await b.ConfigureAwait(false));
        }

        private async Task<TwoClientUi> UiAsync(Worker worker)
        {
            var reply = await worker.SendAsync("ui", null, CommandDeadline, _total.Token).ConfigureAwait(false);
            return reply is { Ok: true, Result: { } json } && JsonSerializer.Deserialize<TwoClientUi>(json) is { } ui
                ? ui : throw new InvalidOperationException($"worker {worker.Role} could not read its UI: {reply.Error}");
        }

        private string NextCapture(string step) => $"{++_captures:00}-{step}";

        /// <summary>One capture per client, A then B, each taken as the foreground application.</summary>
        private async Task CaptureBothAsync(string step)
        {
            var name = NextCapture(step);
            foreach (var worker in new[] { A, B })
            {
                var granted = await GrantForegroundAsync(worker).ConfigureAwait(false);
                var reply = await worker.SendAsync("capture", name, CommandDeadline, _total.Token).ConfigureAwait(false);
                CheckCapture(worker, reply.Ok ? reply.Result : null, name, reply.Error is { } error ? $"{error}; {granted}" : null);
            }
        }

        /// <summary>Lets a worker take the foreground for its next capture. Only the foreground
        /// process may hand it on, so both the other worker — usually the one holding it, since it
        /// captured last — and this controller grant it; whichever is entitled to succeeds. The
        /// worker still asserts it actually got it, and fails the capture with what holds the
        /// foreground if not. Returns the grants, for that failure's detail.</summary>
        private async Task<string> GrantForegroundAsync(Worker worker)
        {
            var other = ReferenceEquals(worker, A) ? B : A;
            var reply = await other.SendAsync("grant-foreground", worker.Pid.ToString(CultureInfo.InvariantCulture),
                CommandDeadline, _total.Token).ConfigureAwait(false);
            var mine = AllowSetForegroundWindow(worker.Pid);
            return $"grant by {other.Role}={reply.Result ?? reply.Error} grant by controller={mine}";
        }

        private void CheckCapture(Worker worker, string? path, string name, string? error = null)
        {
            var size = path is null ? null : PngSize(path);
            Check(size is (SyncPlayTwoClientWorker.CaptureWidth, SyncPlayTwoClientWorker.CaptureHeight),
                $"{name}: {worker.Role} captured at {SyncPlayTwoClientWorker.CaptureWidth}x{SyncPlayTwoClientWorker.CaptureHeight} "
                    + "as the foreground application",
                () => error ?? $"{path} size={size}");
            Console.WriteLine("CAPTURE: " + path);
        }

        /// <summary>The open popup was really on screen when it was captured: visible on the
        /// captured area, above its host window in the z-order, and not covered at its centre.</summary>
        private void CheckStacking(Worker worker, string name, string popup, string host, TwoClientStacking? stacking,
            string? error)
        {
            string Detail() => stacking?.Detail ?? error ?? "no stacking reported";
            Check(stacking is { Visible: true }, $"{name}: {worker.Role}'s {popup} window is visible on the captured area", Detail);
            Check(stacking is { AboveHost: true }, $"{name}: {worker.Role}'s {popup} is above the {host} in the z-order", Detail);
            Check(stacking is { Unobscured: true }, $"{name}: nothing covers {worker.Role}'s {popup} at its centre", Detail);
        }

        /// <summary>Opens each client's SyncPlay flyout from the icon on screen, asserts the group
        /// it renders — name, state, and the one participant name both sessions share — and
        /// captures it open.</summary>
        private async Task CheckFlyoutsAsync(string step, string state)
        {
            var name = NextCapture(step);
            foreach (var worker in new[] { A, B })
                await CheckFlyoutAsync(worker, name, joined: true, state).ConfigureAwait(false);
        }

        private async Task CheckFlyoutAsync(Worker worker, string capture, bool joined, string? state = null)
        {
            var granted = await GrantForegroundAsync(worker).ConfigureAwait(false);
            var reply = await worker.SendAsync("flyout", capture, CommandDeadline, _total.Token).ConfigureAwait(false);
            var flyout = reply is { Ok: true, Result: { } json } ? JsonSerializer.Deserialize<TwoClientFlyout>(json) : null;
            var failure = reply.Error is { } error ? $"{error}; {granted}" : null;
            var valid = joined
                ? flyout is { Joined: true } && flyout.GroupName == GroupName && flyout.GroupState == state
                    && flyout.Participants.SequenceEqual([SyncPlayTwoClientServer.UserName])
                : flyout is { Joined: false, GroupName: "", Participants.Count: 0 };
            Check(valid, joined
                    ? $"{capture}: {worker.Role}'s flyout shows group \"{GroupName}\", {state}, participant {SyncPlayTwoClientServer.UserName}"
                    : $"{capture}: {worker.Role}'s flyout shows no current group",
                () => flyout is null ? failure ?? "no flyout"
                    : $"host={flyout.Host} joined={flyout.Joined} name=[{flyout.GroupName}] state=[{flyout.GroupState}] "
                        + $"participants=[{string.Join(", ", flyout.Participants)}] status=[{flyout.Status}]");
            CheckStacking(worker, capture, "SyncPlay flyout", flyout?.Host == "overlay" ? "player overlay" : "shell window",
                flyout?.Stacking, failure);
            CheckCapture(worker, flyout?.Capture, capture, failure);
        }

        /// <summary>Opens each client's queue popup and asserts every row as drawn against the
        /// server's queue: number, title, the check mark on the playing entry only, and a remove
        /// button on every other row. Waits for the view model's rows first, so a title still on
        /// its way from the server is not read as wrong.</summary>
        private async Task CheckQueuesAsync(string step)
        {
            var view = Server.View(_group)!;
            var rows = view.Queue.Select((e, i) => (Display: EpisodeOf(e.Item).QueueDisplay, Current: i == view.PlayingIndex))
                .ToList();
            var expected = rows.Select(r => (r.Current ? "*" : "") + r.Display).ToList();
            await WaitBothAsync((x, y) => x.Queue?.SequenceEqual(expected) == true && y.Queue?.SequenceEqual(expected) == true,
                SettleDeadline, $"{step}: both clients hold the server's queue").ConfigureAwait(false);
            var name = NextCapture(step);
            foreach (var worker in new[] { A, B })
            {
                var granted = await GrantForegroundAsync(worker).ConfigureAwait(false);
                var reply = await worker.SendAsync("queue", name, CommandDeadline, _total.Token).ConfigureAwait(false);
                var queue = reply is { Ok: true, Result: { } json } ? JsonSerializer.Deserialize<TwoClientQueue>(json) : null;
                var failure = reply.Error is { } error ? $"{error}; {granted}" : null;
                var drawn = queue?.Rows ?? [];
                Check(drawn.Count == rows.Count && drawn.Select((row, i) => row.Position == $"{i + 1}."
                        && row.Display == rows[i].Display && row.Current == rows[i].Current && row.Removable == !rows[i].Current)
                        .All(ok => ok),
                    $"{name}: {worker.Role}'s queue popup marks entry {view.PlayingIndex + 1} of {rows.Count} as playing",
                    () => queue is null ? failure ?? "no queue" : string.Join("; ", drawn.Select(r =>
                        $"{r.Position} {r.Display}{(r.Current ? " [current]" : "")}{(r.Removable ? " [x]" : "")}")));
                CheckStacking(worker, name, "queue popup", "player overlay", queue?.Stacking, failure);
                CheckCapture(worker, queue?.Capture, name, failure);
            }
        }

        // ---- check 7 ----------------------------------------------------------------------

        private async Task CheckLeaveWhilePlayingAsync(Guid entry)
        {
            var before = await A.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(Server.View(_group) is { State: "Playing" } && !before.Paused && before.InGroup,
                "the group is playing when A leaves", () => Describe(before));
            var requests = Server.SyncRequestCount;
            var left = await A.SendAsync("leave", null, CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(left.Ok, "A leaves through the flyout's leave call", () => left.Error ?? "");
            await UntilAsync(() => Server.View(_group) is { Members: [var only] } && only == B.Token,
                SettleDeadline, "the server drops A's session from the group").ConfigureAwait(false);
            var leaveRequests = Server.CommandRequests(A.Token!, requests);
            Check(leaveRequests is [{ Route: "Leave" }] && Server.CommandRequests(B.Token!, requests).Count == 0,
                "A's leave is exactly one Leave, and B sent nothing", () => Routes(requests));

            var a1 = await WaitAsync(A, s => !s.InGroup, SettleDeadline, "A drops its membership").ConfigureAwait(false);
            await Task.Delay(1000, _total.Token).ConfigureAwait(false);
            var a2 = await A.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(a2 is { State: "Playing", Paused: false, InGroup: false } && a2.Path == before.Path
                && a2.LoadGeneration == before.LoadGeneration && a2.PlayingItemId == before.PlayingItemId
                && a2.Position - a1.Position > 0.6,
                "A keeps the same loaded file advancing locally after leaving",
                () => $"before {Describe(before)} then {Describe(a1)} -> {Describe(a2)} same path={a2.Path == before.Path}");
            var b = await B.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
            Check(b is { InGroup: true, Paused: false } && b.GroupId == _group && b.PlaylistItemId == entry,
                "B stays a playing member of the group", () => Describe(b));
            // B's participant count is asserted at the end, not here: it is the leg a same-user
            // leave has been known to break, and everything after it should still run first.
            var first = Server.Episodes[0];
            await ShowStepAsync("a-left", new Shown("", first), new Shown(Status("Playing"), first, "1/3", AnyCount: true))
                .ConfigureAwait(false);

            // B pauses the group, which is now B alone. The echo is held first, for the same
            // proof as every other leg: nothing moves before the frame.
            var mark = Server.SyncRequestCount;
            var frames = Server.FrameCount;
            var emitted = Server.EmittedCount;
            Server.HoldEcho();
            TwoClientSnapshot heldA, heldB;
            try
            {
                var paused = await B.SendAsync("toggle-pause", null, CommandDeadline, _total.Token).ConfigureAwait(false);
                Check(paused.Ok, "B pauses through the player view model", () => paused.Error ?? "");
                await UntilAsync(() => Server.CommandRequests(B.Token!, mark).Any(r => r.Route == "Pause")
                    && Server.HeldCount > 0, CommandDeadline, "the server receives B's Pause").ConfigureAwait(false);
                await Task.Delay(400, _total.Token).ConfigureAwait(false);
                (heldA, heldB) = await BothAsync().ConfigureAwait(false);
                Check(!heldB.Paused && !heldA.Paused, "with the echo held, B is still playing, and so is A",
                    () => $"A {Describe(heldA)} B {Describe(heldB)}");
            }
            finally
            {
                Server.ReleaseEcho();
            }
            var bPaused = await WaitAsync(B, s => s is { Paused: true, Loading: false }, SettleDeadline,
                "B pauses on the group's echo").ConfigureAwait(false);
            Check(Server.EmittedSince(emitted).Any(c => c.Command == "Pause" && c.Recipients is [var to] && to == B.Token),
                "the group's Pause went to B alone");
            await Task.Delay(800, _total.Token).ConfigureAwait(false);
            var aAfter = await A.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
            // Half the wait, not all of it: the clip is 10 fps, so the position moves in 0.1 s steps
            // and lags the clock by up to one, and a run on the guest measured exactly 0.8 across
            // this 800 ms. A paused or stalled A moves 0, which this still catches.
            Check(bPaused.Paused && aAfter is { Paused: false, InGroup: false } && aAfter.Path == before.Path
                && aAfter.Position - heldA.Position > 0.4,
                "B paused by echo while A kept playing", () => $"A {Describe(heldA)} -> {Describe(aAfter)}");
            // From A's Leave itself, not from the mark before it: the clock's ordinary Ping can land
            // between the two, and that is a request made while still a member.
            var leaveIndex = leaveRequests[0].Index;
            Check(Server.SyncRequestsSince(leaveIndex + 1).Count(r => r.Token == A.Token) == 0,
                "A sent no group request of any kind after its Leave", () => Routes(leaveIndex + 1));
            Check(Server.FramesTo(A.Token!, frames).Count == 0, "no group frame reached A after it left");
            Check(Server.CommandRequests(B.Token!, mark) is [{ Route: "Pause" }],
                "B's pause is exactly one Pause", () => Routes(mark));

            // Last, because opening A's flyout outside a group lists the groups, which is a
            // request; and because B's participants are the leg most likely to fail. B still
            // showing "1" and TEST is the user-visible outcome only: the own-name rule already
            // keeps TEST before the List reconcile answers, so this leg does not prove the
            // reconcile. The --sync-play legs do.
            await CheckFlyoutAsync(A, NextCapture("a-left-flyout"), joined: false).ConfigureAwait(false);
            await ShowStepAsync("final", new Shown("", first), new Shown(Status("Paused"), first, "1/3"))
                .ConfigureAwait(false);
            await CheckFlyoutAsync(B, NextCapture("final-flyout"), joined: true, "Paused").ConfigureAwait(false);
        }

        // ---- teardown ---------------------------------------------------------------------

        public async Task<bool> TeardownAsync()
        {
            var ok = true;
            foreach (var worker in new[] { _a, _b })
            {
                if (worker is null)
                    continue;
                var graceful = await worker.CloseAsync(CloseDeadline).ConfigureAwait(false);
                Report(graceful, $"worker {worker.Role} closed its window and exited on request",
                    $"worker {worker.Role} had to be killed by its captured PID {worker.Pid}");
                ok &= graceful;
                worker.Dispose();
            }
            _server?.Dispose();
            if (_runDir is not null)
            {
                var removed = await DeleteRunDirAsync(_runDir).ConfigureAwait(false);
                Report(removed, "the run's temporary app-data roots were removed",
                    "the run's temporary app-data roots could not be removed: " + _runDir);
                ok &= removed;
            }
            _total.Dispose();
            return ok;

            void Report(bool valid, string pass, string fail)
            {
                if (valid)
                {
                    Assertions++;
                    Console.WriteLine("PASS: " + pass);
                }
                else
                    Console.WriteLine("FAIL: " + fail);
            }
        }

        // ---- plumbing ---------------------------------------------------------------------

        private async Task<(TwoClientSnapshot A, TwoClientSnapshot B)> BothAsync()
        {
            var a = A.SnapshotAsync(CommandDeadline, _total.Token);
            var b = B.SnapshotAsync(CommandDeadline, _total.Token);
            return (await a.ConfigureAwait(false), await b.ConfigureAwait(false));
        }

        private async Task<(TwoClientSnapshot A, TwoClientSnapshot B)> WaitBothAsync(
            Func<TwoClientSnapshot, TwoClientSnapshot, bool> condition, TimeSpan timeout, string what, bool fail = true)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var (a, b) = await BothAsync().ConfigureAwait(false);
                if (condition(a, b))
                    return (a, b);
                if (DateTime.UtcNow >= deadline)
                {
                    if (!fail)
                        return (a, b);
                    throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: {what} "
                        + $"[A {Describe(a)}; B {Describe(b)}; group={Server.View(_group)?.State ?? "none"}]");
                }
                await Task.Delay(100, _total.Token).ConfigureAwait(false);
            }
        }

        private async Task<TwoClientSnapshot> WaitAsync(Worker worker, Func<TwoClientSnapshot, bool> condition,
            TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var snapshot = await worker.SnapshotAsync(CommandDeadline, _total.Token).ConfigureAwait(false);
                if (condition(snapshot))
                    return snapshot;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: {what} [{Describe(snapshot)}]");
                await Task.Delay(100, _total.Token).ConfigureAwait(false);
            }
        }

        private async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: {what}");
                await Task.Delay(50, _total.Token).ConfigureAwait(false);
            }
        }

        /// <summary>Every SyncPlay request since a mark, attributed — the evidence a failed
        /// request-count assertion needs. Tokens are shortened to the role they belong to.</summary>
        private string Routes(int mark) => string.Join(", ", Server.SyncRequestsSince(mark)
            .Select(r => $"{(r.Token == A.Token ? "A" : r.Token == B.Token ? "B" : "?")}:{r.Route}"));

        private static string Describe(TwoClientSnapshot s)
            => FormattableString.Invariant(
                $"{s.Role}[{s.State} group={(s.InGroup ? "yes" : "no")} paused={s.Paused} pos={s.Position:0.000} gen={s.LoadGeneration} loading={s.Loading}]");

        private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);
    }

    // ---- one worker process --------------------------------------------------------------

    private sealed class Worker : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<TwoClientReply>> _pending = new();
        private readonly TaskCompletionSource<TwoClientReply> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeLock = new();
        private readonly StreamWriter _log;
        private StreamWriter? _writer;
        private int _nextId;

        private Worker(string role, string root, Process process, NamedPipeServerStream pipe, StreamWriter log)
        {
            Role = role;
            Root = root;
            Process = process;
            Pid = process.Id;
            _pipe = pipe;
            _log = log;
        }

        public string Role { get; }
        public string Root { get; }
        public Process Process { get; }
        public int Pid { get; }
        public TwoClientSnapshot? Hello { get; private set; }
        public string? Token { get; set; }

        public static Worker Launch(string exe, string role, string root, int left, int top, string logs)
        {
            var name = $"lw-two-client-{Environment.ProcessId}-{role}-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var argument in new[] { SyncPlayTwoClientWorker.Mode, name, role, root,
                         left.ToString(CultureInfo.InvariantCulture), top.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            start.Environment["LIGHTWEAVER_TEST_APPDATA_ROOT"] = root;
            // The worker decides its video output from the switch alone, whatever this process has.
            start.Environment.Remove("LIGHTWEAVER_MPV_NO_VIDEO");
            start.Environment["LIGHTWEAVER_MPV_NO_AUDIO"] = "1";
            start.Environment["LIGHTWEAVER_MPV_MUTE"] = "1";
            start.Environment["LIGHTWEAVER_UPDATE_DEFER_START"] = "1";
            var log = new StreamWriter(Path.Combine(logs, $"worker-{role}.log"), append: false) { AutoFlush = true };
            var process = Process.Start(start) ?? throw new InvalidOperationException($"worker {role} did not start");
            var worker = new Worker(role, root, process, pipe, log);
            process.OutputDataReceived += (_, e) => worker.Echo(e.Data);
            process.ErrorDataReceived += (_, e) => worker.Echo(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return worker;
        }

        private void Echo(string? line)
        {
            if (line is null)
                return;
            lock (_log)
            {
                try { _log.WriteLine(line); }
                catch (ObjectDisposedException) { }
            }
            Console.WriteLine($"[{Role}] {line}");
        }

        public async Task ConnectAsync(TimeSpan timeout, CancellationToken total)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(total);
            limit.CancelAfter(timeout);
            var connect = _pipe.WaitForConnectionAsync(limit.Token);
            var exited = Process.WaitForExitAsync(limit.Token);
            await Task.WhenAny(connect, exited).ConfigureAwait(false);
            if (!connect.IsCompletedSuccessfully)
            {
                if (Process.HasExited)
                    throw new InvalidOperationException($"worker {Role} exited with code {Process.ExitCode} before connecting");
                try
                {
                    await connect.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: worker {Role} never connected its pipe");
                }
            }
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _ = ReadAsync();
            TwoClientReply hello;
            try
            {
                hello = await _hello.Task.WaitAsync(timeout, total).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: worker {Role} never showed its window");
            }
            if (!hello.Ok || hello.Snapshot is null)
                throw new InvalidOperationException($"worker {Role} could not start: {hello.Error}");
            Hello = hello.Snapshot;
        }

        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (JsonSerializer.Deserialize<TwoClientReply>(line) is not { } reply)
                        continue;
                    if (reply.Id == 0)
                        _hello.TrySetResult(reply);
                    else if (_pending.TryRemove(reply.Id, out var waiter))
                        waiter.TrySetResult(reply);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
            {
            }
            var gone = new InvalidOperationException($"worker {Role}'s pipe closed");
            _hello.TrySetException(gone);
            foreach (var waiter in _pending.Values)
                waiter.TrySetException(gone);
        }

        public async Task<TwoClientReply> SendAsync(string op, string? arg, TimeSpan timeout, CancellationToken total)
        {
            var id = Interlocked.Increment(ref _nextId);
            var waiter = new TaskCompletionSource<TwoClientReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            var line = JsonSerializer.Serialize(new TwoClientCommand(id, op, arg));
            lock (_writeLock)
                _writer!.WriteLine(line);
            try
            {
                return await waiter.Task.WaitAsync(timeout, total).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"deadline {timeout.TotalSeconds:0} s: worker {Role} did not answer {op}");
            }
        }

        public async Task<TwoClientSnapshot> SnapshotAsync(TimeSpan timeout, CancellationToken total)
        {
            var reply = await SendAsync("snapshot", null, timeout, total).ConfigureAwait(false);
            return reply is { Ok: true, Snapshot: { } snapshot }
                ? snapshot
                : throw new InvalidOperationException($"worker {Role} could not report its state: {reply.Error}");
        }

        /// <summary>Graceful first: the worker closes its window, which runs the product's own
        /// shutdown (group client, socket, player), and exits. Only if that does not happen in
        /// time is the process killed — through the handle this run got from <c>Process.Start</c>,
        /// so it can only ever be this run's child, never a PID that has since been reused and
        /// never anything found by name.</summary>
        public async Task<bool> CloseAsync(TimeSpan timeout)
        {
            if (Process.HasExited)
                return Process.ExitCode == 0;
            using var limit = new CancellationTokenSource(timeout);
            try
            {
                if (_writer is not null)
                    await SendAsync("close", null, timeout, limit.Token).ConfigureAwait(false);
                await Process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return Process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException
                or InvalidOperationException or IOException)
            {
            }
            if (Process.HasExited)
                return Process.ExitCode == 0;
            try
            {
                Process.Kill(entireProcessTree: false);
                Process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            return false;
        }

        public void Dispose()
        {
            _pipe.Dispose();
            lock (_log)
                _log.Dispose();
            Process.Dispose();
        }
    }

    // ---- the claims, as arithmetic -------------------------------------------------------
    // Pure, so the self-test can execute them against hand-built snapshots; the two-client
    // scenario calls exactly these.

    /// <summary>Where a snapshot's player is at another instant, at playback rate 1.</summary>
    private static double At(TwoClientSnapshot s, long ticks)
        => s.Position + (s.Paused ? 0 : (ticks - s.CapturedUtcTicks) / (double)TimeSpan.TicksPerSecond);

    /// <summary>Two players' disagreement, both moved to the later of their two capture instants,
    /// so the gap between asking A and asking B is not counted as drift.</summary>
    private static double Disagreement(TwoClientSnapshot a, TwoClientSnapshot b)
    {
        var at = Math.Max(a.CapturedUtcTicks, b.CapturedUtcTicks);
        return Math.Abs(At(a, at) - At(b, at));
    }

    /// <summary>How far a playing client is from where the group's Unpause puts it:
    /// <c>PositionTicks + (t - When)</c>.</summary>
    private static double ScheduleOffset(TwoClientSnapshot s, SyncPlayTwoClientServer.EmittedCommand start)
        => Math.Abs(s.Position - (start.PositionTicks / (double)TimeSpan.TicksPerSecond
            + (new DateTime(s.CapturedUtcTicks, DateTimeKind.Utc) - start.When).TotalSeconds));

    /// <summary>"Nothing moved while the echo was held": the transport state did not flip, the
    /// same file is loaded, and the position did what that state implies — a paused player stayed
    /// put, a playing one kept advancing.</summary>
    private static bool Unchanged(TwoClientSnapshot before, TwoClientSnapshot first, TwoClientSnapshot second)
    {
        if (first.Paused != before.Paused || second.Paused != before.Paused
            || second.PlaylistItemId != before.PlaylistItemId || second.LoadGeneration != before.LoadGeneration
            || !first.HasPosition || !second.HasPosition)
            return false;
        return before.Paused
            ? Math.Abs(first.Position - before.Position) < StillTolerance
                && Math.Abs(second.Position - before.Position) < StillTolerance
            : second.Position - first.Position > 0.2;
    }

    private static bool Near(TwoClientSnapshot s, double target, double tolerance)
        => s is { Paused: true, HasPosition: true, Loading: false } && Math.Abs(s.Position - target) <= tolerance;

    private static void CheckClaimArithmetic(Action<bool, string> check)
    {
        var entry = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc).Ticks;
        TwoClientSnapshot S(bool paused, double position, long ticks, int generation = 3)
            => new("X", 1, "", "", null, Guid.Empty, "Playing", 1, true, false, null, null, [], null, entry, null,
                true, false, paused, true, position, 30, generation, null, ticks);
        var second = TimeSpan.TicksPerSecond;

        check(Unchanged(S(true, 4.00, t0), S(true, 4.01, t0 + second), S(true, 4.02, t0 + 2 * second)),
            "a paused player that stayed put counts as unchanged");
        check(!Unchanged(S(true, 4.00, t0), S(true, 4.00, t0 + second), S(true, 4.40, t0 + 2 * second)),
            "a paused player that moved does not");
        // Within the stillness tolerance on purpose, so only the pause flip can tell it apart.
        check(!Unchanged(S(true, 4.00, t0), S(false, 4.00, t0 + second), S(false, 4.05, t0 + 2 * second)),
            "a player that unpaused before the echo does not");
        check(Unchanged(S(false, 5.0, t0), S(false, 5.1, t0 + second / 10), S(false, 5.6, t0 + 6 * second / 10)),
            "a playing player that kept advancing counts as unchanged");
        check(!Unchanged(S(false, 5.0, t0), S(false, 5.1, t0 + second / 10), S(false, 5.1, t0 + 6 * second / 10)),
            "a playing player that stopped advancing does not");
        check(!Unchanged(S(true, 4.0, t0), S(true, 4.0, t0), S(true, 4.0, t0, generation: 4)),
            "a player that loaded another file does not");

        // B asked a second after A and played on for that second: in step, not a second apart.
        check(Disagreement(S(false, 10.0, t0), S(false, 11.0, t0 + second)) < 0.01,
            "positions read a second apart are compared at one instant");
        check(Disagreement(S(false, 10.0, t0), S(false, 10.9, t0 + second)) is > 0.09 and < 0.11,
            "a real 100 ms disagreement survives the adjustment");
        check(Disagreement(S(true, 10.0, t0), S(true, 10.0, t0 + second)) < 0.01,
            "paused players are not advanced by the adjustment");

        var start = new SyncPlayTwoClientServer.EmittedCommand(0, Guid.Empty, entry, "Unpause",
            new DateTime(t0 + second, DateTimeKind.Utc), new DateTime(t0, DateTimeKind.Utc), 4 * second, []);
        check(ScheduleOffset(S(false, 6.0, t0 + 3 * second), start) < 0.01,
            "a client two seconds past When sits two seconds past the start position");
        check(ScheduleOffset(S(false, 6.0, t0 + 4 * second), start) is > 0.99 and < 1.01,
            "a client that started a second late is a second off the schedule");
    }

    // ---- shared helpers ------------------------------------------------------------------

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static string? PersistedDevice(string root)
    {
        var path = Path.Combine(root, "device-id");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
    }

    /// <summary>A PNG's width and height off its IHDR chunk, or null for anything else.</summary>
    private static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            var header = new byte[24];
            using (var file = File.OpenRead(path))
                if (file.Read(header, 0, header.Length) != header.Length)
                    return null;
            if (header[0] != 0x89 || header[1] != (byte)'P' || header[12] != (byte)'I' || header[15] != (byte)'R')
                return null;
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private static bool LibMpvLoads()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        if ((File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
            || NativeLibrary.TryLoad("libmpv-2.dll", out handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }

    /// <summary>A 30 s clip from one lavfi source, built like the binding fixture's and cached
    /// beside it, with a keyframe every second. Unlike there a missing ffmpeg is a FAILURE with its
    /// reason, never a skip: this mode exists to be run on a guest that is meant to have it.</summary>
    private static string? BuildClip(string source, out string? failure)
    {
        failure = null;
        var clip = Path.Combine(Path.GetTempPath(), "lightweaver-tests", $"syncplay-two-client-{source}-640x360-10fps-30s.mkv");
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return clip;
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        try
        {
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i {source}=duration=30:size=640x360:rate=10 "
                + $"-c:v libx264 -pix_fmt yuv420p -g 10 \"{clip}\"") { UseShellExecute = false });
            if (ffmpeg is null || !ffmpeg.WaitForExit(120_000))
            {
                failure = "ffmpeg did not finish building the clip within 120 s";
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            failure = "ffmpeg is not on PATH";
            return null;
        }
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return clip;
        failure = "ffmpeg produced no usable clip at " + clip;
        return null;
    }

    /// <summary>Removes the run directory, and only that: it must be directly under the temp
    /// directory and carry this run's prefix, whatever path the caller hands in. Retried, because a
    /// worker's files can stay locked for a moment after its process has exited.</summary>
    private static async Task<bool> DeleteRunDirAsync(string runDir)
    {
        var full = Path.GetFullPath(runDir);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(RunPrefix, StringComparison.Ordinal))
            return false;
        for (var attempt = 0; attempt < 20 && Directory.Exists(full); attempt++)
        {
            try { Directory.Delete(full, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (Directory.Exists(full))
                await Task.Delay(500).ConfigureAwait(false);
        }
        return !Directory.Exists(full);
    }

    // ---- the windowless server self-test -------------------------------------------------

    /// <summary>
    /// <c>--sync-play-two-client-server</c>: the server the two-client mode stands on, driven by raw
    /// HTTP and WebSocket clients and one real SDK sign-in, with no window, no player and no
    /// second process. It is where the server's own claims get executed rather than argued — the
    /// per-device tokens, the session attribution every "the peer sent nothing" assertion depends
    /// on, the echo barrier, the 12.0 timing rules, Range serving and serialized sends — so a
    /// break in any of them goes red here, on any machine, before a guest ever runs the real thing.
    /// </summary>
    public static int RunServer() => RunServerAsync().GetAwaiter().GetResult();

    private static async Task<int> RunServerAsync()
    {
        var count = 0;
        void Check(bool valid, string name)
        {
            if (!valid)
                throw new InvalidOperationException(name);
            count++;
            Console.WriteLine("PASS: " + name);
        }

        var media = Path.Combine(Path.GetTempPath(), "lw-two-client-server-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[200_000];
        new Random(17).NextBytes(bytes);
        await File.WriteAllBytesAsync(media, bytes).ConfigureAwait(false);
        var sessions = new List<RawSession>();
        try
        {
            CheckClaimArithmetic(Check);
            using var server = new SyncPlayTwoClientServer([media, media, media]);
            server.Diagnostic += line => Console.WriteLine(line);
            await CheckIdentityAsync(server, sessions, Check).ConfigureAwait(false);
            var (a, b) = (sessions[^2], sessions[^1]);
            await CheckGroupProtocolAsync(server, a, b, Check).ConfigureAwait(false);
            await CheckMediaAsync(server, b, bytes, Check).ConfigureAwait(false);
            await CheckSerializedSendsAsync(server, b, Check).ConfigureAwait(false);
            Console.WriteLine($"RESULT: PASS ({count} sync play two-client server assertions)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: sync play two-client server: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"RESULT: FAIL ({count} sync play two-client server assertions passed before the failure)");
            return 1;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
            File.Delete(media);
        }
    }

    private static async Task CheckIdentityAsync(SyncPlayTwoClientServer server, List<RawSession> sessions,
        Action<bool, string> check)
    {
        var first = await RawSession.SignInAsync(server, "raw-device-a").ConfigureAwait(false);
        var b = await RawSession.SignInAsync(server, "raw-device-b").ConfigureAwait(false);
        sessions.Add(first);
        check(first.Token != b.Token && first.UserId == server.UserId && b.UserId == server.UserId
            && server.Http.DeviceForToken(first.Token) == "raw-device-a"
            && server.Http.DeviceForToken(b.Token) == "raw-device-b",
            "two devices signing in as one user get two tokens, each bound to its device");

        var a = await RawSession.SignInAsync(server, "raw-device-a").ConfigureAwait(false);
        check(a.Token != first.Token && server.Http.DeviceForToken(first.Token) is null
            && server.Http.TokenForDevice("raw-device-a") == a.Token,
            "a device that signs in again replaces its token, so a token always names one device");
        check(await first.GetStatusAsync("/Users/Me").ConfigureAwait(false) == 401
            && await a.GetStatusAsync("/Users/Me").ConfigureAwait(false) == 200
            && await RawSession.AnonymousStatusAsync(server, "/Users/Me").ConfigureAwait(false) == 401,
            "the replaced token and no token at all are refused; the live one is accepted");

        // The production half: the SDK's own sign-in has to carry the persisted device id, or the
        // two-client mode could not bind a token to a worker at all.
        using (var service = new JellyfinService())
        {
            var login = await service.LoginAsync(server.Origin, SyncPlayTwoClientServer.UserName,
                SyncPlayTwoClientWorker.Password).ConfigureAwait(false);
            var device = CredentialStore.GetOrCreateDeviceId();
            check(login.Success && login.Credentials?.AccessToken is { } token
                && server.Http.DeviceForToken(token) == device && login.Credentials.UserId == server.UserId,
                "the production sign-in names its persisted device id and gets a token bound to it");
            var me = await service.Client.Users.Me.GetAsync().ConfigureAwait(false);
            check(me?.Id == server.UserId, "the production client authenticates with that token afterwards");
        }

        await a.ConnectAsync().ConfigureAwait(false);
        await b.ConnectAsync().ConfigureAwait(false);
        sessions.Add(a);
        sessions.Add(b);
        await Settle(() => server.Http.Connections.Count(c => c.IsOpen) >= 2).ConfigureAwait(false);
        check(server.Http.Connections.Where(c => c.IsOpen).Select(c => c.Token).ToHashSet()
                is var open && open.Contains(a.Token) && open.Contains(b.Token),
            "each session socket is attributed to the token that opened it");
        var refused = false;
        try
        {
            using var stranger = new ClientWebSocket();
            stranger.Options.SetRequestHeader("Authorization", "MediaBrowser Token=\"never-issued\"");
            await stranger.ConnectAsync(new Uri(server.Origin.Replace("http://", "ws://") + "/socket"),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            refused = true;
        }
        check(refused, "an upgrade with a token the server never issued is refused");
    }

    private static async Task CheckGroupProtocolAsync(SyncPlayTwoClientServer server, RawSession a, RawSession b,
        Action<bool, string> check)
    {
        await a.PostAsync("/SyncPlay/New", """{"groupName":"Self test"}""").ConfigureAwait(false);
        var joined = await a.NextUpdateAsync().ConfigureAwait(false);
        var group = joined.GroupInfo()?.GroupId ?? Guid.Empty;
        check(joined.Type == SyncPlayGroupUpdateTypes.GroupJoined && group != Guid.Empty
            && server.View(group) is { Members: [var creator] } && creator == a.Token,
            "New answers the creator alone with GroupJoined");
        var list = await b.GetStringAsync("/SyncPlay/List").ConfigureAwait(false);
        check(list.Contains(group.ToString("D"), StringComparison.OrdinalIgnoreCase),
            "List shows the group to another session");
        await b.PostAsync("/SyncPlay/Join", $$"""{"groupId":"{{group:D}}"}""").ConfigureAwait(false);
        var bJoined = await b.NextUpdateAsync().ConfigureAwait(false);
        var aSaw = await a.NextUpdateAsync().ConfigureAwait(false);
        check(bJoined.Type == SyncPlayGroupUpdateTypes.GroupJoined && bJoined.GroupInfo()?.GroupId == group
            && aSaw.Type == SyncPlayGroupUpdateTypes.UserJoined && aSaw.Text() == SyncPlayTwoClientServer.UserName
            && bJoined.GroupInfo()?.Participants is [SyncPlayTwoClientServer.UserName]
            && server.View(group) is { Members.Count: 2 },
            "Join answers the joiner, tells the other member, and the one user is one participant name");

        await a.PostAsync("/SyncPlay/SetNewQueue",
            $$"""{"playingQueue":["{{server.ItemId:D}}"],"playingItemPosition":0,"startPositionTicks":0}""").ConfigureAwait(false);
        var queueA = (await a.NextUpdateAsync().ConfigureAwait(false)).Queue();
        var queueB = (await b.NextUpdateAsync().ConfigureAwait(false)).Queue();
        var entry = queueA?.Playlist?.FirstOrDefault()?.PlaylistItem ?? Guid.Empty;
        check(entry != Guid.Empty && queueB?.Playlist?.FirstOrDefault()?.PlaylistItem == entry
            && queueA!.Playlist![0].Item == server.ItemId,
            "SetNewQueue sends both members one server-minted PlaylistItemId for the item");
        check((await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting"
            && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting",
            "a new queue puts the group in Waiting");
        await a.PostAsync("/SyncPlay/Ping", """{"ping":0}""").ConfigureAwait(false);
        await b.PostAsync("/SyncPlay/Ping", """{"ping":0}""").ConfigureAwait(false);
        await a.PostAsync("/SyncPlay/Ready", Ready(entry, 0)).ConfigureAwait(false);
        check(server.View(group)?.State == "Waiting" && await b.NextFrameAsync(TimeSpan.FromMilliseconds(400))
                .ConfigureAwait(false) is null,
            "one Ready of two leaves the group waiting");
        await b.PostAsync("/SyncPlay/Ready", Ready(entry, 0)).ConfigureAwait(false);
        check((await a.NextCommandAsync().ConfigureAwait(false)).Command == "Pause"
            && (await b.NextCommandAsync().ConfigureAwait(false)).Command == "Pause"
            && (await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Paused"
            && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Paused",
            "the last Ready settles the group paused and tells both members");

        // The barrier, and the per-session counting every two-client assertion rests on.
        var mark = server.SyncRequestCount;
        var frames = server.FrameCount;
        server.HoldEcho();
        await a.PostAsync("/SyncPlay/Unpause", null).ConfigureAwait(false);
        check(server.CommandRequests(a.Token, mark) is [{ Route: "Unpause" }]
            && server.CommandRequests(b.Token, mark).Count == 0,
            "a held Unpause is counted against its sender only");
        check(server.HeldCount == 1, "a held Unpause's decision is queued rather than made");
        check(await a.NextFrameAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false) is null
            && await b.NextFrameAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false) is null
            && server.FramesTo(a.Token, frames).Count == 0 && server.FramesTo(b.Token, frames).Count == 0
            && server.View(group)?.State == "Paused",
            "while the echo is held no frame leaves and the group has not changed");
        server.ReleaseEcho();
        var unpauseA = await a.NextCommandAsync().ConfigureAwait(false);
        var unpauseB = await b.NextCommandAsync().ConfigureAwait(false);
        var lead = (unpauseA.WhenUtc ?? default) - (unpauseA.EmittedAtUtc ?? default);
        check(unpauseA.Command == "Unpause" && unpauseB.Command == "Unpause" && unpauseA.When == unpauseB.When
            && lead >= TimeSpan.FromMilliseconds(499) && lead <= TimeSpan.FromMilliseconds(510)
            && server.View(group)?.State == "Playing",
            "release delivers one scheduled Unpause to both, max(2 x ping, 500 ms) ahead");
        await a.NextUpdateAsync().ConfigureAwait(false);
        await b.NextUpdateAsync().ConfigureAwait(false);

        await Task.Delay(1200).ConfigureAwait(false);
        mark = server.SyncRequestCount;
        await b.PostAsync("/SyncPlay/Pause", null).ConfigureAwait(false);
        var pause = await a.NextCommandAsync().ConfigureAwait(false);
        await b.NextCommandAsync().ConfigureAwait(false);
        var ran = (pause.EmittedAtUtc ?? default) - (unpauseA.WhenUtc ?? default);
        check(server.CommandRequests(b.Token, mark) is [{ Route: "Pause" }] && server.CommandRequests(a.Token, mark).Count == 0,
            "the counting is per session in the other direction too");
        check(pause.Command == "Pause" && pause.When == pause.EmittedAt
            && Math.Abs((pause.PositionTicks ?? 0) - ran.Ticks) < TimeSpan.FromMilliseconds(50).Ticks,
            "a pause is dated now and stops at the elapsed position");
        await a.NextUpdateAsync().ConfigureAwait(false);
        await b.NextUpdateAsync().ConfigureAwait(false);

        await a.PostAsync("/SyncPlay/Ping", """{"ping":400}""").ConfigureAwait(false);
        await b.PostAsync("/SyncPlay/Unpause", null).ConfigureAwait(false);
        var slow = await a.NextCommandAsync().ConfigureAwait(false);
        await b.NextCommandAsync().ConfigureAwait(false);
        var slowLead = (slow.WhenUtc ?? default) - (slow.EmittedAtUtc ?? default);
        check(slowLead >= TimeSpan.FromMilliseconds(799) && slowLead <= TimeSpan.FromMilliseconds(810),
            "the pre-roll follows the highest member ping");
        await a.NextUpdateAsync().ConfigureAwait(false);
        await b.NextUpdateAsync().ConfigureAwait(false);
        await a.PostAsync("/SyncPlay/Ping", """{"ping":0}""").ConfigureAwait(false);

        await a.PostAsync("/SyncPlay/Seek", """{"positionTicks":120000000}""").ConfigureAwait(false);
        var seekA = await a.NextCommandAsync().ConfigureAwait(false);
        var seekB = await b.NextCommandAsync().ConfigureAwait(false);
        check(seekA is { Command: "Seek", PositionTicks: 120000000 } && seekB is { Command: "Seek", PositionTicks: 120000000 }
            && (await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting"
            && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting",
            "a seek sends both members the target and waits");
        await a.PostAsync("/SyncPlay/Ready", Ready(entry, 120000000)).ConfigureAwait(false);
        await b.PostAsync("/SyncPlay/Ready", Ready(entry, 120000000)).ConfigureAwait(false);
        var resumed = await a.NextCommandAsync().ConfigureAwait(false);
        await b.NextCommandAsync().ConfigureAwait(false);
        check(resumed is { Command: "Unpause", PositionTicks: 120000000 },
            "a seek made while playing resumes at the target once every member is ready");
        await a.NextUpdateAsync().ConfigureAwait(false);
        await b.NextUpdateAsync().ConfigureAwait(false);

        await CheckQueueProtocolAsync(server, a, b, group, check).ConfigureAwait(false);

        // Leave: the leaver is told, the other member is told, and the group is the other member's.
        mark = server.SyncRequestCount;
        await a.PostAsync("/SyncPlay/Leave", null).ConfigureAwait(false);
        var left = await a.NextUpdateAsync().ConfigureAwait(false);
        var told = await b.NextUpdateAsync().ConfigureAwait(false);
        check(left.Type == SyncPlayGroupUpdateTypes.GroupLeft && told.Type == SyncPlayGroupUpdateTypes.UserLeft
            && server.View(group) is { Members: [var remaining] } && remaining == b.Token,
            "Leave tells both and leaves the other session as the only member");
        frames = server.FrameCount;
        var afterLeave = server.SyncRequestCount;
        await b.PostAsync("/SyncPlay/Pause", null).ConfigureAwait(false);
        check((await b.NextCommandAsync().ConfigureAwait(false)).Command == "Pause"
            && await a.NextFrameAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false) is null
            && server.FramesTo(a.Token, frames).Count == 0
            && server.SyncRequestsSince(afterLeave).All(r => r.Token == b.Token),
            "after the leave the group's echo reaches the remaining member and never the one who left");
        await b.NextUpdateAsync().ConfigureAwait(false);
        check(server.CommandRequests(a.Token, mark) is [{ Route: "Leave" }],
            "the leaver's only request since its leave is the Leave");
    }

    /// <summary>Moving within a three-entry queue: the three moves behind the barrier, each
    /// resuming once both members are ready, the refusals that change nothing, removal, and the
    /// monotonic <c>LastUpdate</c> under all of them. Enters and leaves with the group playing and
    /// every frame drained.</summary>
    private static async Task CheckQueueProtocolAsync(SyncPlayTwoClientServer server, RawSession a, RawSession b,
        Guid group, Action<bool, string> check)
    {
        var ids = server.Episodes.Select(e => e.Id).ToArray();
        var queued = string.Join(",", ids.Select(id => $"\"{id:D}\""));
        await a.PostAsync("/SyncPlay/SetNewQueue",
            $$"""{"playingQueue":[{{queued}}],"playingItemPosition":0,"startPositionTicks":0}""").ConfigureAwait(false);
        var first = (await a.NextUpdateAsync().ConfigureAwait(false)).Queue();
        var firstB = (await b.NextUpdateAsync().ConfigureAwait(false)).Queue();
        var entries = first?.Playlist?.Select(e => e.PlaylistItem ?? Guid.Empty).ToArray() ?? [];
        check(entries.Length == 3 && entries.Distinct().Count() == 3 && !entries.Contains(Guid.Empty)
            && first!.Playlist!.Select(e => e.Item ?? Guid.Empty).SequenceEqual(ids) && first.PlayingItemIndex == 0
            && firstB?.Playlist?.Select(e => e.PlaylistItem ?? Guid.Empty).SequenceEqual(entries) == true,
            "a three-item queue mints one distinct PlaylistItemId per entry, the same for both members");
        var stamp = first!.LastUpdate ?? default;
        await a.NextUpdateAsync().ConfigureAwait(false);
        await b.NextUpdateAsync().ConfigureAwait(false);
        await a.PostAsync("/SyncPlay/Ready", Ready(entries[0], 0)).ConfigureAwait(false);
        await b.PostAsync("/SyncPlay/Ready", Ready(entries[0], 0)).ConfigureAwait(false);
        await a.NextCommandAsync().ConfigureAwait(false);
        await b.NextCommandAsync().ConfigureAwait(false);
        check((await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Paused"
            && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Paused",
            "the three-item queue settles paused on its first entry");

        string Move(Guid entry) => $$"""{"playlistItemId":"{{entry:D}}"}""";

        async Task HeldMoveAsync(string what, RawSession sender, RawSession other, string route, string json,
            int index, string reason)
        {
            var mark = server.SyncRequestCount;
            var frames = server.FrameCount;
            var before = server.View(group)!;
            server.HoldEcho();
            await sender.PostAsync("/SyncPlay/" + route, json).ConfigureAwait(false);
            check(server.CommandRequests(sender.Token, mark) is [{ } sent] && sent.Route == route
                && server.CommandRequests(other.Token, mark).Count == 0 && server.HeldCount == 1,
                $"{what}: a held {route} is counted against its sender only and queued, not decided");
            check(await sender.NextFrameAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false) is null
                && await other.NextFrameAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false) is null
                && server.FramesTo(a.Token, frames).Count == 0 && server.FramesTo(b.Token, frames).Count == 0
                && server.View(group) is { } held && held.PlayingIndex == before.PlayingIndex
                && held.State == before.State && held.QueueStamp == before.QueueStamp,
                $"{what}: while the echo is held, {route} sends nothing and moves nothing");
            server.ReleaseEcho();
            var queueA = (await a.NextUpdateAsync().ConfigureAwait(false)).Queue();
            var queueB = (await b.NextUpdateAsync().ConfigureAwait(false)).Queue();
            check(queueA is { } q && q.Reason == reason && q.PlayingItemIndex == index && q.StartPositionTicks == 0
                && q.Playlist?.Select(e => e.PlaylistItem ?? Guid.Empty).SequenceEqual(entries) == true
                && q.LastUpdate > stamp && queueB is { } r && r.PlayingItemIndex == index && r.LastUpdate == q.LastUpdate,
                $"{what}: release sends both the same queue, entry ids kept, index {index}, a later LastUpdate");
            stamp = queueA!.LastUpdate ?? default;
            check((await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting"
                && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Waiting",
                $"{what}: and waits for both members");
            await a.PostAsync("/SyncPlay/Ready", Ready(entries[index], 0)).ConfigureAwait(false);
            check(await a.NextFrameAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false) is null,
                $"{what}: one Ready of two keeps it waiting");
            await b.PostAsync("/SyncPlay/Ready", Ready(entries[index], 0)).ConfigureAwait(false);
            var startA = await a.NextCommandAsync().ConfigureAwait(false);
            var startB = await b.NextCommandAsync().ConfigureAwait(false);
            var lead = (startA.WhenUtc ?? default) - (startA.EmittedAtUtc ?? default);
            check(startA is { Command: "Unpause", PositionTicks: 0 } && startA.PlaylistItemId == entries[index]
                && startB.Command == "Unpause" && startB.When == startA.When && lead >= TimeSpan.FromMilliseconds(499)
                && (await a.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Playing"
                && (await b.NextUpdateAsync().ConfigureAwait(false)).State()?.State == "Playing",
                $"{what}: the last Ready resumes the group from zero on the new entry");
        }

        async Task RefusedAsync(string what, RawSession sender, string route, string json)
        {
            var frames = server.FrameCount;
            var before = server.View(group)!;
            await sender.PostAsync("/SyncPlay/" + route, json).ConfigureAwait(false);
            check(await a.NextFrameAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false) is null
                && await b.NextFrameAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false) is null
                && server.FramesTo(a.Token, frames).Count == 0 && server.FramesTo(b.Token, frames).Count == 0
                && server.View(group) is { } after && after.PlayingIndex == before.PlayingIndex
                && after.State == before.State && after.QueueStamp == before.QueueStamp,
                what);
        }

        await HeldMoveAsync("NextItem while paused", a, b, "NextItem", Move(entries[0]), 1, "NextItem").ConfigureAwait(false);
        await RefusedAsync("a NextItem naming an entry that is no longer playing changes nothing", a, "NextItem",
            Move(entries[0])).ConfigureAwait(false);
        await HeldMoveAsync("PreviousItem while playing", b, a, "PreviousItem", Move(entries[1]), 0, "PreviousItem")
            .ConfigureAwait(false);
        await RefusedAsync("a PreviousItem at the start of the queue changes nothing", b, "PreviousItem",
            Move(entries[0])).ConfigureAwait(false);
        await HeldMoveAsync("SetPlaylistItem", a, b, "SetPlaylistItem", Move(entries[2]), 2, "SetCurrentItem")
            .ConfigureAwait(false);
        await RefusedAsync("a NextItem at the end of the queue changes nothing", a, "NextItem", Move(entries[2]))
            .ConfigureAwait(false);
        await RefusedAsync("a SetPlaylistItem naming no entry changes nothing", a, "SetPlaylistItem",
            Move(Guid.NewGuid())).ConfigureAwait(false);

        var removeMark = server.SyncRequestCount;
        await b.PostAsync("/SyncPlay/RemoveFromPlaylist",
            $$"""{"playlistItemIds":["{{entries[1]:D}}"],"clearPlayingItem":true,"clearPlaylist":false}""")
            .ConfigureAwait(false);
        var removedA = (await a.NextUpdateAsync().ConfigureAwait(false)).Queue();
        var removedB = (await b.NextUpdateAsync().ConfigureAwait(false)).Queue();
        check(removedA is { Reason: "RemoveItems", PlayingItemIndex: 1 } shorter
            && shorter.Playlist?.Select(e => e.PlaylistItem ?? Guid.Empty).SequenceEqual([entries[0], entries[2]]) == true
            && shorter.LastUpdate > stamp && removedB is { } other && other.LastUpdate == shorter.LastUpdate
            && other.Playlist?.Count == 2 && server.CommandRequests(b.Token, removeMark) is [{ Route: "RemoveFromPlaylist" }],
            "RemoveFromPlaylist sends both the shorter queue, still playing the same entry, with a later LastUpdate");
        check(await a.NextFrameAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false) is null
            && await b.NextFrameAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false) is null
            && server.View(group) is { State: "Playing", PlaylistItemId: { } still } && still == entries[2],
            "removing an entry that is not playing leaves the group's state alone");
    }

    private static string Ready(Guid entry, long ticks)
        => $$"""{"playlistItemId":"{{entry:D}}","positionTicks":{{ticks}},"isPlaying":false,"when":"{{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}}"}""";

    private static async Task CheckMediaAsync(SyncPlayTwoClientServer server, RawSession b, byte[] bytes,
        Action<bool, string> check)
    {
        var path = $"/Videos/{server.ItemId:D}/stream?static=true";
        using (var head = await b.SendAsync(HttpMethod.Head, path, null).ConfigureAwait(false))
            check(head.StatusCode == System.Net.HttpStatusCode.OK && head.Content.Headers.ContentLength == bytes.Length
                && head.Headers.AcceptRanges.Contains("bytes"),
                "HEAD answers the whole length and advertises byte ranges");
        using (var partial = await b.SendAsync(HttpMethod.Get, path, "bytes=100-199").ConfigureAwait(false))
        {
            var body = await partial.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            check(partial.StatusCode == System.Net.HttpStatusCode.PartialContent && body.Length == 100
                && body.AsSpan().SequenceEqual(bytes.AsSpan(100, 100))
                && partial.Content.Headers.ContentRange?.ToString() == $"bytes 100-199/{bytes.Length}",
                "a Range request gets 206 and exactly those bytes");
        }
        using (var tail = await b.SendAsync(HttpMethod.Get, path, "bytes=199990-").ConfigureAwait(false))
        {
            var body = await tail.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            check(tail.StatusCode == System.Net.HttpStatusCode.PartialContent && body.Length == 10
                && body.AsSpan().SequenceEqual(bytes.AsSpan(199_990)),
                "an open-ended Range runs to the end of the file");
        }
        using (var past = await b.SendAsync(HttpMethod.Get, path, $"bytes={bytes.Length}-").ConfigureAwait(false))
            check(past.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable,
                "a Range past the end gets 416");
        check(await RawSession.AnonymousStatusAsync(server, path).ConfigureAwait(false) == 401,
            "media without a token is refused");
        check(server.Http.MediaServes.Where(s => s.Status is 200 or 206).All(s => s.Token == b.Token)
            && server.Http.MediaServes.Count(s => s.Token == b.Token) == 4,
            "every media answer is attributed to the session that asked");
    }

    private static async Task CheckSerializedSendsAsync(SyncPlayTwoClientServer server, RawSession b,
        Action<bool, string> check)
    {
        var connection = server.Http.Connections.Last(c => c.IsOpen && c.Token == b.Token);
        var sends = Enumerable.Range(0, 64)
            .Select(i => connection.SendAsync($$"""{"MessageType":"Probe","Data":{{i}}}""")).ToArray();
        var failed = false;
        try { await Task.WhenAll(sends).ConfigureAwait(false); }
        catch (InvalidOperationException) { failed = true; }
        var received = new List<string>();
        for (var i = 0; i < 64 && await b.NextFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) is { } frame; i++)
            received.Add(frame);
        // What the two-client mode relies on, and NOT proof that ServerConnection's own gate is
        // what provides it: the runtime's managed WebSocket also serializes concurrent sends, so
        // this stays green with the gate removed. The gate is kept so the order is this fixture's
        // guarantee rather than a runtime implementation detail.
        check(!failed && received.Count == 64
            && received.Select((frame, i) => frame.Contains($"\"Data\":{i}}}", StringComparison.Ordinal)).All(x => x),
            "64 concurrent sends on one socket all arrive, in the order they were queued");
    }

    private static async Task Settle(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);
    }

    /// <summary>A bare client of the synthetic server: its own HttpClient and, once connected, its
    /// own session socket whose frames are pumped into a channel — reading a ClientWebSocket with a
    /// timeout would abort it, a channel read with one does not.</summary>
    private sealed class RawSession : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly string _origin;
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
        private ClientWebSocket? _socket;

        private RawSession(string origin, string token, Guid userId)
        {
            _origin = origin;
            Token = token;
            UserId = userId;
        }

        public string Token { get; }
        public Guid UserId { get; }

        public static async Task<RawSession> SignInAsync(SyncPlayTwoClientServer server, string device)
        {
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/Users/AuthenticateByName")
            {
                Content = new StringContent("""{"Username":"TEST","Pw":"x"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization",
                $"MediaBrowser Client=\"Harness\", Device=\"raw\", DeviceId=\"{device}\", Version=\"1\"");
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return new RawSession(server.Origin, body.RootElement.GetProperty("AccessToken").GetString()!,
                body.RootElement.GetProperty("User").GetProperty("Id").GetGuid());
        }

        public static async Task<int> AnonymousStatusAsync(SyncPlayTwoClientServer server, string path)
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(server.Origin + path).ConfigureAwait(false);
            return (int)response.StatusCode;
        }

        public async Task ConnectAsync()
        {
            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("Authorization", $"MediaBrowser Token=\"{Token}\"");
            await _socket.ConnectAsync(new Uri(_origin.Replace("http://", "ws://") + "/socket"),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
            _ = PumpAsync(_socket);
        }

        private async Task PumpAsync(ClientWebSocket socket)
        {
            var buffer = new byte[16 * 1024];
            var assembled = new List<byte>();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    assembled.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                    if (!result.EndOfMessage)
                        continue;
                    _frames.Writer.TryWrite(Encoding.UTF8.GetString([.. assembled]));
                    assembled.Clear();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            _frames.Writer.TryComplete();
        }

        public async Task<string?> NextFrameAsync(TimeSpan timeout)
        {
            using var limit = new CancellationTokenSource(timeout);
            try { return await _frames.Reader.ReadAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (ChannelClosedException) { return null; }
        }

        public async Task<SyncPlayGroupUpdateMessage> NextUpdateAsync()
        {
            var frame = await NextFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                ?? throw new TimeoutException("no group update arrived");
            return SessionMessageReader.TryRead(frame, out var type, out var data)
                && type == SessionMessageTypes.SyncPlayGroupUpdate
                && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is { } update
                ? update : throw new InvalidOperationException("expected a group update, got " + Kind(frame));
        }

        public async Task<SyncPlayCommandMessage> NextCommandAsync()
        {
            var frame = await NextFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                ?? throw new TimeoutException("no command arrived");
            return SessionMessageReader.TryRead(frame, out var type, out var data)
                && type == SessionMessageTypes.SyncPlayCommand
                && SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(data) is { } command
                ? command : throw new InvalidOperationException("expected a command, got " + Kind(frame));
        }

        private static string Kind(string frame)
            => SessionMessageReader.TryRead(frame, out var type, out _) ? type : "an unreadable frame";

        public Task PostAsync(string path, string? json)
            => SendAsync(HttpMethod.Post, path, null, json).ContinueWith(t =>
            {
                using var response = t.Result;
                response.EnsureSuccessStatusCode();
            }, TaskScheduler.Default);

        public async Task<int> GetStatusAsync(string path)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null).ConfigureAwait(false);
            return (int)response.StatusCode;
        }

        public async Task<string> GetStringAsync(string path)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? range, string? json = null)
        {
            var request = new HttpRequestMessage(method, _origin + path);
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");
            if (range is not null)
                request.Headers.TryAddWithoutValidation("Range", range);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return _http.SendAsync(request);
        }

        public void Dispose()
        {
            _socket?.Dispose();
            _http.Dispose();
        }
    }
}
