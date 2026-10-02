namespace LightWeaver.Jellyfin;

/// <summary>What a SyncPlay command asks the local player to do. <see cref="Noop"/> covers every
/// command this client is right to ignore: one aimed at the group before this client joined, one
/// for a queue entry that is not the one playing, and the server's re-send to a client it thinks is
/// lost when this client is in fact already where it should be.</summary>
public enum SyncPlayActionKind
{
    Noop,
    /// <summary>The group starts LATER. Seek now if <see cref="SyncPlayAction.NeedsSeek"/>, then
    /// unpause at <see cref="SyncPlayAction.DueLocalUtc"/> — the gap between the two is the
    /// pre-roll the server deliberately leaves for it.</summary>
    SeekAndArmUnpause,
    /// <summary>The group is already playing. Seek to <see cref="SyncPlayAction.TargetTicks"/> if
    /// <see cref="SyncPlayAction.NeedsSeek"/> and unpause at once.</summary>
    UnpauseNow,
    /// <summary>Pause FIRST, then seek to <see cref="SyncPlayAction.TargetTicks"/>. That order is
    /// the action, not an implementation detail: seeking while still playing lets mpv run on past
    /// the target between the seek landing and the pause landing, and the group's stop point is
    /// exactly what a pause is for.</summary>
    Pause,
    /// <summary>Seek and stay paused. The group goes to <c>Waiting</c> on a seek and the server
    /// sends its own <c>Unpause</c> once every member has reported ready, so unpausing here would
    /// be this client starting alone.</summary>
    Seek,
    /// <summary>Seek and LEAVE THE TRANSPORT STATE ALONE. Only
    /// <see cref="SyncPlayScheduler.ResolveDrift"/> produces this, and only while the group is
    /// playing — so the player is playing too, and has to still be playing afterwards.
    ///
    /// <para>Its own kind rather than a <see cref="Seek"/> for exactly that reason: the two differ
    /// only in what they do to pause, they arrive at the same call site, and a drift correction
    /// handled as a <see cref="Seek"/> would pause this client for good, since a paused player
    /// cannot drift back into position on its own.</para></summary>
    DriftSeek,
    Stop,
}

/// <summary>A resolved SyncPlay command: what to do, where, and when.</summary>
/// <param name="TargetTicks">The position the group is to be at, in .NET ticks. Already carries the
/// elapsed-since-<c>When</c> correction where one applies.</param>
/// <param name="DueLocalUtc">The LOCAL instant to act at. Only
/// <see cref="SyncPlayActionKind.SeekAndArmUnpause"/> puts this in the future; everything else acts
/// immediately and carries the resolution instant.</param>
/// <param name="NeedsSeek">Whether the player is far enough from <paramref name="TargetTicks"/> to
/// be worth moving. A seek that changes nothing still costs a demuxer flush and, under transcode, a
/// new server-side session.</param>
public sealed record SyncPlayAction(
    SyncPlayActionKind Kind,
    long TargetTicks = 0,
    DateTime DueLocalUtc = default,
    bool NeedsSeek = false)
{
    public static readonly SyncPlayAction Noop = new(SyncPlayActionKind.Noop);

    /// <summary>The target in the unit the player takes. The one conversion in the path, kept here
    /// for the same reason <c>RemoteCommand</c> keeps its own: it is what a wrong seek gets blamed
    /// on.</summary>
    public double TargetSeconds => TargetTicks / (double)TimeSpan.TicksPerSecond;
}

/// <summary>Everything about the local side a SyncPlay command is resolved against.</summary>
/// <param name="JoinedAtUtc">When this client joined the group, in SERVER UTC. A command emitted
/// before that was aimed at a group this client was not yet in.</param>
/// <param name="PlaylistItemId">The queue entry that is playing, as the server minted it.</param>
/// <param name="IsPaused">Whether the player is paused right now.</param>
/// <param name="IsBuffering">Whether the player is buffering. A drift correction during a buffer
/// would be measured against a position that is not moving.</param>
/// <param name="LastApplied">The last command that was acted on, for duplicate detection and as the
/// reference the group's expected position is extrapolated from.</param>
/// <param name="LastDriftSeekUtc">The local instant of the last drift correction.</param>
public sealed record SyncPlayState(
    DateTime JoinedAtUtc,
    Guid? PlaylistItemId = null,
    bool IsPaused = true,
    bool IsBuffering = false,
    SyncPlayCommandMessage? LastApplied = null,
    DateTime LastDriftSeekUtc = default);

/// <summary>Turns a SyncPlay command into the action it names, and nothing else. Pure and static
/// for the same reason <see cref="RemoteCommand"/> is: group playback is otherwise only assertable
/// with a window, a player, a socket and a second client on the other end of it, and the parts that
/// actually go wrong — a stale command applied, a duplicate re-seeking a client that was already
/// right, a pre-roll collapsed into a late start — are all in this mapping.</summary>
public static class SyncPlayScheduler
{
    /// <summary>How far out of position the player has to be before a seek is worth it. The same
    /// 400 ms jellyfin-web uses to decide a skip, so two clients of different makes agree about
    /// what "in sync" means.</summary>
    public static readonly TimeSpan SeekThreshold = TimeSpan.FromMilliseconds(400);

    /// <summary>Floor on the interval between drift corrections. A seek costs a demuxer flush and
    /// the position it is measured against only settles a moment later, so correcting on every
    /// sample would chase its own tail.</summary>
    public static readonly TimeSpan MinDriftInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>Resolves one <c>SyncPlayCommand</c>.</summary>
    /// <param name="command">The command as it arrived.</param>
    /// <param name="state">The local side. See <see cref="SyncPlayState"/>.</param>
    /// <param name="offset">Server clock minus local clock, from <see cref="ServerClock.Offset"/>.</param>
    /// <param name="localNowUtc">Local UTC now.</param>
    /// <param name="currentPositionSeconds">Where the player is.</param>
    public static SyncPlayAction Resolve(SyncPlayCommandMessage? command, SyncPlayState state,
        TimeSpan offset, DateTime localNowUtc, double currentPositionSeconds)
    {
        if (command?.WhenUtc is not { } whenServer)
            return SyncPlayAction.Noop;
        var kind = command.Command switch
        {
            SyncPlayCommandTypes.Unpause => SyncPlayActionKind.UnpauseNow,
            SyncPlayCommandTypes.Pause => SyncPlayActionKind.Pause,
            SyncPlayCommandTypes.Seek => SyncPlayActionKind.Seek,
            SyncPlayCommandTypes.Stop => SyncPlayActionKind.Stop,
            _ => SyncPlayActionKind.Noop,
        };
        if (kind == SyncPlayActionKind.Noop)
            return SyncPlayAction.Noop;

        // Emitted before this client was in the group. The server replays a group's current state
        // to a joining session, so without this a client that joins a paused group applies the
        // command that paused it minutes ago and seeks to a position the group has long left.
        //
        // UNSETTLED, and it wants a live server to settle (folded into the M5 live checks). Reading
        // the 12.0 source, a join-time replay is a NEW SendCommand with EmittedAt = UtcNow and only
        // When carried over from the group's LastActivity — which would mean the replay is never
        // stale by this test and the rule can never fire. The rule is kept because it is what the
        // protocol work settled on and it costs nothing when it cannot fire; what the live check has
        // to answer is the other direction, because JoinedAtUtc is a SERVER instant derived from
        // ServerClock.Offset: an over-estimated offset (or a clock that is not ready yet, where the
        // offset is zero by fiat) puts JoinedAtUtc in the future and drops EVERY command until
        // server-now catches up, silently. If it turns out to fire at all, it wants a tolerance
        // margin — at least the measured ping — rather than the bare comparison.
        if (command.EmittedAtUtc is { } emitted && emitted < state.JoinedAtUtc)
            return SyncPlayAction.Noop;

        // A Stop is the one command that outlives the queue entry: it is how the server ends a
        // group's playback, and by the time it arrives the entry it names may already be gone.
        if (kind != SyncPlayActionKind.Stop && !SameItem(command.PlaylistItemId, state.PlaylistItemId))
            return SyncPlayAction.Noop;

        var serverNow = localNowUtc + offset;
        var localWhen = whenServer - offset;
        var target = TargetTicks(command, kind, whenServer, serverNow, currentPositionSeconds);
        var needsSeek = command.PositionTicks is not null
            && Math.Abs(target - Ticks(currentPositionSeconds)) > SeekThreshold.Ticks;

        // The server re-sends a command when a client looks lost to it. Acting on it again is what
        // the server wants ONLY if this client is in fact wrong: re-seeking a client that is
        // already in position turns the server's correction into the stall it was correcting.
        if (kind != SyncPlayActionKind.Stop && IsDuplicate(command, state.LastApplied)
            && !Diverged(kind, state, needsSeek))
            return SyncPlayAction.Noop;

        return kind switch
        {
            // The pre-roll is the point. The server sets When to now + max(highestPing * 2, 500 ms)
            // precisely so every member can get itself into position first and then start on the
            // instant; starting on arrival instead makes this client early by that whole margin.
            SyncPlayActionKind.UnpauseNow when localWhen > localNowUtc =>
                new SyncPlayAction(SyncPlayActionKind.SeekAndArmUnpause, target, localWhen, needsSeek),
            SyncPlayActionKind.UnpauseNow =>
                new SyncPlayAction(SyncPlayActionKind.UnpauseNow, target, localNowUtc, needsSeek),
            // Pause, Seek and Stop act at once even if When is in the future. The server dates all
            // three "now", and being early on a pause costs a frame while being late costs the
            // group's stop point.
            _ => new SyncPlayAction(kind, target, localNowUtc, needsSeek),
        };
    }

    /// <summary>Whether the player has drifted far enough from where the group should be to be
    /// worth a seek, and nothing else: no speed nudge. Retiming the audio clock is how every other
    /// client does the small corrections, and it is not available here —
    /// <c>MpvPlayer.Speed</c> shows an OSD line and raises <c>SpeedChanged</c>, so a background
    /// correction would flash at the user and rewrite the speed menu, and a bitstreamed
    /// <c>audio-spdif</c> passthrough track cannot be retimed at all. Passthrough is a first-class
    /// feature of this client, so the correction that works for every track is the one to have.
    ///
    /// <para>Suppressed unless the group is actually playing (the last command was an
    /// <c>Unpause</c>), that <c>Unpause</c>'s instant has arrived, the player is playing and not
    /// buffering, and the command was for the entry that is playing — all of them are ways of
    /// measuring drift against a position that means nothing.</para></summary>
    public static SyncPlayAction ResolveDrift(SyncPlayState state, TimeSpan offset,
        DateTime localNowUtc, double currentPositionSeconds)
    {
        if (state.LastApplied is not { Command: SyncPlayCommandTypes.Unpause } last
            || last.WhenUtc is not { } whenServer || last.PositionTicks is not { } startTicks)
            return SyncPlayAction.Noop;
        if (state.IsBuffering || !SameItem(last.PlaylistItemId, state.PlaylistItemId))
            return SyncPlayAction.Noop;
        // A player that is not moving cannot drift, and seeking it would not start it: the
        // correction below only moves the position, so against a stationary player it would fire
        // again every MinDriftInterval, flushing the demuxer — and, under transcode, opening a new
        // server-side session — for as long as the pause lasted. The group's next command puts the
        // position right anyway, via Resolve, which is also where a paused member belongs.
        if (state.IsPaused)
            return SyncPlayAction.Noop;
        if (localNowUtc - state.LastDriftSeekUtc < MinDriftInterval)
            return SyncPlayAction.Noop;
        var serverNow = localNowUtc + offset;
        // The group has not started yet. This is the NORMAL case, not an edge one: the server dates
        // every resume When = now + max(highestPing * 2, 500 ms), so the pre-roll Resolve just armed
        // is at least half a second long and every suppression above is satisfied inside it — the
        // last command IS an Unpause, for this entry, on a player that is playing. Without this the
        // elapsed term goes NEGATIVE, the client is reported as having drifted by up to the whole
        // pre-roll, and the correction is a seek BACKWARDS in exactly the window the pre-roll exists
        // to protect, re-armed every MinDriftInterval until the group catches up with it.
        if (serverNow <= whenServer)
            return SyncPlayAction.Noop;
        var expected = startTicks + (serverNow - whenServer).Ticks;
        return Math.Abs(expected - Ticks(currentPositionSeconds)) > SeekThreshold.Ticks
            ? new SyncPlayAction(SyncPlayActionKind.DriftSeek, expected, localNowUtc, NeedsSeek: true)
            : SyncPlayAction.Noop;
    }

    /// <summary>Where the group is now.
    ///
    /// <para>A command whose <c>When</c> has already passed is one this client got late — the group
    /// started without it — so the position to land on is the group's start position plus however
    /// long it has been running, not the start position itself. Only an unpause extrapolates: a
    /// pause, a seek and a stop all leave the group stationary at exactly the position they
    /// carry.</para>
    ///
    /// <para><c>PositionTicks</c> is nullable in the contract. Falling back to zero would read as a
    /// seek to the start of the file, so a command without one leaves the player where it is.</para></summary>
    private static long TargetTicks(SyncPlayCommandMessage command, SyncPlayActionKind kind,
        DateTime whenServer, DateTime serverNow, double currentPositionSeconds)
    {
        if (command.PositionTicks is not { } ticks)
            return Ticks(currentPositionSeconds);
        return kind == SyncPlayActionKind.UnpauseNow && serverNow > whenServer
            ? ticks + (serverNow - whenServer).Ticks
            : ticks;
    }

    /// <summary>The four fields that make two commands the same command. <c>GroupId</c> is not one
    /// of them: membership is one group at a time, and an update from another group is refused
    /// before it reaches here.</summary>
    private static bool IsDuplicate(SyncPlayCommandMessage command, SyncPlayCommandMessage? last)
        => last is not null
            && last.Command == command.Command
            && last.When == command.When
            && last.PositionTicks == command.PositionTicks
            && last.PlaylistItemId == command.PlaylistItemId;

    /// <summary>Whether the local state disagrees with what a repeated command asked for. Both
    /// halves matter: a client that is paused when the group is playing is wrong wherever its
    /// position is, and one that is playing from the wrong place is wrong however right its
    /// transport state looks.</summary>
    private static bool Diverged(SyncPlayActionKind kind, SyncPlayState state, bool needsSeek)
        => needsSeek || kind switch
        {
            SyncPlayActionKind.UnpauseNow => state.IsPaused,
            SyncPlayActionKind.Pause or SyncPlayActionKind.Seek => !state.IsPaused,
            _ => false,
        };

    /// <summary>Queue-entry comparison. An absent or empty id on either side is "unspecified" and
    /// matches: the server omits nulls, and a command it did not scope to an entry is not one to
    /// throw away.</summary>
    private static bool SameItem(Guid? left, Guid? right)
        => left is null || right is null || left == Guid.Empty || right == Guid.Empty
            || left == right;

    private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);
}
