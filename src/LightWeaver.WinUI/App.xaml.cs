using LightWeaver.Diagnostics;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;

namespace LightWeaver.WinUI;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        // Same order as the WPF app: logging, then the saved UI language before any text is created.
        AppLog.StartSession();
        AppLog.Verbose = Settings.VerboseLogging;
        L.Initialize(Settings.UiLanguage);
        Imaging.ImageDiskCache.MaxCacheBytes = Math.Max(50, Settings.ImageCacheMaxMb) * 1024L * 1024;
        Imaging.MetadataCache.ScheduleEviction(TimeSpan.FromDays(30));
        InitializeComponent();
        InstallCrashHandlers();
    }

    /// <summary>A crash file for every exception that escapes: XAML's UI thread, any other thread,
    /// and faulted tasks nobody awaited. Only the task case is handled, as in the WPF app.</summary>
    private void InstallCrashHandlers()
    {
        UnhandledException += (_, e) => AppLog.Crash("winui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                AppLog.Crash(e.IsTerminating ? "AppDomain (terminating)" : "AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            AppLog.Crash("unobserved Task", e.Exception);
        };
    }

    public static AppViewModel Model { get; } = new();

    /// <summary>The one settings instance every page and the player read; Settings writes it.</summary>
    public static AppSettings Settings { get; } = SettingsStore.Load();

    /// <summary>A setting changed and was saved; the player applies it to what is playing.</summary>
    public static event EventHandler? SettingsChanged;

    /// <summary>Saves the settings and tells the app; false when they could not be written.</summary>
    public static bool SaveSettings()
    {
        var saved = SettingsStore.Save(Settings);
        SettingsChanged?.Invoke(null, EventArgs.Empty);
        return saved;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Threading.UiDispatcher.Current = new WinUI.Player.WinUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(Model);
        _window.Closed += (_, _) =>
        {
            Services.ImageLoader.FlushSummary("shutdown");
            AppLog.Flush();
        };
        _window.Activate();
        _ = Services.Updater.StartAsync(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), Settings);
        try
        {
            // Restores the saved session (CredentialStore, DPAPI) the same way the WPF app does.
            await Model.StartupAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("startup", "session restore failed: " + ex.GetType().Name);
        }
        _window.OnSessionReady();
    }
}
