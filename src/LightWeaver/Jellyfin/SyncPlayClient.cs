using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Diagnostics;
using Microsoft.Kiota.Abstractions;

namespace LightWeaver.Jellyfin;

/// <summary>What the server lets this user do with groups (Jellyfin's
/// <c>UserPolicy.SyncPlayAccess</c>). <see cref="Unknown"/> is "not read yet", not a fourth server
/// value: denial arrives as a plain 403 with no socket message behind it, so a client that has not
/// asked cannot tell the difference between allowed and forbidden.</summary>
public enum SyncPlayAccessLevel
{
    Unknown,
    None,
    JoinGroups,
    CreateAndJoinGroups,
}

/// <summary>Something the user should be told about that is not a state change: another member
/// coming or going, or a join that failed.</summary>
public enum SyncPlayNoticeKind
{
    UserJoined,
    UserLeft,
    /// <summary>The group was gone by the time the join landed. The join FAILED.</summary>
    GroupDoesNotExist,
    /// <summary>The group is playing from a library this user cannot see. The join FAILED.</summary>
    LibraryAccessDenied,
    /// <summary>The socket came back too late to restore the session — past the server's 60 s
    /// <c>WebSocketLostTimeout</c>, membership is gone and this client has left locally.</summary>
    RejoinFailed,
    /// <summary>The server refused this client's SyncPlay requests. Either the user's
    /// <c>SyncPlayAccess</c> was taken away mid-session or the session is no longer in the
    /// group.</summary>
    AccessDenied,
}

/// <summary>A notice for the UI. <see cref="Text"/> is the user NAME for the two membership kinds
/// and null for the rest — the server sends a display name for those two and a group id, which is
/// not worth showing, for the others.</summary>
public sealed record SyncPlayNotice(SyncPlayNoticeKind Kind, string? Text = null);

/// <summary>One group as <c>GET /SyncPlay/List</c> describes it.</summary>
public sealed record SyncPlayGroupSummary(Guid GroupId, string GroupName, IReadOnlyList<string> Participants);

/// <summary>The three volatile facts about the local player a command has to be resolved against.
/// Supplied by a delegate rather than held here: the player belongs to the window, this does not,
/// and a position copied into this class would be one dispatcher hop stale by the time a command
/// arrived — against a 500 ms protocol tolerance.</summary>
public sealed record SyncPlayPlayerState(bool IsPaused, bool IsBuffering, double PositionSeconds)
{
    /// <summary>What a client with no player attached looks like: stopped, at the start. The
    /// scheduler's suppressions all read the right way round from here.</summary>
    public static readonly SyncPlayPlayerState Idle = new(IsPaused: true, IsBuffering: false, 0);
}

/// <summary>
/// One SyncPlay group membership, and everything the protocol needs around it: the ten REST calls
/// the MVP uses, the nine group updates the socket delivers, the monotonic queue guard, the
/// buffering debounce and the re-join that resynchronises after a dropped socket.
///
/// <para>One per APP rather than one per profile. A group binds to a server <c>SessionInfo</c> and
/// there is one mpv, so there is at most one group at a time; it is owned by the
/// <see cref="ProfileKey"/> that joined it, and joining on another profile leaves the first, which
/// is what the server would do anyway.</para>
///
/// <para>Framework-free on purpose, exactly like <see cref="SessionSocket"/>: every event is a
/// plain <c>Action</c> raised on whichever thread produced it — the socket's receive loop for the
/// group updates, a thread-pool thread for the debounce, the CALLER'S OWN thread for the
/// <see cref="GroupLeft"/> that <see cref="LeaveAsync"/> raises, and a thread-pool continuation
/// for the <see cref="Notice"/> and <see cref="GroupLeft"/> a refused run of POSTs or a failed
/// re-join produces. Marshalling belongs to the caller, and it has to be
/// <c>Dispatcher.BeginInvoke</c>: <see cref="SessionSocket.Dispose"/> joins the receive loop on
/// the UI thread for up to a second at logout and exit, so a blocking marshal out of a handler
/// here deadlocks against it.</para>
/// </summary>
public sealed class SyncPlayClient : IDisposable, ISyncPlayReporter
{
    /// <summary>How long a buffer has to last before the group is told about it. A
    /// <c>Buffering</c> report pauses EVERY member, so the ordinary cache blip at a seek or a
    /// keyframe must not cost the whole group a stall — and the server's own wait timeout is 30 s,
    /// so a quarter of a second of patience is free.</summary>
    private static readonly TimeSpan BufferingDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Consecutive 403s that end the membership. One is a race — a leave that crossed a
    /// command in flight — but a run of them means the server has stopped accepting this client,
    /// either because its <c>SyncPlayAccess</c> was revoked or because the session is no longer in
    /// the group, and there is no socket message for either.</summary>
    private const int MaxConsecutiveDenials = 3;

    private readonly LiveSessionService _live;
    private readonly Func<string, JellyfinService?> _resolveSession;
    private readonly object _lock = new();

    /// <summary>One-shot, re-armed per buffer. A <c>System.Threading.Timer</c> and not a
    /// <c>DispatcherTimer</c> for the same reason <c>SessionSocket._keepAlive</c> is one: a stall
    /// that is starving the UI thread is exactly when this has to fire.</summary>
    private readonly System.Threading.Timer _bufferingTimer;

    /// <summary>The membership, replaced wholesale rather than mutated field by field. Every write
    /// is under <see cref="_lock"/>; a read is one reference read, so a caller on another thread
    /// sees a group that was consistent at some recent moment instead of a half-updated one.</summary>
    private volatile Membership? _group;

    /// <summary>A join that has been POSTed and not yet confirmed. Membership starts at the
    /// socket's <c>GroupJoined</c> and nowhere else, so a <c>GroupDoesNotExist</c> or a
    /// <c>LibraryAccessDenied</c> — both of which mean the join FAILED — has no half-joined state
    /// to unwind.</summary>
    private volatile Pending? _pending;

    private ServerClock? _clock;
    private int _denials;

    /// <summary>Guards the re-join against a reconnect storm. The socket can raise
    /// <c>Connected</c> again while the previous restore is still in flight, and two restores would
    /// be two <c>POST /SyncPlay/Join</c> for one outage. Written under <see cref="_lock"/>, with
    /// <see cref="_rejoinAgain"/>, so the hand-back and the re-run request are one step.</summary>
    private bool _rejoining;

    /// <summary>Whether a reconnect arrived while a restore was in flight. It is NOT dropped: the
    /// socket the restore was started for is already gone, so the POST it is waiting on can only
    /// answer for a connection that no longer exists, and dropping it leaves the live socket
    /// holding a membership that was never resynchronised. One re-run covers any number of
    /// reconnects — they all want the same thing.</summary>
    private bool _rejoinAgain;

    /// <summary>Bumped every time the membership changes, so a REST call that spans a change can
    /// tell "still the group I started against" from "the same fields again". A leave that lands
    /// while a restore is in flight is the case it exists for: without it the POST re-joins this
    /// session server-side a round trip after the client left, and a member that is in the group
    /// and never reports <c>Ready</c> costs every other member the server's 30 s group wait on
    /// every resume.</summary>
    private long _membershipEpoch;

    /// <summary>Guards <see cref="JoinAsync"/> against being run twice at once — a double-clicked
    /// group row would otherwise be two <c>POST /SyncPlay/Join</c> and two <c>GroupJoined</c>, and
    /// at the player binding two loads of the same file. The same shape as
    /// <c>ItemDetailView.LoadVersionsAsync</c>'s <c>_versionsFetching</c>, interlocked rather than
    /// a plain flag because nothing pins this class to the UI thread.</summary>
    private int _joining;

    /// <summary>Whether a <c>Buffering</c> was actually sent, so the <c>Ready</c> that releases the
    /// group is only sent when the group was in fact stopped on this client's account.</summary>
    private bool _bufferingReported;
    // A newer, brief stall can supersede a queued Ready without sending another Buffering.
    // Keep the release obligation until a current Ready is actually dispatched.
    private bool _bufferingNeedsReady;
    private Task _bufferingReport = Task.CompletedTask;
    private long _bufferingCycle;
    private long _readySequence;

    /// <summary>Whether a debounce window is open. See <see cref="ReportBuffering"/>.</summary>
    private bool _bufferingPending;

    private bool _bufferingIsPlaying;
    private long _bufferingTicks;
    private bool _handlerFaultLogged;
    private bool _clockMissingLogged;
    private bool _disposed;

    /// <summary>The resolver is the app view model's warm-session lookup, the same one
    /// <see cref="LiveSessionService"/> takes: the REST calls have to go to the session that owns
    /// the group, which is not necessarily the active profile.</summary>
    public SyncPlayClient(LiveSessionService live, Func<string, JellyfinService?> resolveSession)
    {
        _live = live;
        _resolveSession = resolveSession;
        _bufferingTimer = new System.Threading.Timer(
            _ => _ = DetachedAsync(ReportBufferingAsync, "buffering"), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _live.SyncPlayGroupUpdated += OnGroupUpdate;
        _live.SyncPlayCommandReceived += OnCommand;
        _live.Connected += OnConnected;
        _live.AuthenticationRejected += OnAuthenticationRejected;
        _live.ProfileStopped += OnProfileStopped;
    }

    /// <summary>The group was joined and the server has confirmed it — INCLUDING a restore, where
    /// the group was already held and the re-join after a dropped socket asked for it back. The
    /// server answers a restore with the same <c>GroupJoined</c> it answers a first join with, and
    /// this client cannot tell them apart, so a subscriber that reloads the file on every one of
    /// these reloads it on every reconnect. <see cref="PlaylistItemId"/> and the queue guard are
    /// carried across a restore for exactly that reason: what changed is knowable from the
    /// <see cref="QueueChanged"/> that follows, not from here.</summary>
    public event Action<SyncPlayGroupInfo>? GroupJoined;

    /// <summary>Membership ended, for any reason. Carries the group that was left.</summary>
    public event Action<Guid>? GroupLeft;

    /// <summary>The group's queue changed, and the change was newer than the last one applied.</summary>
    public event Action<SyncPlayQueueUpdate>? QueueChanged;

    /// <summary>The group wants the player moved. Everything the caller needs is on the action;
    /// see <see cref="SyncPlayScheduler"/>.</summary>
    public event Action<SyncPlayAction>? ActionRequired;

    /// <summary>The group's state is now <c>Idle</c>, <c>Waiting</c>, <c>Paused</c> or
    /// <c>Playing</c>.</summary>
    public event Action<SyncPlayStateUpdate>? StateChanged;

    /// <summary>Something to tell the user that is not a state change. See
    /// <see cref="SyncPlayNoticeKind"/>.</summary>
    public event Action<SyncPlayNotice>? Notice;

    /// <summary>How the local player is doing. Set by the player binding; until then every command
    /// resolves against <see cref="SyncPlayPlayerState.Idle"/>, which is what a client with nothing
    /// playing honestly looks like.
    ///
    /// <para>CALLED SYNCHRONOUSLY ON THE SOCKET'S RECEIVE LOOP, once per command. It must not
    /// block and must not <c>Dispatcher.Invoke</c>: <see cref="SessionSocket.Dispose"/> joins that
    /// loop on the UI thread for up to a second at logout and exit, so a blocking marshal from
    /// here deadlocks against it — the same rule the class doc states for the events, and this is
    /// the one seam where it is a CALL rather than a raise. Read the player's own cached
    /// properties; do not ask mpv anything that waits.</para></summary>
    public Func<SyncPlayPlayerState>? ReadPlayer { get; set; }

    /// <summary>What the server lets this user do. Read with
    /// <see cref="RefreshAccessAsync"/> — denial is a 403 and never a message, so nothing else can
    /// discover it.</summary>
    public SyncPlayAccessLevel Access { get; private set; } = SyncPlayAccessLevel.Unknown;

    public bool IsInGroup => _group is not null;
    public bool IsMembershipPending => _pending is not null;
    public Guid? GroupId => _group?.GroupId;
    public string? GroupName => _group?.GroupName;
    public IReadOnlyList<string> Participants => _group?.Participants ?? [];

    /// <summary>The group's last known state, or null outside a group.</summary>
    public string? GroupState => _group?.State;

    /// <summary>The queue entry the group is playing, as the SERVER minted it. Not the item id and
    /// not stable across a new queue; every <c>Ready</c> and <c>Buffering</c> echoes it.</summary>
    public Guid? PlaylistItemId => _group?.PlaylistItemId;

    /// <summary>When this client joined, in SERVER UTC. A command emitted before it was aimed at a
    /// group this client was not in.</summary>
    public DateTime JoinedAtUtc => _group?.JoinedAtUtc ?? default;

    /// <summary>The profile whose session owns the group, or null outside one.</summary>
    public string? ProfileKey => _group?.ProfileKey ?? _pending?.ProfileKey;

    /// <summary>The last command that was acted on. The player binding needs it to build a
    /// <see cref="SyncPlayState"/> for <see cref="SyncPlayScheduler.ResolveDrift"/>.</summary>
    public SyncPlayCommandMessage? LastApplied => _group?.LastApplied;

    /// <summary>The clock every instant in this protocol is expressed against, or null outside a
    /// group. Created on join, because it polls and pings and there is nothing to poll for
    /// otherwise.</summary>
    public ServerClock? Clock => _clock;

    /// <summary>Reads the user's SyncPlay level off their policy. The one way to know: there is no
    /// <c>JoinGroupDenied</c> message in 12.0 — jellyfin-web still handles one, which is stale —
    /// and a client that simply tried would get a bare 403 with no way to phrase it.</summary>
    public async Task<SyncPlayAccessLevel> RefreshAccessAsync(string profileKey)
    {
        if (_resolveSession(profileKey) is not { IsConnected: true } service)
            return Access;
        try
        {
            var me = await service.Client.Users.Me.GetAsync().ConfigureAwait(false);
            Access = me?.Policy?.SyncPlayAccess switch
            {
                UserPolicy_SyncPlayAccess.CreateAndJoinGroups => SyncPlayAccessLevel.CreateAndJoinGroups,
                UserPolicy_SyncPlayAccess.JoinGroups => SyncPlayAccessLevel.JoinGroups,
                UserPolicy_SyncPlayAccess.None => SyncPlayAccessLevel.None,
                // Absent rather than one of the three: an older server, or a policy the SDK does
                // not model. Unknown and not None — refusing the feature on a field that was simply
                // missing would be the worse guess.
                _ => SyncPlayAccessLevel.Unknown,
            };
            AppLog.Detail("syncplay", $"event=access outcome=read level={Access}");
            return Access;
        }
        catch (Exception ex)
        {
            // By type, never by message: an SDK failure carries the request URL.
            AppLog.Detail("syncplay", $"event=access outcome=failure error={ex.GetType().Name}");
            return Access;
        }
    }

    /// <summary>The groups this user could join. Empty on any failure — the caller's answer to
    /// "there are no groups" and "the server would not say" is the same empty list.</summary>
    public async Task<IReadOnlyList<SyncPlayGroupSummary>> ListGroupsAsync(string profileKey, bool throwOnFailure = false)
    {
        if (_resolveSession(profileKey) is not { IsConnected: true } service)
            return [];
        try
        {
            var groups = await service.Client.SyncPlay.List.GetAsync().ConfigureAwait(false);
            var summaries = new List<SyncPlayGroupSummary>();
            foreach (var group in groups ?? [])
                if (group.GroupId is { } id && id != Guid.Empty)
                    summaries.Add(new SyncPlayGroupSummary(id, group.GroupName ?? "",
                        group.Participants is { } names ? [.. names] : []));
            AppLog.Detail("syncplay", FormattableString.Invariant(
                $"event=list outcome=success groups={summaries.Count}"));
            return summaries;
        }
        catch (Exception ex)
        {
            AppLog.Detail("syncplay", $"event=list outcome=failure error={ex.GetType().Name}");
            if (throwOnFailure) throw new InvalidOperationException("Could not list SyncPlay groups.");
            return [];
        }
    }

    /// <summary>Joins a group. True only means the server accepted the POST: membership starts when
    /// the socket delivers <c>GroupJoined</c>, and the server answers the same 204 to a join it is
    /// about to refuse with <c>GroupDoesNotExist</c> or <c>LibraryAccessDenied</c>.
    ///
    /// <para>A group already held is left first, whichever profile owns it. There is one mpv, so
    /// two groups could only mean one of them being silently ignored.</para>
    ///
    /// <para>One at a time: a second call while the first is in flight returns false rather than
    /// posting again.</para></summary>
    public async Task<bool> JoinAsync(string profileKey, Guid groupId)
    {
        if (_disposed || groupId == Guid.Empty)
            return false;
        if (_resolveSession(profileKey) is not { IsConnected: true } service)
        {
            AppLog.Detail("syncplay", $"event=join outcome=noop reason=no_session session={AppLog.ShortHash(profileKey)}");
            return false;
        }
        // A double-clicked group row is two joins one dispatcher hop apart: two POSTs, two
        // GroupJoined raises, and at the player binding the same file loaded twice.
        if (Interlocked.CompareExchange(ref _joining, 1, 0) != 0)
        {
            AppLog.Detail("syncplay", "event=join outcome=noop reason=in_flight");
            return false;
        }
        try
        {
            if (_group is { } held && (held.GroupId != groupId || !ProfileMatches(held.ProfileKey, profileKey)))
                await LeaveAsync().ConfigureAwait(false);

            Interlocked.Exchange(ref _denials, 0);
            _pending = new Pending(profileKey, groupId);
            // The clock before the join, not after it: the group's first command can arrive on the
            // socket before this method has returned, and resolving it against an unmeasured clock
            // means resolving it against an offset of zero by fiat.
            if (EnsureClock(service) is not { } clock)
                return false;
            await ResyncAsync(clock).ConfigureAwait(false);
            var sent = await PostAsync("join",
                token => service.Client.SyncPlay.Join.PostAsync(new JoinGroupRequestDto { GroupId = groupId },
                    cancellationToken: token)).ConfigureAwait(false);
            if (!sent && _pending is { } pending && pending.GroupId == groupId)
                _pending = null;
            return sent;
        }
        finally
        {
            Volatile.Write(ref _joining, 0);
        }
    }

    /// <summary>Leaves the group, locally whatever the server says. The POST is best-effort — a
    /// server that cannot be reached must not be able to hold this client in a group it has left,
    /// and the server drops the membership with the session anyway.</summary>
    public async Task LeaveAsync()
    {
        var profileKey = ProfileKey;
        if (profileKey is not null && _resolveSession(profileKey) is { IsConnected: true } service)
            await PostAsync("leave",
                token => service.Client.SyncPlay.Leave.PostAsync(cancellationToken: token)).ConfigureAwait(false);
        ClearGroup("left");
    }

    /// <summary>Creates a group; membership still starts only at the socket's GroupJoined.</summary>
    public async Task<bool> CreateAsync(string profileKey, string name)
    {
        if (_disposed || string.IsNullOrWhiteSpace(name)
            || _resolveSession(profileKey) is not { IsConnected: true } service
            || Interlocked.CompareExchange(ref _joining, 1, 0) != 0) return false;
        try
        {
            if (_group is not null) await LeaveAsync().ConfigureAwait(false);
            _pending = new Pending(profileKey, Guid.Empty);
            Interlocked.Exchange(ref _denials, 0);
            if (EnsureClock(service) is not { } clock) return false;
            await ResyncAsync(clock).ConfigureAwait(false);
            var sent = await PostAsync("create", token => service.Client.SyncPlay.New.PostAsync(
                new NewGroupRequestDto { GroupName = name.Trim() }, cancellationToken: token)).ConfigureAwait(false);
            if (!sent && _pending is { GroupId: var id } && id == Guid.Empty) _pending = null;
            return sent;
        }
        finally { Volatile.Write(ref _joining, 0); }
    }

    public Task<bool> SetNewQueueAsync(IReadOnlyList<Guid> items, int position = 0, long ticks = 0)
    {
        if (items.Count == 0 || position < 0 || position >= items.Count || items.Any(id => id == Guid.Empty))
            return Task.FromResult(false);
        return PostQueueAsync("set_queue", (service, token) => service.Client.SyncPlay.SetNewQueue.PostAsync(
            new PlayRequestDto { PlayingQueue = items.Select(id => (Guid?)id).ToList(), PlayingItemPosition = position,
                StartPositionTicks = Math.Max(0, ticks) }, cancellationToken: token));
    }

    public Task<bool> NextItemAsync() => PlaylistItemId is { } id
        ? NextItemAsync(id) : Task.FromResult(false);

    public Task<bool> NextItemAsync(Guid expectedPlaylistItem)
    {
        Membership group;
        lock (_lock)
        {
            if (_group is not { } current || current.PlaylistItemId != expectedPlaylistItem)
                return Task.FromResult(false);
            group = current;
        }
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        return PostAsync("next", token => service.Client.SyncPlay.NextItem.PostAsync(
            new NextItemRequestDto { PlaylistItemId = expectedPlaylistItem }, cancellationToken: token));
    }

    public Task<bool> PreviousItemAsync() => PlaylistItemId is { } id
        ? PostQueueAsync("previous", (service, token) => service.Client.SyncPlay.PreviousItem.PostAsync(
            new PreviousItemRequestDto { PlaylistItemId = id }, cancellationToken: token)) : Task.FromResult(false);

    public Task<bool> SetPlaylistItemAsync(Guid id) => id != Guid.Empty
        ? PostQueueAsync("select", (service, token) => service.Client.SyncPlay.SetPlaylistItem.PostAsync(
            new SetPlaylistItemRequestDto { PlaylistItemId = id }, cancellationToken: token)) : Task.FromResult(false);

    public Task<bool> RemovePlaylistItemAsync(Guid id) => id != Guid.Empty
        ? PostQueueAsync("remove", (service, token) => service.Client.SyncPlay.RemoveFromPlaylist.PostAsync(
            new RemoveFromPlaylistRequestDto { PlaylistItemIds = [id], ClearPlayingItem = true,
                ClearPlaylist = false }, cancellationToken: token)) : Task.FromResult(false);

    private Task<bool> PostQueueAsync(string verb, Func<JellyfinService, CancellationToken, Task> post)
    {
        if (_group is not { } group || _resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        return PostAsync(verb, token => post(service, token));
    }

    /// <summary>Asks the GROUP to pause. The server broadcasts a <c>Pause</c> command to every
    /// member, this client included, and the local player moves when that echo arrives and not
    /// before — which is the whole point of routing local transport through the group: a member
    /// that paused itself first would be a round trip ahead of everybody else, and the group has no
    /// way to know it happened.
    ///
    /// <para>False is "the group was not asked": no membership, no session, or a server that
    /// refused or could not be reached. The caller shows that; there is nothing else to see,
    /// because a request the group never took up produces no echo either.</para></summary>
    public Task<bool> PauseAsync()
    {
        if (_group is not { } group)
            return Task.FromResult(false);
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        return PostAsync("pause", token => service.Client.SyncPlay.Pause.PostAsync(cancellationToken: token));
    }

    /// <summary>Asks the group to resume. The server does not answer this with "playing now": it
    /// picks an instant a little way into the future, tells every member to be at the group's
    /// position by then, and holds the group in <c>Waiting</c> until the slowest one says it is
    /// ready. So the local player stays exactly where it is until the echo, and then waits again.
    ///
    /// <para>False is "the group was not asked", as <see cref="PauseAsync"/>.</para></summary>
    public Task<bool> UnpauseAsync()
    {
        if (_group is not { } group)
            return Task.FromResult(false);
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        return PostAsync("unpause", token => service.Client.SyncPlay.Unpause.PostAsync(cancellationToken: token));
    }

    /// <summary>Asks the group to seek. The server moves EVERY member to
    /// <paramref name="positionTicks"/> and parks the group in <c>Waiting</c> until each one has
    /// reported it landed, so a scrub here is a scrub for everyone — and the local player does not
    /// move off its own slider until the command comes back.
    ///
    /// <para>Absolute, because the protocol has no relative seek: the group cannot carry a delta,
    /// only a position, and the caller is the only thing that knows what the delta was relative
    /// to.</para>
    ///
    /// <para>False is "the group was not asked", as <see cref="PauseAsync"/>.</para></summary>
    public Task<bool> SeekAsync(long positionTicks)
    {
        if (_group is not { } group)
            return Task.FromResult(false);
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        // The target, because nothing else records it: the echo that follows carries the position
        // the group RESOLVED to, which is what a mis-clamped or mis-converted request looks like
        // from the outside once the server has had it.
        AppLog.Detail("syncplay", FormattableString.Invariant(
            $"event=seek outcome=requested ticks={positionTicks}"));
        return PostAsync("seek", token => service.Client.SyncPlay.Seek.PostAsync(
            new SeekRequestDto { PositionTicks = positionTicks }, cancellationToken: token));
    }

    /// <summary>Asks the group to stop. The server sends every member a <c>Stop</c> and leaves them
    /// all in the group with nothing playing, so this ends the group's playback rather than this
    /// client's membership — leaving is <see cref="LeaveAsync"/>, and it is a different thing.
    ///
    /// <para>False is "the group was not asked", as <see cref="PauseAsync"/>.</para></summary>
    public Task<bool> StopAsync()
    {
        if (_group is not { } group)
            return Task.FromResult(false);
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return Task.FromResult(false);
        return PostAsync("stop", token => service.Client.SyncPlay.Stop.PostAsync(cancellationToken: token));
    }

    /// <summary>Tells the group this client is in position. Sent after a seek lands and after a
    /// buffer clears; the server holds the whole group in <c>Waiting</c> until every member has.
    ///
    /// <para><c>When</c> is this client's own instant converted TO SERVER TIME. The server computes
    /// <c>UtcNow - When</c> and, past 2000 ms either way, silently treats the elapsed time as zero
    /// and logs that the client is "not time syncing properly" — nothing reaches the client at all.
    /// So sending a local instant on a machine whose clock is a minute out degrades every member's
    /// sync with no error anywhere.</para></summary>
    public SyncPlayReportContext? ReportContext
    {
        get { lock (_lock) return CurrentReportContext; }
    }

    private SyncPlayReportContext? CurrentReportContext => _group is { } group && !_disposed
        ? new(_membershipEpoch, group.GroupId, group.PlaylistItemId) : null;

    public Task ReportReadyAsync(bool isPlaying, long positionTicks, SyncPlayReportContext? expectedContext = null)
    {
        Func<Task>? send;
        lock (_lock)
        {
            if (expectedContext is { } expected && expected != CurrentReportContext)
                return Task.CompletedTask;
            send = PrepareReadyLocked(isPlaying, positionTicks);
        }
        return send?.Invoke() ?? Task.CompletedTask;
    }

    private Func<Task>? PrepareReadyLocked(bool isPlaying, long positionTicks)
    {
        if (_group is not { } group || _disposed
            || _resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return null;
        var epoch = _membershipEpoch;
        var bufferingCycle = _bufferingCycle;
        var readySequence = ++_readySequence;
        var report = _bufferingReport;
        // Position and its timestamp describe the same observation, even when Buffering's
        // response holds the Ready request for a while.
        var when = ServerInstant();
        return SendAsync;

        async Task SendAsync()
        {
            await report.ConfigureAwait(false);
            Task sending;
            lock (_lock)
            {
                if (_disposed || _membershipEpoch != epoch || _bufferingCycle != bufferingCycle
                    || _readySequence != readySequence
                    || _group is not { } current
                    || current.GroupId != group.GroupId || current.PlaylistItemId != group.PlaylistItemId)
                    return;
                _bufferingNeedsReady = false;
                sending = StartReport(() => service.Client.SyncPlay.Ready.PostAsync(new ReadyRequestDto
                {
                    PlaylistItemId = group.PlaylistItemId ?? Guid.Empty,
                    PositionTicks = positionTicks,
                    IsPlaying = isPlaying,
                    When = when,
                }, cancellationToken: CancellationToken.None));
            }
            await PostAsync("ready", _ => sending).ConfigureAwait(false);
        }
    }

    /// <summary>The player started or stopped buffering. Debounced by
    /// <see cref="BufferingDebounce"/>, because the report pauses the WHOLE group: a cache blip
    /// that clears in 80 ms would otherwise stop four other people for a round trip each way.
    ///
    /// <para>Clearing a buffer that WAS reported sends the <c>Ready</c> that releases the group.
    /// Nothing else would: the server parks the group in <c>Waiting</c> on a buffering report and
    /// waits for every member to say it is back.</para></summary>
    public bool ReportBuffering(bool buffering, long positionTicks, bool isPlaying, bool ready = true,
        SyncPlayReportContext? expectedContext = null)
    {
        bool release;
        Func<Task>? releasing = null;
        lock (_lock)
        {
            if (_disposed || _group is null
                || (expectedContext is { } expected && expected != CurrentReportContext))
                return false;
            if (buffering)
            {
                _bufferingTicks = positionTicks;
                _bufferingIsPlaying = isPlaying;
                // Armed on the EDGE. A caller that signals a continuing stall repeatedly — which is
                // what a poll-driven one does — would otherwise reset the window on every call, so
                // the debounce would never expire and the group would never learn about a stall
                // that never ends. Not re-arming after a report has gone out is the other half:
                // one POST per stall, because each one pauses every other member.
                if (!_bufferingPending && !_bufferingReported)
                {
                    _bufferingCycle++;
                    _bufferingPending = true;
                    Arm(BufferingDebounce);
                }
                return false;
            }
            Arm(Timeout.InfiniteTimeSpan);
            release = _bufferingNeedsReady && ready;
            _bufferingReported = false;
            _bufferingPending = false;
            _bufferingTicks = positionTicks;
            if (release)
                releasing = PrepareReadyLocked(isPlaying, positionTicks);
        }
        if (releasing is not null)
            _ = DetachedAsync(releasing, "ready");
        else
            AppLog.Detail("syncplay", "event=buffering outcome=debounced");
        return release;
    }

    public void Dispose()
    {
        ServerClock? clock;
        // Under the lock, with the clock taken out in the same step: EnsureClock re-checks
        // _disposed under this lock, so between them a join that got past the check at the top of
        // JoinAsync cannot leave a fresh ServerClock behind, polling and pinging for the life of
        // the process with nothing left holding a reference to stop it.
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            clock = _clock;
            _clock = null;
            _group = null;
            _pending = null;
        }
        _live.SyncPlayGroupUpdated -= OnGroupUpdate;
        _live.SyncPlayCommandReceived -= OnCommand;
        _live.Connected -= OnConnected;
        _live.AuthenticationRejected -= OnAuthenticationRejected;
        _live.ProfileStopped -= OnProfileStopped;
        _bufferingTimer.Dispose();
        // Outside the lock, for the reason ClearGroup gives: Dispose cancels an in-flight
        // measurement, and the poll loop has no business unwinding under a lock the socket thread
        // also takes.
        clock?.Dispose();
    }

    /// <summary>The socket came back. Re-joining the group this client already holds is the
    /// protocol's own resynchronisation primitive — <c>SyncPlayManager.JoinGroup</c> has an
    /// explicit "restore session" branch — and it is the only way to be sent the group's current
    /// queue and command again after missing however many of them the outage cost.
    ///
    /// <para>Server-side membership is keyed to the <c>SessionInfo</c> and survives a dropped
    /// socket until the 60 s <c>WebSocketLostTimeout</c>. Past that the re-join legitimately fails,
    /// and the honest answer is to leave locally and say so rather than sit in a group the server
    /// has forgotten.</para></summary>
    private void OnConnected(string profileKey)
    {
        if (_group is not { } group || !ProfileMatches(group.ProfileKey, profileKey))
            return;
        lock (_lock)
        {
            if (_rejoining)
            {
                _rejoinAgain = true;
                return;
            }
            _rejoining = true;
        }
        _ = RejoinAsync(profileKey);
    }

    /// <summary>The restore, and the re-run the guard owes a reconnect that arrived while one was
    /// in flight. Dropping that reconnect instead — a socket that dies and returns inside one POST
    /// round trip does exactly that — would leave the LIVE socket holding a membership that was
    /// never resynchronised, with nothing left to notice: per the group lifecycle's own rule an
    /// idle client sends nothing, so the next edge might never come.</summary>
    private async Task RejoinAsync(string profileKey)
    {
        do
        {
            try
            {
                // The membership as of NOW, on every pass. The re-run exists because a reconnect
                // landed inside a restore, and that window is wide enough for the user to leave
                // the group and join another: a re-run posting the group the FIRST reconnect saw
                // would ask the server for the old one, which server-side LEAVES the one this
                // client holds — and the GroupJoined answering it names a group nothing here is
                // waiting for, so it is dropped. Client in no group, server in the old one, and
                // nothing left that would ever notice.
                if (_group is { } group && ProfileMatches(group.ProfileKey, profileKey))
                    await RestoreAsync(group).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing may escape: the guard has to be handed back on the way out or no later
                // reconnect ever re-joins, and nobody awaits this task, so a throw would reach the
                // unobserved-task handler and spend a crash-retention slot. By type, never by
                // message — an SDK failure carries the request URL.
                AppLog.Detail("syncplay", $"event=rejoin outcome=failure error={ex.GetType().Name}");
            }
        }
        while (TakeRejoinRerun());
    }

    private async Task RestoreAsync(Membership group)
    {
        if (_resolveSession(group.ProfileKey) is not { IsConnected: true } service)
            return;
        // The offset first. A machine that lost its socket may have been asleep, and every
        // instant this client is about to send or receive is expressed against the offset.
        if (EnsureClock(service) is not { } clock)
            return;
        await ResyncAsync(clock).ConfigureAwait(false);
        long epoch;
        lock (_lock)
        {
            // The SAME group, not merely a group: the resync above is two round trips, and a leave
            // and a fresh join inside them would otherwise have this POST ask for the group the
            // user left — which is the server leaving the one this client now holds.
            if (_group is not { } current || current.GroupId != group.GroupId)
            {
                AppLog.Detail("syncplay", "event=rejoin outcome=noop reason=group_changed");
                return;
            }
            epoch = _membershipEpoch;
        }
        var restored = await PostAsync("rejoin",
            token => service.Client.SyncPlay.Join.PostAsync(new JoinGroupRequestDto { GroupId = group.GroupId },
                cancellationToken: token)).ConfigureAwait(false);
        if (!restored)
        {
            // Only against the membership this restore was started for. A leave and a fresh join
            // inside the POST's round trip would otherwise have their new group torn down by an
            // answer about the old one.
            if (Holds(epoch))
            {
                Raise(Notice, new SyncPlayNotice(SyncPlayNoticeKind.RejoinFailed), "rejoin_failed");
                ClearGroup("rejoin_failed");
            }
            return;
        }
        if (!LeftSince(epoch))
            return;
        // The leave won locally and the restore won on the server: this session is back in the
        // group a round trip after the user left it, as a member that will never report Ready —
        // which is the server's 30 s group wait, for everyone, on every resume. Undo it.
        AppLog.Info("syncplay", "event=rejoin outcome=undone reason=left");
        await PostAsync("leave",
            token => service.Client.SyncPlay.Leave.PostAsync(cancellationToken: token)).ConfigureAwait(false);
    }

    /// <summary>Takes the re-run flag, or hands the guard back. One step under the lock: a
    /// reconnect landing between a separate read and clear would set a flag nobody reads again,
    /// which is the dropped reconnect this exists to prevent.</summary>
    private bool TakeRejoinRerun()
    {
        lock (_lock)
        {
            if (_rejoinAgain)
            {
                _rejoinAgain = false;
                return true;
            }
            _rejoining = false;
            return false;
        }
    }

    /// <summary>Whether the membership a REST call was started against is still the one held.</summary>
    private bool Holds(long epoch)
    {
        lock (_lock)
            return _group is not null && _membershipEpoch == epoch;
    }

    /// <summary>Whether the membership a REST call was started against ended while it was in
    /// flight AND nothing replaced it. A group joined in the meantime is deliberately not this
    /// case: undoing it would leave the group the user has just joined.
    ///
    /// <para>A join that has been POSTed and not yet confirmed counts as replacing it. Membership
    /// starts at <c>GroupJoined</c>, so between the POST and the socket's answer there is no group
    /// held — and a corrective <c>Leave</c> sent in that gap lands AFTER the join it knows nothing
    /// about, taking the user straight back out of the group they just joined, silently.</para></summary>
    private bool LeftSince(long epoch)
    {
        lock (_lock)
            return _group is null && _pending is null && _membershipEpoch != epoch;
    }

    /// <summary>The nine group updates. Four are mandatory — <c>GroupJoined</c>, <c>GroupLeft</c>,
    /// <c>NotInGroup</c> and <c>PlayQueue</c> — <c>StateUpdate</c> feeds the UI, and the remaining
    /// four are notices, two of which mean a join FAILED.</summary>
    private void OnGroupUpdate(string profileKey, SyncPlayGroupUpdateMessage update)
    {
        if (_disposed || !Owns(profileKey, update.GroupId, update.Type))
            return;
        switch (update.Type)
        {
            case SyncPlayGroupUpdateTypes.GroupJoined:
                ApplyJoin(profileKey, update);
                break;
            case SyncPlayGroupUpdateTypes.GroupLeft:
            case SyncPlayGroupUpdateTypes.NotInGroup:
                ClearGroup(update.Type);
                break;
            case SyncPlayGroupUpdateTypes.PlayQueue:
                ApplyQueue(update);
                break;
            case SyncPlayGroupUpdateTypes.StateUpdate:
                ApplyState(update);
                break;
            case SyncPlayGroupUpdateTypes.UserJoined:
                ApplyParticipant(update.Text(), joined: true);
                break;
            case SyncPlayGroupUpdateTypes.UserLeft:
                ApplyParticipant(update.Text(), joined: false);
                break;
            case SyncPlayGroupUpdateTypes.GroupDoesNotExist:
                FailJoin(SyncPlayNoticeKind.GroupDoesNotExist);
                break;
            case SyncPlayGroupUpdateTypes.LibraryAccessDenied:
                FailJoin(SyncPlayNoticeKind.LibraryAccessDenied);
                break;
            default:
                AppLog.Detail("syncplay", "event=group_update outcome=ignored");
                break;
        }
    }

    private void ApplyJoin(string profileKey, SyncPlayGroupUpdateMessage update)
    {
        if (update.GroupInfo() is not { GroupId: { } id } info || id == Guid.Empty)
        {
            AppLog.Detail("syncplay", "event=join outcome=unreadable");
            return;
        }
        Membership group;
        lock (_lock)
        {
            // Carried across a RESTORE and dropped on a genuine join: the queue guard is scoped to
            // a group, and a new group's first update would otherwise be compared against an
            // instant from the last one.
            var previous = _group is { } existing && existing.GroupId == id ? existing : null;
            group = new Membership(profileKey, id, info.GroupName ?? "",
                info.Participants is { } names ? [.. names] : [],
                info.State ?? previous?.State,
                // The instant of THIS join, restore or not: it is what the stale-command rule is
                // measured against, and after an outage the commands worth acting on are the ones
                // emitted since the session came back.
                ServerNow(),
                previous?.PlaylistItemId,
                previous?.LastQueueUpdate,
                previous?.LastApplied);
            _group = group;
            _membershipEpoch++;
            ResetBufferingLocked();
        }
        _pending = null;
        Interlocked.Exchange(ref _denials, 0);
        AppLog.Info("syncplay", FormattableString.Invariant(
            $"event=join outcome=success group={AppLog.ShortHash(id.ToString())} participants={group.Participants.Count}"));
        Raise(GroupJoined, info, SyncPlayGroupUpdateTypes.GroupJoined);
        if (info.State is { Length: > 0 } state)
            Raise(StateChanged, new SyncPlayStateUpdate(state, SyncPlayGroupUpdateTypes.GroupJoined),
                SyncPlayGroupUpdateTypes.StateUpdate);
    }

    /// <summary><c>LastUpdate</c> is a monotonic guard, and OLDER OR EQUAL is dropped. Equal
    /// matters as much as older: the server re-sends the current queue on a restore and on every
    /// membership change, so an equal update is the same queue arriving again — applying it would
    /// re-raise <see cref="QueueChanged"/> and, at the player binding, reload the file the group is
    /// already watching.</summary>
    private void ApplyQueue(SyncPlayGroupUpdateMessage update)
    {
        if (update.Queue() is not { } queue)
        {
            AppLog.Detail("syncplay", "event=queue outcome=unreadable");
            return;
        }
        Membership updated;
        lock (_lock)
        {
            if (_group is not { } group)
                return;
            if (queue.LastUpdate is { } stamp && group.LastQueueUpdate is { } applied && stamp <= applied)
            {
                AppLog.Detail("syncplay", "event=queue outcome=stale");
                return;
            }
            updated = group with
            {
                PlaylistItemId = PlayingEntry(queue),
                LastQueueUpdate = queue.LastUpdate ?? group.LastQueueUpdate,
            };
            if (updated.PlaylistItemId != group.PlaylistItemId) ResetBufferingLocked();
            _group = updated;
        }
        AppLog.Detail("syncplay", FormattableString.Invariant(
            $"event=queue outcome=applied reason={queue.Reason ?? "none"} entries={queue.Playlist?.Count ?? 0}"));
        Raise(QueueChanged, queue, SyncPlayGroupUpdateTypes.PlayQueue);
    }

    private void ApplyState(SyncPlayGroupUpdateMessage update)
    {
        if (update.State() is not { } state)
        {
            AppLog.Detail("syncplay", "event=state outcome=unreadable");
            return;
        }
        lock (_lock)
        {
            if (_group is not { } group)
                return;
            _group = group with { State = state.State ?? group.State };
        }
        AppLog.Detail("syncplay", $"event=state outcome=applied state={state.State ?? "none"}");
        Raise(StateChanged, state, SyncPlayGroupUpdateTypes.StateUpdate);
    }

    /// <summary>The membership list is kept in step here rather than waiting for the server to
    /// re-describe the group: <c>UserJoined</c> and <c>UserLeft</c> carry a display name and
    /// nothing else, and no second <c>GroupJoined</c> follows them.</summary>
    private void ApplyParticipant(string? name, bool joined)
    {
        if (name is not { Length: > 0 })
            return;
        lock (_lock)
        {
            if (_group is not { } group)
                return;
            var names = new List<string>(group.Participants);
            if (joined)
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
            else
            {
                names.RemoveAll(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
            }
            _group = group with { Participants = names };
        }
        Raise(Notice, new SyncPlayNotice(joined ? SyncPlayNoticeKind.UserJoined : SyncPlayNoticeKind.UserLeft, name),
            joined ? SyncPlayGroupUpdateTypes.UserJoined : SyncPlayGroupUpdateTypes.UserLeft);
    }

    /// <summary>A refused join. The pending join is dropped and NOTHING is joined: these two
    /// updates are the server's answer to the <c>Join</c> POST it already acknowledged, so a client
    /// that treated them as an ordinary notice would sit in a group it is not in, echoing a
    /// playlist item id it was never given.
    ///
    /// <para>A membership HELD when one of these lands is the same failure wearing the restore's
    /// clothes. <c>SyncPlayManager.JoinGroup</c> is the only thing that emits either update, and
    /// the restore posts the same <c>Join</c> — which the server answers 204 whether it is about
    /// to honour it or not — so a refusal arriving against a held group says that the group this
    /// client thinks it is in is gone, or is playing from a library it may no longer see. Keeping
    /// it would keep the group id, the playlist item id and a polling clock for a group that does
    /// not exist, and per the lifecycle's own rule an idle client sends nothing, so the
    /// <c>NotInGroup</c> that would eventually correct it may never come.</para></summary>
    private void FailJoin(SyncPlayNoticeKind kind)
    {
        _pending = null;
        AppLog.Info("syncplay", $"event=join outcome=refused reason={kind}");
        Raise(Notice, new SyncPlayNotice(kind), kind.ToString());
        // After the notice, so the UI has the reason before it is told the group is gone — the
        // same order the failed re-join uses. A no-op when nothing was held, which is the ordinary
        // refused-first-join path: membership starts at GroupJoined and nowhere else.
        ClearGroup(kind.ToString());
    }

    private void OnCommand(string profileKey, SyncPlayCommandMessage command)
    {
        if (_disposed || _group is not { } group
            || !ProfileMatches(group.ProfileKey, profileKey)
            || (command.GroupId is { } id && id != Guid.Empty && id != group.GroupId))
            return;
        var player = ReadLocalPlayer();
        var state = new SyncPlayState(group.JoinedAtUtc, group.PlaylistItemId, player.IsPaused,
            player.IsBuffering, group.LastApplied);
        var action = SyncPlayScheduler.Resolve(command, state, _clock?.Offset ?? TimeSpan.Zero,
            DateTime.UtcNow, player.PositionSeconds);
        if (action.Kind == SyncPlayActionKind.Noop)
        {
            AppLog.Detail("syncplay", $"event=command outcome=noop kind={command.Command ?? "none"}");
            return;
        }
        lock (_lock)
        {
            if (_group is not { } current || current.GroupId != group.GroupId)
                return;
            _group = current with { LastApplied = command };
        }
        AppLog.Detail("syncplay", FormattableString.Invariant(
            $"event=command outcome=resolved kind={action.Kind} target_s={action.TargetSeconds:0.###} seek={action.NeedsSeek}"));
        Raise(ActionRequired, action, command.Command ?? "command");
    }

    /// <summary>The debounce expired, so the buffer is real. This is the call that pauses every
    /// other member of the group.
    ///
    /// <para>Everything it was armed with is re-read under the lock, because by the time this runs
    /// the timer has already been let go: <see cref="ReportBuffering"/> with <c>false</c> cancels
    /// the window, but a <c>Change</c> that lost the race has dispatched this callback anyway. The
    /// clear takes the lock first, sees no report has gone out, sends no <c>Ready</c> — and an
    /// unconditional callback would then report a buffer that is over, parking the whole group in
    /// <c>Waiting</c> with no edge left to release it: an edge-driven caller produces no further
    /// edge, and what ends it is the server's 30 s group wait, for every member. The membership is
    /// re-read for the wider form of the same thing — a <c>ClearGroup</c> past the check at the top
    /// would leave <c>_bufferingReported</c> true across a membership boundary, and the next
    /// group's first buffer-clear would send a <c>Ready</c> for a <c>Buffering</c> nobody
    /// sent.</para></summary>
    private async Task ReportBufferingAsync()
    {
        if (_group is not { } group
            || _resolveSession(group.ProfileKey) is not { IsConnected: true } service)
        {
            // The window closes whatever happens. It is armed on the EDGE, so a pending flag left
            // standing is a stall that can never arm it again until the buffer has cleared first.
            lock (_lock)
                _bufferingPending = false;
            AppLog.Detail("syncplay", "event=buffering outcome=noop reason=no_session");
            return;
        }
        Task previous;
        long epoch;
        long cycle;
        Guid? item;
        long ticks;
        bool isPlaying;
        DateTimeOffset when;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_bufferingPending || _group is not { } current || current.GroupId != group.GroupId)
            {
                _bufferingPending = false;
                AppLog.Detail("syncplay", "event=buffering outcome=cancelled");
                return;
            }
            _bufferingReported = true;
            _bufferingNeedsReady = true;
            previous = _bufferingReport;
            _bufferingReport = completion.Task;
            _bufferingPending = false;
            epoch = _membershipEpoch;
            cycle = _bufferingCycle;
            item = current.PlaylistItemId;
            ticks = _bufferingTicks;
            isPlaying = _bufferingIsPlaying;
            when = ServerInstant();
        }
        AppLog.Info("syncplay", "event=buffering outcome=reported");
        try
        {
            // Reports remain ordered across a restore too: the server must finish processing an
            // older Buffering before this membership's Buffering or Ready can release the group.
            await previous.ConfigureAwait(false);
            Task sending;
            lock (_lock)
            {
                if (_disposed || _membershipEpoch != epoch || _bufferingCycle != cycle
                    || _group is not { } current || current.GroupId != group.GroupId
                    || current.PlaylistItemId != item) return;
                sending = StartReport(() => service.Client.SyncPlay.Buffering.PostAsync(new BufferRequestDto
                {
                    PlaylistItemId = item ?? Guid.Empty,
                    PositionTicks = ticks,
                    IsPlaying = isPlaying,
                    When = when,
                }, cancellationToken: CancellationToken.None));
            }
            await PostAsync("buffering", _ => sending).ConfigureAwait(false);
        }
        finally { completion.TrySetResult(); }
    }

    // Starting the SDK request belongs under the membership lock; denial handling and subscriber
    // notifications belong outside it, including when the SDK throws before returning a Task.
    private static Task StartReport(Func<Task> post)
    {
        try { return post(); }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    /// <summary>Every SyncPlay POST goes through here, for the 403 rule. A single 403 is a race —
    /// a leave that crossed a command in flight — but denial has NO socket message behind it, so a
    /// run of them is the only sign that the server has stopped accepting this client and the only
    /// thing that can end a membership that no longer exists.</summary>
    private async Task<bool> PostAsync(string what, Func<CancellationToken, Task> post)
    {
        try
        {
            await post(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _denials, 0);
            AppLog.Detail("syncplay", $"event={what} outcome=success");
            return true;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            var denials = Interlocked.Increment(ref _denials);
            AppLog.Info("syncplay", FormattableString.Invariant(
                $"event={what} outcome=denied consecutive={denials}"));
            if (denials >= MaxConsecutiveDenials && _group is not null)
            {
                Raise(Notice, new SyncPlayNotice(SyncPlayNoticeKind.AccessDenied), "access_denied");
                ClearGroup("denied");
            }
            return false;
        }
        catch (Exception ex)
        {
            // By type, never by message: an SDK failure carries the request URL.
            AppLog.Detail("syncplay", $"event={what} outcome=failure error={ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Drops the membership locally and raises <see cref="GroupLeft"/> exactly once. Every
    /// way out of a group lands here: the two socket updates, a leave, a refused run of POSTs and a
    /// re-join that came back too late.</summary>
    private void ResetBufferingLocked()
    {
        _bufferingCycle++;
        _bufferingReported = false;
        _bufferingNeedsReady = false;
        _bufferingPending = false;
        // Keep in-flight HTTP ordering across membership/queue resets. Its completion owns no
        // new membership obligation, but a new Ready must not overtake an old Buffering.
        Arm(Timeout.InfiniteTimeSpan);
    }

    private void ClearGroup(string reason)
    {
        Membership? group;
        ServerClock? clock;
        lock (_lock)
        {
            group = _group;
            clock = _clock;
            if (group is not null)
                _membershipEpoch++;
            _group = null;
            _clock = null;
            ResetBufferingLocked();
        }
        _pending = null;
        Interlocked.Exchange(ref _denials, 0);
        // Outside the lock: Dispose cancels an in-flight measurement, and the poll loop has no
        // business unwinding under a lock the socket thread also takes.
        clock?.Dispose();
        if (group is null)
            return;
        AppLog.Info("syncplay", $"event=leave outcome=success reason={reason} "
            + $"group={AppLog.ShortHash(group.GroupId.ToString())}");
        Raise(GroupLeft, group.GroupId, reason);
    }

    /// <summary>Whether an update belongs to this client's group. A <c>GroupJoined</c> and the two
    /// refusal updates are the answers to a PENDING join, so they are matched against that instead;
    /// everything else is refused unless it names the group actually held, which is the guard
    /// <see cref="SyncPlayScheduler"/>'s duplicate rule assumes has already happened.</summary>
    private bool Owns(string profileKey, Guid? groupId, string? type)
    {
        if (_pending is { } pending && ProfileMatches(pending.ProfileKey, profileKey)
            && type is SyncPlayGroupUpdateTypes.GroupJoined or SyncPlayGroupUpdateTypes.GroupDoesNotExist
                or SyncPlayGroupUpdateTypes.LibraryAccessDenied)
            return true;
        return _group is { } group && ProfileMatches(group.ProfileKey, profileKey)
            && (groupId is null || groupId == Guid.Empty || groupId == group.GroupId);
    }

    /// <summary>The group's clock, or null once this client has been disposed. Checked under the
    /// same lock <see cref="Dispose"/> takes: a join that got past the check at the top of
    /// <see cref="JoinAsync"/> lands here after an await, and a clock created after Dispose has
    /// nothing left to dispose it — it would poll and ping for the life of the process.</summary>
    private ServerClock? EnsureClock(JellyfinService service)
    {
        lock (_lock)
            return _disposed ? null : _clock ??= ServerClock.ForSession(service);
    }

    /// <summary>A measured offset from nothing, which is what a join and a reconnect both need.
    ///
    /// <para><c>ForceUpdateAsync</c> alone is NOT that: it files one measurement into a window
    /// that may still hold eight from before, and selection is by smallest delay, so a 10 ms
    /// sample taken before a suspend or a clock step beats a 50 ms one taken after it and the
    /// stale champion stands until the ring evicts it — eight measurements, seven or eight minutes
    /// of settled polling. <c>Resync</c> is what empties the window; <c>ForceUpdateAsync</c> is
    /// what makes the caller wait for a measurement to land in the emptied one, since the
    /// measurement <c>Resync</c> starts is not awaited and the clock serialises the two on its own
    /// gate. Both, in that order, or the offset every instant in this protocol is expressed
    /// against is whatever it was before the outage — and nothing reports that: the server treats a
    /// <c>When</c> more than 2000 ms out as no elapsed time at all and says so only in its own
    /// log.</para></summary>
    private static async Task ResyncAsync(ServerClock clock)
    {
        clock.Resync();
        await clock.ForceUpdateAsync().ConfigureAwait(false);
    }

    /// <summary>Runs one of the paths nobody awaits, with nothing allowed out of it.
    /// <see cref="PostAsync"/> catches its own failures, but <c>_resolveSession</c> is a
    /// caller-supplied delegate invoked outside it, and an exception on a task nobody observes
    /// reaches the unobserved-task handler: a crash file, and one of the few crash-retention slots
    /// spent, for what is normally a logout race. The house pattern is
    /// <see cref="LiveSessionService"/>'s own start path.</summary>
    private async Task DetachedAsync(Func<Task> work, string what)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // By type, never by message: an SDK failure carries the request URL.
            AppLog.Detail("syncplay", $"event={what} outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>The owning profile's session has gone — its token was refused, or it was logged
    /// out. Nothing else would end the membership: every REST call resolves the session by profile
    /// key, so once the lookup answers null they all become silent no-ops and the client would sit
    /// in the group for ever, showing a pill for a session that no longer exists.</summary>
    private void OnAuthenticationRejected(string profileKey) => ClearOwned(profileKey, "auth_rejected");

    private void OnProfileStopped(string profileKey) => ClearOwned(profileKey, "session_closed");

    private void ClearOwned(string profileKey, string reason)
    {
        if (ProfileKey is { } owner && ProfileMatches(owner, profileKey))
            ClearGroup(reason);
    }

    /// <summary>The player's own state, never letting a faulty provider out. The player binding
    /// reads mpv properties in here, and a throw would otherwise unwind the socket's receive loop
    /// and take the reconnect ladder with it.</summary>
    private SyncPlayPlayerState ReadLocalPlayer()
    {
        if (ReadPlayer is not { } read)
            return SyncPlayPlayerState.Idle;
        try
        {
            return read() ?? SyncPlayPlayerState.Idle;
        }
        catch (Exception ex)
        {
            LogHandlerFault("player_state", ex);
            return SyncPlayPlayerState.Idle;
        }
    }

    /// <summary>Now, in SERVER time. See <see cref="ReportReadyAsync"/> for why a local instant
    /// would degrade the whole group's sync without producing an error anywhere.</summary>
    private DateTimeOffset ServerInstant() => new(ServerNow());

    private DateTime ServerNow()
    {
        if (_clock is { } clock)
            return clock.ToServer(DateTime.UtcNow);
        // Not reachable from inside a group today — the clock is created before the join POST and
        // dropped with the membership — but the fallback is silently the whole-group degradation
        // the docs around it warn about: a local instant sent as a server one is out by the
        // machine's own skew, and past 2000 ms the server treats the elapsed time as zero and logs
        // it nowhere this client can see. First to the breadcrumb ring, the rest verbose-only, the
        // same shape as a handler fault: this is on the Ready and Buffering path.
        var line = "event=clock outcome=missing";
        if (_clockMissingLogged)
            AppLog.Detail("syncplay", line);
        else
            AppLog.Info("syncplay", line);
        _clockMissingLogged = true;
        return DateTime.UtcNow;
    }

    /// <summary>The queue entry the group is playing. Both spellings of the item id are read
    /// elsewhere; the playlist item id has only one name but two spellings of its VALUE, which is
    /// why it is parsed rather than deserialized — see <see cref="SyncPlayQueueItem"/>. It is the
    /// one every <c>Ready</c> and <c>Buffering</c> has to echo.</summary>
    private static Guid? PlayingEntry(SyncPlayQueueUpdate queue)
        => queue.Playlist is { Count: > 0 } playlist
            && queue.PlayingItemIndex is { } index && index >= 0 && index < playlist.Count
            ? playlist[index].PlaylistItem
            : null;

    private static bool ProfileMatches(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-arms the debounce. Always called under <see cref="_lock"/>, so the timer cannot
    /// be armed by one thread while another is clearing the group out from under it.</summary>
    private void Arm(TimeSpan due)
    {
        try
        {
            _bufferingTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced Dispose. Nothing to arm.
        }
    }

    /// <summary>The same discipline <see cref="SessionSocket.Raise{T}"/> has, and for the same
    /// reason: these events are raised on the socket's receive-loop thread, so an escaping
    /// subscriber exception unwinds the loop and takes the reconnect ladder — and with it every
    /// command that would have followed — down for the life of the process. One bad subscriber
    /// costs one event instead.</summary>
    private void Raise<T>(Action<T>? handler, T message, string kind)
    {
        if (handler is null)
            return;
        try
        {
            handler(message);
        }
        catch (Exception ex)
        {
            LogHandlerFault(kind, ex);
        }
    }

    /// <summary>First fault to the breadcrumb ring, the rest verbose-only: a handler that throws on
    /// one event of a kind usually throws on all of them, and the ring holds 500 entries in
    /// total.</summary>
    private void LogHandlerFault(string kind, Exception ex)
    {
        // The type, not the message: a handler exception can carry item titles or a URL.
        var line = $"event=handler outcome=failure kind={kind} error={ex.GetType().Name}";
        if (_handlerFaultLogged)
            AppLog.Detail("syncplay", line);
        else
            AppLog.Info("syncplay", line);
        _handlerFaultLogged = true;
    }

    /// <summary>A join that has been sent and not yet confirmed.</summary>
    private sealed record Pending(string ProfileKey, Guid GroupId);

    /// <summary>The whole membership in one immutable value, so a cross-thread read is one
    /// reference read of a consistent group rather than six fields caught mid-update.</summary>
    private sealed record Membership(
        string ProfileKey,
        Guid GroupId,
        string GroupName,
        IReadOnlyList<string> Participants,
        string? State,
        DateTime JoinedAtUtc,
        Guid? PlaylistItemId,
        DateTimeOffset? LastQueueUpdate,
        SyncPlayCommandMessage? LastApplied);
}
