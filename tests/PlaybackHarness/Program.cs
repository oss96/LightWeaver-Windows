using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Diagnostics;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.Views;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--serve")
            return HttpMediaFixture.Run(args[1], args[2], args[3]).GetAwaiter().GetResult();
        Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT",
            Path.Combine(Path.GetTempPath(), "lw-playback-harness-" + Guid.NewGuid().ToString("N")));
        if (args.Contains("--quality-contract"))
            return QualityContractFixture.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--live-session"))
            return LiveSessionFixture.RunAsync().GetAwaiter().GetResult();
        // Its own mode like the two above, and for the opposite reason: it needs nothing at all —
        // no window, no mpv, no server — so it has no business paying for the default run's mpv
        // instance, and a wait-at-the-write-head assertion measured in seconds has no business
        // slowing every other run down.
        if (args.Contains("--partial-stream"))
            return PartialStreamFixture.Run();
        // Same reasoning again: the clock arithmetic and the command mapping are pure, and the one
        // leg that wants a server brings its own TcpListener — so nothing here needs mpv, and the
        // default run should not pay for one on its account.
        if (args.Contains("--sync-play"))
            return SyncPlayFixture.RunAsync().GetAwaiter().GetResult();
        // The SyncPlay half that does need mpv, and it cannot ride in the mode above: a real
        // MpvPlayer wants the STA thread Main started on, with its dispatcher pumping, and
        // --sync-play is async with ConfigureAwait(false) throughout, so it finishes on whatever
        // pool thread the last continuation landed on. It stays out of the default run for the
        // usual reason — it builds a clip and plays it, which no other leg here should pay for.
        if (args.Contains("--sync-play-binding"))
            return SyncPlayBindingFixture.Run();
        if (args.Contains("--sync-play-ui"))
            return SyncPlayUiFixture.Run();
        if (args.Contains("--sync-play-integration"))
            return SyncPlayIntegrationFixture.Run();
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Test("buffer deserialization bounds", () =>
        {
            Equal(32, JsonSerializer.Deserialize<AppSettings>("{\"VideoBufferMiB\":-1}")!.VideoBufferMiB);
            Equal(1024, JsonSerializer.Deserialize<AppSettings>("{\"VideoBufferMiB\":2147483647}")!.VideoBufferMiB);
            Equal(128, JsonSerializer.Deserialize<AppSettings>("{}")!.VideoBufferMiB);
        });
        Test("stream address validation and display privacy", () =>
        {
            foreach (var input in new[] { "", "not a url", "/relative.mp4", "file:///C:/movie.mp4", "https://user:password@example.test/video" })
                Equal(false, StreamAddress.TryParse(input, out _));
            Equal(true, StreamAddress.TryParse(" https://example.test/video%20one.mp4?api_key=private#fragment ", out var address));
            Equal("video one.mp4", StreamAddress.DisplayName(address!));
            Equal("example.test", StreamAddress.DisplayName(new Uri("https://example.test/?token=private")));
        });
        Test("normal diagnostics strip arbitrary URL secrets", () =>
        {
            AppLog.Verbose = true;
            var input = "https://example.test/video.mp4?sig=unknown-query&session=another-secret#hidden-fragment " +
                "HTTP://user:credential@example.test/path#fragment-only https://example.test/?custom=opaque";
            AppLog.MpvLine("info", input);
            AppLog.Error("fixture", input);
            AppLog.Flush();
            var logPath = Path.Combine(AppLog.Dir, "mpv.log");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!File.Exists(logPath) || !File.ReadAllText(logPath).Contains("https://example.test/video.mp4", StringComparison.Ordinal))
                && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            foreach (var file in new[] { "mpv.log", "app.log" })
            {
                var log = File.ReadAllText(Path.Combine(AppLog.Dir, file));
                foreach (var secret in new[] { "unknown-query", "another-secret", "hidden-fragment", "credential", "fragment-only", "opaque" })
                    Equal(false, log.Contains(secret, StringComparison.Ordinal));
                Equal(true, log.Contains("https://example.test/video.mp4", StringComparison.Ordinal));
            }
            var redact = typeof(AppLog).GetMethod("Redact", BindingFlags.Static | BindingFlags.NonPublic)!;
            var once = (string)redact.Invoke(null, [input])!;
            Equal(once, (string)redact.Invoke(null, [once])!);
            Equal("http://", (string)redact.Invoke(null, ["http://?private"])!);
            AppLog.Verbose = false;
        });
        Test("stream shortcut resolves saved bindings and help consistently", () =>
        {
            var settings = new AppSettings();
            Equal("Ctrl+L", PlayerActionDispatcher.Resolve(settings.KeyBindings)[PlayerAction.OpenStream]);
            settings.KeyBindings[nameof(PlayerAction.OpenStream)] = "Ctrl+Shift+L";
            Equal(false, PlayerActionDispatcher.TryResolve(System.Windows.Input.Key.L,
                System.Windows.Input.ModifierKeys.Control, settings.KeyBindings, out _));
            Equal(true, PlayerActionDispatcher.TryResolve(System.Windows.Input.Key.L,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
                settings.KeyBindings, out var action));
            Equal(PlayerAction.OpenStream, action);
            var row = AppShortcuts.BuildRows(settings).SelectMany(group => group)
                .Single(item => item.Function == PlayerActionDispatcher.DisplayName(PlayerAction.OpenStream));
            Equal(true, row.Bindable);
            Equal("Ctrl+Shift+L", row.Keys);
            settings.KeyBindings[nameof(PlayerAction.OpenStream)] = "";
            Equal(false, PlayerActionDispatcher.TryResolve(System.Windows.Input.Key.L,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
                settings.KeyBindings, out _));
        });
        Test("mpv accepts buffer limits at creation and runtime", () =>
        {
            var settings = new AppSettings { VideoBufferMiB = 256 };
            using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher, settings);
            Equal("268435456", player.GetPropertyString("demuxer-max-bytes"));
            Equal(3600d, double.Parse(player.GetPropertyString("cache-secs")!, System.Globalization.CultureInfo.InvariantCulture));
            settings.VideoBufferMiB = 32;
            player.ApplyVideoBuffer(settings);
            Equal("33554432", player.GetPropertyString("demuxer-max-bytes"));
        });
        // Ungated on purpose, unlike its flagged neighbours below: it needs no server, no profile
        // and no Application — just hand-built track rows and the production matcher.
        StickySubtitleFixture.Run(Test);
        MediaVersionFixture.Run(Test);
        MediaVersionStoreFixture.Run(Test);
        if (args.Contains("--slider-drag"))
            SliderDragFixture.Run(Test, EnsureApp().Resources);
        if (!SeekMagnitudeFixture.TrySkip())
            Test("relative seek moves by exactly the requested amount", SeekMagnitudeFixture.Run);
        if (args.Contains("--remote-buffer") && !RemoteBufferFixture.TrySkip())
            Test("remote Jellyfin playback uses configured buffer", RemoteBufferFixture.Run);
        // Behind a flag like its neighbour above, and for the same reason: it needs a reachable
        // server and a signed-in profile, so a default run must not depend on the network.
        if (args.Contains("--resume-types") && !ResumeItemTypesFixture.TrySkip())
            Test("continue watching asks for playable leaf items only", ResumeItemTypesFixture.Run);
        if (args.Contains("--overlay-lifetime"))
        {
            Test("overlay closed before pending bounds sync is harmless", () =>
            {
                var app = EnsureApp();
                var main = new MainWindow();
                var field = typeof(MainWindow).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    main.Show();
                    main.PlayUrl(args.Last(), null);
                    Pump(TimeSpan.FromSeconds(2));
                    var overlay = (OverlayWindow)field.GetValue(main)!;
                    if (!overlay.IsVisible) throw new InvalidOperationException("Precondition: playback overlay must be visible.");
                    Test("cancelled owner close keeps overlay synchronization active", () =>
                    {
                        System.ComponentModel.CancelEventHandler cancel = (_, e) => e.Cancel = true;
                        main.Closing += cancel;
                        try { main.Close(); }
                        finally { main.Closing -= cancel; }
                        Equal(true, main.IsVisible);
                        Equal(true, overlay.IsVisible);
                        typeof(MainWindow).GetMethod("SyncOverlayBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                    });
                    overlay.Close();
                    typeof(MainWindow).GetMethod("SyncOverlayBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                }
                finally
                {
                    // The negative control deliberately leaves a closed overlay referenced.
                    // Clear it only after the assertion so this harness can exit cleanly.
                    field.SetValue(main, null);
                    main.Close();
                    app.Shutdown();
                }
            });
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>The one <see cref="System.Windows.Application"/> this process may have.
    /// Constructing a second one throws, and two legs need it now: the overlay-lifetime leg for
    /// its windows, and the slider fixture for the theme dictionaries a ControlTemplate's
    /// StaticResources resolve against.
    /// <para>Note the overlay-lifetime leg still calls <c>Shutdown()</c> in its own teardown, so it
    /// remains the LAST leg that may use the Application. Anything added after it needs that call
    /// moved out of that leg first.</para></summary>
    private static App EnsureApp()
    {
        if (System.Windows.Application.Current is App existing)
            return existing;
        var app = new App();
        app.InitializeComponent();
        return app;
    }

    private static void Test(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex)
        {
            _failures++;
            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            Console.WriteLine($"FAIL: {name}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
