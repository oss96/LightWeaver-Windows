using LightWeaver.Localization;
using LightWeaver.Settings;

namespace LightWeaver.Updates;

/// <summary>What Settings › Updates and the update toast can ask the updater to do.</summary>
public enum UpdateAction
{
    Check,
    Download,
    DownloadAndInstall,
    InstallNow,
    OpenReleasePage,
}

/// <summary>Settings › Updates for one updater status, as the WPF app's Updates card shows it: the
/// status line, the download's progress, the one action the state offers and whether a check may
/// start. <see cref="Action"/> is <see cref="UpdateAction.Check"/> when checking is all there is to do.</summary>
public sealed record UpdatePane(string Status, bool IsError, double? Progress, UpdateAction Action, string ActionLabel,
    bool ActionEnabled, bool CanCheck)
{
    public static UpdatePane For(UpdateStatus status, UpdatePolicy policy)
    {
        var version = status.Update?.DisplayVersion;
        var hasInstaller = status.Update?.SetupAssetUri is not null;
        var download = policy == UpdatePolicy.ManualInstall ? UpdateAction.DownloadAndInstall : UpdateAction.Download;
        var downloadLabel = policy == UpdatePolicy.ManualInstall
            ? L.Text("SettingsView.DownloadAndInstall.35531833") : L.Text("SettingsView.Download.9ccefefe");
        var check = L.Text("Settings.CheckUpdates");
        return status.Phase switch
        {
            UpdatePhase.Checking => new(L.Text("Settings.Checking"), false, null, UpdateAction.Check, check, false, false),
            UpdatePhase.UpToDate => new(L.Text("Settings.UpToDate"), false, null, UpdateAction.Check, check, true, true),
            UpdatePhase.Available when hasInstaller => new(status.Detail ?? L.Format("Settings.UpdateAvailable", version),
                false, null, download, downloadLabel, true, true),
            UpdatePhase.Available => new(status.Detail ?? L.Format("Settings.UpdateAvailable", version),
                false, null, UpdateAction.OpenReleasePage, L.Text("SettingsView.OpenReleasePage.e7bd56f3"), true, true),
            UpdatePhase.Downloading => new(status.Progress is { } done
                    ? L.Format("Settings.RevertDownloading", (int)(done * 100))
                    : L.Format("SettingsView.DownloadingVersion.8a4557dc", version),
                false, status.Progress, download, downloadLabel, false, false),
            UpdatePhase.ReadyToInstall => new(Ready(status, policy, version), false, null,
                UpdateAction.InstallNow, L.Text("SettingsView.InstallNow.e4cf6a26"), true, true),
            UpdatePhase.Installing => new(L.Text("SettingsView.StartingTheInstaller.0f1f95e3"), false, null,
                UpdateAction.InstallNow, L.Text("SettingsView.InstallNow.e4cf6a26"), false, false),
            UpdatePhase.Error => new(status.Detail ?? L.Text("SettingsView.UpdateOperationFailed.6a3b525a"), true, null,
                hasInstaller ? download : UpdateAction.Check, L.Text("SettingsView.Retry.8a625e56"), true, true),
            _ => new(L.Text("Settings.NotChecked"), false, null, UpdateAction.Check, check, true, true),
        };
    }

    /// <summary>A staged update installs on exit under the automatic-install policy, else when the
    /// user chooses; a failed check since then is added, because the package stays staged.</summary>
    private static string Ready(UpdateStatus status, UpdatePolicy policy, string? version)
        => (policy == UpdatePolicy.AutoDownloadAndInstall
                ? L.Format("SettingsView.VersionIsReadyItWillInstallWhen.15e2a08e", version)
                : L.Format("SettingsView.VersionIsVerifiedAndReadyToInstall.a3763580", version))
            + (status.Detail is { Length: > 0 } detail ? L.Format("SettingsView.LastCheck.f89df2b0", detail) : "");
}
