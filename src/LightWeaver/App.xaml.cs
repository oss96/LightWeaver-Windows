using System.IO;
using System.Windows;

namespace LightWeaver;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>File passed on the command line to play at startup, if any.</summary>
    public string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // First thing in the process, before the app-data migration and before any window
        // exists: a crash during startup is precisely the one that leaves no other evidence.
        InstallCrashHandlers();
        Diagnostics.AppLog.StartSession();
        StartupLog($"App.OnStartup enter (args={e.Args.Length})");
        MigrateLegacyAppData();
        if (e.Args.Length > 0)
            StartupFile = e.Args[0];
        base.OnStartup(e);
        // The Debug "(Debug)" title marker used to be queued from here at
        // DispatcherPriority.ApplicationIdle. Measured 2026-07-31 (B29): it did run and did
        // assign, but only ~330 ms AFTER the window's HWND existed and was on screen, so the
        // window was visibly titled plain "LightWeaver" for its first third of a second and
        // any check faster than that read the wrong title. ApplicationIdle is also the lowest
        // foreground priority, i.e. starvable by render/animation work. The marker now lives
        // in MainWindow's constructor, before the HWND exists — no dispatcher dependency and
        // no MainWindow-null guard to no-op silently.
        ArmCrashTestHook();
        RunLogExportTestHook();
        StartupLog("App.OnStartup exit");
    }

    /// <summary>Diagnostics: <c>LIGHTWEAVER_EXPORT_LOGS_TO=&lt;path&gt;</c> writes the diagnostics
    /// bundle at startup and records the count in <c>app.log</c>.
    /// <para>This exists because the Settings button opens a native common-item save dialog, and
    /// that dialog is not enumerable through UIA as a window of this process — measured, only the
    /// main window is listed, even while the dialog is plainly on screen. Without a headless entry
    /// point the zip's CONTENTS could not be asserted at all, and "the button exists" is the kind
    /// of assertion this repo has been bitten by. It exercises the same
    /// <see cref="Diagnostics.AppLog.ExportTo"/> the button calls; only the path comes from
    /// elsewhere.</para></summary>
    private static void RunLogExportTestHook()
    {
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_EXPORT_LOGS_TO") is not { Length: > 0 } target)
            return;
        try
        {
            var count = Diagnostics.AppLog.ExportTo(target);
            Diagnostics.AppLog.Milestone("diag", $"export test hook wrote {count} log file(s)");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("diag", $"export test hook failed error={ex.GetType().Name}");
        }
    }

    /// <summary>Flushes queued verbose mpv lines. The writer is a background thread, so without
    /// this the tail of a session's mpv.log is lost on exit with nothing in the file admitting
    /// it. The image cache's counter window is flushed here too, so a session that ended below the
    /// automatic threshold still accounts for what it fetched (and one that never touched an image
    /// writes nothing).</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Imaging.ImageCache.FlushSummary("shutdown");
        Diagnostics.AppLog.Flush();
        base.OnExit(e);
    }

    /// <summary>
    /// Writes a crash file for every exception that escapes, from all three places one can:
    /// the WPF Dispatcher (UI thread), any other thread via the AppDomain, and faulted Tasks
    /// nobody awaited.
    ///
    /// <para>Only the Task case is *handled*. A Dispatcher or AppDomain exception is logged and
    /// then allowed to kill the process exactly as before: swallowing an unknown exception
    /// leaves the app running on invalidated state, which produces a second, wronger bug report
    /// than the crash it hid. An unobserved Task exception, by contrast, is already non-fatal by
    /// default in modern .NET — <c>SetObserved</c> only stops the finalizer thread re-raising it,
    /// and keeps the app's behaviour identical to today's.</para>
    /// </summary>
    private static void InstallCrashHandlers()
    {
        Current.DispatcherUnhandledException += (_, args) =>
            Diagnostics.AppLog.Crash("Dispatcher", args.Exception);

        // Fires for the SAME exception the Dispatcher handler just logged, because not setting
        // e.Handled lets WPF rethrow. AppLog.Crash de-duplicates by exception identity — measured
        // before it did: one deliberate UI-thread throw produced two crash files 6 ms apart, i.e.
        // two of the ten retention slots for one crash, and on a faster path both writes would
        // have landed on the same millisecond-stamped filename.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Diagnostics.AppLog.Crash(args.IsTerminating ? "AppDomain (terminating)" : "AppDomain", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Observed FIRST, logged second. This runs on the finalizer thread; if logging threw
            // here, skipping SetObserved would turn a benign unobserved exception into a hard
            // process kill — the exact regression the comment below promises cannot happen.
            args.SetObserved();
            Diagnostics.AppLog.Crash("unobserved Task", args.Exception);
        };
    }

    /// <summary>
    /// Test hook: <c>LIGHTWEAVER_CRASH_TEST=dispatcher|background|task</c> raises a deliberate
    /// unhandled exception on that path ~3 s after startup. Unset — every real launch — is a
    /// no-op, and so is any value that is not exactly one of the three: an earlier version treated
    /// every non-"task" value as "dispatcher", so <c>=0</c>, <c>=false</c> or a typo would make a
    /// shipped binary crash itself three seconds in.
    ///
    /// <para>This exists because a crash handler nobody has ever seen fire is not a feature. The
    /// standing lesson in this repo is that an assertion must be shown to fail on a broken build;
    /// the equivalent here is that the crash file must be shown to appear on a real unhandled
    /// exception, and there is no other way to raise one on demand in a shipped binary.</para>
    /// </summary>
    private static void ArmCrashTestHook()
    {
        var mode = Environment.GetEnvironmentVariable("LIGHTWEAVER_CRASH_TEST")?.Trim().ToLowerInvariant();
        if (mode is not ("dispatcher" or "background" or "task"))
            return;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            switch (mode)
            {
                case "task":
                    // Faulted, never awaited, and — the part the first version got wrong — the
                    // reference is dropped and collection is deferred to a LATER tick. Collecting
                    // immediately after Task.Run raced a task that had not faulted yet and was
                    // still rooted by its work item: measured 1 crash file in 4 runs. Now the
                    // task is given time to fault and unroot before the GC is asked for it.
                    _ = Task.Run(() => throw new InvalidOperationException(
                        "LIGHTWEAVER_CRASH_TEST=task deliberate unobserved task exception"));
                    var collector = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2),
                    };
                    collector.Tick += (_, _) =>
                    {
                        collector.Stop();
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    };
                    collector.Start();
                    return;

                case "background":
                    // A throw on a pool thread: reaches the AppDomain handler, not the Dispatcher.
                    new Thread(() => throw new InvalidOperationException(
                        "LIGHTWEAVER_CRASH_TEST=background deliberate unhandled exception"))
                    { IsBackground = false, Name = "lw-crash-test" }.Start();
                    return;

                default:
                    throw new InvalidOperationException(
                        "LIGHTWEAVER_CRASH_TEST=dispatcher deliberate unhandled exception");
            }
        };
        timer.Start();
    }

    /// <summary>Test hook: LIGHTWEAVER_STARTUP_LOG=&lt;path&gt; appends timestamped
    /// startup-ordering lines (OnStartup enter/exit, MainWindow's constructor with the
    /// Debug title marker, and SourceInitialized) so the real sequence is measurable
    /// instead of inferred — this is what diagnosed B29. Read by
    /// bug-tests/test-b29-debug-title.ps1. No-op when the variable is unset.</summary>
    internal static void StartupLog(string line)
    {
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_STARTUP_LOG") is not { Length: > 0 } path)
            return;
        try
        {
            File.AppendAllText(path, FormattableString.Invariant(
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"));
        }
        catch (IOException)
        {
            // logging is best-effort
        }
    }

    /// <summary>One-time carry-over from the misspelled pre-rename app-data folder
    /// (%LOCALAPPDATA%\LightWeever): credentials, settings, window state and the image
    /// cache move to LightWeaver so updating never logs the user out or drops
    /// preferences. Must run before anything reads settings or credentials.</summary>
    private static void MigrateLegacyAppData()
    {
        // Debug builds run in an isolated app-data folder (LightWeaver-Debug) and never
        // migrate the release folders — keep the two environments fully separate.
#if !DEBUG
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacy = Path.Combine(root, "LightWeever");
            var current = Path.Combine(root, "LightWeaver");
            if (!Directory.Exists(legacy))
                return;
            if (!Directory.Exists(current))
            {
                Directory.Move(legacy, current);
                return;
            }
            // Both exist (new version already ran once): fill gaps only, then retire
            // the legacy folder — never overwrite newer data with old.
            foreach (var file in Directory.GetFiles(legacy))
            {
                var dest = Path.Combine(current, Path.GetFileName(file));
                if (!File.Exists(dest))
                    File.Move(file, dest);
            }
            foreach (var dir in Directory.GetDirectories(legacy))
            {
                var dest = Path.Combine(current, Path.GetFileName(dir));
                if (!Directory.Exists(dest))
                    Directory.Move(dir, dest);
            }
            Directory.Delete(legacy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort: worst case the app starts fresh and the user logs in again — which
            // looks to the user like being logged out for no reason, so it goes in the log.
            Diagnostics.AppLog.Error("appdata",
                $"legacy folder migration failed error={ex.GetType().Name}");
        }
#endif
    }
}
