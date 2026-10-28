using LightWeaver.Player;
using LightWeaver.Threading;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Player;

namespace LightWeaver.WinUI.Shell;

/// <summary>The Windows media overlay and hardware media keys (SMTC), as in the WPF app: title and
/// artwork of what plays, play, pause, stop, and next/previous by Settings › Playback's media-key
/// choice; the timeline updates every 5 s.</summary>
public sealed partial class MainWindow
{
    private SystemMediaControls? _systemMedia;
    private IUiTimer? _timelineTimer;

    private void InitializeMediaControls(PlaybackController playback)
    {
        _systemMedia = SystemMediaControls.TryCreate(WindowHandle, UiDispatcher.Current, url => Services.ImageLoader.DownloadToFileAsync(url, playback.PlaybackSession));
        if (_systemMedia is not { } media) return;
        var vm = playback.ViewModel;
        media.PlayPressed += () => vm.IsPaused = false;
        media.PausePressed += () => vm.IsPaused = true;
        media.StopPressed += () => playback.Stop();
        media.NextPressed += vm.TransportNext;
        media.PreviousPressed += vm.TransportPrev;
        void UpdateEnabled() => media.SetNextPreviousEnabled(vm.CanSkipNext, vm.CanSkipPrevious);
        Model.Queue.Changed += UpdateEnabled;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerViewModel.CanSkipNext) or nameof(PlayerViewModel.CanSkipPrevious)) UpdateEnabled();
            else if (e.PropertyName == nameof(PlayerViewModel.IsPaused) && playback.IsPlaying) media.SetPlaybackState(playing: !vm.IsPaused);
        };
        _timelineTimer = UiDispatcher.Current.CreateTimer(TimeSpan.FromSeconds(5), () =>
        {
            if (playback.Player is { } player) media.UpdateTimeline(player.TimePos, player.Duration);
        });
        playback.Started += (_, item) =>
        {
            _ = media.UpdateDisplayAsync(item.PlaybackTitle, item.PlaybackSubtitle, item.ThumbUrl ?? item.PosterUrl);
            media.SetPlaybackState(playing: true);
            UpdateEnabled();
            _timelineTimer.Start();
        };
        playback.Stopped += (_, _) =>
        {
            _timelineTimer.Stop();
            media.ClearDisplay();
        };
    }
}
