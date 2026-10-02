using LightWeaver.Diagnostics;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.ViewModels;

namespace LightWeaver;

/// <summary>
/// Connects group membership to the window's asynchronous load path. Protocol parsing, transport
/// decisions, scheduling and mpv operations live in the client, gate, scheduler and binding.
/// </summary>
public partial class MainWindow
{
    private readonly SyncPlayClient _syncPlay;
    private SyncPlayPlayerBinding? _syncPlayBinding;
    private int _syncPlayEpoch;
    private int _syncPlayLoadRequest;
    private int _syncPlayPlaybackSequence = -1;
    private Guid? _syncPlayGroupId;
    private Guid? _syncPlayLoadedEntry;
    private Guid? _syncPlayRequestedEntry;
    private SyncPlayQueueUpdate? _syncPlayQueue;
    private bool _syncPlayLoading;
    private bool _syncPlayAwaitingLoad;
    private SyncPlayAction? _syncPlayDeferredAction;
    private int _syncPlayPosts;
    private int _syncPlayEofGeneration = -1;
    private (string Key, IReadOnlyList<Guid> Items, int Index, long Ticks)? _syncPlayCreateQueue;
    private readonly Dictionary<Guid, string> _syncPlayTitles = [];

    private bool IsGroupPlayback => _syncPlay.IsInGroup
        && _syncPlayGroupId == _syncPlay.GroupId
        && _syncPlayLoadedEntry is { } entry && entry == _syncPlay.PlaylistItemId
        && _syncPlayPlaybackSequence == _playbackSequence && _app.State == AppState.Playing;

    private void AttachSyncPlay()
    {
        _playerViewModel.TransportGate = OnLocalTransport;
        _syncPlay.GroupJoined += info => DispatchSyncPlay(() => OnSyncPlayGroupJoined(info));
        _syncPlay.GroupLeft += id => Dispatcher.BeginInvoke(() => OnSyncPlayGroupLeft(id));
        _syncPlay.QueueChanged += queue => DispatchSyncPlay(() => OnSyncPlayQueueChanged(queue));
        _syncPlay.ActionRequired += action =>
        {
            var entry = _syncPlay.PlaylistItemId;
            DispatchSyncPlay(() =>
            {
                if (entry == _syncPlay.PlaylistItemId) OnSyncPlayActionRequired(action);
            });
        };
        _syncPlay.StateChanged += _ => DispatchSyncPlay(UpdateSyncPlayInfo);
        _syncPlay.Notice += notice => Dispatcher.BeginInvoke(() =>
        {
            if (_windowClosing) return;
            UpdateSyncPlayInfo();
            ShowToast(notice.Kind switch
            {
                SyncPlayNoticeKind.UserJoined => $"{notice.Text} joined the group",
                SyncPlayNoticeKind.UserLeft => $"{notice.Text} left the group",
                SyncPlayNoticeKind.GroupDoesNotExist => "Could not join: the group no longer exists",
                SyncPlayNoticeKind.LibraryAccessDenied => "Could not join: you cannot see what the group is playing",
                SyncPlayNoticeKind.RejoinFailed => "Left the group: the connection was lost too long",
                _ => "SyncPlay refused this session",
            });
        });
    }

    private void DispatchSyncPlay(Action action)
    {
        var group = _syncPlay.GroupId;
        var key = _syncPlay.ProfileKey;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_windowClosing && group == _syncPlay.GroupId && key == _syncPlay.ProfileKey) action();
        });
    }

    private void AttachSyncPlayPlayer()
    {
        if (_player is not { } player) return;
        _syncPlayBinding = new(player, Dispatcher, _syncPlay, () => IsGroupPlayback
            ? (new SyncPlayState(_syncPlay.JoinedAtUtc, _syncPlay.PlaylistItemId,
                LastApplied: _syncPlay.LastApplied), _syncPlay.Clock?.Offset ?? TimeSpan.Zero) : null);
        _syncPlayBinding.StopRequested += () => OnSyncPlayActionRequired(new(SyncPlayActionKind.Stop));
        player.EndReached += OnSyncPlayEndReached;
        _syncPlay.ReadPlayer = () => _syncPlayBinding?.ReadState() ?? SyncPlayPlayerState.Idle;
    }

    // PlayItem negotiates asynchronously; only LoadFile owns the new mpv generation.
    private void ArmSyncPlayLoad(int generation)
    {
        var context = _syncPlay.ReportContext;
        if (!IsGroupPlayback || context is not { } expected
            || expected.GroupId != _syncPlayGroupId || expected.PlaylistItemId != _syncPlayLoadedEntry)
        { _syncPlayBinding?.Reset(); return; }
        _syncPlayAwaitingLoad = false;
        _syncPlayBinding?.ArmForLoad(generation, startPaused: true, expectedContext: expected);
        if (_syncPlayDeferredAction is { } action)
        {
            _syncPlayDeferredAction = null;
            _syncPlayBinding?.Apply(action, generation);
        }
    }

    private void UpdateSyncPlayInfo()
    {
        _playerViewModel.SetSyncPlay(_syncPlay.IsInGroup
            ? new(_syncPlay.GroupName ?? "SyncPlay", _syncPlay.Participants, _syncPlay.GroupState) : null);
        _playerViewModel.SyncPlayPending = _syncPlayPosts > 0 || _syncPlay.IsMembershipPending;
    }

    private void OnSyncPlayEndReached()
    {
        if (_player is not { } player || !IsGroupPlayback || player.IsLoading
            || _playerViewModel.DurationSeconds <= 0 || _syncPlayEofGeneration == player.LoadGeneration
            || _syncPlayLoadedEntry is not { } entry || entry != _syncPlay.PlaylistItemId) return;
        _syncPlayEofGeneration = player.LoadGeneration;
        _ = PostSyncPlayAsync(() => _syncPlay.NextItemAsync(entry));
    }

    private void OnSyncPlayGroupJoined(SyncPlayGroupInfo info)
    {
        if (_windowClosing) return;
        _syncPlayEpoch++;
        if (_syncPlayGroupId != info.GroupId)
        {
            _syncPlayLoadRequest++;
            _syncPlayBinding?.Reset();
            _syncPlayLoadedEntry = null;
            _syncPlayRequestedEntry = null;
            _syncPlayQueue = null;
            _syncPlayTitles.Clear();
            _app.Queue.Clear();
            _syncPlayDeferredAction = null;
            _syncPlayPlaybackSequence = -1;
        }
        _syncPlayGroupId = info.GroupId;
        UpdateSyncPlayInfo();
        if (IsGroupPlayback && _syncPlay.ReportContext is { } context
            && context.GroupId == info.GroupId && context.PlaylistItemId == _syncPlayLoadedEntry)
            _syncPlayBinding?.RefreshReportContext(context);
        // A restore can suppress an equal queue update. Restart only unfinished item lookup.
        if (_syncPlayRequestedEntry is not null && _syncPlayLoadedEntry != _syncPlayRequestedEntry)
        {
            _syncPlayRequestedEntry = null;
            if (_syncPlayQueue is { } queue) OnSyncPlayQueueChanged(queue);
        }
        if (_syncPlayCreateQueue is { } seed && seed.Key == _syncPlay.ProfileKey)
        {
            _syncPlayCreateQueue = null;
            _ = PostSyncPlayAsync(() => _syncPlay.SetNewQueueAsync(seed.Items, seed.Index, seed.Ticks));
        }
    }

    private void OnSyncPlayGroupLeft(Guid id)
    {
        if (_windowClosing || (_syncPlayGroupId is { } held && held != id)) return;
        _syncPlayEpoch++;
        _syncPlayLoadRequest++;
        // An unstarted negotiated group load must not appear after the user leaves.
        if (_syncPlayAwaitingLoad && _syncPlayPlaybackSequence == _playbackSequence)
            StopPlaybackAndReturn();
        _syncPlayBinding?.Reset();
        _syncPlayGroupId = null;
        _syncPlayLoadedEntry = null;
        _syncPlayRequestedEntry = null;
        _syncPlayQueue = null;
        _syncPlayDeferredAction = null;
        _syncPlayPlaybackSequence = -1;
        _syncPlayAwaitingLoad = false;
        UpdateSyncPlayInfo();
    }

    private static SyncPlayQueueItem? CurrentEntry(SyncPlayQueueUpdate? queue)
        => queue is { Playlist: { } entries, PlayingItemIndex: { } index }
            && index >= 0 && index < entries.Count ? entries[index] : null;

    private void OnSyncPlayQueueChanged(SyncPlayQueueUpdate queue)
    {
        _syncPlayQueue = queue;
        RefreshSyncPlayQueueRows();
        _ = LoadSyncPlayQueueTitlesAsync(queue);
        if (CurrentEntry(queue) is not { Item: not null, PlaylistItem: not null } entry)
        {
            // Metadata lookup for a replacement invalidates the sequence, but the outgoing
            // loaded entry still owns the file that must stop when the group queue empties.
            var hadGroupPlayback = _syncPlayGroupId == _syncPlay.GroupId
                && _syncPlayLoadedEntry is not null && _app.State == AppState.Playing;
            _syncPlayLoadRequest++;
            _syncPlayRequestedEntry = null;
            _syncPlayLoadedEntry = null;
            _syncPlayDeferredAction = null;
            _syncPlayBinding?.Reset();
            _syncPlayAwaitingLoad = false;
            _syncPlayPlaybackSequence = -1;
            if (hadGroupPlayback) StopPlaybackAndReturn();
            return;
        }
        if (entry.PlaylistItem == _syncPlayLoadedEntry || entry.PlaylistItem == _syncPlayRequestedEntry) return;
        // Invalidate the outgoing negotiation BEFORE awaiting the next entry's metadata.
        _playbackSequence++;
        _syncPlayPlaybackSequence = -1;
        _syncPlayAwaitingLoad = false;
        _ = DiscardUnreportedLoad();
        _syncPlayDeferredAction = null;
        _ = LoadGroupItemAsync(entry, queue.StartPositionTicks ?? 0);
    }

    private void RefreshSyncPlayQueueRows()
    {
        _playerViewModel.SetSyncPlayQueue((_syncPlayQueue?.Playlist ?? []).Select((entry, index) =>
            new QueueRow(index, entry.Item is { } id && _syncPlayTitles.TryGetValue(id, out var title)
                ? title : $"Group item {index + 1}", index == _syncPlayQueue?.PlayingItemIndex,
                entry.PlaylistItem)).ToArray());
    }

    private async Task LoadSyncPlayQueueTitlesAsync(SyncPlayQueueUpdate queue)
    {
        if (_syncPlay.ProfileKey is not { } key || _app.FindSessionByKey(key) is not { } jf) return;
        var epoch = _syncPlayEpoch;
        var missing = (queue.Playlist ?? []).Select(e => e.Item).OfType<Guid>()
            .Where(id => !_syncPlayTitles.ContainsKey(id)).Distinct().ToArray();
        try
        {
            foreach (var batch in missing.Chunk(100))
            {
                var items = await jf.GetItemsByIdsAsync(batch, CancellationToken.None);
                if (_windowClosing || epoch != _syncPlayEpoch || key != _syncPlay.ProfileKey
                    || !ReferenceEquals(queue, _syncPlayQueue)) return;
                foreach (var item in items) _syncPlayTitles[item.Id] = item.QueueDisplay;
                RefreshSyncPlayQueueRows();
            }
        }
        catch (Exception ex) { AppLog.Detail("syncplay", $"event=queue_titles outcome=failure error={ex.GetType().Name}"); }
    }

    private async Task LoadGroupItemAsync(SyncPlayQueueItem entry, long ticks)
    {
        if (entry.Item is not { } itemId || entry.PlaylistItem is not { } playlistId
            || _syncPlay.ProfileKey is not { } key
            || _app.FindSessionByKey(key) is not { IsConnected: true } jf) return;
        var epoch = _syncPlayEpoch;
        var request = ++_syncPlayLoadRequest;
        _syncPlayRequestedEntry = playlistId;
        _syncPlayBinding?.Reset();
        try
        {
            var item = await jf.GetItemAsync(itemId);
            if (item is null || _windowClosing || epoch != _syncPlayEpoch || request != _syncPlayLoadRequest
                || key != _syncPlay.ProfileKey || playlistId != _syncPlay.PlaylistItemId) return;
            _syncPlayTitles[itemId] = item.QueueDisplay;
            _syncPlayLoading = true;
            _syncPlayAwaitingLoad = true;
            _syncPlayLoadedEntry = playlistId;
            // Let the binding settle the queue's initial seek after the initial playback restart.
            // A FileLoaded resume seek can otherwise be mistaken for an already-settled load.
            if (ticks > 0) _syncPlayDeferredAction ??= new(SyncPlayActionKind.Seek, TargetTicks: ticks);
            try { PlayItem(item, 0, session: jf); }
            finally { _syncPlayLoading = false; }
            RefreshSyncPlayQueueRows();
            ActivateForRemotePlay();
            AppLog.Info("syncplay", $"event=load outcome=group item={itemId:N}");
        }
        catch (Exception ex)
        {
            AppLog.Info("syncplay", $"event=load outcome=failure error={ex.GetType().Name}");
            if (epoch == _syncPlayEpoch && request == _syncPlayLoadRequest)
                ShowToast("Could not load the group's item.");
        }
        finally
        {
            if (request == _syncPlayLoadRequest) _syncPlayRequestedEntry = null;
        }
    }

    private void OnSyncPlayActionRequired(SyncPlayAction action)
    {
        if (action.Kind == SyncPlayActionKind.Noop) return;
        if (action.Kind == SyncPlayActionKind.Stop)
        {
            _syncPlayLoadRequest++;
            _syncPlayRequestedEntry = null;
            _syncPlayLoadedEntry = null;
            _syncPlayDeferredAction = null;
            _syncPlayBinding?.Reset();
            if (_app.State == AppState.Playing) StopPlaybackAndReturn();
            _syncPlayPlaybackSequence = -1;
            _syncPlayAwaitingLoad = false;
            return;
        }
        if (_syncPlayRequestedEntry is not null || _syncPlayAwaitingLoad)
        {
            _syncPlayDeferredAction = action;
            return;
        }
        if (!IsGroupPlayback)
        {
            if (action.Kind != SyncPlayActionKind.Pause && CurrentEntry(_syncPlayQueue) is { } entry)
            {
                _syncPlayDeferredAction = action;
                _ = LoadGroupItemAsync(entry, action.TargetTicks);
            }
            return;
        }
        if (_player is { } player) _syncPlayBinding?.Apply(action, player.LoadGeneration);
    }

    private bool OnLocalTransport(TransportRequest request)
    {
        var decision = SyncPlayGate.Resolve(request, _syncPlay.IsInGroup,
            _player?.Pause ?? true, _player?.TimePos ?? 0, IsGroupPlayback ? _player?.Duration ?? 0 : 0);
        if (decision.Outcome == GateOutcome.Apply) return false;
        if (decision.Outcome == GateOutcome.Drop)
        {
            ShowToast(decision.Reason == "no_duration" ? "Wait for the group's item to load."
                : "Leave the group to use that control.");
            return true;
        }
        _ = PostSyncPlayAsync(() => decision.Verb switch
        {
            "pause" => _syncPlay.PauseAsync(),
            "unpause" => _syncPlay.UnpauseAsync(),
            "seek" => _syncPlay.SeekAsync(decision.PositionTicks),
            "stop" => _syncPlay.StopAsync(),
            "next" => _syncPlay.NextItemAsync(),
            "previous" => _syncPlay.PreviousItemAsync(),
            "select" => SelectSyncPlayQueueItemAsync(decision.PlaylistItemId!.Value),
            "remove" => RemoveSyncPlayQueueItemAsync(decision.PlaylistItemId!.Value),
            _ => Task.FromResult(false),
        });
        return true;
    }

    private async Task<bool> PostSyncPlayAsync(Func<Task<bool>> post)
    {
        _syncPlayPosts++;
        _playerViewModel.SyncPlayPending = true;
        try
        {
            var sent = await post();
            if (!sent && !_windowClosing) ShowToast("The group did not accept that. Try again.");
            return sent;
        }
        catch (Exception ex)
        {
            AppLog.Info("syncplay", $"event=local outcome=failure error={ex.GetType().Name}");
            if (!_windowClosing) ShowToast("The group could not be reached.");
            return false;
        }
        finally
        {
            _syncPlayPosts--;
            UpdateSyncPlayInfo();
        }
    }

    private void RequestStop(string source)
    {
        if (!OnLocalTransport(new(TransportKind.Stop))) StopPlaybackAndReturn();
    }

    internal Task<IReadOnlyList<SyncPlayGroupSummary>> ListSyncPlayGroupsAsync()
        => _app.ActiveSessionKey is { } key ? _syncPlay.ListGroupsAsync(key, throwOnFailure: true)
            : Task.FromResult<IReadOnlyList<SyncPlayGroupSummary>>([]);

    internal async Task<bool> JoinSyncPlayGroupAsync(Guid groupId)
    {
        if (_app.ActiveSessionKey is not { } key) return false;
        if (await _syncPlay.RefreshAccessAsync(key) == SyncPlayAccessLevel.None)
        { ShowToast("This account cannot join SyncPlay groups."); return false; }
        return await PostSyncPlayAsync(() => _syncPlay.JoinAsync(key, groupId));
    }

    internal async Task LeaveSyncPlayGroupAsync()
    {
        _syncPlayCreateQueue = null;
        await _syncPlay.LeaveAsync();
        UpdateSyncPlayInfo();
    }

    internal async Task<bool> CreateSyncPlayGroupAsync(string name)
    {
        if (_app.ActiveSessionKey is not { } key || string.IsNullOrWhiteSpace(name)) return false;
        var access = await _syncPlay.RefreshAccessAsync(key);
        if (access is SyncPlayAccessLevel.None or SyncPlayAccessLevel.JoinGroups)
        { ShowToast("This account cannot create SyncPlay groups."); return false; }
        // Only seed items owned by this profile. A raw URL cannot become a server queue item.
        if (_playingItem is { } item && ReferenceEquals(_playbackJf, _app.FindSessionByKey(key)))
        {
            var items = _app.Queue.IsActive ? _app.Queue.Items.Select(i => i.Id).ToArray() : [item.Id];
            _syncPlayCreateQueue = (key, items, _app.Queue.IsActive ? _app.Queue.CurrentIndex : 0,
                (long)((_player?.TimePos ?? 0) * TimeSpan.TicksPerSecond));
        }
        var sent = await PostSyncPlayAsync(() => _syncPlay.CreateAsync(key, name));
        if (!sent) _syncPlayCreateQueue = null;
        return sent;
    }

    internal Task<bool> SelectSyncPlayQueueItemAsync(Guid id)
        => _syncPlayQueue?.Playlist?.Any(e => e.PlaylistItem == id) == true
            ? _syncPlay.SetPlaylistItemAsync(id) : Task.FromResult(false);

    private Task<bool> RemoveSyncPlayQueueItemAsync(Guid id)
        => _syncPlayQueue?.Playlist?.Any(e => e.PlaylistItem == id) == true
            ? _syncPlay.RemovePlaylistItemAsync(id) : Task.FromResult(false);

    private bool SubmitSyncPlayQueue(IReadOnlyList<MediaItem> items, int index, long ticks, JellyfinService owner)
    {
        if (!_syncPlay.IsInGroup) return false;
        if (_syncPlay.ProfileKey is { } key && ReferenceEquals(owner, _app.FindSessionByKey(key)))
            _ = PostSyncPlayAsync(() => _syncPlay.SetNewQueueAsync(items.Select(i => i.Id).ToArray(), index, ticks));
        else ShowToast("Leave the group to play from another server or account.");
        return true;
    }
}
