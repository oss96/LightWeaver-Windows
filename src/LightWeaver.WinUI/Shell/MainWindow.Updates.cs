using System.Runtime.InteropServices;
using LightWeaver.Updates;
using LightWeaver.WinUI.Pages;
using LightWeaver.WinUI.Services;

namespace LightWeaver.WinUI.Shell;

/// <summary>Update notifications with direct actions and installer handoff on normal window close.</summary>
public sealed partial class MainWindow
{
    private const uint WmQueryEndSession = 0x0011, WmEndSession = 0x0016;
    private const nuint EndSessionSubclassId = 0x4C57_5550;
    private const string ProgressToastKey = "update-progress";
    private readonly UpdateNotices _updateNotices = new();
    private UpdateNotice? _visibleUpdateNotice;

    private unsafe void InitializeUpdates()
    {
        Updater.Changed += OnUpdateChanged;
        _nav.Shown += (_, _) => AnnounceUpdate();
        ToastHost.Dismissed += OnToastDismissed;
        SetWindowSubclass(WindowHandle, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&EndSessionProc, EndSessionSubclassId, 0);
        Closed += (_, _) =>
        {
            Updater.Changed -= OnUpdateChanged;
            ToastHost.Dismissed -= OnToastDismissed;
            Updater.OnExit();
        };
    }

    private void OnUpdateChanged(UpdateStatus status) => AnnounceUpdate();

    private void OnToastDismissed(string key)
    {
        if (key == ProgressToastKey) _updateNotices.DismissProgress(Updater.Status);
        if (key == "update") _visibleUpdateNotice = null;
    }

    /// <summary>Tells about an update as the WPF app's update banner did: a lasting toast with the
    /// download's progress, and once that it was found or is ready, or not at all while Settings ›
    /// Updates shows it. On Login it waits for the next page.</summary>
    private void AnnounceUpdate()
    {
        var place = _nav.Current?.Key == "login" ? UpdateNoticePlace.Login
            : _nav.CurrentPage is SettingsPage { ShowsUpdates: true } ? UpdateNoticePlace.UpdatesPane
            : _playerMode == PlayerMode.Full ? UpdateNoticePlace.FullPlayer
            : UpdateNoticePlace.Elsewhere;
        if (_updateNotices.Progress(Updater.Status, place) is { } progress) ToastHost.ShowLasting(ToastKind.Download, progress, ProgressToastKey);
        else ToastHost.Remove(ProgressToastKey);
        if (_visibleUpdateNotice is { } visible && !visible.CanRemain(Updater.Status, App.Settings.UpdatePolicy, place))
        {
            ToastHost.Remove("update");
            _visibleUpdateNotice = null;
        }
        if (place is UpdateNoticePlace.Login or UpdateNoticePlace.FullPlayer) return;
        if (_updateNotices.Next(Updater.Status, App.Settings.UpdatePolicy, place) is not { } notice) return;
        _visibleUpdateNotice = notice;
        ToastHost.Show(ToastKind.Info, notice.Message, notice.ActionLabel, () => _ = RunUpdateNoticeAsync(notice), "update", lasting: true);
    }

    private async Task RunUpdateNoticeAsync(UpdateNotice notice)
    {
        if (!notice.CanRun(Updater.Status, App.Settings.UpdatePolicy)) return;
        await Updater.RunAsync(notice.Action, Quit);
        if (UpdateNotice.Failure(Updater.Status, App.Settings.UpdatePolicy) is not { } failure) return;
        _visibleUpdateNotice = failure;
        ToastHost.Show(ToastKind.Error, failure.Message, failure.ActionLabel, () => _ = RunUpdateNoticeAsync(failure), "update");
    }

    /// <summary>A Settings page that tells the window when Settings › Updates comes into or leaves view.</summary>
    private SettingsPage NewSettingsPage(string? section = null)
    {
        var page = new SettingsPage(this, section);
        page.ShowsUpdatesChanged += (_, _) => AnnounceUpdate();
        return page;
    }

    [UnmanagedCallersOnly]
    private static nint EndSessionProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession) Updater.SessionEnding = true;
        else if (message == WmEndSession) Updater.SessionEnding = wParam != 0;
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
