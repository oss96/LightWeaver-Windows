using System.Collections.ObjectModel;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.Settings;

namespace LightWeaver.ViewModels;

/// <summary>A selectable track row in the overlay popups. Id -1 = "Off" (subtitles).
/// Badge is the two-letter language chip ("" = no badge).</summary>
public sealed record TrackOption(int Id, string Display, bool IsSelected, string Badge = "")
{
    public override string ToString() => Display;
}

/// <summary>A user's exact subtitle intent for one mpv load generation. Track ids are useful only
/// within that generation; the descriptor is what lets the owner verify the observed selection.</summary>
public sealed record SubtitleSelectionRequest(
    int Generation, int? TrackId, bool Off, string? Lang, string? Title, bool Forced);

/// <summary>An audio output device row in the overlay device popup. Shares the
/// TrackOptionTemplate property shape (Display/IsSelected/Badge) so the popup reuses
/// the garnet-selected row template unchanged.</summary>
public sealed record AudioDeviceOption(string Name, string Display, bool IsSelected)
{
    public string Badge => "";
    public override string ToString() => Display;
}

/// <summary>One label/value line of the overlay info panel.</summary>
public sealed record InfoRow(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>A row of the overlay queue popup. The playing row can't be removed.</summary>
public sealed record QueueRow(int Index, string Display, bool IsCurrent)
{
    public bool CanRemove => !IsCurrent;
    public string Position => $"{Index + 1}.";
    public override string ToString() => Display;
}

/// <summary>A speed choice in the overlay speed popup.</summary>
public sealed record SpeedOption(double Value, bool IsSelected)
{
    public string Display => FormattableString.Invariant($"{Value:0.##}×");
    public override string ToString() => Display;
}

/// <summary>
/// Binds the overlay transport controls to an <see cref="MpvPlayer"/>.
/// All notifications arrive on the UI thread (MpvPlayer dispatches its events there).
/// </summary>
public sealed class PlayerViewModel : ObservableObject
{
    private MpvPlayer? _player;
    private bool _isPaused;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 100;
    private bool _isMuted;
    private bool _isBuffering;
    private bool _isLoading;
    private bool _rtxVsrEnabled;
    private bool _rtxHdrEnabled;
    private int _maxLumaNits = 1000;

    /// <summary>True while the seek slider thumb is being dragged — suppresses
    /// position feedback from mpv so the thumb doesn't fight the user.</summary>
    public bool IsSeeking { get; set; }

    public PlayerViewModel()
    {
        TogglePauseCommand = new RelayCommand(() => { if (_player is not null) _player.Pause = !_player.Pause; });
    }

    public RelayCommand TogglePauseCommand { get; }

    public ObservableCollection<TrackOption> VideoTracks { get; } = [];
    public ObservableCollection<TrackOption> AudioTracks { get; } = [];
    public ObservableCollection<TrackOption> SubtitleTracks { get; } = [];

    // The UI collection reflects mpv's last observation, which can lag behind rapid key presses.
    // Keep a generation-scoped cursor at the latest requested row until mpv acknowledges it.
    private int _subtitleCursorGeneration = -1;
    private int _subtitleCursorIndex;
    private bool _subtitleCursorPending;
    private int? _subtitleCursorPendingId;

    // ---- Now playing (OSD title) ----

    private string _nowPlayingTitle = "";
    private string _nowPlayingSubtitle = "";
    private string _nowPlayingRelease = "";

    public string NowPlayingTitle => _nowPlayingTitle;
    public string NowPlayingSubtitle => _nowPlayingSubtitle;
    public bool HasNowPlayingSubtitle => _nowPlayingSubtitle.Length > 0;

    /// <summary>Release/air date for the OSD badge row (2026-08-05). Empty when the server has no
    /// premiere date, in which case nothing is shown — a movie's year is already the second line
    /// of the title block, so an approximated badge would only repeat it less precisely.</summary>
    public string NowPlayingRelease => _nowPlayingRelease;
    public bool HasNowPlayingRelease => _nowPlayingRelease.Length > 0;

    /// <summary>Sets the OSD title block; call per playback start.</summary>
    public void SetNowPlaying(string title, string? subtitle = null, string? release = null)
    {
        _nowPlayingTitle = title;
        _nowPlayingSubtitle = subtitle ?? "";
        _nowPlayingRelease = release ?? "";
        OnPropertyChanged(nameof(NowPlayingTitle));
        OnPropertyChanged(nameof(NowPlayingSubtitle));
        OnPropertyChanged(nameof(HasNowPlayingSubtitle));
        OnPropertyChanged(nameof(NowPlayingRelease));
        OnPropertyChanged(nameof(HasNowPlayingRelease));
    }

    public void Attach(MpvPlayer player)
    {
        _player = player;
        player.PositionChanged += pos => { if (!IsSeeking) PositionSecondsFromPlayer(pos); };
        player.DurationChanged += d => { _durationSeconds = d; OnPropertyChanged(nameof(DurationSeconds)); OnPropertyChanged(nameof(TimeDisplay)); };
        player.PauseChanged += p => { _isPaused = p; OnPropertyChanged(nameof(IsPaused)); };
        player.VolumeChanged += v => { _volume = v; OnPropertyChanged(nameof(Volume)); };
        player.MuteChanged += m =>
        {
            _isMuted = m;
            OnPropertyChanged(nameof(IsMuted));
            // Only a mute the USER asked for flashes the indicator. mpv reports its initial
            // mute state when the property observer is registered, so raising this
            // unconditionally would flash the pill at the start of every file.
            if (_muteFeedbackPending)
            {
                _muteFeedbackPending = false;
                VolumeFeedback?.Invoke();
            }
        };
        player.BufferingChanged += b =>
        {
            _isBuffering = b;
            OnPropertyChanged(nameof(IsBuffering));
            RaisePlaybackStatus();
        };
        player.LoadingChanged += l =>
        {
            _isLoading = l;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(TransportVisible));
            RaisePlaybackStatus();
        };
        player.EndReached += () =>
        {
            _isPaused = true;
            OnPropertyChanged(nameof(IsPaused));
            // File ended with the card still pending (short credits, user seeked past
            // the countdown, …) — advance immediately instead of stranding on EOF.
            // _durationSeconds == 0 means we're mid-transition (<see cref="ResetFileGeometry"/>
            // ran at load, the new file's duration hasn't arrived): a stale eof-reached
            // post from the OUTGOING file must not advance past the incoming one.
            // _upNextAutoAdvance gates it too: in manual mode EOF must leave the card up and
            // wait, or "off" would still advance — just at the end instead of on a countdown.
            if (_upNextItem is not null && !_upNextDismissed && _durationSeconds > 0 && _upNextAutoAdvance)
            {
                UpNextDetail("fired", "eof");
                PlayUpNextNow();
            }
        };
        player.TracksChanged += RebuildTrackOptions;
        player.SpeedChanged += OnSpeedChanged;
        RebuildSpeedOptions();
        player.RtxAvailabilityChanged += NotifyRtxAvailabilityChanged;
        // The static (GPU) cause is set before Attach — push it to the bindings now.
        NotifyRtxAvailabilityChanged();
        player.VideoStateChanged += NotifyStatusBadgesChanged;
        player.ChaptersChanged += () =>
        {
            if (!_useServerChapters)
                SetChapterStarts(player.Chapters.Select(c => c.TimeSeconds));
        };
    }

    // ---- Chapters ----

    private List<double> _chapterStarts = [];
    private bool _useServerChapters;

    /// <summary>Chapter start positions in seconds, sorted. Raised on change (UI thread).</summary>
    public IReadOnlyList<double> ChapterStarts => _chapterStarts;
    public event Action? ChaptersChanged;

    public bool HasChapters => _chapterStarts.Count > 0;

    /// <summary>Server metadata chapters win over mpv's; call with empty to reset per file.</summary>
    public void SetServerChapters(IEnumerable<double> startsSeconds)
    {
        var list = startsSeconds.ToList();
        _useServerChapters = list.Count > 0;
        SetChapterStarts(list);
    }

    private void SetChapterStarts(IEnumerable<double> starts)
    {
        _chapterStarts = starts.OrderBy(s => s).ToList();
        OnPropertyChanged(nameof(HasChapters));
        ChaptersChanged?.Invoke();
    }

    public void NextChapter()
    {
        var next = _chapterStarts.FirstOrDefault(s => s > _positionSeconds + 1, -1);
        if (next < 0)
        {
            ActionDetail("next_chapter", _chapterStarts.Count == 0 ? "no_chapters" : "last_chapter");
            return;
        }
        Seek(next);
    }

    /// <summary>Within 3 s of a chapter start jumps to the previous chapter, else restarts the current one.</summary>
    public void PreviousChapter()
    {
        if (_chapterStarts.Count == 0)
        {
            ActionDetail("previous_chapter", "no_chapters");
            return;
        }
        var currentIdx = _chapterStarts.FindLastIndex(s => s <= _positionSeconds);
        if (currentIdx < 0)
        {
            Seek(0);
            return;
        }
        var withinGrace = _positionSeconds - _chapterStarts[currentIdx] <= 3;
        var target = withinGrace && currentIdx > 0 ? _chapterStarts[currentIdx - 1] : _chapterStarts[currentIdx];
        Seek(target);
    }

    // The track buttons hide when there is nothing to choose: fewer than two rows
    // (subtitles always have the synthetic "Off" row, so one real track = a choice).
    public bool HasVideoChoices => VideoTracks.Count >= 2;
    public bool HasAudioChoices => AudioTracks.Count >= 2;
    public bool HasSubtitleChoices => SubtitleTracks.Count >= 2;

    /// <summary>Whether the merged Tracks button has anything to offer (P10 M11). The three
    /// per-type flags still gate their own SECTION inside the one flyout, so a file with only
    /// subtitle choices opens a flyout with only a Subtitles section.</summary>
    public bool HasTrackChoices => HasVideoChoices || HasAudioChoices || HasSubtitleChoices;

    private void RebuildTrackOptions(int generation)
    {
        if (_player is null || generation != _player.LoadGeneration)
            return;
        VideoTracks.Clear();
        foreach (var t in _player.Tracks.Where(t => t.Type == "video"))
            VideoTracks.Add(new TrackOption(t.Id, t.Display, t.Selected));

        AudioTracks.Clear();
        foreach (var t in _player.Tracks.Where(t => t.Type == "audio"))
            AudioTracks.Add(new TrackOption(t.Id, t.Display, t.Selected, LanguageBadge.For(t.Lang)));

        SubtitleTracks.Clear();
        var subs = _player.Tracks.Where(t => t.Type == "sub").ToList();
        SubtitleTracks.Add(new TrackOption(-1, "Off", subs.All(s => !s.Selected)));
        foreach (var t in subs)
            SubtitleTracks.Add(new TrackOption(t.Id, t.Display, t.Selected, LanguageBadge.For(t.Lang)));

        var observedId = subs.FirstOrDefault(t => t.Selected)?.Id;
        if (_subtitleCursorGeneration != generation)
        {
            _subtitleCursorGeneration = generation;
            _subtitleCursorPending = false;
            _subtitleCursorIndex = IndexOfSelected(SubtitleTracks);
        }
        else if (_subtitleCursorPending)
        {
            // Off is not an observation when the real list is empty: the synthetic row is marked
            // selected by vacuous truth, but no sid state has actually been acknowledged yet.
            var acknowledged = subs.Count > 0 && observedId == _subtitleCursorPendingId;
            if (acknowledged)
            {
                _subtitleCursorPending = false;
                _subtitleCursorIndex = IndexOfSelected(SubtitleTracks);
            }
        }
        else
        {
            _subtitleCursorIndex = IndexOfSelected(SubtitleTracks);
        }

        OnPropertyChanged(nameof(HasVideoChoices));
        OnPropertyChanged(nameof(HasAudioChoices));
        OnPropertyChanged(nameof(HasSubtitleChoices));
        OnPropertyChanged(nameof(HasTrackChoices));

        RefreshAudioDevices();
    }

    // ---- Audio output device (OSD switcher) ----

    public ObservableCollection<AudioDeviceOption> AudioDevices { get; } = [];

    /// <summary>The device button hides unless there is a real choice: two or more
    /// actual outputs (mpv's own "auto" pseudo-entry doesn't count).</summary>
    public bool HasAudioDeviceChoices { get; private set; }

    /// <summary>Rebuilds the device rows from mpv's live audio-device-list. Selection is
    /// mpv's current audio-device (the applied state, not the saved preference — they
    /// differ when a saved device is missing and playback fell back to auto).</summary>
    public void RefreshAudioDevices()
    {
        if (_player is null)
            return;
        var devices = _player.GetAudioDevices().Where(d => d.Name != "auto").ToList();
        var current = _player.CurrentAudioDevice;
        // Second safety net for a vanished saved device: Create()'s validation can run
        // before mpv can enumerate outputs. Once the list is real, self-heal to auto so
        // audio recovers (mpv reinits the ao); the saved preference stays untouched.
        if (current != "auto" && devices.Count > 0 && devices.All(d => d.Name != current))
        {
            _player.SelectAudioDevice("auto");
            current = "auto";
        }
        AudioDevices.Clear();
        AudioDevices.Add(new AudioDeviceOption("auto", "System default",
            current == "auto" || devices.All(d => d.Name != current)));
        foreach (var d in devices)
            AudioDevices.Add(new AudioDeviceOption(d.Name, d.Description, d.Name == current));
        var has = devices.Count >= 2;
        if (has != HasAudioDeviceChoices)
        {
            HasAudioDeviceChoices = has;
            OnPropertyChanged(nameof(HasAudioDeviceChoices));
        }
    }

    /// <summary>Applies an output device live and makes it the persisted default —
    /// the OSD pick and the Settings dropdown share AppSettings.AudioDevice.</summary>
    public void SelectAudioDevice(AudioDeviceOption option)
    {
        if (_player is null)
        {
            ActionDetail("set_audio_device", "no_player");
            return;
        }
        _player.SelectAudioDevice(option.Name);
        if (_settings is not null)
        {
            _settings.AudioDevice = option.Name;
            SettingsStore.Save(_settings);
        }
        RefreshAudioDevices();
    }

    public void SelectVideo(TrackOption option) => _player?.SelectVideoTrack(option.Id);

    public void SelectAudio(TrackOption option) => _player?.SelectAudioTrack(option.Id);

    /// <summary>Raised before mpv's sid changes. The owner receives both the generation-local id
    /// and exact descriptor, so a queued FILE_LOADED can reconcile this request without using the
    /// language-first fallback intended only for a later file.</summary>
    public event Action<SubtitleSelectionRequest>? SubtitleChosen;

    public void SelectSubtitle(TrackOption option)
    {
        if (_player is not { } player)
            return;
        var id = option.Id < 0 ? (int?)null : option.Id;
        var track = id is { } trackId
            ? player.Tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == trackId)
            : null;
        if (id is not null && track is null)
            return;

        var optionIndex = SubtitleTracks.IndexOf(option);
        if (optionIndex >= 0)
        {
            _subtitleCursorGeneration = player.LoadGeneration;
            _subtitleCursorIndex = optionIndex;
            _subtitleCursorPending = true;
            _subtitleCursorPendingId = id;
        }
        var request = new SubtitleSelectionRequest(player.LoadGeneration, id, id is null,
            track?.Lang, track?.Title, track?.Forced ?? false);
        SubtitleChosen?.Invoke(request);
        player.SelectSubtitleTrack(id);
    }

    /// <summary>
    /// Verbose-only record for a player action that reached this layer and then did NOTHING.
    ///
    /// <para>The SUCCESSFUL paths are deliberately not recorded here. A key press is already
    /// written down as <c>[shortcut] event=invoke</c> (MainWindow) and an OSD press as
    /// <c>[overlay] event=interaction</c>, and both funnel through this class — so a success
    /// record would write one user action two or three times. What those intent records cannot
    /// know is that the action was REFUSED, which is the whole content of this one: pressing A on
    /// a file with a single audio track was, until now, indistinguishable in the log from pressing
    /// nothing at all.</para>
    /// </summary>
    private static void ActionDetail(string action, string reason)
    {
        // Ahead of the formatting, like every other Detail site: these sit on key-repeat paths.
        if (!Diagnostics.AppLog.Verbose)
            return;
        Diagnostics.AppLog.Detail("player", $"event=action action={action} outcome=noop reason={reason}");
    }

    /// <summary>Cycles audio tracks (B key). Needs at least two tracks.</summary>
    public void CycleAudioTrack()
    {
        if (_player is null)
        {
            ActionDetail("cycle_audio", "no_player");
            return;
        }
        if (AudioTracks.Count < 2)
        {
            ActionDetail("cycle_audio", "insufficient_tracks");
            return;
        }
        var idx = IndexOfSelected(AudioTracks);
        _player.SelectAudioTrack(AudioTracks[(idx + 1) % AudioTracks.Count].Id);
    }

    /// <summary>Cycles subtitles: first → … → last → Off → first.</summary>
    public void CycleSubtitleTrack()
    {
        if (_player is null)
        {
            ActionDetail("cycle_subtitle", "no_player");
            return;
        }
        // One real subtitle track still counts as a choice — the synthetic "Off" row is the
        // second one — so this refusal means the file has no subtitles at all.
        if (SubtitleTracks.Count < 2)
        {
            ActionDetail("cycle_subtitle", "insufficient_tracks");
            return;
        }
        var generation = _player.LoadGeneration;
        if (_subtitleCursorGeneration != generation)
        {
            _subtitleCursorGeneration = generation;
            _subtitleCursorIndex = IndexOfSelected(SubtitleTracks);
            _subtitleCursorPending = false;
        }
        _subtitleCursorIndex = (_subtitleCursorIndex + 1) % SubtitleTracks.Count;
        var next = SubtitleTracks[_subtitleCursorIndex];
        _subtitleCursorPending = true;
        _subtitleCursorPendingId = next.Id < 0 ? null : next.Id;
        SelectSubtitle(next);
    }

    /// <summary>Executes S presses captured before FileLoaded, after the authoritative current
    /// generation list has rebuilt the options. Returns false when no real subtitle row exists yet,
    /// so a later embedded/external track wave can retry the same count.</summary>
    public bool ExecuteQueuedSubtitleCycles(int count)
    {
        if (_player is null || count <= 0)
            return true;
        if (SubtitleTracks.Count < 2)
            return false;
        for (var i = 0; i < count; i++)
            CycleSubtitleTrack();
        return true;
    }

    /// <summary>Aligns the subtitle cycle cursor to a restoration that the current mpv snapshot
    /// has exactly acknowledged. Rebuilds from that snapshot first, so cycle execution does not
    /// depend on TracksChanged subscriber timing.</summary>
    public bool PrimeSubtitleCycleCursor(int generation, int? trackId)
    {
        if (_player is not { } player || generation != player.LoadGeneration)
            return false;
        var subs = player.Tracks.Where(t => t.Type == "sub").ToList();
        var acknowledged = trackId is { } id
            ? subs.Any(t => t.Id == id && t.Selected)
            : subs.Count > 0 && subs.All(t => !t.Selected);
        if (!acknowledged)
            return false;

        RebuildTrackOptions(generation);
        var optionIndex = -1;
        for (var i = 0; i < SubtitleTracks.Count; i++)
        {
            if ((trackId is null && SubtitleTracks[i].Id < 0)
                || (trackId is not null && SubtitleTracks[i].Id == trackId.Value))
            {
                optionIndex = i;
                break;
            }
        }
        if (optionIndex < 0)
            return false;

        _subtitleCursorGeneration = generation;
        _subtitleCursorIndex = optionIndex;
        _subtitleCursorPending = false;
        _subtitleCursorPendingId = trackId;
        return true;
    }

    /// <summary>Cycles video tracks (M16; mirrors CycleAudioTrack). Needs two tracks.</summary>
    public void CycleVideoTrack()
    {
        if (_player is null)
        {
            ActionDetail("cycle_video", "no_player");
            return;
        }
        if (VideoTracks.Count < 2)
        {
            ActionDetail("cycle_video", "insufficient_tracks");
            return;
        }
        var idx = IndexOfSelected(VideoTracks);
        _player.SelectVideoTrack(VideoTracks[(idx + 1) % VideoTracks.Count].Id);
    }

    private static int IndexOfSelected(ObservableCollection<TrackOption> tracks)
    {
        for (var i = 0; i < tracks.Count; i++)
            if (tracks[i].IsSelected)
                return i;
        return 0;
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value) && _player is not null)
                _player.Pause = value;
        }
    }

    /// <summary>Zeroes the per-file geometry. Call where a load is <em>initiated</em>, not
    /// where it completes: until the new file's duration arrives, <c>DurationSeconds == 0</c>
    /// is what marks the transition, and both the EOF up-next guard and the Up Next trigger
    /// key off it. Without this the outgoing file's duration stays live across the swap and a
    /// stale <c>eof-reached</c> post advances past the incoming file (B1), or one last
    /// coalesced position from the old file arms the card over the new one.</summary>
    public void ResetFileGeometry()
    {
        _positionSeconds = 0;
        _durationSeconds = 0;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(TimeDisplay));
    }

    /// <summary>Two-way slider binding: set = seek.</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (SetProperty(ref _positionSeconds, value))
            {
                OnPropertyChanged(nameof(TimeDisplay));
                if (IsSeeking && _player is not null)
                    _player.TimePos = value;
            }
        }
    }

    // ---- Playback speed (Phase 5 M2) ----

    private static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];
    private double _speed = 1.0;

    public ObservableCollection<SpeedOption> SpeedOptions { get; } = [];

    public string SpeedDisplay => FormattableString.Invariant($"{_speed:0.##}×");

    private void OnSpeedChanged(double speed)
    {
        _speed = speed;
        RebuildSpeedOptions();
        OnPropertyChanged(nameof(SpeedDisplay));
    }

    private void RebuildSpeedOptions()
    {
        SpeedOptions.Clear();
        foreach (var s in Speeds)
            SpeedOptions.Add(new SpeedOption(s, Math.Abs(s - _speed) < 0.001));
    }

    public void SelectSpeed(SpeedOption option)
    {
        if (_player is not null)
            _player.Speed = option.Value;
    }

    /// <summary>Steps to the next faster preset (+ key).</summary>
    public void IncreaseSpeed()
    {
        if (_player is null)
            return;
        var next = Speeds.FirstOrDefault(s => s > _speed + 0.001, Speeds[^1]);
        _player.Speed = next;
    }

    /// <summary>Steps to the next slower preset (- key).</summary>
    public void DecreaseSpeed()
    {
        if (_player is null)
            return;
        var next = Speeds.LastOrDefault(s => s < _speed - 0.001, Speeds[0]);
        _player.Speed = next;
    }

    // ---- Playback queue (Phase 5 M1) ----

    private PlayQueue? _queue;

    /// <summary>Queue rows for the overlay popup; rebuilt on queue changes.</summary>
    public ObservableCollection<QueueRow> QueueRows { get; } = [];

    public bool HasQueue => _queue is { IsActive: true };
    public string QueueBadge => _queue is { IsActive: true } q ? $"{q.CurrentIndex + 1}/{q.Count}" : "";

    /// <summary>A queue navigation picked this item; MainWindow starts the playback
    /// (the queue index has already moved).</summary>
    public event Action<MediaItem>? QueuePlayRequested;

    /// <summary>Wires the app-level queue into the overlay bindings (call once).</summary>
    public void AttachQueue(PlayQueue queue)
    {
        _queue = queue;
        queue.Changed += RebuildQueueRows;
        RebuildQueueRows();
    }

    private void RebuildQueueRows()
    {
        QueueRows.Clear();
        if (_queue is { IsActive: true } q)
            for (var i = 0; i < q.Items.Count; i++)
                QueueRows.Add(new QueueRow(i, q.Items[i].QueueDisplay, i == q.CurrentIndex));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueBadge));
        NotifySkipStateChanged();
    }

    // ---- OSD next/prev enable state (M14) + transport mapping (M17) --------------

    private Settings.TransportAction NextAction => _settings?.MediaNextAction ?? Settings.TransportAction.QueueStep;
    private Settings.TransportAction PrevAction => _settings?.MediaPrevAction ?? Settings.TransportAction.QueueStep;

    /// <summary>True when the media-key "next" has somewhere to go. Chapter/Seek mappings are
    /// always actionable; queue-stepping defers to <see cref="CanGoNext"/>.</summary>
    public bool CanSkipNext => NextAction != Settings.TransportAction.QueueStep || CanGoNext;

    public bool CanSkipPrevious => PrevAction != Settings.TransportAction.QueueStep || CanGoPrevious;

    private void NotifySkipStateChanged()
    {
        OnPropertyChanged(nameof(CanSkipNext));
        OnPropertyChanged(nameof(CanSkipPrevious));
        NotifyNextPrevChanged();
    }

    // ---- The one next/previous pair (Phase 10) -----------------------------------
    //
    // There used to be TWO pairs in the OSD: a queue pair (skip_previous/skip_next, driven by
    // the M17 transport mapping) and a dedicated episode pair (Phase 7, pure series-order
    // stepping). In the everyday case - an episode playing from a season queue that is already
    // in series order - they did exactly the same thing, side by side, which is how the
    // duplication was reported.
    //
    // They were not redundant in general, so this is a merge and not a deletion. Precedence
    // below keeps every case the episode pair alone would have dropped:
    //   * an explicit queue wins (playlists, shuffled queues, movie queues - none of which the
    //     episode pair could step, since it only appears for episode playback),
    //   * then the armed Up Next episode,
    //   * then the series-order neighbour (an episode played with no queue at all, where the
    //     queue pair offered no "previous" and only a conditional "next").
    //
    // The buttons deliberately do NOT honour the M17 chapter/seek mapping any more. Chapter
    // stepping has its own dedicated pair a few controls along, already shown only when the
    // file has chapters, so mapping this pair onto chapters made one of them redundant and
    // stole the only OSD route to the next episode. The mapping still governs the media keys
    // and SMTC, which is what it was introduced for.

    // THE TWO DIRECTIONS ARE DELIBERATELY ASYMMETRIC, and both halves were pinned down by a
    // suite failure rather than reasoned out in advance:
    //
    //  * FORWARDS the queue is authoritative, so the series fallback is gated behind
    //    !HasQueue. test-m14-skip-next's end-boundary leg caught the ungated version: at 3/3 of
    //    a three-episode playlist the fallback found a series-order next episode and kept the
    //    button live, so "Next" would have walked straight out of a queue the user curated.
    //    A queue's forward extent is an explicit statement about what comes next.
    //
    //  * BACKWARDS it is not, so there is no gate. test-episode-step's mid-season leg caught the
    //    over-corrected version: the M15 "watch from here" queue starts AT the current episode,
    //    so it never has a previous entry, and gating on HasQueue killed "previous" for every
    //    episode opened from a detail view - which is precisely what the old dedicated episode
    //    pair existed to provide. A queue's backward extent is an artifact of where you happened
    //    to start, not a statement about what came before.
    //
    // (The armed Up Next stays outside the forward gate - it is the queue's own end-of-item
    // hand-off rather than a competing source, and MediaNext behaved that way before the merge.)

    /// <summary>Somewhere to go forwards: a queue entry, an armed Up Next, or - only when no
    /// queue is active - the next episode in series order.</summary>
    public bool CanGoNext => _queue?.PeekNext() is not null
        || _upNextItem is not null
        || (!HasQueue && _nextEpisode is not null);

    /// <summary>Somewhere to go back to: a queue entry, else the previous episode in series
    /// order. Ungated on purpose - see the note above.</summary>
    public bool CanGoPrevious => _queue?.PeekPrevious() is not null || _prevEpisode is not null;

    /// <summary>The pair shows when either direction is meaningful.</summary>
    public bool ShowNextPrevButtons => CanGoNext || CanGoPrevious;

    public string NextTooltip => "Next (N)";
    public string PreviousTooltip => "Previous (P)";

    /// <summary>Queue entry, else the armed Up Next, else — only with no active queue — the
    /// next episode in series order. Mirrors <see cref="CanGoNext"/> exactly.</summary>
    public void GoNext()
    {
        if (_queue?.PeekNext() is not null) { NextInQueue(); return; }
        if (_upNextItem is not null) { PlayUpNextNow(); return; }
        if (!HasQueue && _nextEpisode is { } next) EpisodeStepRequested?.Invoke(next);
        // Every source exhausted. The button hides itself in this state (CanGoNext) but the N key
        // does not, so this is what a press against the end of a queue looks like.
        else ActionDetail("next", "nothing_next");
    }

    /// <summary>Queue entry, else the previous episode in series order. Mirrors
    /// <see cref="CanGoPrevious"/> exactly — ungated, unlike the forward direction.</summary>
    public void GoPrevious()
    {
        if (_queue?.PeekPrevious() is not null) { PreviousInQueue(); return; }
        if (_prevEpisode is { } prev) EpisodeStepRequested?.Invoke(prev);
        else ActionDetail("previous", "nothing_previous");
    }

    private void NotifyNextPrevChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(ShowNextPrevButtons));
    }

    /// <summary>Settings changed the transport mapping — refresh the bindings.</summary>
    public void NotifyTransportConfigChanged() => NotifySkipStateChanged();

    /// <summary>Media-key / SMTC "next" per the transport mapping (M17). Queue mode now runs
    /// the merged <see cref="GoNext"/>, so a media key reaches the next episode in exactly the
    /// cases the OSD button does.</summary>
    public void TransportNext()
    {
        switch (NextAction)
        {
            case Settings.TransportAction.ChapterStep: NextChapter(); break;
            case Settings.TransportAction.Seek: _player?.SeekRelative(_settings?.TransportSeekSeconds ?? 10); break;
            default: GoNext(); break;
        }
    }

    public void TransportPrev()
    {
        switch (PrevAction)
        {
            case Settings.TransportAction.ChapterStep: PreviousChapter(); break;
            case Settings.TransportAction.Seek: _player?.SeekRelative(-(_settings?.TransportSeekSeconds ?? 10)); break;
            default: GoPrevious(); break;
        }
    }

    /// <summary>Next queue entry (N key / overlay button); no-op at the end.</summary>
    public void NextInQueue()
    {
        if (_queue?.MoveNext() is { } item)
            QueuePlayRequested?.Invoke(item);
        else
            ActionDetail("next_in_queue", "queue_end");
    }

    /// <summary>Previous queue entry (P key / overlay button); no-op at the start.</summary>
    public void PreviousInQueue()
    {
        if (_queue?.MovePrevious() is { } item)
            QueuePlayRequested?.Invoke(item);
        else
            ActionDetail("previous_in_queue", "queue_start");
    }

    public void JumpToQueueRow(QueueRow row)
    {
        if (_queue?.JumpTo(row.Index) is { } item)
            QueuePlayRequested?.Invoke(item);
    }

    public void RemoveQueueRow(QueueRow row) => _queue?.RemoveAt(row.Index);

    // ---- Auto-play next episode (Up Next card) ----

    private MediaItem? _upNextItem;
    private bool _upNextDismissed;
    private bool _upNextCardVisible;
    private bool _upNextCountdownActive;
    private bool _upNextAutoAdvance = true;
    private int _upNextRemaining;
    private System.Windows.Threading.DispatcherTimer? _upNextTimer;
    private int _upNextCountdownSeconds = 10;

    /// <summary>The user chose (or the countdown decided) to play this item now.</summary>
    public event Action<MediaItem>? UpNextPlayRequested;

    /// <summary>The card shows whenever an armed episode is past the trigger point.
    /// Deliberately NOT tied to the countdown: with "Auto-play next episode" off the card is
    /// still offered — with a Play action and no timer at all — and nothing advances until the
    /// user accepts it (decided 2026-07-30). The setting governs auto-advance, not the card.</summary>
    public bool UpNextVisible => _upNextCardVisible;

    /// <summary>Whether the card is counting down. False in manual mode, which hides the
    /// "Playing in N s" line and leaves Play now / Dismiss as the only ways forward.</summary>
    public bool UpNextHasCountdown => _upNextCountdownActive;

    public string UpNextTitle => _upNextItem?.Name ?? "";
    public string UpNextSubtitle => _upNextItem is { } i
        ? string.Join("  ·  ", new[] { i.SeriesName ?? "", i.EpisodeTag }.Where(s => s.Length > 0))
        : "";
    public string? UpNextThumbUrl => _upNextItem?.ThumbUrl ?? _upNextItem?.PosterUrl;

    /// <summary>Empty in manual mode, so the text and the element's visibility agree — a test
    /// (or a reader) can assert on either and get the same answer.</summary>
    public string UpNextCountdownText => _upNextCountdownActive ? $"Playing in {_upNextRemaining} s" : "";

    /// <summary>Arms (or clears, with null) the Up Next card for the current playback.
    /// <paramref name="autoAdvance"/> false arms it as a manual offer: the card appears at the
    /// trigger point but no countdown runs and EOF does not advance.</summary>
    public void SetUpNext(MediaItem? item, int countdownSeconds = 10, bool autoAdvance = true)
    {
        _upNextItem = item;
        _upNextCountdownSeconds = Math.Max(3, countdownSeconds);
        _upNextAutoAdvance = autoAdvance;
        _upNextDismissed = false;
        _upNextRemaining = 0;
        _upNextCardVisible = false;
        StopUpNextCountdown();
        NotifyUpNextChanged();
    }

    public void PlayUpNextNow()
    {
        if (_upNextItem is not { } item)
        {
            ActionDetail("play_up_next", "no_up_next");
            return;
        }
        StopUpNextCountdown();
        _upNextCardVisible = false;
        _upNextItem = null;
        NotifyUpNextChanged();
        UpNextPlayRequested?.Invoke(item);
    }

    public void DismissUpNext()
    {
        _upNextDismissed = true;
        _upNextCardVisible = false;
        StopUpNextCountdown();
        NotifyUpNextChanged();
    }

    private void UpdateUpNext(double pos)
    {
        if (_upNextItem is null || _upNextDismissed || _durationSeconds <= 0)
            return;
        // Credits start when known, else the last 30 seconds.
        var creditsStart = _skipSegments.FirstOrDefault(s => s.Kind == SkipSegmentKind.Credits)?.StartSeconds;
        var trigger = Math.Max(5, creditsStart ?? _durationSeconds - 30);
        if (pos < trigger)
        {
            // Seeked back out of the credits with the card up: disarm, rather than
            // abandoning the position the user just chose. Crossing the trigger again
            // re-arms with a full countdown.
            if (_upNextCardVisible)
            {
                _upNextCardVisible = false;
                StopUpNextCountdown();
                UpNextDetail("disarmed", "seek_before_trigger");
                NotifyUpNextChanged();
            }
            return;
        }
        if (_upNextCardVisible)
            return;
        _upNextCardVisible = true;
        // Manual mode shows the same card and starts no timer — the Play now button is the
        // only thing that advances it.
        if (_upNextAutoAdvance)
        {
            _upNextRemaining = _upNextCountdownSeconds;
            _upNextCountdownActive = true;
            _upNextTimer ??= CreateUpNextTimer();
            _upNextTimer.Start();
        }
        // At the ARM, never per tick. This method runs on every position update and the countdown
        // ticks once a second; a record per tick would be exactly the chatter that makes a verbose
        // log useless (and would keep a paused player writing to disk).
        UpNextDetail("armed", _upNextAutoAdvance ? "auto_advance" : "manual");
        NotifyUpNextChanged();
    }

    private System.Windows.Threading.DispatcherTimer CreateUpNextTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            // The countdown is wall-clock, so it would otherwise run down while the user
            // has paused (or the stream stalled) and start the next episode unattended.
            if (_isPaused || _isBuffering)
                return;
            _upNextRemaining--;
            if (_upNextRemaining <= 0)
            {
                UpNextDetail("fired", "countdown");
                PlayUpNextNow();
            }
            else
            {
                OnPropertyChanged(nameof(UpNextCountdownText));
            }
        };
        return timer;
    }

    /// <summary>Verbose-only record for the Up Next card's own state machine — armed, disarmed,
    /// and the two ways it can fire. The countdown TICK is deliberately not recorded; the
    /// <c>[overlay] event=interaction action=up-next-*</c> records already cover the button
    /// presses, so what is added here is the automatic behaviour nothing else can see.</summary>
    private static void UpNextDetail(string outcome, string reason)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        Diagnostics.AppLog.Detail("player", $"event=up_next outcome={outcome} reason={reason}");
    }

    private void StopUpNextCountdown()
    {
        _upNextTimer?.Stop();
        _upNextCountdownActive = false;
    }

    private void NotifyUpNextChanged()
    {
        NotifySkipStateChanged();   // the armed Up Next feeds CanSkipNext (M14)
        OnPropertyChanged(nameof(UpNextVisible));
        OnPropertyChanged(nameof(UpNextHasCountdown));
        OnPropertyChanged(nameof(UpNextTitle));
        OnPropertyChanged(nameof(UpNextSubtitle));
        OnPropertyChanged(nameof(UpNextThumbUrl));
        OnPropertyChanged(nameof(UpNextCountdownText));
    }

    // ---- Series-order neighbours ------------------------------------------------
    // Fed by MainWindow per playback. Since Phase 10 these are the LAST fallback of the merged
    // next/previous pair rather than a second pair of buttons of their own.

    private MediaItem? _prevEpisode;
    private MediaItem? _nextEpisode;

    /// <summary>Next/previous picked a series-order episode; MainWindow starts it
    /// through the normal "watch from here" path.</summary>
    public event Action<MediaItem>? EpisodeStepRequested;

    /// <summary>Sets (or clears) the series-order neighbors for the current playback.</summary>
    public void SetEpisodeNeighbors(bool isEpisode, MediaItem? previous, MediaItem? next)
    {
        _prevEpisode = isEpisode ? previous : null;
        _nextEpisode = isEpisode ? next : null;
        NotifyNextPrevChanged();
    }

    // ---- Info panel (live playback stats + server media info) ----

    /// <summary>Live mpv playback state; refreshed by the overlay while its info
    /// panel is open (1 s cadence).</summary>
    public ObservableCollection<InfoRow> LiveStats { get; } = [];

    /// <summary>Server-side media metadata of the playing item (empty for local files).</summary>
    public ObservableCollection<InfoRow> MediaInfoRows { get; } = [];

    public bool HasMediaInfo => MediaInfoRows.Count > 0;

    // How the current stream reaches us ("Direct play" / "Transcode (HLS)");
    // null for local files — the info panel omits the row.
    private string? _playMethod;

    /// <summary>Sets the play-method row of the info panel; call per playback.</summary>
    public void SetPlayMethod(string? method) => _playMethod = method;

    /// <summary>Sets the media-info section per playback; null/empty hides it.</summary>
    public void SetMediaInfo(IEnumerable<InfoRow>? rows)
    {
        MediaInfoRows.Clear();
        foreach (var row in rows ?? [])
            MediaInfoRows.Add(row);
        OnPropertyChanged(nameof(HasMediaInfo));
    }

    /// <summary>Re-reads the live mpv properties into <see cref="LiveStats"/>.</summary>
    public void RefreshLiveStats()
    {
        if (_player is not { } player)
            return;
        string? Prop(string name)
            => player.GetPropertyString(name) is { Length: > 0 } v ? v : null;

        var rows = new List<InfoRow>();

        if (_playMethod is { } method)
            rows.Add(new InfoRow("Play method", method));

        // video-format is the short name ("h264"); video-codec is the long description.
        if ((Prop("video-format") ?? Prop("video-codec")) is { } vcodec)
            rows.Add(new InfoRow("Video", vcodec));
        if (Prop("video-params/w") is { } w && Prop("video-params/h") is { } h)
        {
            var res = $"{w}x{h}";
            if (Prop("video-params/aspect") is { } aspect
                && double.TryParse(aspect, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var a) && a > 0)
                res += $" ({a:0.##}:1)";
            rows.Add(new InfoRow("Resolution", res));
        }
        if (Prop("container-fps") is { } fps
            && double.TryParse(fps, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f))
            rows.Add(new InfoRow("Frame rate", FormattableString.Invariant($"{f:0.###} fps")));

        var gamma = Prop("video-params/gamma");
        if (gamma is not null)
        {
            var hdr = gamma is "pq" or "hlg" ? "HDR" : "SDR";
            var primaries = Prop("video-params/primaries");
            rows.Add(new InfoRow("Dynamic range",
                primaries is null ? $"{hdr} ({gamma})" : $"{hdr} ({gamma} / {primaries})"));
        }

        rows.Add(new InfoRow("Hardware decoding",
            Prop("hwdec-current") is { } hw and not "no" ? hw : "off"));

        if (Prop("video-bitrate") is { } vbr
            && double.TryParse(vbr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bps) && bps > 0)
            rows.Add(new InfoRow("Video bitrate", FormattableString.Invariant($"{bps / 1_000_000:0.0} Mbps")));

        if (Prop("audio-codec-name") is { } acodec)
        {
            var audio = acodec;
            if (Prop("audio-params/channel-count") is { } ch)
                audio += $" · {ch} ch";
            if (Prop("audio-params/samplerate") is { } sr && int.TryParse(sr, out var hz))
                audio += $" · {hz / 1000.0:0.#} kHz";
            rows.Add(new InfoRow("Audio", audio));
        }

        rows.Add(new InfoRow("RTX",
            $"VSR {(player.RtxVsrActive ? "on" : "off")} · RTX HDR {(player.RtxHdrActive ? "on" : "off")}"));

        if (Prop("frame-drop-count") is { } drops)
            rows.Add(new InfoRow("Dropped frames", drops));

        // Update in place when the row set is unchanged — a full reset every second
        // flickers and churns the automation tree out from under UIA readers.
        if (LiveStats.Count == rows.Count
            && LiveStats.Select(r => r.Label).SequenceEqual(rows.Select(r => r.Label)))
        {
            for (var i = 0; i < rows.Count; i++)
                if (LiveStats[i] != rows[i])
                    LiveStats[i] = rows[i];
        }
        else
        {
            LiveStats.Clear();
            foreach (var row in rows)
                LiveStats.Add(row);
        }
    }

    // ---- Trickplay ----

    /// <summary>Preview source for the seek bar; null = no trickplay (time-only tooltip).
    /// Set per playback from the metadata fetch, read by the overlay on hover.</summary>
    public TrickplayProvider? Trickplay { get; private set; }

    public void SetTrickplay(TrickplayProvider? provider) => Trickplay = provider;

    // ---- Jump forward/back ----

    private AppSettings? _settings;

    /// <summary>Gives the view-model the live settings instance (jump sizes are read
    /// per keypress so settings edits apply immediately).</summary>
    public void Configure(AppSettings settings) => _settings = settings;

    /// <summary>Passes the overlay's subtitle lift through to mpv (100 = default bottom).</summary>
    public void SetSubtitlePosition(int percent) => _player?.SetSubtitlePosition(percent);

    /// <summary>The user's resting sub-pos (settings); the control-bar lift composes
    /// against this instead of a hardcoded 100.</summary>
    public int SubtitleBasePosition => Math.Clamp(_settings?.SubtitleBasePosition ?? 100, 0, 150);

    /// <summary>Raised when settings live-apply changed <see cref="SubtitleBasePosition"/>.
    /// <see cref="Views.OverlayWindow"/> listens and recomposes, which is what makes it the
    /// ONE writer of sub-pos: settings used to call <see cref="SetSubtitlePosition"/> directly
    /// and the next bar show/hide immediately overwrote it (Phase 10 M4).</summary>
    public event Action? SubtitleBasePositionChanged;

    /// <summary>Settings changed the resting subtitle position — ask the overlay to recompose
    /// rather than writing sub-pos here.</summary>
    public void NotifySubtitleBasePositionChanged() => SubtitleBasePositionChanged?.Invoke();

    /// <summary>Raised by the rebindable <see cref="Player.PlayerAction.ToggleFullscreen"/>.
    /// Fullscreen is a window operation and <c>MainWindow.ToggleFullscreen</c> owns it, but
    /// <see cref="Player.PlayerActionDispatcher.Dispatch"/> is handed only this view-model — so the
    /// action asks, and the window (which also serves F11, the video double-click and the OSD
    /// button) does it.</summary>
    public event Action? FullscreenRequested;

    /// <summary>Ask the shell to toggle fullscreen; see <see cref="FullscreenRequested"/>.</summary>
    public void RequestFullscreen() => FullscreenRequested?.Invoke();

    /// <summary>Ask the shell to open the stream address view; the shell enforces browse-only use.</summary>
    public event Action? OpenStreamRequested;

    public void RequestOpenStream() => OpenStreamRequested?.Invoke();

    public void JumpForward() => _player?.SeekRelative(_settings?.SkipForwardSeconds ?? 10);

    public void JumpBack() => _player?.SeekRelative(-(_settings?.SkipBackwardSeconds ?? 10));

    // One setting for both directions, unlike the plain jump above: the large seek exists to cover
    // ground, and an asymmetric coarse step is harder to undo than it is useful.
    public void JumpForwardLarge() => _player?.SeekRelative(_settings?.LargeSkipSeconds ?? 30);

    public void JumpBackLarge() => _player?.SeekRelative(-(_settings?.LargeSkipSeconds ?? 30));

    // No setting to read: a frame is a frame. mpv pauses itself on either of these, and the pause
    // observer carries that back into IsPaused, so nothing here has to touch playback state.
    public void FrameStepForward() => _player?.FrameStep();

    public void FrameStepBack() => _player?.FrameBackStep();

    /// <summary>Seek to the given position (used on slider click / drag end).</summary>
    public void Seek(double seconds)
    {
        if (_player is null)
            return;
        _player.TimePos = seconds;
        PositionSecondsFromPlayer(seconds);
    }

    public double DurationSeconds => _durationSeconds;

    /// <summary>Raised when the user changes what they hear — the volume level or the mute
    /// flag — and only then (P10 M10). The overlay's volume indicator listens here rather
    /// than to <c>PropertyChanged</c>, because mpv reports both properties on file load and
    /// a transient readout must not fire on playback start.
    ///
    /// This setter is the single choke point for the level: every user path writes it (the
    /// Up/Down actions, the wheel-over-video gesture, and the OSD slider's two-way binding),
    /// while mpv's own <c>VolumeChanged</c> writes the backing field directly in
    /// <see cref="Attach"/>. So subscribing here catches all four triggers without needing
    /// each of them to remember to raise it.</summary>
    public event Action? VolumeFeedback;

    /// <summary>Set by <see cref="ToggleMute"/> and consumed by the <c>MuteChanged</c>
    /// handler — mute round-trips through mpv, so the state is only correct to display once
    /// mpv reports it back.</summary>
    private bool _muteFeedbackPending;

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value) && _player is not null)
                _player.Volume = value;
            // Deliberately outside the changed-check: Down at 0 (or Up at 100) changes
            // nothing, and the indicator confirming "0%" is exactly the feedback the user
            // pressing the key is asking for.
            VolumeFeedback?.Invoke();
        }
    }

    /// <summary>mpv's real mute flag (M13) — independent of Volume, so unmuting
    /// restores the prior level. Follows the player via MuteChanged.</summary>
    public bool IsMuted => _isMuted;

    /// <summary>True from a load until mpv can display frames. The transport bar is hidden while
    /// it holds: before this the OSD rendered a normal-looking bar parked at 0:00, which is
    /// indistinguishable from a stuck player — reported by the user after B31, when a load that
    /// was merely slow read as the bug not being fixed.</summary>
    public bool IsLoading => _isLoading;

    /// <summary>The transport bar shows only once there is something to transport. Loading is the
    /// only state that hides it: a mid-playback stall deliberately keeps the controls live, since
    /// the position is real by then and seeking out of a stall is a reasonable thing to want.</summary>
    public bool TransportVisible => !_isLoading;

    /// <summary>Caption under the activity ring, or empty when it should not be shown. One ring
    /// serves both states and the word is what tells them apart.</summary>
    public string PlaybackStatusText => _isLoading ? "Loading…" : _isBuffering ? "Buffering…" : string.Empty;

    public bool HasPlaybackStatus => _isLoading || _isBuffering;

    private void RaisePlaybackStatus()
    {
        OnPropertyChanged(nameof(PlaybackStatusText));
        OnPropertyChanged(nameof(HasPlaybackStatus));
    }

    /// <summary>True while mpv is stalled on the demuxer cache (paused-for-cache) —
    /// drives the OSD buffering indicator (M26).</summary>
    public bool IsBuffering => _isBuffering;

    public void ToggleMute()
    {
        if (_player is { } p)
        {
            _muteFeedbackPending = true;
            p.Mute = !p.Mute;
        }
    }

    public string TimeDisplay => $"{Format(_positionSeconds)} / {Format(_durationSeconds)}";

    /// <summary>False while the RTX filters can't apply (HDR source suppressed by the
    /// green-frame guard, or no NVIDIA RTX GPU) — the overlay toggles disable
    /// themselves on it. The toggles' checked state stays the user's preference.</summary>
    public bool RtxAvailable => _player?.RtxUnavailableReason is null;

    public string RtxVsrTooltip => _player?.RtxUnavailableReason is { } reason
        ? $"Unavailable: {reason}"
        : "RTX Video Super Resolution (d3d11vpp scaling-mode=nvidia)";

    public string RtxHdrTooltip => _player?.RtxUnavailableReason is { } reason
        ? $"Unavailable: {reason}"
        : "RTX Video HDR (d3d11vpp=nvidia-true-hdr)";

    // ---- OSD status badges (passive pills under the now-playing title) ----

    /// <summary>Slot 1 of the OSD status badges: "HDR" for natively HDR (PQ/HLG) sources,
    /// else null. Phase 8 M7.1: RTX-active state (HDR/VSR) is shown ONLY by the garnet
    /// gem toggles now, so this passive pill carries only what the toggles can't — the
    /// native-source HDR flag (RTX is suppressed on HDR sources, so the two never clash).</summary>
    public string? StatusHdrLabel => _player is { } p && p.IsHdrContent ? "HDR" : null;

    public bool HasStatusHdr => StatusHdrLabel is not null;

    /// <summary>RTX Super Resolution applied state — no longer a status pill (M7.1); the
    /// garnet RTX VSR toggle is the sole indicator. Retained for any non-badge consumer.</summary>
    public bool StatusVsrActive => _player?.RtxVsrActive == true;

    /// <summary>Slot 3: source resolution label (4K / 1080p / 720p / SD), null while
    /// no video is loaded. Same thresholds as the detail-view gem pill.</summary>
    public string? StatusResolutionLabel => (_player?.VideoWidth ?? 0) is var w and > 0
        ? w >= 3400 ? "4K" : w >= 1900 ? "1080p" : w >= 1200 ? "720p" : "SD"
        : null;

    public bool HasStatusResolution => StatusResolutionLabel is not null;

    private void NotifyStatusBadgesChanged()
    {
        OnPropertyChanged(nameof(StatusHdrLabel));
        OnPropertyChanged(nameof(HasStatusHdr));
        OnPropertyChanged(nameof(StatusVsrActive));
        OnPropertyChanged(nameof(StatusResolutionLabel));
        OnPropertyChanged(nameof(HasStatusResolution));
    }

    private void NotifyRtxAvailabilityChanged()
    {
        OnPropertyChanged(nameof(RtxAvailable));
        OnPropertyChanged(nameof(RtxVsrTooltip));
        OnPropertyChanged(nameof(RtxHdrTooltip));
    }

    public bool RtxVsrEnabled
    {
        get => _rtxVsrEnabled;
        set
        {
            if (SetProperty(ref _rtxVsrEnabled, value))
                _player?.SetRtxFilters(_rtxVsrEnabled, _rtxHdrEnabled, _maxLumaNits);
        }
    }

    public bool RtxHdrEnabled
    {
        get => _rtxHdrEnabled;
        set
        {
            if (SetProperty(ref _rtxHdrEnabled, value))
                _player?.SetRtxFilters(_rtxVsrEnabled, _rtxHdrEnabled, _maxLumaNits);
        }
    }

    /// <summary>Applies the persisted RTX defaults (called once after Attach; the
    /// property setters push the filters to mpv).</summary>
    public void ApplyRtxDefaults(AppSettings settings)
    {
        _maxLumaNits = settings.RtxHdrMaxLumaNits;
        RtxVsrEnabled = settings.RtxVsrDefault;
        RtxHdrEnabled = settings.RtxHdrDefault;
    }

    private void PositionSecondsFromPlayer(double pos)
    {
        _positionSeconds = pos;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(TimeDisplay));
        UpdateActiveSkipSegment(pos);
        UpdateUpNext(pos);
    }

    // ---- Skip intro/credits ----

    private IReadOnlyList<SkipSegment> _skipSegments = [];
    private readonly HashSet<SkipSegment> _autoSkipped = [];
    private SkipSegment? _activeSkip;
    private bool _autoSkipIntro;
    private bool _autoSkipCredits;

    public bool SkipButtonVisible => _activeSkip is not null;
    public string SkipButtonLabel => _activeSkip?.Label ?? "";

    /// <summary>Set per playback: the item's skippable segments and the auto-skip settings.</summary>
    public void SetSkipSegments(IReadOnlyList<SkipSegment> segments, bool autoSkipIntro, bool autoSkipCredits)
    {
        _skipSegments = segments;
        _autoSkipIntro = autoSkipIntro;
        _autoSkipCredits = autoSkipCredits;
        _autoSkipped.Clear();
        _activeSkip = null;
        OnPropertyChanged(nameof(SkipButtonVisible));
        OnPropertyChanged(nameof(SkipButtonLabel));
    }

    /// <summary>Skips past the currently active segment (button / S key).</summary>
    public void SkipActiveSegment()
    {
        if (_activeSkip is { } segment)
            Seek(segment.EndSeconds);
        else
            ActionDetail("skip_segment", "no_active_segment");
    }

    private void UpdateActiveSkipSegment(double pos)
    {
        var active = _skipSegments.FirstOrDefault(s =>
            pos >= s.PromptStartSeconds && pos < s.PromptEndSeconds);

        if (!ReferenceEquals(active, _activeSkip))
        {
            _activeSkip = active;
            OnPropertyChanged(nameof(SkipButtonVisible));
            OnPropertyChanged(nameof(SkipButtonLabel));
        }

        // Auto-skip fires once per segment per playback (seeking back re-shows the button only).
        if (active is not null
            && pos >= active.StartSeconds && pos < active.EndSeconds - 1
            && ShouldAutoSkip(active.Kind)
            && _autoSkipped.Add(active))
        {
            Seek(active.EndSeconds);
        }
    }

    private bool ShouldAutoSkip(SkipSegmentKind kind) => kind switch
    {
        // An armed Up Next card owns the credits window — auto-skip would jump
        // straight to EOF and cut the countdown short.
        SkipSegmentKind.Credits => _autoSkipCredits && _upNextItem is null,
        SkipSegmentKind.Recap => _autoSkipIntro,
        _ => _autoSkipIntro,
    };

    private static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
