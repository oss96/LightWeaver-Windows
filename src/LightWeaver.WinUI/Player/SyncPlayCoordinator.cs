using LightWeaver.Details;
using LightWeaver.Diagnostics;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Threading;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Shell;

namespace LightWeaver.WinUI.Player;

/// <summary>SyncPlay in v2 (design 09), following the WPF app's MainWindow.SyncPlay: joins, leaves
/// and creates groups, loads the group's current item through the playback controller, applies the
/// group's commands through the shared player binding, and routes local transport through the group.</summary>
internal sealed class SyncPlayCoordinator : IDisposable
{
    private readonly AppViewModel _app;
    private readonly PlaybackController _playback;
    private readonly IUiDispatcher _ui;
    private SyncPlayPlayerBinding? _binding;
    private int _epoch;
    private int _loadRequest;
    private int _playbackSequence = -1;
    private Guid? _groupId;
    private Guid? _loadedEntry;
    private Guid? _requestedEntry;
    private SyncPlayQueueUpdate? _queue;
    private bool _awaitingLoad;
    private SyncPlayAction? _deferredAction;
    private int _posts;
    private int _eofGeneration = -1;
    private (string Key, IReadOnlyList<Guid> Items, int Index, long Ticks)? _createQueue;
    private readonly Dictionary<Guid, string> _titles = [];
    private bool _closed;

    public SyncPlayCoordinator(AppViewModel app, PlaybackController playback, SyncPlayClient client, IUiDispatcher ui)
    {
        _app = app;
        _playback = playback;
        Client = client;
        _ui = ui;
        Vm.TransportGate = OnLocalTransport;
        client.GroupJoined += info => Dispatch(() => OnGroupJoined(info));
        client.GroupLeft += id => _ui.Post(() => OnGroupLeft(id));
        client.QueueChanged += queue => Dispatch(() => OnQueueChanged(queue));
        client.ActionRequired += action =>
        {
            var entry = client.PlaylistItemId;
            Dispatch(() => { if (entry == client.PlaylistItemId) OnActionRequired(action); });
        };
        client.StateChanged += _ => Dispatch(UpdateInfo);
        client.ParticipantsChanged += _ => Dispatch(UpdateInfo);
        client.Notice += notice => _ui.Post(() =>
        {
            if (_closed) return;
            UpdateInfo();
            Announce(notice);
        });
    }

    public SyncPlayClient Client { get; }

    /// <summary>Raised for join and leave notices with a merge key; the window shows a toast or a
    /// pill over video.</summary>
    public event Action<string, string?>? Announcement;

    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(3);
    private readonly List<string> _joined = [];
    private readonly List<string> _left = [];
    private DateTimeOffset _joinedAt;
    private DateTimeOffset _leftAt;

    private PlayerViewModel Vm => _playback.ViewModel;

    /// <summary>Whether what plays is the group's current item, loaded for the group.</summary>
    internal bool IsGroupPlayback => Client.IsInGroup
        && _groupId == Client.GroupId
        && _loadedEntry is { } entry && entry == Client.PlaylistItemId
        && _playbackSequence == _playback.Sequence && _playback.IsPlaying;

    /// <summary>Hooks the shared binding to the player once it exists.</summary>
    public void AttachPlayer(MpvPlayer player)
    {
        _binding = new SyncPlayPlayerBinding(player, new WinUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()), Client,
            () => IsGroupPlayback && Client.DriftState() is { } state ? (state, Client.Clock?.Offset ?? TimeSpan.Zero) : null);
        _binding.StopRequested += () => OnActionRequired(new(SyncPlayActionKind.Stop));
        player.EndReached += OnEndReached;
        Client.ReadPlayer = () => _binding?.ReadState() ?? SyncPlayPlayerState.Idle;
    }

    /// <summary>A local play while in a group goes to the group's queue instead.</summary>
    public bool TrySubmit(IReadOnlyList<MediaItem> items, int index, long ticks, JellyfinService owner)
    {
        if (!Client.IsInGroup) return false;
        if (Client.ProfileKey is { } key && ReferenceEquals(owner, _app.FindSessionByKey(key)))
            _ = PostAsync(() => Client.SetNewQueueAsync([.. items.Select(i => i.Id)], index, ticks));
        else ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.LeaveToPlayFromAnother"));
        return true;
    }

    /// <summary>A new local load; the binding takes it over when it is the group's item.</summary>
    public void OnLoaded(int generation)
    {
        if (_playback.IsGroupLoading) _playbackSequence = _playback.Sequence;
        var context = Client.ReportContext;
        if (!IsGroupPlayback || context is not { } expected || expected.GroupId != _groupId || expected.PlaylistItemId != _loadedEntry)
        {
            _binding?.Reset();
            return;
        }
        _awaitingLoad = false;
        _binding?.ArmForLoad(generation, startPaused: true, expectedContext: expected);
        if (_deferredAction is { } action)
        {
            _deferredAction = null;
            _binding?.Apply(action, generation);
        }
    }

    /// <summary>Local playback stopped: nothing of the group stays loaded.</summary>
    public void OnStopped()
    {
        _binding?.Reset();
        _awaitingLoad = false;
        _playbackSequence = -1;
        _loadedEntry = null;
        _requestedEntry = null;
        _loadRequest++;
    }

    public void ResetBinding() => _binding?.Reset();

    public Task<IReadOnlyList<SyncPlayGroupSummary>> ListGroupsAsync()
        => _app.ActiveSessionKey is { } key ? Client.ListGroupsAsync(key, throwOnFailure: true) : Task.FromResult<IReadOnlyList<SyncPlayGroupSummary>>([]);

    public async Task<bool> JoinAsync(Guid groupId)
    {
        if (_app.ActiveSessionKey is not { } key) return false;
        if (await Client.RefreshAccessAsync(key) == SyncPlayAccessLevel.None)
        {
            ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.CannotJoin"));
            return false;
        }
        return await PostAsync(() => Client.JoinAsync(key, groupId));
    }

    public async Task LeaveAsync()
    {
        _createQueue = null;
        await Client.LeaveAsync();
        UpdateInfo();
    }

    /// <summary>A new group named by the user; what plays now becomes its queue.</summary>
    public async Task<bool> CreateAsync(string name)
    {
        if (_app.ActiveSessionKey is not { } key || string.IsNullOrWhiteSpace(name)) return false;
        var access = await Client.RefreshAccessAsync(key);
        if (access is SyncPlayAccessLevel.None or SyncPlayAccessLevel.JoinGroups)
        {
            ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.CannotCreate"));
            return false;
        }
        if (_playback.Current is { } item && _playback.Player is { } player)
        {
            var items = _app.Queue.IsActive ? _app.Queue.Items.Select(i => i.Id).ToArray() : [item.Id];
            _createQueue = (key, items, _app.Queue.IsActive ? _app.Queue.CurrentIndex : 0, (long)(player.TimePos * TimeSpan.TicksPerSecond));
        }
        var sent = await PostAsync(() => Client.CreateAsync(key, name));
        if (!sent) _createQueue = null;
        return sent;
    }

    private void Dispatch(Action action)
    {
        var group = Client.GroupId;
        var key = Client.ProfileKey;
        _ui.Post(() => { if (!_closed && group == Client.GroupId && key == Client.ProfileKey) action(); });
    }

    private void Announce(SyncPlayNotice notice)
    {
        var text = notice.Kind switch
        {
            SyncPlayNoticeKind.UserJoined => L.Format("SyncPlay.Joined", Merge(_joined, ref _joinedAt, notice.Text)),
            SyncPlayNoticeKind.UserLeft => L.Format("SyncPlay.Left", Merge(_left, ref _leftAt, notice.Text)),
            SyncPlayNoticeKind.GroupDoesNotExist => L.Text("SyncPlay.GroupGone"),
            SyncPlayNoticeKind.LibraryAccessDenied => L.Text("SyncPlay.NoAccess"),
            SyncPlayNoticeKind.RejoinFailed => L.Text("SyncPlay.ConnectionLost"),
            _ => L.Text("SyncPlay.Refused"),
        };
        var key = notice.Kind switch
        {
            SyncPlayNoticeKind.UserJoined => "syncplay-joined",
            SyncPlayNoticeKind.UserLeft => "syncplay-left",
            _ => null,
        };
        Announcement?.Invoke(text, key);
    }

    /// <summary>Events within 3 s of the previous one merge into one line ("Alex and Jo").</summary>
    private static string Merge(List<string> names, ref DateTimeOffset last, string? name)
    {
        var now = DateTimeOffset.Now;
        if (now - last > MergeWindow) names.Clear();
        last = now;
        if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
        return names.Count switch
        {
            0 => name ?? "",
            1 => names[0],
            2 => L.Format("SyncPlay.TwoNames", names[0], names[1]),
            _ => L.Format("SyncPlay.NameAndOthers", names[0], names.Count - 1),
        };
    }

    private void UpdateInfo()
    {
        Vm.SetSyncPlay(Client.IsInGroup ? new(Client.GroupName ?? "SyncPlay", Client.Participants, Client.GroupState) : null);
        Vm.SyncPlayPending = _posts > 0 || Client.IsMembershipPending;
    }

    private void OnEndReached()
    {
        if (_playback.Player is not { } player || !IsGroupPlayback || player.IsLoading || Vm.DurationSeconds <= 0
            || _eofGeneration == player.LoadGeneration || _loadedEntry is not { } entry || entry != Client.PlaylistItemId) return;
        _eofGeneration = player.LoadGeneration;
        _ = PostAsync(() => Client.NextItemAsync(entry));
    }

    private void OnGroupJoined(SyncPlayGroupInfo info)
    {
        _epoch++;
        if (_groupId != info.GroupId)
        {
            _loadRequest++;
            _binding?.Reset();
            _loadedEntry = null;
            _requestedEntry = null;
            _queue = null;
            _titles.Clear();
            _app.Queue.Clear();
            _deferredAction = null;
            _playbackSequence = -1;
        }
        _groupId = info.GroupId;
        UpdateInfo();
        if (IsGroupPlayback && Client.ReportContext is { } context && context.GroupId == info.GroupId && context.PlaylistItemId == _loadedEntry)
            _binding?.RefreshReportContext(context);
        if (_requestedEntry is not null && _loadedEntry != _requestedEntry)
        {
            _requestedEntry = null;
            if (_queue is { } queue) OnQueueChanged(queue);
        }
        if (_createQueue is { } seed && seed.Key == Client.ProfileKey)
        {
            _createQueue = null;
            _ = PostAsync(() => Client.SetNewQueueAsync(seed.Items, seed.Index, seed.Ticks));
        }
    }

    private void OnGroupLeft(Guid id)
    {
        if (_closed || (_groupId is { } held && held != id)) return;
        _epoch++;
        _loadRequest++;
        if (_awaitingLoad && _playbackSequence == _playback.Sequence) _playback.Stop();
        _binding?.Reset();
        _groupId = null;
        _loadedEntry = null;
        _requestedEntry = null;
        _queue = null;
        _deferredAction = null;
        _playbackSequence = -1;
        _awaitingLoad = false;
        UpdateInfo();
    }

    private static SyncPlayQueueItem? CurrentEntry(SyncPlayQueueUpdate? queue)
        => queue is { Playlist: { } entries, PlayingItemIndex: { } index } && index >= 0 && index < entries.Count ? entries[index] : null;

    private void OnQueueChanged(SyncPlayQueueUpdate queue)
    {
        _queue = queue;
        RefreshQueueRows();
        _ = LoadQueueTitlesAsync(queue);
        if (CurrentEntry(queue) is not { Item: not null, PlaylistItem: not null } entry)
        {
            var hadGroupPlayback = _groupId == Client.GroupId && _loadedEntry is not null && _playback.IsPlaying;
            _loadRequest++;
            _requestedEntry = null;
            _loadedEntry = null;
            _deferredAction = null;
            _binding?.Reset();
            _awaitingLoad = false;
            _playbackSequence = -1;
            if (hadGroupPlayback) _playback.Stop();
            return;
        }
        if (entry.PlaylistItem == _loadedEntry || entry.PlaylistItem == _requestedEntry) return;
        _playbackSequence = -1;
        _awaitingLoad = false;
        _deferredAction = null;
        _ = LoadGroupItemAsync(entry, queue.StartPositionTicks ?? 0);
    }

    private void RefreshQueueRows()
    {
        Vm.SetSyncPlayQueue([.. (_queue?.Playlist ?? []).Select((entry, index) =>
            new QueueRow(index, entry.Item is { } id && _titles.TryGetValue(id, out var title) ? title : L.Format("SyncPlay.GroupItem", index + 1),
                index == _queue?.PlayingItemIndex, entry.PlaylistItem)
            {
                DisplayProvider = () => entry.Item is { } liveId && _titles.TryGetValue(liveId, out var liveTitle) ? liveTitle : L.Format("SyncPlay.GroupItem", index + 1),
            })]);
    }

    private async Task LoadQueueTitlesAsync(SyncPlayQueueUpdate queue)
    {
        if (Client.ProfileKey is not { } key || _app.FindSessionByKey(key) is not { } session) return;
        var epoch = _epoch;
        var missing = (queue.Playlist ?? []).Select(e => e.Item).OfType<Guid>().Where(id => !_titles.ContainsKey(id)).Distinct().ToArray();
        try
        {
            foreach (var batch in missing.Chunk(100))
            {
                var items = await session.GetItemsByIdsAsync(batch, CancellationToken.None);
                if (_closed || epoch != _epoch || key != Client.ProfileKey || !ReferenceEquals(queue, _queue)) return;
                foreach (var item in items) _titles[item.Id] = item.QueueDisplay;
                RefreshQueueRows();
            }
        }
        catch (Exception ex)
        {
            AppLog.Detail("syncplay", $"event=queue_titles outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>Plays the group's current item again after it failed, through the group's own load
    /// path so it stays the group's playback; false outside a group.</summary>
    internal bool RetryGroupItem(PlaybackChoice choice)
    {
        if (!Client.IsInGroup || CurrentEntry(_queue) is not { Item: not null, PlaylistItem: not null } entry) return false;
        _playbackSequence = -1;
        _awaitingLoad = false;
        _deferredAction = null;
        _requestedEntry = null;
        _loadedEntry = null;
        _ = LoadGroupItemAsync(entry, choice.Start.Ticks, choice with { Start = TimeSpan.Zero });
        return true;
    }

    private async Task LoadGroupItemAsync(SyncPlayQueueItem entry, long ticks, PlaybackChoice? choice = null)
    {
        if (entry.Item is not { } itemId || entry.PlaylistItem is not { } playlistId || Client.ProfileKey is not { } key
            || _app.FindSessionByKey(key) is not { IsConnected: true } session) return;
        var epoch = _epoch;
        var request = ++_loadRequest;
        _requestedEntry = playlistId;
        _binding?.Reset();
        try
        {
            var item = await session.GetItemAsync(itemId);
            if (item is null || _closed || epoch != _epoch || request != _loadRequest || key != Client.ProfileKey || playlistId != Client.PlaylistItemId) return;
            _titles[itemId] = item.QueueDisplay;
            _awaitingLoad = true;
            _loadedEntry = playlistId;
            if (ticks > 0) _deferredAction ??= new(SyncPlayActionKind.Seek, TargetTicks: ticks);
            _playbackSequence = _playback.PlayForGroup(item, session, choice);
            RefreshQueueRows();
            AppLog.Info("syncplay", $"event=load outcome=group item={itemId:N}");
        }
        catch (Exception ex)
        {
            AppLog.Info("syncplay", $"event=load outcome=failure error={ex.GetType().Name}");
            if (epoch == _epoch && request == _loadRequest) ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.CouldNotLoad"));
        }
        finally
        {
            if (request == _loadRequest) _requestedEntry = null;
        }
    }

    private void OnActionRequired(SyncPlayAction action)
    {
        if (action.Kind == SyncPlayActionKind.Noop) return;
        if (action.Kind == SyncPlayActionKind.Stop)
        {
            _loadRequest++;
            _requestedEntry = null;
            _loadedEntry = null;
            _deferredAction = null;
            _binding?.Reset();
            if (_playback.IsPlaying) _playback.Stop();
            _playbackSequence = -1;
            _awaitingLoad = false;
            return;
        }
        if (_requestedEntry is not null || _awaitingLoad)
        {
            _deferredAction = action;
            return;
        }
        if (!IsGroupPlayback)
        {
            if (action.Kind != SyncPlayActionKind.Pause && CurrentEntry(_queue) is { } entry)
            {
                _deferredAction = action;
                _ = LoadGroupItemAsync(entry, action.TargetTicks);
            }
            return;
        }
        if (_playback.Player is { } player) _binding?.Apply(action, player.LoadGeneration);
    }

    private bool OnLocalTransport(TransportRequest request)
    {
        var player = _playback.Player;
        var decision = SyncPlayGate.Resolve(request, Client.IsInGroup, player?.Pause ?? true, player?.TimePos ?? 0, IsGroupPlayback ? player?.Duration ?? 0 : 0);
        if (decision.Outcome == GateOutcome.Apply) return false;
        if (decision.Outcome == GateOutcome.Drop)
        {
            ToastHost.Show(ToastKind.Info, decision.Reason == "no_duration" ? L.Text("SyncPlay.WaitForItem") : L.Text("SyncPlay.LeaveToUseControl"));
            return true;
        }
        _ = PostAsync(() => decision.Verb switch
        {
            "pause" => Client.PauseAsync(),
            "unpause" => Client.UnpauseAsync(),
            "seek" => Client.SeekAsync(decision.PositionTicks),
            "stop" => Client.StopAsync(),
            "next" => Client.NextItemAsync(),
            "previous" => Client.PreviousItemAsync(),
            "select" => SelectQueueItemAsync(decision.PlaylistItemId!.Value),
            "remove" => RemoveQueueItemAsync(decision.PlaylistItemId!.Value),
            _ => Task.FromResult(false),
        });
        return true;
    }

    private Task<bool> SelectQueueItemAsync(Guid id)
        => _queue?.Playlist?.Any(e => e.PlaylistItem == id) == true ? Client.SetPlaylistItemAsync(id) : Task.FromResult(false);

    private Task<bool> RemoveQueueItemAsync(Guid id)
        => _queue?.Playlist?.Any(e => e.PlaylistItem == id) == true ? Client.RemovePlaylistItemAsync(id) : Task.FromResult(false);

    private async Task<bool> PostAsync(Func<Task<bool>> post)
    {
        _posts++;
        Vm.SyncPlayPending = true;
        try
        {
            var sent = await post();
            if (!sent && !_closed) ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.NotAccepted"));
            return sent;
        }
        catch (Exception ex)
        {
            AppLog.Info("syncplay", $"event=local outcome=failure error={ex.GetType().Name}");
            if (!_closed) ToastHost.Show(ToastKind.Error, L.Text("SyncPlay.Unreachable"));
            return false;
        }
        finally
        {
            _posts--;
            UpdateInfo();
        }
    }

    public void Dispose()
    {
        _closed = true;
        _binding?.Dispose();
        Client.Dispose();
    }
}
