using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Shell;

namespace LightWeaver.WinUI.Player;

/// <summary>Plays titles in v2 (design 13), following the WPF app's PlayItem flow: a finished
/// download plays from disk, a running one over loopback, everything else is negotiated with the
/// server (the detail page's version, quality, audio and subtitles), with one retry through the
/// transcoder when direct play fails. Reports start, progress and stop, resumes, arms Up Next and
/// skip segments, and loads chapters, subtitles, media info, versions and trickplay.</summary>
internal sealed class PlaybackController : IDisposable
{
    private readonly AppViewModel _app;
    private readonly IAppShell _shell;
    private readonly VideoSurface _surface;
    private readonly Func<nint> _window;
    private readonly AppSettings _settings;
    private LiveSettings? _applied;
    private MpvPlayer? _player;
    private PlaybackReporter? _reporter;
    private JellyfinService? _session;
    private MediaItem? _item;
    private PlaybackChoice? _choice;
    private PlaybackDecision? _decision;
    private Task<MediaSourceStreams?> _streams = Task.FromResult<MediaSourceStreams?>(null);
    private Task<List<ExternalSubtitle>> _externalSubtitles = Task.FromResult(new List<ExternalSubtitle>());
    private DownloadItem? _download;
    private DownloadItem? _requestedDownload;
    private double _partialRuntimeSeconds;
    private bool _partialStream;
    private double _partialStartSeconds;
    private (int? Audio, int? Subtitle)? _restoreTracks;
    private int _restoredGeneration = -1;
    private bool _detached;
    private int _sequence;
    private int _intent;
    private int _streamChange;
    private bool _disposed;
    private int _generation = -1;
    private long _resumeTicks;
    private bool _local;
    private bool _transcodeRetried;
    private bool _reportStarted;
    private int _maxMbps;
    private int? _qualityRequest;
    private bool _forceTranscode;
    private TimeSpan _failedAt;
    private string? _directUrl;
    private Uri? _link;
    private SubtitleCarryOver? _carriedSubtitle;
    private bool _subtitlePickedHere;
    private int _carryGeneration = -1;
    private int _tracksReadyGeneration = -1;
    private int _loadedGeneration = -1;
    private SubtitleSelectionRequest? _subtitleIntent;
    private long _subtitleChoiceTimestamp;
    private bool _negotiating;

    public PlaybackController(AppViewModel app, IAppShell shell, VideoSurface surface, Func<nint> window)
    {
        _app = app;
        _shell = shell;
        _surface = surface;
        _window = window;
        _settings = App.Settings;
        App.SettingsChanged += (_, _) => ApplySettings();
        ViewModel.Configure(_settings);
        ViewModel.AttachQueue(app.Queue);
        ViewModel.UpNextPlayRequested += item => PlayNext(item);
        ViewModel.QueuePlayRequested += item => PlayNext(item);
        ViewModel.EpisodeStepRequested += item => Play(item, null, session: PlaybackSession);
        ViewModel.QualityChangeRequested += request => _ = ReloadAsync(request.MaxBitrateMbps, request.ForceTranscode, null);
        ViewModel.VersionChangeRequested += sourceId => _ = ReloadAsync(_qualityRequest, _forceTranscode, sourceId);
        ViewModel.SubtitleChosen += OnSubtitleChosen;
    }

    /// <summary>The transport's state: position, tracks, chapters, speed, queue, Up Next, skip.</summary>
    public PlayerViewModel ViewModel { get; } = new();

    public MpvPlayer? Player => _player;

    /// <summary>The title playing, or null when nothing plays.</summary>
    public MediaItem? Current => _item;

    /// <summary>Playback started or moved to another title.</summary>
    public event EventHandler<MediaItem>? Started;

    /// <summary>Playback stopped (Back, a failure, or the end of the last title).</summary>
    public event EventHandler? Stopped;

    /// <summary>Playback failed for good; the title stays current so Retry can start it again.</summary>
    public event EventHandler<PlaybackFailure>? Failed;

    /// <summary>The server's stream decision for what plays; null for a local file.</summary>
    public PlaybackDecision? Decision => _decision;

    /// <summary>How far a download still being written reaches into the title, for the timeline's
    /// downloaded range; null for anything else.</summary>
    public double? DownloadedSeconds => _partialRuntimeSeconds > 0 && _download is { } download
        && _shell.Downloads.Find(download.ItemId) is { } now && DownloadsModel.DownloadedFraction(now) is { } fraction
            ? fraction * _partialRuntimeSeconds : null;

    /// <summary>A download still being written that could not play from disk, or whose stream
    /// ended before the file did, as 1.x handled it: a download that has finished meanwhile
    /// reopens its file at the same position and tracks (the partial stream's token is retired
    /// once the whole file was served, so the next seek fails), otherwise playback goes on from
    /// the owning server with the download's version and track choices while the download keeps
    /// running. Without a reachable server it ends with a message. An end of file is a real end
    /// once the download has finished. False when this is not such a playback.</summary>
    private bool FallBackFromPartialStream(string reason, bool failed)
    {
        if (!_partialStream || _item is not { } item || _download is not { } download) return false;
        var finished = _shell.Downloads.GetCompletedDownload(item.Id);
        if (finished is not null && !failed) return false;
        var player = _player;
        var loaded = player is not null && _loadedGeneration == _generation && player.LoadGeneration == _generation;
        var position = DownloadsModel.ResumeAfterPartialStream(loaded ? player!.TimePos : 0, loaded ? player!.Duration : 0, _partialStartSeconds);
        _partialStream = false;
        _partialRuntimeSeconds = 0;
        ViewModel.SeekLimit = null;
        _resumeTicks = (long)(position * TimeSpan.TicksPerSecond);
        if (finished is not null)
        {
            Diagnostics.AppLog.Info("player", $"event=partial_stream_fallback outcome=local reason={reason} item={item.Id:N}");
            _restoreTracks = loaded
                ? (player!.Tracks.FirstOrDefault(t => t is { Type: "audio", Selected: true })?.Id, player.Tracks.FirstOrDefault(t => t is { Type: "sub", Selected: true })?.Id)
                : null;
            _download = finished;
            ViewModel.TrackNames = tracks => DownloadTracks.Named(finished, tracks);
            ViewModel.SetLivePlayMethod(() => L.Text("Player.LocalFile"));
            Load(finished.FilePath, null);
            return true;
        }
        Diagnostics.AppLog.Info("player", $"event=partial_stream_fallback outcome=server reason={reason} item={item.Id:N}");
        ViewModel.TrackNames = null;
        ViewModel.SetUpNext(null);
        if (_detached || !Session.IsConnected || _shell.Connectivity.IsOffline)
        {
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.TheDownloadStoppedSoPlaybackEndedEarly.dc8b8add"));
            Stop();
            return true;
        }
        var tracks = DownloadPlayback.FallbackTracks(download, loaded ? player!.Tracks : [], loaded);
        _choice = (_choice ?? new PlaybackChoice(null, null, null, TimeSpan.Zero, _qualityRequest, _forceTranscode)) with
        {
            VersionId = download.MediaSourceId ?? _choice?.VersionId,
            AudioIndex = tracks.Audio?.Index,
            SubtitleIndex = tracks.SubtitleOff ? -1 : tracks.Subtitle?.Index,
        };
        _local = false;
        _download = null;
        _streams = Session.GetMediaStreamsAsync(item.Id, _choice.VersionId);
        _ = NegotiateAsync(item, _sequence, forceTranscode: false);
        _ = LoadMetadataAsync(item, _sequence);
        return true;
    }

    /// <summary>A seek past what the running download has written is refused with a message,
    /// rather than left waiting for bytes that may be minutes away.</summary>
    private bool StillDownloading(double target)
    {
        var reachable = _download is { } download && _shell.Downloads.Find(download.ItemId) is { } now
            ? DownloadsModel.ReachableSeconds(now, _partialRuntimeSeconds) : null;
        if (target <= (reachable ?? ViewModel.PositionSeconds)) return false;
        ToastHost.Show(ToastKind.Info, L.Text("Player.StillDownloading"), mergeKey: "still-downloading");
        return true;
    }

    /// <summary>Whether what plays is a downloaded file.</summary>
    public bool IsLocal => _local;

    /// <summary>The session the current title plays under; null when nothing plays.</summary>
    public JellyfinService? PlaybackSession => _item is null || _detached ? null : _session;

    /// <summary>A remote "play now": <paramref name="items"/> become the queue from
    /// <paramref name="index"/>, under the session that was cast to; no start position resumes.</summary>
    public void PlayRemote(IReadOnlyList<MediaItem> items, int index, long startTicks, JellyfinService session)
    {
        if (SyncPlay?.TrySubmit(items, index, startTicks, session) == true) return;
        _app.Queue.Set([.. items], index);
        Start(items[index], startTicks > 0 ? new PlaybackChoice(null, null, null, TimeSpan.FromTicks(startTicks), null) : null, session);
    }

    /// <summary>The failure on screen, until Retry, Stop or another title.</summary>
    public PlaybackFailure? Failure { get; private set; }

    private JellyfinService Session => _session ?? _app.Jellyfin;

    /// <summary>Counts every start and stop; a group load compares it to tell its own playback.</summary>
    public int Sequence => _sequence;

    /// <summary>Counts every play and stop request; a lookup that started a play compares it to
    /// tell whether something newer has happened since.</summary>
    public int Intent => _intent;

    public bool IsPlaying => _item is not null;

    /// <summary>SyncPlay, when the window has set it up.</summary>
    public SyncPlayCoordinator? SyncPlay { get; set; }

    private bool _groupLoading;

    /// <summary>Whether the title starting now is the group's item, loaded through PlayForGroup.</summary>
    internal bool IsGroupLoading => _groupLoading;

    /// <summary>The group's current item, played in the group's session; returns its sequence.</summary>
    internal int PlayForGroup(MediaItem item, JellyfinService session)
    {
        _groupLoading = true;
        try
        {
            _app.Queue.Clear();
            Start(item, null, session);
        }
        finally
        {
            _groupLoading = false;
        }
        return _sequence;
    }

    /// <summary>Plays a title under <paramref name="session"/> (the active one by default). A queue
    /// plays from <paramref name="index"/>; an episode alone continues into the rest of its show.</summary>
    public void Play(MediaItem item, PlaybackChoice? choice, IReadOnlyList<MediaItem>? queue = null, int index = 0, JellyfinService? session = null)
    {
        var owner = session ?? _app.Jellyfin;
        if (SyncPlay?.TrySubmit(queue is { Count: > 0 } ? queue : [item], queue is { Count: > 0 } ? index : 0, choice?.Start.Ticks ?? 0, owner) == true)
        {
            _surface.CancelHold();
            return;
        }
        if (queue is { Count: > 0 })
        {
            _app.Queue.Set([.. queue], Math.Clamp(index, 0, queue.Count - 1));
            item = queue[Math.Clamp(index, 0, queue.Count - 1)];
        }
        else if (item is { Type: BaseItemDto_Type.Episode, SeriesId: { } seriesId }
            && !_shell.Downloads.Snapshot().Any(d => d.ItemId == item.Id && DownloadMatchesSession(d, owner)
                && (d.Status == DownloadStatus.Completed || d.VideoCompleted)))
        {
            _ = PlayFromEpisodeAsync(item, seriesId, choice, owner);
            return;
        }
        else
        {
            _app.Queue.Clear();
        }
        Start(item, choice, owner);
    }

    /// <summary>Plays a saved download from its file under the server it was downloaded from,
    /// whichever server is active; one still downloading plays from the start, from the bytes
    /// already on disk, and goes on from the server where those cannot play (see
    /// <see cref="FallBackFromPartialStream"/>). An episode continues into its show once the file plays.
    /// With that server not signed in (<paramref name="owner"/> null) the file plays with no
    /// server behind it.</summary>
    public void PlayDownload(DownloadItem download, MediaItem item, JellyfinService? owner)
    {
        if (owner is not null && SyncPlay?.TrySubmit([item], 0, 0, owner) == true)
        {
            _surface.CancelHold();
            return;
        }
        _app.Queue.Clear();
        var finished = _shell.Downloads.GetCompletedDownload(download.ItemId) is not null;
        var fromStart = finished ? null : new PlaybackChoice(null, null, null, TimeSpan.Zero, null);
        Start(item, fromStart, owner, download, detached: owner is null);
    }

    /// <summary>Items deleted on the playback session's server leave the queue, Up Next and the
    /// episode neighbors; Up Next moves on to the queue's next entry.</summary>
    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        if (_item is not { } item) return;
        _app.Queue.RemoveAll(ids);
        if (ViewModel.ForgetItems(ids) && _app.Queue.PeekNext() is { } next) ArmUpNext(next, item);
    }

    /// <summary>A deleted title keeps playing to the end, but once another starts it leaves the
    /// queue, so Previous or its row cannot start it again.</summary>
    private void PruneRemovedFromQueue()
    {
        var removals = Removals(Session);
        var gone = _app.Queue.Items.Where(entry => removals.Contains(entry.Id)).Select(entry => entry.Id).ToHashSet();
        if (gone.Count > 0) _app.Queue.RemoveAll(gone);
    }

    /// <summary>The profile the current title plays under; null when nothing plays.</summary>
    public string? PlaybackProfileKey => PlaybackSession is { } session ? HomeLayoutStore.ProfileKey(session.UserId, session.ServerUrl) : null;

    private ItemRemovals Removals(JellyfinService session) => ItemRemovals.ForProfile(HomeLayoutStore.ProfileKey(session.UserId, session.ServerUrl));

    /// <summary>Stops playback, sends the stop report and frees the server's transcode.</summary>
    public void Stop()
    {
        _intent++;
        if (_item is null) return;
        Failure = null;
        _sequence++;
        _app.PublishStopReport(_reporter?.Stop());
        if (!_reportStarted && _decision is { } decision) _ = Session.DiscardUnplayedDecisionAsync(decision);
        _player?.Stop();
        _item = null;
        _negotiating = false;
        _decision = null;
        _session = null;
        _generation = _loadedGeneration = _tracksReadyGeneration = _carryGeneration = -1;
        _app.Queue.Clear();
        ViewModel.SetUpNext(null);
        SyncPlay?.OnStopped();
        _app.LeavePlayback();
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An episode started alone plays on into the episodes after it ("watch from here").
    /// Anything started, stopped or disposed during the lookup wins over it.</summary>
    private async Task PlayFromEpisodeAsync(MediaItem episode, Guid seriesId, PlaybackChoice? choice, JellyfinService session)
    {
        var intent = ++_intent;
        bool Superseded() => _disposed || intent != _intent;
        try
        {
            var removals = Removals(session);
            var readAt = removals.Mark();
            var episodes = removals.Filter(await session.GetSeriesEpisodesAsync(seriesId), readAt);
            if (Superseded()) return;
            if (removals.Contains(episode.Id))
            {
                CancelRemoved();
                return;
            }
            var at = episodes.FindIndex(e => e.Id == episode.Id);
            if (at >= 0 && episodes.Skip(at).Where(e => e.IsPlayable).ToList() is { Count: > 1 } slice)
            {
                _app.Queue.Set(slice, 0);
                Start(slice[0], choice, session);
                return;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("player", $"event=episode_queue outcome=failure error={ex.GetType().Name}");
        }
        if (Superseded()) return;
        if (Removals(session).Contains(episode.Id))
        {
            CancelRemoved();
            return;
        }
        _app.Queue.Clear();
        Start(episode, choice, session);
    }

    /// <summary>The title asked for was deleted on the server before it could start.</summary>
    private void CancelRemoved()
    {
        ToastHost.Show(ToastKind.Info, L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7"), mergeKey: "item-missing");
        _surface.CancelHold();
        if (_item is not null) Stop();
        else Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Arms Up Next using the current show's lead.</summary>
    private void ArmUpNext(MediaItem next, MediaItem leaving)
        => ViewModel.SetUpNext(next, _settings.AutoPlayCountdownSeconds, _settings.AutoPlayNextEpisode, UpNextLead.For(_settings, Session.ServerUrl, Session.UserId, leaving));

    /// <summary>The next title of the queue or Up Next; the queue keeps its place.</summary>
    private void PlayNext(MediaItem item)
    {
        if (Removals(Session).Contains(item.Id))
        {
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7"), mergeKey: "item-missing");
            if (_item is { } playing && _app.Queue.Items.ToList().FindIndex(entry => entry.Id == playing.Id) is >= 0 and var at) _app.Queue.JumpTo(at);
            _app.Queue.RemoveAll(new HashSet<Guid> { item.Id });
            return;
        }
        _app.Queue.TryAdvanceTo(item);
        Start(item, null, _session);
    }

    private void Start(MediaItem item, PlaybackChoice? choice, JellyfinService? session = null, DownloadItem? download = null, bool detached = false)
    {
        _intent++;
        if (!EnsurePlayer())
        {
            _surface.CancelHold();
            return;
        }
        _surface.HoldForLoad();
        if (!_groupLoading) SyncPlay?.ResetBinding();
        _app.PublishStopReport(_reporter?.Stop());
        if (!_reportStarted && _decision is { } stale) _ = Session.DiscardUnplayedDecisionAsync(stale);
        var sequence = ++_sequence;
        Failure = null;
        _directUrl = null;
        _link = null;
        _surface.Waiting = false;
        _session = session ?? _app.Jellyfin;
        _partialRuntimeSeconds = 0;
        _partialStream = false;
        _restoreTracks = null;
        PruneRemovedFromQueue();
        _requestedDownload = download;
        _detached = detached;
        _item = item;
        _generation = _loadedGeneration = _tracksReadyGeneration = _carryGeneration = -1;
        _choice = choice;
        _decision = null;
        _streams = Task.FromResult<MediaSourceStreams?>(null);
        _externalSubtitles = Task.FromResult(new List<ExternalSubtitle>());
        _download = null;
        _transcodeRetried = false;
        _reportStarted = false;
        _resumeTicks = choice?.Start.Ticks ?? DetailModel.DefaultStart(item).Ticks;
        _maxMbps = choice?.MaxBitrateMbps ?? _settings.MaxStreamingBitrateMbps;
        _qualityRequest = choice?.MaxBitrateMbps;
        _forceTranscode = choice?.ForceTranscode ?? false;
        _subtitlePickedHere = false;
        _negotiating = false;

        ViewModel.SetNowPlaying(item.PlaybackTitle, item.PlaybackSubtitle, item.PlaybackRelease);
        ViewModel.SetServerChapters(item.Chapters.Select(c => c.StartSeconds));
        ViewModel.SetSkipSegments([], _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        ViewModel.SetUpNext(null);
        ViewModel.SetEpisodeNeighbors(false, null, null);
        ViewModel.TrackNames = null;
        ViewModel.SetKnownDuration(0);
        ViewModel.SeekLimit = null;
        ViewModel.SetTrickplay(null);
        ViewModel.SetVersions([], null);
        ViewModel.SetMediaInfo(null);
        ViewModel.SetPlayMethod(null);
        ViewModel.SetQualityState(null, false, _settings.MaxStreamingBitrateMbps, available: false);
        if (_app.Queue.PeekNext() is { } queuedNext) ArmUpNext(queuedNext, item);
        _app.EnterPlayback();
        Started?.Invoke(this, item);

        var savedDownload = download ?? _shell.Downloads.Snapshot().FirstOrDefault(d => d.ItemId == item.Id && DownloadMatchesSession(d));
        if (savedDownload is not null && _shell.Downloads.GetCompletedDownload(item.Id) is { } done)
        {
            _download = done;
            ViewModel.TrackNames = tracks => DownloadTracks.Named(done, tracks);
            _local = true;
            ViewModel.SetLivePlayMethod(() => L.Text("Player.LocalFile"));
            Load(done.FilePath, null);
            LoadLocalMetadata(item, sequence);
            return;
        }
        var partial = savedDownload is null || SyncPlay?.Client.IsInGroup == true ? null
            : _shell.Downloads.TryGetPartialStreamUrl(item.Id, _resumeTicks, item.RuntimeTicks);
        if (partial is not null && savedDownload is { } running)
        {
            _download = running;
            _partialStream = true;
            _partialStartSeconds = TimeSpan.FromTicks(_resumeTicks).TotalSeconds;
            _partialRuntimeSeconds = TimeSpan.FromTicks(item.RuntimeTicks ?? running.RuntimeTicks ?? 0).TotalSeconds;
            ViewModel.SeekLimit = StillDownloading;
            ViewModel.TrackNames = tracks => DownloadTracks.Named(running, tracks);
            _local = true;
            ViewModel.SetLivePlayMethod(() => L.Text("Player.LocalFileDownloading"));
            Load(partial, null);
            LoadLocalMetadata(item, sequence);
            return;
        }
        if (_detached && download is not null)
        {
            _local = true;
            OnPlaybackFailed(download.Status == DownloadStatus.Downloading
                ? L.Text("MainWindow.NotEnoughOfThisDownloadHasArrived.6f4c9c8c")
                : L.Text("Downloads.IncompleteVideo"));
            return;
        }
        _local = false;
        _streams = choice is null ? Task.FromResult<MediaSourceStreams?>(null) : _session.GetMediaStreamsAsync(item.Id, choice.VersionId);
        _ = LoadMetadataAsync(item, sequence);
        _ = NegotiateAsync(item, sequence, forceTranscode: false);
    }

    /// <summary>Plays a local file or a direct HTTP(S) address with no server behind it: no queue,
    /// no reports, no metadata. A link plays under the name its path gives (<see cref="StreamLink"/>)
    /// with its host under it, until mpv names it, with a ring until its first frame.</summary>
    public void PlayUrl(string url, string? title = null)
    {
        if (SyncPlay?.Client.IsInGroup == true)
        {
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.LeaveTheGroupToOpenALocal.cfca5a03"));
            return;
        }
        if (!EnsurePlayer()) return;
        _intent++;
        _surface.HoldForLoad();
        _app.PublishStopReport(_reporter?.Stop());
        if (!_reportStarted && _decision is { } stale) _ = Session.DiscardUnplayedDecisionAsync(stale);
        ++_sequence;
        Failure = null;
        _app.Queue.Clear();
        _link = Uri.TryCreate(url, UriKind.Absolute, out var address) && !address.IsFile ? address : null;
        var (guess, host) = _link is { } link ? StreamLink.Title(link) : (Path.GetFileNameWithoutExtension(url) is { Length: > 0 } file ? file : url, null);
        var name = title is { Length: > 0 } ? title : guess;
        _surface.Waiting = _link is not null;
        _item = new MediaItem { Id = Guid.NewGuid(), Name = name };
        _session = _app.Jellyfin;
        _directUrl = url;
        _choice = null;
        _decision = null;
        _streams = Task.FromResult<MediaSourceStreams?>(null);
        _externalSubtitles = Task.FromResult(new List<ExternalSubtitle>());
        _download = null;
        _requestedDownload = null;
        _detached = false;
        _partialRuntimeSeconds = 0;
        _partialStream = false;
        _restoreTracks = null;
        _transcodeRetried = true;
        _reportStarted = false;
        _resumeTicks = 0;
        _local = true;
        _qualityRequest = null;
        _forceTranscode = false;
        _subtitlePickedHere = false;
        _negotiating = false;
        ViewModel.SetNowPlaying(name, host, null);
        ViewModel.SetServerChapters([]);
        ViewModel.SetSkipSegments([], _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        ViewModel.SetUpNext(null);
        ViewModel.SetEpisodeNeighbors(false, null, null);
        ViewModel.TrackNames = null;
        ViewModel.SetKnownDuration(0);
        ViewModel.SeekLimit = null;
        ViewModel.SetTrickplay(null);
        ViewModel.SetVersions([], null);
        ViewModel.SetMediaInfo(null);
        ViewModel.SetPlayMethod(null);
        ViewModel.SetQualityState(null, false, _settings.MaxStreamingBitrateMbps, available: false);
        _app.EnterPlayback();
        Started?.Invoke(this, _item);
        ViewModel.SetLivePlayMethod(_link is null ? () => L.Text("MainWindow.LocalFile.cdf72b6f") : () => L.Text("Player.Link"));
        Load(url, null);
    }

    /// <summary>The bitrate cap for negotiation; 0 asks for the original.</summary>
    private int MaxBitrateBps => _maxMbps * 1_000_000;

    private bool DownloadMatchesSession(DownloadItem download, JellyfinService? session = null)
    {
        var server = (session ?? Session).ServerUrl;
        if (string.IsNullOrEmpty(server) && _shell.CacheProfileKey is { Length: > 33 } profile
            && profile[32] == '@' && Guid.TryParseExact(profile[..32], "N", out _)) server = profile[33..];
        return Uri.TryCreate(download.ServerUrl, UriKind.Absolute, out var saved) && Uri.TryCreate(server, UriKind.Absolute, out var active)
            && saved.GetLeftPart(UriPartial.Path).TrimEnd('/') == active.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private string? VersionId(MediaItem item)
        => _choice?.VersionId ?? MediaVersionStore.Load(HomeLayoutStore.ProfileKey(Session.UserId, Session.ServerUrl), item.Id);

    /// <summary>Asks the server how to play <paramref name="item"/> and loads the answer; false when
    /// another title, or a later quality or version change, has superseded the request.</summary>
    private async Task<bool> NegotiateAsync(MediaItem item, int sequence, bool forceTranscode)
    {
        var session = Session;
        var change = _streamChange;
        bool Current() => !_disposed && sequence == _sequence && change == _streamChange && _item?.Id == item.Id;
        var sourceId = _decision?.MediaSourceId ?? VersionId(item);
        var subtitleIndex = _choice?.SubtitleIndex;
        var carry = subtitleIndex is not null && !_subtitlePickedHere ? null : _carriedSubtitle;
        _negotiating = true;
        if (carry is not null || subtitleIndex is not null)
        {
            try
            {
                var streams = await session.GetMediaStreamsAsync(item.Id, sourceId);
                if (!Current()) return false;
                _streams = Task.FromResult(streams);
                sourceId = streams?.SourceId ?? sourceId;
                if (carry is not null) subtitleIndex = carry.Off ? -1 : streams is null ? null : carry.MatchSource(streams.Subtitles)?.Index;
                else if (subtitleIndex < 0) _carriedSubtitle = new(true, null, null, false);
                else if (streams?.Subtitles.FirstOrDefault(s => s.Index == subtitleIndex) is { } selected)
                    _carriedSubtitle = new(false, selected.Language, selected.Title, selected.IsForced);
                else _carriedSubtitle = null;
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Error("player", $"event=carried_subtitle outcome=no_streams error={ex.GetType().Name}");
                if (!Current()) return false;
                if (carry is not null) subtitleIndex = carry.Off ? -1 : null;
            }
        }
        var decision = await session.NegotiatePlaybackAsync(item.Id, MaxBitrateBps, forceTranscode || _forceTranscode,
            _choice?.AudioIndex, subtitleIndex, sourceId);
        if (!Current())
        {
            if (decision is not null) _ = session.DiscardUnplayedDecisionAsync(decision);
            return false;
        }
        _negotiating = false;
        _decision = decision;
        _externalSubtitles = decision is { SubtitleIsBurnedIn: true } ? Task.FromResult(new List<ExternalSubtitle>())
            : session.GetExternalSubtitlesAsync(item.Id, decision?.MediaSourceId ?? sourceId);
        ViewModel.SetLivePlayMethod(() => decision is { IsTranscode: true } ? L.Text("Player.Transcode") : L.Text("Player.DirectPlay"));
        ViewModel.SetCurrentVersion(decision?.MediaSourceId ?? VersionId(item));
        Load(decision?.Url ?? session.GetStreamUrl(item.Id, VersionId(item)), session.AuthorizationHeader);
        return true;
    }

    private void Load(string url, string? authorization)
    {
        if (_player is null) return;
        Diagnostics.AppLog.Info("player", $"event=load source={(_link is not null ? "link" : _local ? "local" : _decision is { IsTranscode: true } ? "transcode" : "direct")} item={_item?.Id:N}");
        _player.SetHttpHeaders(authorization);
        _subtitleIntent = null;
        _loadedGeneration = _tracksReadyGeneration = _carryGeneration = -1;
        _generation = _player.LoadFile(url);
        _reportStarted = false;
        SyncPlay?.OnLoaded(_generation);
    }

    /// <summary>Quality or version changed mid-play: negotiate again and continue at the same
    /// position, paused or not.</summary>
    private async Task ReloadAsync(int? maxBitrateMbps, bool forceTranscode, string? versionId)
    {
        if (_item is not { } item || _player is not { } player || _local) return;
        _streamChange++;
        var position = TimeSpan.FromSeconds(player.TimePos);
        var wasPaused = player.Pause;
        _qualityRequest = maxBitrateMbps;
        _maxMbps = maxBitrateMbps ?? _settings.MaxStreamingBitrateMbps;
        _forceTranscode = forceTranscode;
        if (versionId is not null) _choice = (_choice ?? new PlaybackChoice(null, null, null, TimeSpan.Zero, maxBitrateMbps, forceTranscode)) with { VersionId = versionId, AudioIndex = null, SubtitleIndex = null };
        if (versionId is not null) MediaVersionStore.Save(HomeLayoutStore.ProfileKey(Session.UserId, Session.ServerUrl), item.Id, versionId);
        _app.PublishStopReport(_reporter?.Stop());
        _resumeTicks = position.Ticks;
        var previous = _decision;
        _decision = versionId is null ? previous : null;
        if (!await NegotiateAsync(item, _sequence, forceTranscode)) return;
        if (wasPaused) player.Pause = true;
        ViewModel.SetQualityState(maxBitrateMbps, forceTranscode, _settings.MaxStreamingBitrateMbps);
    }

    private bool EnsurePlayer()
    {
        if (_player is not null) return true;
        try
        {
            var (width, height) = _surface.BufferPixels;
            var target = new MpvVideoTarget.Composition(Math.Max(1, width), Math.Max(1, height), DisplayProbe.ForWindow(_window()));
            _player = MpvPlayer.Create(target, new WinUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()), _settings);
            _applied = LiveSettings.Of(_settings);
        }
        catch (Exception ex) when (ex is DllNotFoundException or MpvException)
        {
            Diagnostics.AppLog.Error("player", "mpv failed to initialize", ex);
            ToastHost.Show(ToastKind.Error, L.Format("Player.EngineFailed", ex.Message));
            return false;
        }
        // Ahead of the view-model's EOF handler, which may start the next title: the end of the
        // last title must be judged on the title that ended.
        _player.EndReached += OnEndReached;
        _surface.Attach(_player);
        ViewModel.Attach(_player);
        SyncPlay?.AttachPlayer(_player);
        ViewModel.ApplyRtxDefaults(_settings);
        _reporter = new PlaybackReporter(() => Session.Client, () => (_player?.TimePos ?? 0, _player?.Pause ?? true));
        _player.FileLoaded += OnFileLoaded;
        _player.TracksChanged += OnTracksChanged;
        _player.PlaybackFailed += OnPlaybackFailed;
        _player.PlaybackStalled += reason => ToastHost.Show(ToastKind.Error, reason);
        _player.PassthroughUnavailable += reason => ToastHost.Show(ToastKind.Error, reason);
        _player.AudioResyncFailed += reason => ToastHost.Show(ToastKind.Error, reason);
        return true;
    }

    /// <summary>Settings changed: of subtitles, audio, buffer, decoding and the RTX filters, what
    /// changed applies to what is playing; a group mpv rejected is tried again on the next save.</summary>
    private void ApplySettings()
    {
        ViewModel.NotifyTransportConfigChanged();
        ViewModel.NotifySubtitleBasePositionChanged();
        ViewModel.ApplyRtxDefaults(_settings);
        if (_player is not { } player || _applied is not { } applied) return;
        _applied = applied.ApplyChanges(LiveSettings.Of(_settings), group => group switch
        {
            LiveSettingGroups.SubtitleStyle => player.ApplySubtitleStyle(_settings),
            LiveSettingGroups.Audio => player.ApplyAudioOptions(_settings),
            LiveSettingGroups.VideoBuffer => player.ApplyVideoBuffer(_settings),
            LiveSettingGroups.HardwareDecoding => player.SetHardwareDecoding(_settings.HardwareDecoding),
            _ => true,
        }, (group, ex) => Diagnostics.AppLog.Error("player", $"event=settings_apply outcome=failure group={group}", ex));
    }

    /// <summary>The display under the window changed (another monitor, HDR switched).</summary>
    public void Retarget() => _player?.Retarget(DisplayProbe.ForWindow(_window()));

    private void OnFileLoaded(int generation)
    {
        if (_player is not { } player || generation != _generation || _item is not { } item) return;
        _loadedGeneration = generation;
        if (_partialRuntimeSeconds > 0) ViewModel.SetKnownDuration(_partialRuntimeSeconds);
        player.RefreshTracks(generation);
        if (_download is { } download)
        {
            foreach (var subtitle in download.SubtitleFiles.Where(s => File.Exists(s.FilePath)))
                player.AddExternalSubtitle(subtitle.FilePath, subtitle.Title, subtitle.Language,
                    DownloadPlayback.UseSavedSubtitlePreference(download, _subtitleChoiceTimestamp) && download.PreferredSubtitleFileIndex == subtitle.StreamIndex);
        }
        else _ = AttachExternalSubtitlesAsync(player, generation, _externalSubtitles);
        _ = ApplyTracksAsync(player, generation);
        if (_resumeTicks > 0) player.TimePos = TimeSpan.FromTicks(_resumeTicks).TotalSeconds;
        if (_link is { } link && StreamLink.MediaTitle(link, player.GetPropertyString("media-title")) is { } named && named != item.Name)
        {
            _item = new MediaItem { Id = item.Id, Name = named };
            ViewModel.SetNowPlaying(named, link.Authority, null);
        }
        if (!_local)
        {
            var method = _decision is { IsTranscode: true } ? L.Text("Player.Transcode") : L.Text("Player.DirectPlay");
            var quality = PlayerViewModel.QualityDescription(_qualityRequest, _forceTranscode, _settings.MaxStreamingBitrateMbps);
            ViewModel.SetQualityState(_qualityRequest, _forceTranscode, _settings.MaxStreamingBitrateMbps, $"{quality} · {method}");
            _reportStarted = true;
            _reporter?.Start(item.Id, _resumeTicks, _decision?.PlaySessionId, _decision?.MediaSourceId, _decision?.IsTranscode ?? false, player.Pause);
        }
        _resumeTicks = 0;
    }

    private async Task AttachExternalSubtitlesAsync(MpvPlayer player, int generation, Task<List<ExternalSubtitle>> pending)
    {
        try
        {
            var subtitles = await pending;
            if (_disposed || generation != _generation || generation != _loadedGeneration || generation != player.LoadGeneration) return;
            foreach (var subtitle in subtitles) player.AddExternalSubtitle(subtitle.Url, subtitle.Title, subtitle.Language);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("player", $"event=external_subtitles outcome=failure error={ex.GetType().Name}");
        }
    }

    private void OnTracksChanged(int generation)
    {
        if (_player is not { } player || generation != _generation || generation != _tracksReadyGeneration) return;
        if (ApplyDownloadSubtitle(player)) return;
        if (generation == _carryGeneration) CarrySubtitle(player, generation);
    }

    private bool ApplyDownloadSubtitle(MpvPlayer player)
    {
        if (_restoredGeneration == _generation) return true;
        if (_download is not { } download || _subtitleIntent is not null
            || _carriedSubtitle is not null && !DownloadPlayback.UseSavedSubtitlePreference(download, _subtitleChoiceTimestamp)) return false;
        var start = DownloadTracks.Playback(download, player.Tracks, _settings.PreferredAudioLanguage, _settings.PreferredSubtitleLanguage);
        if (start.Subtitle == SubtitleStart.Off && (player.Tracks.All(t => t.Type != "sub") || player.Tracks.Any(t => t.Type == "sub" && t.Selected)))
            player.SelectSubtitleTrack(null);
        else if (start.Subtitle == SubtitleStart.Track && player.Tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == start.SubtitleId) is { Selected: false })
            player.SelectSubtitleTrack(start.SubtitleId);
        return true;
    }

    /// <summary>Applies source picks and subtitle intent only to the loaded generation.</summary>
    private async Task ApplyTracksAsync(MpvPlayer player, int generation)
    {
        var decision = _decision;
        MediaSourceStreams? streams = null;
        try
        {
            streams = await _streams;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("player", $"event=track_choice outcome=no_streams error={ex.GetType().Name}");
        }
        if (generation != _generation || player.LoadGeneration != generation) return;
        if (decision is { SubtitleIsBurnedIn: true }) return;
        if (_restoreTracks is { } restore)
        {
            _restoreTracks = null;
            _restoredGeneration = generation;
            if (restore.Audio is { } restoredAudio) player.SelectAudioTrack(restoredAudio);
            player.SelectSubtitleTrack(restore.Subtitle);
            _tracksReadyGeneration = generation;
            return;
        }
        if (_download is { } download)
        {
            var start = DownloadTracks.Playback(download, player.Tracks, _settings.PreferredAudioLanguage, _settings.PreferredSubtitleLanguage);
            if (start.AudioId is { } audioId) player.SelectAudioTrack(audioId);
            _tracksReadyGeneration = generation;
            if (ApplyDownloadSubtitle(player)) return;
        }
        var choice = _choice is { } picked && (picked.AudioIndex is not null || picked.SubtitleIndex is not null) && streams is not null
            && (_decision?.MediaSourceId is not { } playing || streams.SourceId is not { } read || playing == read) ? picked : null;
        if (_download is null && decision is not { IsTranscode: true } && choice is not null)
        {
            if (streams!.Audio.FirstOrDefault(a => a.Index == choice.AudioIndex) is { TypeOrdinal: >= 0 } audio)
            {
                if (player.Tracks.Where(t => t.Type == "audio" && t.ExternalFilename is null).ElementAtOrDefault(audio.TypeOrdinal) is { } track)
                    player.SelectAudioTrack(track.Id);
            }
        }
        else if (_download is null && decision is not { IsTranscode: true } && _settings.PreferredAudioLanguage is { Length: > 0 } audioLanguage
            && player.Tracks.FirstOrDefault(t => t.Type == "audio" && Languages.Matches(t.Lang, audioLanguage)) is { Selected: false } preferredAudio)
        {
            player.SelectAudioTrack(preferredAudio.Id);
        }
        _tracksReadyGeneration = generation;
        if (_subtitleIntent?.Generation == generation)
        {
            _carryGeneration = generation;
            CarrySubtitle(player, generation);
        }
        else if (choice is { SubtitleIndex: { } subtitleIndex } && !_subtitlePickedHere)
        {
            _carryGeneration = generation;
            if (subtitleIndex < 0) player.SelectSubtitleTrack(null);
            else if (streams!.Subtitles.FirstOrDefault(s => s.Index == subtitleIndex) is { } subtitle)
            {
                var id = subtitle.TypeOrdinal >= 0
                    ? player.Tracks.Where(t => t.Type == "sub" && t.ExternalFilename is null).ElementAtOrDefault(subtitle.TypeOrdinal)?.Id
                    : player.Tracks.FirstOrDefault(t => t.Type == "sub" && Languages.Matches(t.Lang, subtitle.Language) && t.Title == subtitle.Title)?.Id;
                if (id is { } trackId) player.SelectSubtitleTrack(trackId);
            }
            if (_carriedSubtitle is not null) CarrySubtitle(player, generation);
        }
        else if (_carriedSubtitle is not null)
        {
            _carryGeneration = generation;
            CarrySubtitle(player, generation);
        }
        else if (choice is null && _settings.PreferredSubtitleLanguage is { Length: > 0 } subtitleLanguage
            && player.Tracks.FirstOrDefault(t => t.Type == "sub" && Languages.Matches(t.Lang, subtitleLanguage)) is { Selected: false } preferredSubtitle)
        {
            player.SelectSubtitleTrack(preferredSubtitle.Id);
        }
    }

    /// <summary>A subtitle picked in the player is carried into every later file of the session.</summary>
    private void OnSubtitleChosen(SubtitleSelectionRequest request)
    {
        if (_player is not { } player || request.Generation != _generation || request.Generation != player.LoadGeneration) return;
        _carriedSubtitle = SubtitleCarryOver.From(request);
        _subtitleIntent = request;
        _subtitleChoiceTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _subtitlePickedHere = true;
        _tracksReadyGeneration = request.Generation;
        _carryGeneration = request.Generation;
        if (!_local && (_decision is { SubtitleIsBurnedIn: true } || _negotiating))
        {
            _streamChange++;
            var sequence = _sequence;
            _surface.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && sequence == _sequence && request.Generation == _generation && ReferenceEquals(_subtitleIntent, request))
                    _ = ReloadAsync(_qualityRequest, _forceTranscode, null);
            });
        }
    }

    /// <summary>Reconciles exact current-file intent before cross-file matching.</summary>
    private void CarrySubtitle(MpvPlayer player, int generation)
    {
        if (_carriedSubtitle is not { } carried || _generation != generation || player.LoadGeneration != generation) return;
        var subtitles = player.Tracks.Where(t => t.Type == "sub").ToList();
        if (_subtitleIntent is { } intent && intent.Generation == generation)
        {
            if (intent.Off) player.SelectSubtitleTrack(null);
            else if (SubtitleCarryOver.MatchExact(intent, generation, subtitles) is { Selected: false } exact) player.SelectSubtitleTrack(exact.Id);
            return;
        }
        if (carried.Off)
        {
            if (subtitles.Count == 0 || subtitles.Any(t => t.Selected)) player.SelectSubtitleTrack(null);
            return;
        }
        if (carried.Match(subtitles) is not { } match) return;
        if (!match.Selected) player.SelectSubtitleTrack(match.Id);
    }

    /// <summary>A direct play that fails gets one retry through the server's transcoder.</summary>
    private void OnPlaybackFailed(string reason)
    {
        if (FallBackFromPartialStream("failed", failed: true)) return;
        if (_item is { } item && !_local && _decision is not { IsTranscode: true } && !_transcodeRetried)
        {
            _transcodeRetried = true;
            Diagnostics.AppLog.Error("player", $"direct play failed ({reason}); retrying via the server transcoder");
            ViewModel.SetLivePlayMethod(() => L.Text("Player.RetryingTranscode"));
            _app.PublishStopReport(_reporter?.Stop());
            _ = NegotiateAsync(item, _sequence, forceTranscode: true);
            return;
        }
        Diagnostics.AppLog.Error("player", $"event=playback_failed reason_length={reason.Length}");
        if (_item is null) return;
        var position = _player?.TimePos ?? 0;
        _failedAt = position > 0 ? TimeSpan.FromSeconds(position) : TimeSpan.FromTicks(_resumeTicks);
        _app.PublishStopReport(_reporter?.Stop());
        _surface.Waiting = false;
        var cause = _link is not null ? FailureCause.Link : _local ? FailureCause.LocalFile : _decision is { IsTranscode: true } ? FailureCause.Transcode : FailureCause.DirectPlay;
        var alternative = cause == FailureCause.Transcode && !_transcodeRetried && _maxMbps != 0 ? FailureAlternative.OriginalQuality
            : !_local && ViewModel.HasVersionChoices ? FailureAlternative.OtherVersion
            : FailureAlternative.None;
        Failure = new PlaybackFailure(reason, cause, alternative);
        Failed?.Invoke(this, Failure);
    }

    /// <summary>Starts the failed title again where it stopped; <paramref name="alternative"/> plays
    /// it at original quality or from the next version instead.</summary>
    public void Retry(FailureAlternative alternative = FailureAlternative.None)
    {
        if (_item is not { } item || Failure is null) return;
        if (_directUrl is { } url)
        {
            PlayUrl(url, item.Name);
            return;
        }
        var choice = (_choice ?? new PlaybackChoice(null, null, null, _failedAt, null)) with { Start = _failedAt, MaxBitrateMbps = _qualityRequest, ForceTranscode = _forceTranscode };
        if (alternative == FailureAlternative.OriginalQuality) choice = choice.AtOriginalQuality();
        if (alternative == FailureAlternative.OtherVersion && ViewModel.Versions.Count > 1)
        {
            var current = ViewModel.Versions.ToList().FindIndex(v => v.IsSelected);
            choice = choice with { VersionId = ViewModel.Versions[(current + 1) % ViewModel.Versions.Count].Id, AudioIndex = null, SubtitleIndex = null };
        }
        Start(item, choice, _detached ? null : _session, _requestedDownload, _detached);
    }

    /// <summary>The end of the last title returns to where playback began; Up Next and the queue
    /// advance through the view-model, and a group's item through SyncPlay.</summary>
    private void OnEndReached()
    {
        if (_item is null) return;
        if (FallBackFromPartialStream("ended_early", failed: false)) return;
        Diagnostics.AppLog.Info("player", FormattableString.Invariant($"event=end_reached position={_player?.TimePos ?? 0:F1} duration={_player?.Duration ?? 0:F1} local={_local}"));
        if (ViewModel.UpNextVisible || _app.Queue.PeekNext() is not null || SyncPlay?.IsGroupPlayback == true) return;
        Stop();
    }

    /// <summary>A downloaded file plays at once; with its server reachable, chapters, skip segments,
    /// trickplay and the episode's neighbors and Up Next follow.</summary>
    private void LoadLocalMetadata(MediaItem item, int sequence)
    {
        if (!_detached && !_shell.Connectivity.IsOffline && Session.IsConnected) _ = LoadMetadataAsync(item, sequence);
    }

    private async Task LoadMetadataAsync(MediaItem item, int sequence)
    {
        var session = Session;
        bool Current() => sequence == _sequence && _item?.Id == item.Id;

        async Task Step(string name, Func<Task> body)
        {
            try
            {
                if (Current()) await body();
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Error("playback-metadata", $"{name} failed", ex);
            }
        }

        var fresh = item;
        await Step("item refresh", async () =>
        {
            fresh = await session.GetItemAsync(item.Id) ?? item;
            if (Current() && fresh.Chapters.Count > 0) ViewModel.SetServerChapters(fresh.Chapters.Select(c => c.StartSeconds));
        });
        if (fresh is { Type: BaseItemDto_Type.Episode, SeriesId: { } seriesId })
            await Step("adjacent episodes", async () =>
            {
                var removals = Removals(session);
                var readAt = removals.Mark();
                var (previous, next) = await session.GetAdjacentEpisodesAsync(seriesId, item.Id);
                if (!Current()) return;
                if (previous is not null && removals.Contains(previous.Id, readAt)) previous = null;
                if (next is not null && removals.Contains(next.Id, readAt)) next = null;
                ViewModel.SetEpisodeNeighbors(true, previous, next);
                if (next is not null && !_app.Queue.IsActive) ArmUpNext(next, fresh);
            });
        await Step("skip segments", async () =>
        {
            var segments = await MediaSegmentsClient.GetSegmentsAsync(session, fresh);
            if (Current() && segments.Count > 0) ViewModel.SetSkipSegments(segments, _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        });
        if (!_local)
            await Step("media info", async () =>
            {
                var info = await session.GetMediaInfoAsync(item.Id);
                if (Current() && info.Count > 0) ViewModel.SetMediaInfo(info.Select(f => new InfoRow(f.Label, f.Value) { LabelProvider = () => f.LiveLabel }));
            });
        if (!_local)
            await Step("versions", async () =>
            {
                var versions = await session.GetMediaVersionsAsync(item.Id);
                if (Current()) ViewModel.SetVersions(versions, _decision?.MediaSourceId ?? VersionId(item));
            });
        await Step("trickplay", async () =>
        {
            var trickplay = await session.GetTrickplayAsync(item.Id, _local ? null : _decision?.MediaSourceId);
            if (Current()) ViewModel.SetTrickplay(trickplay is null ? null : new TrickplaySource(session, item.Id, trickplay));
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _surface.Detach();
        _player?.Dispose();
        _player = null;
    }
}
