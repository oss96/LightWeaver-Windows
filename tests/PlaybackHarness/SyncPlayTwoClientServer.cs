using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

/// <summary>
/// The synthetic Jellyfin a two-client SyncPlay run talks to: <see cref="LiveSessionFixture.FakeJellyfinServer"/>
/// with its opt-in device tokens and media serving switched on, plus the group state the 12.0
/// server keeps — membership per SESSION, the queue with server-minted playlist item ids, the
/// Idle/Waiting/Paused/Playing machine, and the <c>SyncPlayCommand</c>s it schedules.
///
/// <para>Sessions are keyed by token and nothing else. After sign-in the SDK and the session
/// socket authenticate with the token alone, so the token IS the session here exactly as it is on
/// the real server, and every request, socket and frame below is attributed through it.</para>
///
/// <para>The time model is the one BRAIN records off the 12.0 source: an unpause is scheduled
/// <c>When = now + max(2 * highestPing, 500 ms)</c>, a pause, a seek and a stop are dated now and
/// carry the exact position. One deliberate simplification: a new queue waits for every member's
/// <c>Ready</c> and then settles PAUSED rather than starting by itself, so a test drives every
/// transport edge explicitly.</para>
///
/// <para>Moving within the queue is 12.0's <c>WaitingGroupState</c>: <c>NextItem</c>,
/// <c>PreviousItem</c> and <c>SetPlaylistItem</c> keep every entry's id, move the playing index,
/// restart at zero, send the new queue and wait for every member's <c>Ready</c> — and then RESUME,
/// whatever the group was doing, because the server sets <c>ResumePlaying</c> on all three. A
/// <c>NextItem</c>/<c>PreviousItem</c> naming an entry other than the playing one, or with nowhere
/// to go, changes nothing. <c>RemoveFromPlaylist</c> sends the shorter queue and leaves the
/// group's state alone unless it removed the last entry. Every queue update carries a strictly
/// later <c>LastUpdate</c>.</para>
///
/// <para>The echo barrier is the point of this class. While it is held, every group decision —
/// the state change and the frames it sends — is queued instead of made; REST calls are still
/// answered, recorded and attributed. That is what lets a test stand between "the server has the
/// request" and "the members have the echo" and prove nothing moved in between. Decisions are
/// made at release, so the instants they carry are release instants and a held resume still gets
/// its full pre-roll.</para>
/// </summary>
internal sealed class SyncPlayTwoClientServer : IDisposable
{
    public const string UserName = "TEST";

    /// <summary>The minimum pre-roll, the server's <c>DefaultPing</c>.</summary>
    public static readonly TimeSpan DefaultPing = TimeSpan.FromMilliseconds(500);

    /// <summary>Every <c>/SyncPlay/*</c> route a member SENDS to make the group do something, as
    /// opposed to the three it answers with (<c>Ready</c>, <c>Buffering</c>, <c>Ping</c>) and the
    /// one that only reads (<c>List</c>).</summary>
    public static bool IsCommandRoute(string route)
        => route is not ("Ready" or "Buffering" or "Ping" or "List");

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Group> _groups = [];
    private readonly Dictionary<string, long> _pings = [];
    private readonly List<SyncRequest> _syncRequests = [];
    private readonly List<Frame> _frames = [];
    private readonly List<EmittedCommand> _emitted = [];
    private readonly List<Action> _heldWork = [];
    private bool _held;
    private DateTime _lastQueueStamp;

    /// <summary>One episode per file, in order, so each queue entry streams a clip of its own.</summary>
    public SyncPlayTwoClientServer(IReadOnlyList<string> mediaFiles)
    {
        Episodes = mediaFiles.Select((file, index) => new Episode(Guid.NewGuid(), index + 1,
            EpisodeNames[index % EpisodeNames.Length], file)).ToArray();
        Http = new LiveSessionFixture.FakeJellyfinServer
        {
            IssueDeviceTokens = true,
            MediaFileFor = path => Episodes.FirstOrDefault(e => Names(path, e.Id))?.File,
            ResponseOverride = RespondAsync,
        };
    }

    public LiveSessionFixture.FakeJellyfinServer Http { get; }
    public string Origin => Http.Origin;
    public Guid UserId => Http.UserId;

    public const string SeriesName = "Two-client show";

    /// <summary>What each clip shows, so a capture says which entry is on screen.</summary>
    public static readonly string[] EpisodeNames = ["Test card", "Colour bars", "Counter"];

    /// <summary>The one series this library holds, and its episodes in order. Starting the first
    /// from its detail view queues all of them, which is how a queue reaches the group.</summary>
    public Guid SeriesId { get; } = Guid.NewGuid();
    public IReadOnlyList<Episode> Episodes { get; }

    /// <summary>The first episode: the item a single-entry queue plays.</summary>
    public Guid ItemId => Episodes[0].Id;
    public static readonly long RuntimeTicks = TimeSpan.FromSeconds(30).Ticks;

    /// <summary>Raised for anything worth a line in a guest log that is not itself a failure —
    /// today a member's <c>Buffering</c>, which pauses the whole group and would otherwise show up
    /// only as a confusing convergence failure several steps later.</summary>
    public event Action<string>? Diagnostic;

    // ---- the barrier ---------------------------------------------------------------------

    public void HoldEcho()
    {
        lock (_lock)
            _held = true;
    }

    /// <summary>Makes every decision queued while held, in arrival order, then stops holding.</summary>
    public void ReleaseEcho()
    {
        lock (_lock)
        {
            _held = false;
            foreach (var work in _heldWork)
                work();
            _heldWork.Clear();
        }
    }

    public int HeldCount
    {
        get { lock (_lock) return _heldWork.Count; }
    }

    // ---- what a test reads ---------------------------------------------------------------

    public int SyncRequestCount
    {
        get { lock (_lock) return _syncRequests.Count; }
    }

    public IReadOnlyList<SyncRequest> SyncRequestsSince(int mark)
    {
        lock (_lock)
            return [.. _syncRequests.Skip(mark)];
    }

    /// <summary>The command routes one session sent since a mark, in order. The question every
    /// "the peer sent nothing" assertion asks, scoped to a session so the initiator's own request
    /// cannot be counted against the peer.</summary>
    public IReadOnlyList<SyncRequest> CommandRequests(string token, int mark)
        => SyncRequestsSince(mark).Where(r => r.Token == token && IsCommandRoute(r.Route)).ToList();

    public int FrameCount
    {
        get { lock (_lock) return _frames.Count; }
    }

    /// <summary>Frames sent to one session since a mark.</summary>
    public IReadOnlyList<Frame> FramesTo(string token, int mark)
    {
        lock (_lock)
            return _frames.Skip(mark).Where(f => f.Token == token).ToList();
    }

    public EmittedCommand? LastEmitted(string command)
    {
        lock (_lock)
            return _emitted.LastOrDefault(c => c.Command == command);
    }

    public IReadOnlyList<EmittedCommand> EmittedSince(int mark)
    {
        lock (_lock)
            return [.. _emitted.Skip(mark)];
    }

    public int EmittedCount
    {
        get { lock (_lock) return _emitted.Count; }
    }

    public GroupView? View(Guid groupId)
    {
        lock (_lock)
            return _groups.TryGetValue(groupId, out var group) ? group.ToView() : null;
    }

    public GroupView? GroupOf(string token)
    {
        lock (_lock)
            return _groups.Values.FirstOrDefault(g => g.Members.Contains(token))?.ToView();
    }

    public void Dispose() => Http.Dispose();

    // ---- HTTP ----------------------------------------------------------------------------

    private Task<string?> RespondAsync(LiveSessionFixture.HttpCapture request)
    {
        var path = request.Path;
        if (path.StartsWith("/SyncPlay/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(SyncPlay(request, path["/SyncPlay/".Length..]));
        foreach (var episode in Episodes)
        {
            var itemPath = $"/Items/{episode.Id:D}";
            if (path.Equals(itemPath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(Json(Item(episode)));
            if (path.Equals(itemPath + "/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(Json(new
                {
                    PlaySessionId = "two-client-" + Guid.NewGuid().ToString("N"),
                    MediaSources = new[]
                    {
                        new { Id = $"two-client-source-{episode.Number}", SupportsDirectPlay = true, Container = "mkv",
                            RunTimeTicks = RuntimeTicks },
                    },
                }));
        }
        if (path.StartsWith("/Shows/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/Episodes", StringComparison.OrdinalIgnoreCase) && Names(path, SeriesId))
            return Task.FromResult<string?>(Json(new
            {
                Items = Episodes.Select(Item).ToArray(),
                TotalRecordCount = Episodes.Count,
            }));
        if (path.Equals("/Items", StringComparison.OrdinalIgnoreCase))
        {
            // The queue's title lookup asks by id; anything else is an empty library page.
            var named = Episodes.Where(e => Names(request.Query, e.Id)).Select(Item).ToArray();
            return Task.FromResult<string?>(Json(new { Items = named, TotalRecordCount = named.Length }));
        }
        if (path.Equals("/UserViews", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/UserItems/Resume", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/Shows/NextUp", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(Json(new { Items = Array.Empty<object>(), TotalRecordCount = 0 }));
        if (path.Equals("/Items/Latest", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(Json(Array.Empty<object>()));
        if (path.StartsWith("/Sessions/Playing", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/Videos/ActiveEncodings", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(NoContent);
        return Task.FromResult<string?>(null);
    }

    private object Item(Episode episode) => new
    {
        Id = episode.Id,
        Name = episode.Name,
        Type = "Episode",
        MediaType = "Video",
        SeriesId,
        SeriesName,
        ParentIndexNumber = 1,
        IndexNumber = episode.Number,
        RunTimeTicks = RuntimeTicks,
    };

    /// <summary>Whether a path or query names an id, in either of the two forms clients write.</summary>
    private static bool Names(string text, Guid id)
        => text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase);

    private string SyncPlay(LiveSessionFixture.HttpCapture request, string route)
    {
        // The fake server has already refused unknown tokens, so this is always a real session.
        var token = request.Token ?? "";
        var now = DateTime.UtcNow;
        using var body = Parse(request.Body);
        lock (_lock)
            _syncRequests.Add(new SyncRequest(_syncRequests.Count, token, route, request.Body, now));
        switch (route)
        {
            case "List":
                lock (_lock)
                    return Json(_groups.Values.Select(g => g.Info()).ToArray());
            case "Ping":
                if (Long(body, "Ping") is { } ping)
                    lock (_lock)
                        _pings[token] = Math.Clamp(ping, 0, 10_000);
                return NoContent;
            case "New":
                var name = String(body, "GroupName") ?? "Group";
                Submit(() => New(token, name));
                return NoContent;
            case "Join":
                if (Guid.TryParse(String(body, "GroupId"), out var groupId))
                    Submit(() => Join(token, groupId));
                return NoContent;
            case "Leave":
                Submit(() => Leave(token));
                return NoContent;
            case "SetNewQueue":
                var items = Items(body, "PlayingQueue");
                var position = (int)(Long(body, "PlayingItemPosition") ?? 0);
                var start = Long(body, "StartPositionTicks") ?? 0;
                Submit(() => SetNewQueue(token, items, position, start));
                return NoContent;
            case "Pause":
                Submit(() => Pause(token));
                return NoContent;
            case "Unpause":
                Submit(() => Unpause(token));
                return NoContent;
            case "Seek":
                var target = Long(body, "PositionTicks") ?? 0;
                Submit(() => Seek(token, target));
                return NoContent;
            case "Stop":
                Submit(() => Stop(token));
                return NoContent;
            case "Ready":
                var readyEntry = Guid.TryParse(String(body, "PlaylistItemId"), out var entry) ? entry : Guid.Empty;
                Submit(() => Ready(token, readyEntry));
                return NoContent;
            case "Buffering":
                Diagnostic?.Invoke($"server: Buffering from session {ShortToken(token)}");
                Submit(() => Buffering(token));
                return NoContent;
            case "NextItem":
            case "PreviousItem":
                var named = Guid.TryParse(String(body, "PlaylistItemId"), out var current) ? current : Guid.Empty;
                var step = route == "NextItem" ? 1 : -1;
                Submit(() => Step(token, named, step));
                return NoContent;
            case "SetPlaylistItem":
                var chosen = Guid.TryParse(String(body, "PlaylistItemId"), out var picked) ? picked : Guid.Empty;
                Submit(() => SetPlaylistItem(token, chosen));
                return NoContent;
            case "RemoveFromPlaylist":
                var removed = Items(body, "PlaylistItemIds");
                var clearPlaylist = Bool(body, "ClearPlaylist") ?? false;
                var clearPlaying = Bool(body, "ClearPlayingItem") ?? false;
                Submit(() => RemoveFromPlaylist(token, removed, clearPlaylist, clearPlaying));
                return NoContent;
            default:
                // Queue authoring this regression never drives (Queue, MovePlaylistItem, repeat,
                // shuffle). Recorded above, so a stray one is visible to every "no group request"
                // assertion; otherwise accepted and ignored.
                return NoContent;
        }
    }

    private void Submit(Action work)
    {
        lock (_lock)
        {
            if (_held)
                _heldWork.Add(work);
            else
                work();
        }
    }

    // ---- group decisions (always under _lock) --------------------------------------------

    private void New(string token, string name)
    {
        LeaveCurrent(token);
        var group = new Group(Guid.NewGuid(), name, DateTime.UtcNow);
        group.Members.Add(token);
        _groups[group.Id] = group;
        Send(token, LiveSessionFixture.GroupUpdate(group.Id, "GroupJoined", JsonSerializer.Serialize(group.Info())),
            "GroupJoined");
    }

    private void Join(string token, Guid groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            Send(token, LiveSessionFixture.GroupUpdate(groupId, "GroupDoesNotExist", $"\"{groupId:D}\""),
                "GroupDoesNotExist");
            return;
        }
        if (!group.Members.Contains(token))
        {
            LeaveCurrent(token);
            group.Members.Add(token);
            foreach (var other in group.Members.Where(member => member != token))
                Send(other, LiveSessionFixture.GroupUpdate(group.Id, "UserJoined", $"\"{UserName}\""), "UserJoined");
        }
        group.LastUpdatedAt = DateTime.UtcNow;
        Send(token, LiveSessionFixture.GroupUpdate(group.Id, "GroupJoined", JsonSerializer.Serialize(group.Info())),
            "GroupJoined");
        if (group.Queue.Count > 0)
        {
            Send(token, LiveSessionFixture.GroupUpdate(group.Id, "PlayQueue", QueuePayload(group, "NewPlaylist")),
                "PlayQueue");
            Send(token, State(group, "Join"), "StateUpdate");
        }
    }

    private void Leave(string token)
    {
        if (!LeaveCurrent(token))
            Send(token, LiveSessionFixture.GroupUpdate(Guid.Empty, "NotInGroup", "\"\""), "NotInGroup");
    }

    /// <summary>Takes a session out of whatever group it is in. True if it was in one.</summary>
    private bool LeaveCurrent(string token)
    {
        var group = _groups.Values.FirstOrDefault(g => g.Members.Contains(token));
        if (group is null)
            return false;
        group.Members.Remove(token);
        group.Ready.Remove(token);
        Send(token, LiveSessionFixture.GroupUpdate(group.Id, "GroupLeft", $"\"{group.Id:D}\""), "GroupLeft");
        foreach (var other in group.Members)
            Send(other, LiveSessionFixture.GroupUpdate(group.Id, "UserLeft", $"\"{UserName}\""), "UserLeft");
        if (group.Members.Count == 0)
            _groups.Remove(group.Id);
        else if (group.State == "Waiting" && group.Members.All(group.Ready.Contains))
            FinishWait(group, "Leave");
        return true;
    }

    private void SetNewQueue(string token, IReadOnlyList<Guid> items, int position, long startTicks)
    {
        if (GroupFor(token) is not { } group || items.Count == 0 || position < 0 || position >= items.Count)
            return;
        group.Queue.Clear();
        foreach (var item in items)
            group.Queue.Add((item, Guid.NewGuid()));
        group.PlayingIndex = position;
        group.PositionTicks = Math.Max(0, startTicks);
        group.LastActivity = DateTime.UtcNow;
        group.ResumeAfterWait = false;
        group.Ready.Clear();
        group.State = "Waiting";
        SendQueue(group, "NewPlaylist");
        Broadcast(group, State(group, "SetNewQueue"), "StateUpdate");
    }

    /// <summary><c>NextItem</c> (+1) and <c>PreviousItem</c> (-1). The request names the entry
    /// its sender believes is playing, which is how the server tells a skip from a stale or
    /// doubled one: anything else, or a step off either end, changes nothing.</summary>
    private void Step(string token, Guid named, int step)
    {
        if (GroupFor(token) is not { PlayingEntry: { } playing } group || named != playing)
            return;
        var index = group.PlayingIndex + step;
        if (index < 0 || index >= group.Queue.Count)
            return;
        MoveTo(group, index, step > 0 ? "NextItem" : "PreviousItem");
    }

    private void SetPlaylistItem(string token, Guid entry)
    {
        if (GroupFor(token) is not { } group)
            return;
        var index = group.Queue.FindIndex(e => e.Entry == entry);
        if (index < 0)
            return;
        MoveTo(group, index, "SetCurrentItem");
    }

    /// <summary>The shared half of the three: a new playing index from zero, the queue to every
    /// member, and a wait that resumes once all of them are ready.</summary>
    private void MoveTo(Group group, int index, string reason)
    {
        group.PlayingIndex = index;
        group.PositionTicks = 0;
        group.LastActivity = DateTime.UtcNow;
        group.ResumeAfterWait = true;
        group.Ready.Clear();
        group.State = "Waiting";
        SendQueue(group, reason);
        Broadcast(group, State(group, reason), "StateUpdate");
    }

    private void RemoveFromPlaylist(string token, IReadOnlyList<Guid> entries, bool clearPlaylist, bool clearPlaying)
    {
        if (GroupFor(token) is not { } group)
            return;
        var playing = group.PlayingEntry;
        if (clearPlaylist)
            group.Queue.RemoveAll(e => clearPlaying || e.Entry != playing);
        else
            group.Queue.RemoveAll(e => entries.Contains(e.Entry));
        var playingRemoved = playing is { } kept && !group.Queue.Any(e => e.Entry == kept);
        group.PlayingIndex = playingRemoved
            ? Math.Min(Math.Max(0, group.PlayingIndex - 1), group.Queue.Count - 1)
            : group.Queue.FindIndex(e => e.Entry == playing);
        SendQueue(group, "RemoveItems");
        if (playingRemoved && group.PlayingEntry is null)
        {
            // An emptied queue stops the group, as 12.0's idle state does.
            var now = DateTime.UtcNow;
            group.PositionTicks = 0;
            group.LastActivity = now;
            group.State = "Idle";
            Command(group, group.Members, "Stop", now, now, 0);
            Broadcast(group, State(group, "Stop"), "StateUpdate");
        }
    }

    /// <summary>Sends the queue with a <c>LastUpdate</c> strictly after every one before it, even
    /// two updates inside one clock tick: the client drops an update older than OR EQUAL to the
    /// last it applied.</summary>
    private void SendQueue(Group group, string reason)
    {
        _lastQueueStamp = DateTime.UtcNow > _lastQueueStamp ? DateTime.UtcNow : _lastQueueStamp.AddMilliseconds(1);
        group.QueueStamp = _lastQueueStamp;
        Broadcast(group, LiveSessionFixture.GroupUpdate(group.Id, "PlayQueue", QueuePayload(group, reason)),
            "PlayQueue");
    }

    private void Pause(string token)
    {
        if (GroupFor(token) is not { PlayingEntry: not null } group)
            return;
        var now = DateTime.UtcNow;
        switch (group.State)
        {
            case "Playing":
                group.PositionTicks = group.CurrentTicks(now);
                group.LastActivity = now;
                group.State = "Paused";
                Command(group, group.Members, "Pause", now, now, group.PositionTicks);
                Broadcast(group, State(group, "Pause"), "StateUpdate");
                break;
            case "Waiting":
                group.ResumeAfterWait = false;
                break;
            case "Paused":
                // A member that asks again is a member that looks lost: tell it, and only it.
                Command(group, [token], "Pause", group.LastActivity, now, group.PositionTicks);
                break;
        }
    }

    private void Unpause(string token)
    {
        if (GroupFor(token) is not { PlayingEntry: not null } group)
            return;
        var now = DateTime.UtcNow;
        switch (group.State)
        {
            case "Paused":
                StartPlaying(group, now, "Unpause");
                break;
            case "Waiting":
                group.ResumeAfterWait = true;
                break;
            case "Playing":
                Command(group, [token], "Unpause", group.LastActivity, now, group.PositionTicks);
                break;
        }
    }

    private void Seek(string token, long target)
    {
        if (GroupFor(token) is not { PlayingEntry: not null } group)
            return;
        var now = DateTime.UtcNow;
        group.ResumeAfterWait = group.State == "Playing" || (group.State == "Waiting" && group.ResumeAfterWait);
        group.PositionTicks = Math.Clamp(target, 0, RuntimeTicks);
        group.LastActivity = now;
        group.Ready.Clear();
        group.State = "Waiting";
        Command(group, group.Members, "Seek", now, now, group.PositionTicks);
        Broadcast(group, State(group, "Seek"), "StateUpdate");
    }

    private void Stop(string token)
    {
        if (GroupFor(token) is not { } group)
            return;
        var now = DateTime.UtcNow;
        group.PositionTicks = 0;
        group.LastActivity = now;
        group.State = "Idle";
        Command(group, group.Members, "Stop", now, now, 0);
        Broadcast(group, State(group, "Stop"), "StateUpdate");
    }

    private void Ready(string token, Guid entry)
    {
        if (GroupFor(token) is not { } group || group.PlayingEntry != entry)
            return;
        group.Ready.Add(token);
        if (group.State == "Waiting" && group.Members.All(group.Ready.Contains))
            FinishWait(group, "Ready");
    }

    private void Buffering(string token)
    {
        if (GroupFor(token) is not { PlayingEntry: not null } group)
            return;
        var now = DateTime.UtcNow;
        if (group.State == "Playing")
        {
            group.PositionTicks = group.CurrentTicks(now);
            group.LastActivity = now;
            group.ResumeAfterWait = true;
            group.State = "Waiting";
            // Only the session that buffers is unready; everybody else is where they were.
            group.Ready.Clear();
            foreach (var member in group.Members.Where(member => member != token))
                group.Ready.Add(member);
            Command(group, group.Members, "Pause", now, now, group.PositionTicks);
            Broadcast(group, State(group, "Buffer"), "StateUpdate");
        }
        else
        {
            group.Ready.Remove(token);
        }
    }

    private void FinishWait(Group group, string reason)
    {
        var now = DateTime.UtcNow;
        if (group.ResumeAfterWait)
        {
            StartPlaying(group, now, reason);
            return;
        }
        group.LastActivity = now;
        group.State = "Paused";
        Command(group, group.Members, "Pause", now, now, group.PositionTicks);
        Broadcast(group, State(group, reason), "StateUpdate");
    }

    /// <summary>The scheduled resume: <c>When = now + max(2 * highestPing, DefaultPing)</c>.</summary>
    private void StartPlaying(Group group, DateTime now, string reason)
    {
        var highest = group.Members.Select(member => _pings.TryGetValue(member, out var ping)
            ? ping : (long)DefaultPing.TotalMilliseconds).DefaultIfEmpty(0).Max();
        var when = now + TimeSpan.FromMilliseconds(Math.Max(2 * highest, DefaultPing.TotalMilliseconds));
        group.LastActivity = when;
        group.State = "Playing";
        Command(group, group.Members, "Unpause", when, now, group.PositionTicks);
        Broadcast(group, State(group, reason), "StateUpdate");
    }

    private Group? GroupFor(string token) => _groups.Values.FirstOrDefault(g => g.Members.Contains(token));

    private void Command(Group group, IEnumerable<string> recipients, string command, DateTime when,
        DateTime emittedAt, long ticks)
    {
        var to = recipients.ToList();
        var entry = group.PlayingEntry ?? Guid.Empty;
        _emitted.Add(new EmittedCommand(_emitted.Count, group.Id, entry, command, when, emittedAt, ticks, to));
        var json = LiveSessionFixture.SyncCommand(group.Id, entry, command, Instant(when), Instant(emittedAt), ticks);
        foreach (var token in to)
            Send(token, json, command);
    }

    private void Broadcast(Group group, string json, string kind)
    {
        foreach (var member in group.Members)
            Send(member, json, kind);
    }

    /// <summary>Queues a frame on the session's newest open socket. Called under the lock, in
    /// decision order, and the connection's send gate releases in arrival order — so frames reach
    /// each member in the order the server decided them.</summary>
    private void Send(string token, string json, string kind)
    {
        var connection = Http.Connections.LastOrDefault(c => c.Token == token && c.IsOpen);
        _frames.Add(new Frame(_frames.Count, token, kind, connection is not null, DateTime.UtcNow));
        if (connection is null)
        {
            Diagnostic?.Invoke($"server: no open socket for session {ShortToken(token)}; {kind} undelivered");
            return;
        }
        _ = SendQuietlyAsync(connection, json);
    }

    private static async Task SendQuietlyAsync(LiveSessionFixture.ServerConnection connection, string json)
    {
        try
        {
            await connection.SendAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException
            or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private string State(Group group, string reason)
        => LiveSessionFixture.GroupUpdate(group.Id, "StateUpdate",
            JsonSerializer.Serialize(new { State = group.State, Reason = reason }));

    private static string QueuePayload(Group group, string reason) => JsonSerializer.Serialize(new
    {
        Reason = reason,
        LastUpdate = Instant(group.QueueStamp),
        Playlist = group.Queue.Select(e => new { ItemId = e.Item.ToString("N"), PlaylistItemId = e.Entry.ToString("N") })
            .ToArray(),
        PlayingItemIndex = group.PlayingIndex,
        StartPositionTicks = group.PositionTicks,
        IsPlaying = group.State == "Playing",
        ShuffleMode = "Sorted",
        RepeatMode = "RepeatNone",
    });

    // ---- helpers -------------------------------------------------------------------------

    internal static string ShortToken(string token) => token.Length <= 26 ? token : token[..26];

    private static string Instant(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private const string NoContent = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static string Json(object value)
    {
        var body = JsonSerializer.Serialize(value);
        return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    /// <summary>Kiota writes request bodies camelCase; the lookups below match case-insensitively.</summary>
    private static JsonDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Field(JsonDocument? body, string name)
    {
        if (body?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            return null;
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    internal static long? Long(JsonDocument? body, string name)
        => Field(body, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number)
            ? number : null;

    internal static string? String(JsonDocument? body, string name)
        => Field(body, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    internal static bool? Bool(JsonDocument? body, string name)
        => Field(body, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    private static List<Guid> Items(JsonDocument? body, string name)
        => Field(body, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(e => Guid.TryParse(e.GetString(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).ToList()
            : [];

    /// <summary>The body field of a recorded request, case-insensitively.</summary>
    internal static long? BodyLong(SyncRequest request, string name)
    {
        using var body = Parse(request.Body);
        return Long(body, name);
    }

    internal static string? BodyString(SyncRequest request, string name)
    {
        using var body = Parse(request.Body);
        return String(body, name);
    }

    internal static IReadOnlyList<Guid> BodyItems(SyncRequest request, string name)
    {
        using var body = Parse(request.Body);
        return Items(body, name);
    }

    internal static bool? BodyBool(SyncRequest request, string name)
    {
        using var body = Parse(request.Body);
        return Bool(body, name);
    }

    // ---- records -------------------------------------------------------------------------

    internal sealed record Episode(Guid Id, int Number, string Name, string File)
    {
        /// <summary>The overlay's second line for this episode, as <c>MediaItem.PlaybackSubtitle</c>
        /// builds it; the first line is the series name.</summary>
        public string Subtitle => $"S1E{Number}  ·  {Name}";

        /// <summary>The queue row's text, as <c>MediaItem.QueueDisplay</c> builds it.</summary>
        public string QueueDisplay => $"S1E{Number} · {Name}";
    }

    internal sealed record SyncRequest(int Index, string Token, string Route, string Body, DateTime At);

    internal sealed record Frame(int Index, string Token, string Kind, bool Delivered, DateTime At);

    internal sealed record EmittedCommand(int Index, Guid GroupId, Guid PlaylistItemId, string Command,
        DateTime When, DateTime EmittedAt, long PositionTicks, IReadOnlyList<string> Recipients);

    internal sealed record QueueEntry(Guid Item, Guid Entry);

    internal sealed record GroupView(Guid Id, string Name, string State, IReadOnlyList<string> Members,
        Guid? PlaylistItemId, long PositionTicks, DateTime LastActivity, IReadOnlyList<QueueEntry> Queue,
        int PlayingIndex, DateTime QueueStamp);

    private sealed class Group(Guid id, string name, DateTime created)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public List<string> Members { get; } = [];
        public HashSet<string> Ready { get; } = [];
        public List<(Guid Item, Guid Entry)> Queue { get; } = [];
        public int PlayingIndex { get; set; } = -1;
        public string State { get; set; } = "Idle";
        public long PositionTicks { get; set; }
        public DateTime LastActivity { get; set; } = created;
        public DateTime LastUpdatedAt { get; set; } = created;
        public DateTime QueueStamp { get; set; }
        public bool ResumeAfterWait { get; set; }

        public Guid? PlayingEntry => PlayingIndex >= 0 && PlayingIndex < Queue.Count ? Queue[PlayingIndex].Entry : null;

        /// <summary>Where the group is now. While playing that is the position at the last
        /// activity plus however long it has been running; a group that has not reached its
        /// scheduled start yet is still at the start.</summary>
        public long CurrentTicks(DateTime now)
            => State == "Playing" && now > LastActivity
                ? Math.Min(RuntimeTicks, PositionTicks + (now - LastActivity).Ticks) : PositionTicks;

        /// <summary>Participant NAMES, distinct: two sessions of one user are one name, which is
        /// why membership is asserted per session and never off this list.</summary>
        public object Info() => new
        {
            GroupId = Id,
            GroupName = Name,
            State,
            Participants = Members.Select(_ => UserName).Distinct().ToArray(),
            LastUpdatedAt = Instant(LastUpdatedAt),
        };

        public GroupView ToView() => new(Id, Name, State, [.. Members], PlayingEntry, PositionTicks, LastActivity,
            [.. Queue.Select(e => new QueueEntry(e.Item, e.Entry))], PlayingIndex, QueueStamp);
    }
}
