using LightWeaver.Localization;
using LightWeaver.Settings;

namespace LightWeaver.Updates;

/// <summary>An update toast: its text, and whether the update is downloaded and ready to install.</summary>
public sealed record UpdateNotice(string Message, bool Ready);

/// <summary>Where the window is when an update could be told about.</summary>
public enum UpdateNoticePlace
{
    Elsewhere,
    Login,
    UpdatesPane,
}

/// <summary>When to tell about an update outside Settings, as the WPF app's update banner did: once
/// per version when one is found and nothing downloads it on its own (the notify-only policy, or a
/// release without an installer), and once when it is downloaded and ready. A notice counts as told
/// once it is toasted or Settings › Updates shows it; on Login it waits.</summary>
public sealed class UpdateNotices
{
    private readonly HashSet<(Version, bool)> _told = [];

    /// <summary>The toast this status calls for at <paramref name="place"/>, or null when it calls for
    /// none, was told already, or is not to be toasted there.</summary>
    public UpdateNotice? Next(UpdateStatus status, UpdatePolicy policy, UpdateNoticePlace place)
    {
        if (status.Update is not { } update) return null;
        var ready = status.Phase == UpdatePhase.ReadyToInstall;
        var found = status.Phase == UpdatePhase.Available
            && (policy == UpdatePolicy.ManualInstall || update.SetupAssetUri is null);
        if (!ready && !found || place == UpdateNoticePlace.Login || !_told.Add((update.Version, ready))) return null;
        if (place == UpdateNoticePlace.UpdatesPane) return null;
        var version = update.DisplayVersion;
        return new(!ready ? L.Format("Settings.UpdateAvailable", version)
            : policy == UpdatePolicy.AutoDownloadAndInstall ? L.Format("SettingsView.VersionIsReadyItWillInstallWhen.15e2a08e", version)
            : L.Format("SettingsView.VersionIsVerifiedAndReadyToInstall.a3763580", version), ready);
    }
}
