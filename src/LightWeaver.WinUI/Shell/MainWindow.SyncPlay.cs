using System.ComponentModel;
using System.Globalization;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LightWeaver.WinUI.Shell;

/// <summary>SyncPlay in the window (design 09): one live socket per signed-in profile, the flyout
/// under the bar's SyncPlay button, its member-count badge, and join/leave announcements as toasts
/// while browsing or dim pills over the video.</summary>
public sealed partial class MainWindow
{
    private LiveSessionService? _live;
    private SyncPlayCoordinator? _sync;

    private void InitializeSyncPlay()
    {
        _live = new LiveSessionService(Model.FindSessionByKey);
        AttachRemoteControl(_live);
        var playback = Playback();
        _sync = new SyncPlayCoordinator(Model, playback, new SyncPlayClient(_live, Model.FindSessionByKey), new WinUiDispatcher(DispatcherQueue));
        playback.SyncPlay = _sync;
        _sync.Announcement += OnSyncPlayAnnouncement;
        playback.ViewModel.PropertyChanged += OnSyncPlayStateChanged;
        Bar.SyncPlayRequested += (_, anchor) => ShowSyncPlay(anchor);
        Model.SessionActivated += key => DispatcherQueue.TryEnqueue(() => _live?.StartProfile(key));
        Model.SessionClosed += key => _live?.StopProfile(key);
        if (Model.ActiveSessionKey is { } active) _live.StartProfile(active);
        Closed += (_, _) =>
        {
            _closing = true;
            _sync?.Dispose();
            _live?.Dispose();
        };
    }

    private void OnSyncPlayStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.SyncPlay) || _playback is null) return;
        var group = _playback.ViewModel.SyncPlay;
        Bar.SetSyncPlayBadge(group is null ? "" : group.Participants.Count.ToString(CultureInfo.CurrentUICulture));
    }

    private void ShowSyncPlay(FrameworkElement anchor)
    {
        if (_sync is null || _playback is null) return;
        var flyout = FastUi.Flyout(new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            Content = new SyncPlayPanel(_sync, _playback.ViewModel, _sessionUser ?? Model.Jellyfin.UserName ?? ""),
        });
        flyout.ShowAt(anchor);
    }

    /// <summary>Over the video a dim pill top left; elsewhere a toast.</summary>
    private void OnSyncPlayAnnouncement(string text, string? mergeKey) => ToastHost.Show(ToastKind.Info, text, mergeKey: mergeKey);
}
