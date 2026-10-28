using LightWeaver.Diagnostics;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Dispatching;

namespace LightWeaver.WinUI.Services;

/// <summary>The app's one <see cref="UpdateService"/>, the updater the WPF app runs: a check at start
/// and every six hours, the update policy's download and install, and the hand-off to the installer
/// on a normal exit. It is created off the UI thread, because creating it tidies the updates folder
/// and verifies a staged installer again; its status reaches the UI thread, newest only.</summary>
public static class Updater
{
    private static readonly UpdateStatus NotStarted = new(UpdatePhase.Idle, null);
    private static DispatcherQueue? s_ui;
    private static int s_posted;

    public static UpdateService? Service { get; private set; }

    public static UpdateStatus Status => Service?.Status ?? NotStarted;

    /// <summary>The status changed; raised on the UI thread.</summary>
    public static event Action<UpdateStatus>? Changed;

    /// <summary>Windows is ending the session, so no installer starts on the way out.</summary>
    public static bool SessionEnding { get; set; }

    public static async Task StartAsync(DispatcherQueue ui, AppSettings settings)
    {
        s_ui = ui;
        try
        {
            var service = await Task.Run(() => new UpdateService(settings));
            service.StatusChanged += _ => Post();
            Service = service;
            Post();
            service.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("updates", $"event=start outcome=failure error={ex.GetType().Name}");
        }
    }

    public static void OnPolicyChanged() => Service?.OnPolicyChanged();

    /// <summary>Runs what Settings or a toast asked for. An install closes the app with
    /// <paramref name="quit"/>, so the installer can replace it.</summary>
    public static async Task RunAsync(UpdateAction action, Action quit)
    {
        if (Service is not { } service) return;
        switch (action)
        {
            case UpdateAction.Check:
                await service.CheckNowAsync();
                break;
            case UpdateAction.Download:
                await service.DownloadAsync();
                break;
            case UpdateAction.DownloadAndInstall:
                if (await service.DownloadAndInstallInteractiveAsync()) quit();
                break;
            case UpdateAction.InstallNow:
                if (await service.InstallNowAsync()) quit();
                else ToastHost.Show(ToastKind.Error, L.Text("MainWindow.TheStagedUpdateCouldNotBeStarted.8c9f8ddf"));
                break;
            case UpdateAction.OpenReleasePage:
                service.OpenReleasePage();
                break;
        }
    }

    /// <summary>The window closed: starts the installer that Install now queued or the automatic-install
    /// policy calls for, unless Windows is ending the session, and stops the updater.</summary>
    public static void OnExit()
    {
        if (Service is not { } service) return;
        var launch = !SessionEnding && service.ShouldLaunchInstallOnNormalExit;
        AppLog.Detail("updates", "event=exit_handoff " + (launch ? "outcome=applied"
            : SessionEnding ? "outcome=noop reason=session_ending" : "outcome=noop reason=policy"));
        if (launch) service.TryLaunchInstallAfterExit();
        service.Dispose();
    }

    /// <summary>A download reports every 128 KiB; one UI update is queued at a time and shows the newest status.</summary>
    private static void Post()
    {
        if (Interlocked.Exchange(ref s_posted, 1) == 1) return;
        if (s_ui?.TryEnqueue(Raise) != true) Interlocked.Exchange(ref s_posted, 0);
    }

    private static void Raise()
    {
        Interlocked.Exchange(ref s_posted, 0);
        Changed?.Invoke(Status);
    }
}
