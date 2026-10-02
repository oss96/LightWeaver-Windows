using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Jellyfin;
using System.Reflection;

/// <summary>
/// The SyncPlay half that has to touch mpv: the two seams a group binding reads the player
/// through, measured against a real libmpv instance rather than reasoned about.
///
/// <para>Both seams exist because the group asks questions the player could not answer honestly
/// before. <c>TryReadPosition</c> is a position asked FOR NOW, from whatever thread the SyncPlay
/// receive loop happens to be on, and it has to distinguish "mpv has no position" from zero —
/// a Ready report carrying a stale or invented position is a seek for every other member.
/// <c>PlaybackRestarted</c> is the seek-settled signal the player did not have: FILE_LOADED is the
/// demuxer's headers and says nothing about a mid-play seek, so "the new position is actually
/// being played" had no event at all.</para>
///
/// <para>Offscreen by construction: LIGHTWEAVER_MPV_NO_VIDEO puts mpv on vo=null and wid is 0, so
/// no window is created, no GPU adapter is opened and nothing takes the desktop. The clip is local
/// and no server is involved — this mode is about the player, not the protocol
/// (<c>--sync-play</c> owns that half).</para>
/// </summary>
internal static class SyncPlayBindingFixture
{
    /// <summary>How far <c>TryReadPosition</c> may sit from <see cref="MpvPlayer.TimePos"/> around
    /// it. One observe interval plus scheduling; a read that is genuinely synchronous lands inside
    /// the bracket, and a stale one measured against a moving clip does not.</summary>
    private const double PositionToleranceSeconds = 0.5;

    private static int _failures;
    private static int _assertions;

    public static int Run()
    {
        if (!TryBuildClip(out var clip))
            return 0;
        // Process-wide, so both are restored: this mode is its own process today, but the default
        // run drives a real MainWindow and a leaked vo=null would cost someone an hour.
        var previousNoVideo = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO");
        var previousNoAudio = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO");
        try
        {
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
            CheckReadPosition(clip);
            CheckPlaybackRestarted(clip);
            CheckBinding(clip);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", previousNoVideo);
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", previousNoAudio);
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} "
            + $"({_assertions} sync play binding assertions, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- the legs ----------------------------------------------------------------------

    /// <summary>L2 — <see cref="MpvPlayer.TryReadPosition"/>.
    ///
    /// <para>The off-thread half is the point: the SyncPlay receive loop is the caller, so the read
    /// is measured FROM a background thread while the UI thread keeps pumping, which is the only
    /// arrangement in which "libmpv serializes property reads through the playback core" is a claim
    /// about anything. The two boundary answers are asserted with it, because they are what
    /// separates this from <see cref="MpvPlayer.TimePos"/>: nothing loaded reports no position
    /// rather than zero, and a disposed player answers false where a property read would throw
    /// <see cref="ObjectDisposedException"/> on the caller's thread.</para></summary>
    private static void CheckReadPosition(string clip)
    {
        Test("TryReadPosition answers off the UI thread, and answers false where it cannot answer", () =>
        {
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                new AppSettings { HardwareDecoding = false });

            // Nothing loaded: mpv publishes time-pos as unavailable, which is not a position of 0.
            var idleRead = player.TryReadPosition(out var idlePosition);
            Expect(false, idleRead, "a player with nothing loaded reports no position");
            Expect(0d, idlePosition, "and leaves the out parameter at zero");

            player.LoadFile(clip);
            var loadDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!(player.TryReadPosition(out var p) && p > 0) && DateTime.UtcNow < loadDeadline)
                Pump(TimeSpan.FromMilliseconds(50));
            if (!player.TryReadPosition(out var playing) || playing <= 0)
                throw new InvalidOperationException(
                    $"{clip} never reached a playing position within 20 s; the leg below would be "
                    + "measuring a player that is not running.");

            // From a background thread, while the UI thread pumps. TimePos is read on both sides of
            // each call so the comparison is a bracket rather than a race with the clip's own
            // motion: a synchronous read lands inside it, a one-interval-stale one does not.
            var samples = 0;
            var unavailable = 0;
            var maxDelta = 0d;
            Exception? thrown = null;
            var reader = Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var before = player.TimePos;
                        var read = player.TryReadPosition(out var position);
                        var after = player.TimePos;
                        if (!read)
                        {
                            unavailable++;
                            continue;
                        }
                        samples++;
                        var low = Math.Min(before, after);
                        var high = Math.Max(before, after);
                        var delta = position < low ? low - position : position > high ? position - high : 0d;
                        maxDelta = Math.Max(maxDelta, delta);
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            });
            while (!reader.IsCompleted)
                Pump(TimeSpan.FromMilliseconds(20));
            reader.GetAwaiter().GetResult();
            if (thrown is not null)
                throw new InvalidOperationException(
                    $"TryReadPosition threw off the UI thread: {thrown.GetType().Name}: {thrown.Message}");
            Expect(40, samples, "every off-thread read of a playing file answered");
            Expect(0, unavailable, "and none of them reported the position unavailable");
            Expect(true, maxDelta <= PositionToleranceSeconds, FormattableString.Invariant(
                $"off-thread reads stay within {PositionToleranceSeconds:F1}s of TimePos around them (worst {maxDelta:F3}s)"));

            player.Dispose();
            var disposedRead = player.TryReadPosition(out var disposedPosition);
            Expect(false, disposedRead, "a disposed player reports no position instead of throwing");
            Expect(0d, disposedPosition, "and leaves the out parameter at zero");

            Console.WriteLine(FormattableString.Invariant(
                $"  SYNCPLAY-BINDING EVIDENCE: try_read_position samples={samples} unavailable={unavailable} max_delta_s={maxDelta:F3} tolerance_s={PositionToleranceSeconds:F1} threw=none idle_read={idleRead} disposed_read={disposedRead} position_at_start={playing:F3}s"));
        });
    }

    /// <summary>L3 — <see cref="MpvPlayer.PlaybackRestarted"/>.
    ///
    /// <para>Four claims, and the second is the reason the event exists: a mid-play seek
    /// produces a FURTHER raise. Nothing else in the player reports that — FILE_LOADED already
    /// fired for this file and SetLoading(false) is a transition that is silent the second time —
    /// so a group that has just told every member to seek has no local signal that the seek
    /// landed. The payload is checked against what FileLoaded carried, because a Ready report
    /// stamped with the wrong generation answers for the file the user is no longer watching —
    /// and the last claim forces the only state in which WHERE that stamp is read can be seen at
    /// all.</para></summary>
    private static void CheckPlaybackRestarted(string clip)
    {
        Test("PlaybackRestarted fires per load and per seek, carrying the load's generation", () =>
        {
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                new AppSettings { HardwareDecoding = false });
            var clock = Stopwatch.StartNew();
            var restarts = new List<(int Generation, long Ms)>();
            var loaded = new List<int>();
            player.FileLoaded += generation => loaded.Add(generation);
            player.PlaybackRestarted += generation => restarts.Add((generation, clock.ElapsedMilliseconds));

            var first = player.LoadFile(clip);
            WaitFor(() => restarts.Count > 0 && player.Duration > 0, TimeSpan.FromSeconds(20));
            Expect(true, restarts.Count > 0, "the initial load raised PlaybackRestarted");
            Expect(true, loaded.Count > 0, "and FileLoaded raised for the same load");
            Expect(loaded[0], restarts[0].Generation, "the raise carries the generation FileLoaded carried");
            Expect(first, restarts[0].Generation, "which is the generation LoadFile returned");
            var afterLoad = restarts.Count;
            // Read while a file is up: the last load below is a deliberate miss, and mpv has no
            // current-vo to report by the time the evidence line is printed.
            var vo = player.GetPropertyString("current-vo");

            // The seek-settled claim. Mid-clip and well clear of the current position, so mpv has
            // to move and cannot answer out of what is already decoded.
            var target = Math.Round(player.Duration * 0.6, 3);
            player.TimePos = target;
            WaitFor(() => restarts.Count > afterLoad, TimeSpan.FromSeconds(10));
            Expect(true, restarts.Count > afterLoad, "a mid-clip seek raised PlaybackRestarted again");
            Expect(first, restarts[^1].Generation, "the seek's raise still names the file it seeked in");
            var landed = player.TryReadPosition(out var position) ? position : double.NaN;
            var afterSeek = restarts.Count;

            var second = player.LoadFile(clip);
            Expect(true, second != first, "loading again moved the load generation");
            WaitFor(() => restarts.Any(r => r.Generation == second), TimeSpan.FromSeconds(20));
            Expect(true, restarts.Count > afterSeek, "the second load raised PlaybackRestarted");
            Expect(second, restarts[^1].Generation, "carrying the new generation, not the old one");
            var afterSecondLoad = restarts.Count;

            // Where the payload is STAMPED, which the three claims above cannot see: they only ever
            // deliver a raise while the generation it belongs to is still the live one, so a
            // payload read at delivery time would answer them correctly too. The state that tells
            // them apart is the one CheckPositionGeneration forces for positions — the dispatcher
            // BLOCKED while mpv posts, so the raise is provably still queued when the next load
            // bumps the event generation under it. The incoming file does not exist, so mpv can
            // never raise a restart of its own for it and the delivered one is provably the
            // outgoing file's.
            player.TimePos = Math.Round(player.Duration * 0.2, 3);
            Thread.Sleep(600);
            Expect(afterSecondLoad, restarts.Count, "the raise stayed queued while the thread was blocked");
            var superseding = player.LoadFile(Path.Combine(Path.GetTempPath(),
                "lightweaver-no-such-file-" + Guid.NewGuid().ToString("N") + ".mkv"));
            // Still blocked, so mpv's event thread takes the new file's generation while the queued
            // raise cannot run. This is the window a payload stamped at delivery time gets wrong.
            Thread.Sleep(200);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Expect(true, restarts.Count > afterSecondLoad, "the queued raise was delivered after the load");
            Expect(second, restarts[^1].Generation, "and names the file that restarted, not the load that superseded it");
            Expect(superseding, player.LoadGeneration, "while the player itself is on the incoming generation");

            var payloads = string.Join(",", restarts.Select(r => FormattableString.Invariant($"{r.Generation}@{r.Ms}ms")));
            Console.WriteLine(FormattableString.Invariant(
                $"  SYNCPLAY-BINDING EVIDENCE: playback_restarted duration={player.Duration:F2}s load_gen={first} file_loaded_gen={loaded[0]} raises_after_load={afterLoad} seek_target={target:F3}s landed={landed:F3}s raises_after_seek={afterSeek - afterLoad} second_load_gen={second} raises_after_second_load={afterSecondLoad - afterSeek} blocked_seek_raises={restarts.Count - afterSecondLoad} blocked_payload={restarts[^1].Generation} superseded_by_gen={superseding} payloads={payloads} vo={vo}"));
        });
    }

    private sealed class Reporter : ISyncPlayReporter
    {
        public readonly List<(bool Playing, long Ticks)> Ready = [];
        public readonly List<SyncPlayReportContext?> ReadyContexts = [];
        public readonly List<(bool Buffering, long Ticks, bool Playing)> Buffers = [];
        public bool ReportedBuffer;
        public SyncPlayReportContext? ReportContext { get; set; } = new(1, Guid.NewGuid(), Guid.NewGuid());
        public Task ReportReadyAsync(bool isPlaying, long positionTicks, SyncPlayReportContext? expectedContext = null)
        {
            Ready.Add((isPlaying, positionTicks));
            ReadyContexts.Add(expectedContext);
            return Task.CompletedTask;
        }
        public bool ReportBuffering(bool buffering, long positionTicks, bool isPlaying, bool ready = true,
            SyncPlayReportContext? expectedContext = null)
        {
            Buffers.Add((buffering, positionTicks, isPlaying));
            if (buffering || !ReportedBuffer || !ready) return false;
            ReportedBuffer = false;
            Ready.Add((isPlaying, positionTicks));
            return true;
        }
    }

    private static void CheckBinding(string clip)
    {
        Test("binding rejects expired report identity and refreshes a restored loaded item", () =>
        {
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                new AppSettings { HardwareDecoding = false });
            var reporter = new Reporter();
            var groupPresent = true;
            using var binding = new SyncPlayPlayerBinding(player, Dispatcher.CurrentDispatcher, reporter,
                () => groupPresent
                    ? (new SyncPlayState(DateTime.UtcNow, reporter.ReportContext?.PlaylistItemId), TimeSpan.Zero)
                    : null);
            var gen = player.LoadFile(clip);
            var original = reporter.ReportContext!.Value;
            binding.ArmForLoad(gen, true, original);
            WaitFor(() => reporter.Ready.Count == 1, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "current identity reports load Ready");
            Expect<SyncPlayReportContext?>(original, reporter.ReadyContexts[^1], "load Ready carries captured context");
            reporter.Ready.Clear();
            binding.Apply(new(SyncPlayActionKind.SeekAndArmUnpause,
                DueLocalUtc: DateTime.UtcNow.AddMilliseconds(300)), gen);
            var restored = original with { MembershipEpoch = original.MembershipEpoch + 1 };
            reporter.ReportContext = restored;
            void Buffer(bool value) => typeof(SyncPlayPlayerBinding)
                .GetMethod("OnBufferingChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(binding, [value]);
            Buffer(true);
            Buffer(false);
            binding.Apply(new(SyncPlayActionKind.UnpauseNow), gen);
            Expect(SyncPlayPlayerState.Idle, binding.ReadState(), "expired membership reads idle");
            Pump(TimeSpan.FromMilliseconds(450));
            Expect(true, player.Pause, "expired membership blocks action and due timer");
            Expect(0, reporter.Ready.Count, "expired membership sends no Ready");
            Expect(0, reporter.Buffers.Count, "expired membership sends no buffering edges");
            binding.RefreshReportContext(original);
            Expect(0, reporter.Ready.Count, "refresh rejects an old context");
            binding.RefreshReportContext(restored);
            Expect(1, reporter.Ready.Count, "same loaded item reports Ready after membership restore");
            Expect<SyncPlayReportContext?>(restored, reporter.ReadyContexts[^1], "restored Ready carries the new epoch");
            Expect(true, binding.ReadState().IsPaused, "restored membership reads the loaded player");

            reporter.Ready.Clear();
            reporter.ReportContext = restored with { PlaylistItemId = Guid.NewGuid() };
            binding.Apply(new(SyncPlayActionKind.Seek, 20 * TimeSpan.TicksPerSecond), gen);
            Buffer(true);
            Buffer(false);
            binding.RefreshReportContext(reporter.ReportContext.Value);
            Pump(TimeSpan.FromMilliseconds(100));
            Expect(true, player.TimePos < .3, "expired queue entry cannot seek the outgoing file");
            Expect(0, reporter.Ready.Count, "a different queue item cannot refresh the outgoing binding");
            Expect(0, reporter.Buffers.Count, "expired queue entry sends no buffering edges");
            binding.ArmForLoad(gen, true, restored);
            Expect(SyncPlayPlayerState.Idle, binding.ReadState(), "arm rejects an explicitly stale context");

            gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, true, reporter.ReportContext);
            WaitFor(() => reporter.Ready.Count == 1, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "new queue entry can arm its own load");
            reporter.Ready.Clear();
            groupPresent = false;
            Buffer(true);
            Buffer(false);
            binding.Apply(new(SyncPlayActionKind.UnpauseNow), gen);
            Expect(SyncPlayPlayerState.Idle, binding.ReadState(), "absent group reads idle even with matching context");
            Expect(true, player.Pause, "absent group blocks player actions");
            Expect(0, reporter.Ready.Count, "absent group sends no Ready");
            Expect(0, reporter.Buffers.Count, "absent group sends no buffering edges");
            Console.WriteLine("  SYNCPLAY-BINDING EVIDENCE: expired_epoch=quiet expired_entry=quiet restored_same_file=ready current_context=carried vo=null audio=disabled");
        });

        Test("binding seeks, readies, schedules, rejects stale loads, resets and disposes", () =>
        {
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                new AppSettings { HardwareDecoding = false });
            var reporter = new Reporter();
            using var binding = new SyncPlayPlayerBinding(player, Dispatcher.CurrentDispatcher, reporter,
                () => (new SyncPlayState(DateTime.UtcNow, reporter.ReportContext?.PlaylistItemId), TimeSpan.Zero));
            var gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, startPaused: true);
            WaitFor(() => reporter.Ready.Count == 1, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "paused load reports ready once");
            Expect(false, reporter.Ready[0].Playing, "load ready is paused");
            var atStart = player.TimePos;
            Pump(TimeSpan.FromMilliseconds(300));
            Expect(true, Math.Abs(player.TimePos - atStart) < .1, "paused load does not advance");
            Expect("null", player.GetPropertyString("current-vo"), "fixture has no video window");
            var offThread = Task.Run(binding.ReadState).GetAwaiter().GetResult();
            Expect(true, offThread.IsPaused, "socket read uses cached state");

            void Seek(double target, SyncPlayActionKind kind = SyncPlayActionKind.Seek, DateTime due = default)
            {
                reporter.Ready.Clear();
                binding.Apply(new(kind, (long)(target * TimeSpan.TicksPerSecond), due, true), gen);
                WaitFor(() => reporter.Ready.Count > 0, TimeSpan.FromSeconds(5));
                Pump(TimeSpan.FromMilliseconds(30));
                Expect(1, reporter.Ready.Count, kind + " reports Ready once");
                Expect(true, Math.Abs(reporter.Ready[0].Ticks / (double)TimeSpan.TicksPerSecond - target) < .3,
                    kind + " Ready contains the landed position");
            }

            Seek(5);
            Expect(true, player.Pause, "seek stays paused");
            Expect(false, reporter.Ready[0].Playing, "seek Ready is paused");
            var due = DateTime.UtcNow.AddMilliseconds(700);
            DateTime? unpaused = null;
            player.PauseChanged += paused => { if (!paused) unpaused = DateTime.UtcNow; };
            Seek(8, SyncPlayActionKind.SeekAndArmUnpause, due);
            Expect(true, player.Pause, "pre-roll remains paused after seek");
            WaitFor(() => unpaused is not null, TimeSpan.FromSeconds(2));
            Expect(true, unpaused is not null, "due timer unpauses");
            var late = (unpaused!.Value - due).TotalMilliseconds;
            Expect(true, late >= -50 && late <= 100, $"unpause timing is within bounds ({late:0} ms)");
            Expect(1, reporter.Ready.Count, "timer unpause sends no extra Ready");

            var order = new List<string>();
            player.PauseChanged += paused => { if (paused) order.Add("pause"); };
            player.PlaybackRestarted += _ => order.Add("restart");
            Seek(11, SyncPlayActionKind.Pause);
            Expect(true, order.IndexOf("pause") >= 0 && order.IndexOf("pause") < order.IndexOf("restart"),
                "pause lands before seek restart");
            Expect(false, reporter.Ready[0].Playing, "pause-seek Ready is paused");

            reporter.Ready.Clear();
            binding.Apply(new(SyncPlayActionKind.UnpauseNow), gen);
            WaitFor(() => !player.Pause, TimeSpan.FromSeconds(2));
            binding.Apply(new(SyncPlayActionKind.DriftSeek, 15 * TimeSpan.TicksPerSecond, NeedsSeek: true), gen);
            WaitFor(() => player.TimePos >= 14.9, TimeSpan.FromSeconds(3));
            Expect(true, player.TimePos >= 14.9, "drift seek moved the player");
            Expect(false, player.Pause, "drift seek preserved transport");
            Expect(0, reporter.Ready.Count, "drift seek sends no Ready");

            binding.Apply(new(SyncPlayActionKind.SeekAndArmUnpause, DueLocalUtc: DateTime.UtcNow.AddMilliseconds(500)), gen);
            var stale = gen;
            gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, true);
            WaitFor(() => reporter.Ready.Count == 1, TimeSpan.FromSeconds(5));
            reporter.Ready.Clear();
            binding.Apply(new(SyncPlayActionKind.Seek, 20 * TimeSpan.TicksPerSecond), stale);
            Pump(TimeSpan.FromMilliseconds(700));
            Expect(true, player.Pause && player.TimePos < .3, "old action and due timer cannot change new load");
            Expect(0, reporter.Ready.Count, "stale action has no Ready");

            // These edges call the private event handler: forwarding and Ready ownership are
            // measured, not mpv's network-cache detection, which this local clip cannot produce.
            void Buffer(bool value) => typeof(SyncPlayPlayerBinding)
                .GetMethod("OnBufferingChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(binding, [value]);
            binding.Apply(new(SyncPlayActionKind.Seek, 7 * TimeSpan.TicksPerSecond), gen);
            Buffer(true);
            Buffer(false);
            Expect(0, reporter.Ready.Count, "short buffer clear cannot ready before seek settles");
            WaitFor(() => reporter.Ready.Count == 1, TimeSpan.FromSeconds(5));
            Expect(1, reporter.Ready.Count, "short buffering preserves the seek Ready");
            Expect(true, reporter.Buffers.Count == 2 && reporter.Buffers[0].Buffering
                && !reporter.Buffers[1].Buffering, "both buffer edges reach reporter");
            reporter.Ready.Clear();
            reporter.ReportedBuffer = true;
            Buffer(true);
            Buffer(false);
            Expect(1, reporter.Ready.Count, "reported buffer clear owns exactly one Ready");
            Expect(true, reporter.Buffers[^1].Ticks > 0, "buffer report reads real position");

            reporter.Ready.Clear();
            binding.Apply(new(SyncPlayActionKind.SeekAndArmUnpause, DueLocalUtc: DateTime.UtcNow.AddMilliseconds(250)), gen);
            binding.Reset();
            Pump(TimeSpan.FromMilliseconds(400));
            Expect(true, player.Pause, "reset cancels pending unpause");
            binding.Apply(new(SyncPlayActionKind.UnpauseNow), gen);
            Expect(true, player.Pause, "reset binding rejects further actions");
            binding.ArmForLoad(gen, true);
            binding.Apply(new(SyncPlayActionKind.SeekAndArmUnpause, DueLocalUtc: DateTime.UtcNow.AddMilliseconds(250)), gen);
            binding.Dispose();
            Pump(TimeSpan.FromMilliseconds(400));
            Expect(true, player.Pause, "dispose cancels pending unpause");
            player.TimePos = 9;
            Pump(TimeSpan.FromMilliseconds(250));
            Expect(0, reporter.Ready.Count, "disposed binding unsubscribes from player events");
            Console.WriteLine($"  SYNCPLAY-BINDING EVIDENCE: seek_ready=once due_late_ms={late:0} stale=ignored reset=cancelled disposed=quiet buffering_edges=injected vo=null audio=disabled");
        });

        Test("deferred actions use the loaded position and preserve load readiness", () =>
        {
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                new AppSettings { HardwareDecoding = false });
            var reporter = new Reporter();
            var now = DateTime.UtcNow;
            using var binding = new SyncPlayPlayerBinding(player, Dispatcher.CurrentDispatcher, reporter,
                () => (new SyncPlayState(DateTime.UtcNow, reporter.ReportContext?.PlaylistItemId), TimeSpan.Zero), () => now);
            var gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, true);
            binding.Apply(new(SyncPlayActionKind.Seek, 3 * TimeSpan.TicksPerSecond), gen);
            binding.Apply(new(SyncPlayActionKind.Seek, 12 * TimeSpan.TicksPerSecond), gen);
            WaitFor(() => reporter.Ready.Count > 0, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "latest deferred seek produces one Ready");
            Expect(true, Math.Abs(reporter.Ready[0].Ticks / (double)TimeSpan.TicksPerSecond - 12) < .3,
                "latest deferred action wins");

            reporter.Ready.Clear();
            gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, true);
            binding.Apply(new(SyncPlayActionKind.Pause), gen);
            WaitFor(() => reporter.Ready.Count > 0, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "deferred pause without seek retains load Ready");

            reporter.Ready.Clear();
            gen = player.LoadFile(clip);
            binding.ArmForLoad(gen, true);
            binding.Apply(new(SyncPlayActionKind.SeekAndArmUnpause, 5 * TimeSpan.TicksPerSecond,
                now.AddSeconds(1), NeedsSeek: false), gen);
            now = now.AddSeconds(3);
            WaitFor(() => reporter.Ready.Count > 0 && !player.Pause, TimeSpan.FromSeconds(10));
            Expect(1, reporter.Ready.Count, "expired deferred pre-roll sends one Ready");
            Expect(true, player.TimePos >= 6.9 && player.TimePos < 7.5,
                "expired pre-roll extrapolates and re-evaluates NeedsSeek against new file");
            Console.WriteLine("  SYNCPLAY-BINDING EVIDENCE: deferred=latest expired_preroll_target=7s load_ready=retained");
        });
    }

    // ---- the clip ----------------------------------------------------------------------

    /// <summary>A small Matroska clip, built the way <c>PartialStreamFixture</c> builds its own.
    /// Thirty seconds rather than that fixture's six: the legs here seek mid-clip and wait for the
    /// seek to settle, and a clip that reaches EOF underneath them would be measuring keep-open
    /// instead. The encode parameters are in the name so a change here cannot silently reuse
    /// another fixture's cached file. Reports the skip and returns false when ffmpeg is
    /// unavailable — it is a developer tool, not a dependency of the app.</summary>
    internal static bool TryBuildClip(out string clip)
    {
        clip = Path.Combine(Path.GetTempPath(), "lightweaver-tests", "syncplay-320x240-10fps-30s.mkv");
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return true;
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        try
        {
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                "-y -loglevel error -f lavfi -i testsrc2=duration=30:size=320x240:rate=10 "
                + $"-c:v libx264 -pix_fmt yuv420p \"{clip}\"")
            { UseShellExecute = false });
            ffmpeg!.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Console.WriteLine("SKIP: the SyncPlay binding legs need ffmpeg on PATH to build their clip.");
            return false;
        }
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return true;
        Console.WriteLine("SKIP: ffmpeg produced no usable clip at " + clip + ".");
        return false;
    }

    // ---- assertions --------------------------------------------------------------------

    /// <summary>Pumps until the condition holds or the budget runs out; the caller asserts the
    /// condition itself, so a timeout reports as the named claim rather than as a timeout.</summary>
    private static void WaitFor(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (!condition() && DateTime.UtcNow < deadline)
            Pump(TimeSpan.FromMilliseconds(20));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Expect<T>(T expected, T actual, string what)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{what}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}.");
    }

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL: {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
