using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using LightWeaver.Player;
using LightWeaver.Settings;

/// <summary>Proves that a single relative seek moves playback by exactly the requested amount.
///
/// The fixture builds its own clip so the keyframe grid is known and does not divide the seek
/// sizes: testsrc2 at 30 fps with <c>-g 270 -sc_threshold 0</c> is a 9 second GOP, so keyframes
/// fall at 0, 9, 18, 27, 36, 45, 54 ... and every target below lands strictly between two of
/// them. <c>-sc_threshold 0</c> is required — without it x264 adds scene-cut keyframes and the
/// grid stops being predictable.
///
/// Under mpv's <c>hr-seek=default</c> an ABSOLUTE seek is precise while a RELATIVE seek is
/// keyframe-limited, which is the whole point of the control leg: if seeking to 20.0 exactly
/// works and seeking +10 from there does not, the defect is in the relative path alone.
///
/// Measured on the unfixed build — this is the evidence the fix was built from, and what a future
/// red run should be compared against:
///   20.0 +10 -> target 30, measured 36 (next keyframe)
///   20.0 -10 -> target 10, measured  9 (previous keyframe)
///   20.0 +30 -> target 50, measured 54 (next keyframe)
///   50.0 -30 -> target 20, measured 18 (previous keyframe)
/// Every landing is the keyframe PAST the target in its direction of travel: it overshoots, and
/// never undershoots. Post-fix every leg reports an error of 0.000. Do not relax an assertion to
/// make this green — the 0.25 s tolerance is what keeps all four legs load-bearing.
///
/// Runs offscreen: LIGHTWEAVER_MPV_NO_VIDEO puts mpv on vo=null and wid is 0, so no window is
/// created, no GPU adapter is opened and nothing takes the desktop.</summary>
internal static class SeekMagnitudeFixture
{
    /// <summary>The encode parameters are IN the file name on purpose. The guest UIA suite
    /// (<c>.claude/skills/verify/bug-tests/test-seek-magnitude.ps1</c>) builds its own clip in this
    /// same directory for the same bug; when both used one generic name, whichever ran first won
    /// and the other silently measured a clip it did not describe. A parameter change on either
    /// side now produces a different name instead of poisoning the other.</summary>
    private static readonly string ClipPath =
        Path.Combine(Path.GetTempPath(), "lightweaver-tests", "seek-gop9-640x360-30fps.mp4");

    /// <summary>Reports the skip and returns true when the clip cannot be produced. ffmpeg is a
    /// developer tool, not a dependency of the app: an absent one is nothing to test, not a
    /// failure.</summary>
    public static bool TrySkip()
    {
        // A cached clip is reused, but only if it is plausibly complete. A run killed mid-encode
        // leaves a short file behind, and reusing that surfaces as the confusing "never loaded"
        // throw in Run() instead of simply rebuilding. 180 s of x264 is far more than 64 KiB.
        if (File.Exists(ClipPath) && new FileInfo(ClipPath).Length > 64 * 1024) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(ClipPath)!);
        try
        {
            // 9 second GOP at 30 fps; -sc_threshold 0 keeps x264 from inserting scene-cut
            // keyframes, which would make the grid — and therefore the expected values — a guess.
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                "-y -loglevel error -f lavfi -i testsrc2=duration=180:size=640x360:rate=30 "
                + $"-c:v libx264 -g 270 -sc_threshold 0 -pix_fmt yuv420p \"{ClipPath}\"")
            { UseShellExecute = false });
            ffmpeg!.WaitForExit();
            if (ffmpeg.ExitCode != 0)
            {
                Console.WriteLine($"SKIP: relative seek fixture needs ffmpeg; it exited {ffmpeg.ExitCode}.");
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Console.WriteLine("SKIP: relative seek fixture needs ffmpeg on PATH to build its "
                + "known-GOP clip.");
            return true;
        }
        if (File.Exists(ClipPath) && new FileInfo(ClipPath).Length > 64 * 1024) return false;
        Console.WriteLine("SKIP: ffmpeg produced no usable clip at " + ClipPath + ".");
        return true;
    }

    /// <summary>The caller gates this on <see cref="TrySkip"/>, so a missing clip here is a wiring
    /// bug rather than a skip.</summary>
    public static void Run()
    {
        if (!File.Exists(ClipPath))
            throw new InvalidOperationException(
                $"SeekMagnitudeFixture.Run was called without its clip at {ClipPath}; gate it on TrySkip().");
        // NO_VIDEO is restored in the finally below. It is process-wide, and this fixture runs
        // before --overlay-lifetime, which drives a real MainWindow: leaving mpv on vo=null would
        // give that fixture a video-less window and cost someone an hour.
        var previousNoVideo = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO");
        try
        {
            RunOffscreen();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", previousNoVideo);
        }
    }

    private static void RunOffscreen()
    {
        // Program.Main already sets NO_AUDIO globally; NO_VIDEO is set here so this fixture never
        // depends on that ordering, and so it opens no window even when run on its own.
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        // wid 0: no parent HWND. HardwareDecoding=false: software decode, so no GPU is needed.
        using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher, new AppSettings { HardwareDecoding = false });
        var generation = player.LoadFile(ClipPath);
        var loadDeadline = DateTime.UtcNow.AddSeconds(20);
        while (player.Duration <= 0 && DateTime.UtcNow < loadDeadline)
            Pump(TimeSpan.FromMilliseconds(50));
        if (player.Duration <= 0)
            throw new InvalidOperationException($"Clip {ClipPath} never loaded: mpv reported no duration within 20 s.");
        player.Pause = true;
        // current-vo is printed as proof the offscreen escape hatch took: "null" means mpv opened
        // no window and no GPU adapter for this run.
        Console.WriteLine($"SEEK EVIDENCE: clip={ClipPath} duration={player.Duration:F2}s "
            + $"generation={generation} vo={player.GetPropertyString("current-vo")}");

        // Leg 1 — CONTROL. Absolute seeks are precise under hr-seek=default, so this must land on
        // 20.0. If it does not, the diagnosis is wrong: the fault is not in relative seeking, and
        // every measurement below is meaningless.
        player.TimePos = 20.0;
        var control = Settle(player);
        Console.WriteLine($"  control: absolute seek to 20.000 -> {control:F3}");
        if (Math.Abs(control - 20.0) > 0.5)
            throw new InvalidOperationException(
                $"CONTROL FAILED: absolute seek to 20.0 landed at {control:F3}. Absolute seeking is "
                + "not exact here (or 20.0 is unreachable in this clip), so the defect is NOT isolated "
                + "to the relative-seek path and this fixture's whole diagnosis is wrong.");

        // Every leg runs even after one fails, so a run always carries the full set of
        // measurements; the collected failures are reported together at the end.
        var failures = new List<string>();
        void Leg(double start, double delta, double expected)
        {
            if (MeasureSeek(player, start, delta, expected) is { } failure) failures.Add(failure);
        }
        // Leg 2 — forward 10 s from the control position. Target 30.0 sits between keyframes 27
        // and 36.
        Leg(20.0, +10, 30.0);
        // Leg 3 — backward 10 s. Target 10.0 sits between keyframes 9 and 18; a keyframe-limited
        // seek is only 1 s out here, so the measurement is printed either way, but the assertion
        // stands: post-fix this must be 10.0.
        Leg(20.0, -10, 10.0);
        // Leg 4 — forward 30 s. Target 50.0 sits between keyframes 45 and 54.
        Leg(20.0, +30, 50.0);
        // Leg 5 — backward 30 s, started from 50.0 rather than 20.0. From 20.0 a -30 seek clamps
        // at 0, and a clamped seek does not measure the seek MAGNITUDE at all: mpv would land on 0
        // whether it moved by 20 s or by 30, so the assertion could not tell a correct seek from a
        // broken one. 50.0 and its 20.0 target are both far from either end of the 180 s file.
        Leg(50.0, -30, 20.0);
        if (HeldKeyFloodFailure(player) is { } flood) failures.Add(flood);
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(" | ", failures));
    }

    /// <summary>Leg 5 — a smoke test of the command sequence, NOT a test of the B9 stall risk.
    /// Be precise about what this does and does not establish; comments here are read as recorded
    /// evidence.
    ///
    /// What it establishes: issuing 30 exact backward seeks back-to-back does not deadlock mpv or
    /// wedge the command queue — the position still moves and a following absolute seek is still
    /// obeyed.
    ///
    /// What it does NOT establish: that a held arrow is safe on real content. All four seek
    /// actions are in <see cref="PlayerActionDispatcher.RepeatsWhileHeld"/>, so a held arrow
    /// issues one synchronous seek per key repeat on the UI thread, and PlayerActions.cs warns
    /// (the B9 failure class) that a hi-res seek plus re-decode at that rate can flood mpv and
    /// stall the player. This leg cannot see that: the player is PAUSED throughout, so there is no
    /// decode/present pipeline to starve; the clip is 640x360 testsrc2 on vo=null with hwdec off,
    /// so an exact backward seek decodes at most 9 s of trivial content, orders of magnitude less
    /// than long-GOP 4K HEVC; and the assertion is time-blind, so a burst that took 20 s to drain
    /// and froze the UI thread throughout would still pass. The real test is a held-arrow leg on
    /// the guest against real content, measuring that the UI keeps answering during the burst.
    ///
    /// It deliberately asserts no landing position: mpv may coalesce queued relative seeks, so the
    /// endpoint is not a contract and pinning it would fail on a legitimate mpv change.</summary>
    private static string? HeldKeyFloodFailure(MpvPlayer player)
    {
        player.TimePos = 150.0;
        var from = Settle(player);
        // ~30 repeats/s for a second is a realistic Windows auto-repeat burst for a held key.
        for (var i = 0; i < 30; i++)
        {
            player.SeekRelative(-1);
            Pump(TimeSpan.FromMilliseconds(33));
        }
        var after = Settle(player);
        Console.WriteLine($"  held-key flood: 30x -1s from {from:F3} -> {after:F3}");
        if (double.IsNaN(after) || after >= from)
            return $"held-key flood: 30 backward seeks from {from:F3} left the position at "
                + $"{after:F3}; playback did not move backward, which is the B9 stall shape.";
        // Still steerable? A stalled player stops answering seeks altogether.
        player.TimePos = 60.0;
        var recovered = Settle(player);
        Console.WriteLine($"  held-key flood: recovery seek to 60.000 -> {recovered:F3}");
        return Math.Abs(recovered - 60.0) <= 1.0 ? null
            : $"held-key flood: after the burst a plain absolute seek to 60.0 landed at "
                + $"{recovered:F3} — the player stopped responding to seeks (B9 stall).";
    }

    /// <summary>Resets to <paramref name="start"/> absolutely, seeks by <paramref name="delta"/>
    /// once, prints the measurement, and reports a failure when the settled position is further
    /// than 1 s from <paramref name="expected"/>.</summary>
    private static string? MeasureSeek(MpvPlayer player, double start, double delta, double expected)
    {
        player.TimePos = start;
        var from = Settle(player);
        player.SeekRelative(delta);
        var to = Settle(player);
        Console.WriteLine($"  seek {delta:+0;-0}s: from {from:F3} -> {to:F3} "
            + $"(expected {expected:F3}, moved {to - from:F3}, error {to - expected:F3})");
        // 0.25 s, not 1 s. At 1 s the -10 leg was decorative: it lands on keyframe 9.0 against an
        // expectation of 10.0, so it PASSED on the broken build and would have flipped to a fail
        // on a reading of 8.967 — a coin-flip exactly on the boundary. Post-fix every leg reports
        // 0.000, so a tight bound costs nothing and makes all four legs load-bearing.
        return Math.Abs(to - expected) <= 0.25 ? null
            : $"relative seek of {delta:+0;-0}s from {from:F3} landed at {to:F3}, expected "
                + $"{expected:F3} (off by {to - expected:F3}s; it moved {to - from:F3}s instead of {delta:F3}s)";
    }

    /// <summary>Pumps the dispatcher until <c>time-pos</c> has stopped moving, then returns it.
    /// The initial grace matters: immediately after a seek command mpv still reports the OLD
    /// position, which would otherwise read as "already stable".</summary>
    private static double Settle(MpvPlayer player)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var grace = DateTime.UtcNow.AddMilliseconds(500);
        var last = double.NaN;
        var lastChange = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            Pump(TimeSpan.FromMilliseconds(50));
            double.TryParse(player.GetPropertyString("time-pos"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var now);
            if (double.IsNaN(last) || Math.Abs(now - last) > 0.001)
            {
                last = now;
                lastChange = DateTime.UtcNow;
                continue;
            }
            if (DateTime.UtcNow > grace
                && DateTime.UtcNow - lastChange >= TimeSpan.FromMilliseconds(300)
                && player.GetPropertyString("seeking") is not "yes")
                return now;
        }
        return last;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
