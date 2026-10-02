using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightWeaver.Jellyfin;

/// <summary>The <c>MessageType</c> values this client reads off the session socket. Constants
/// rather than an enum: an unknown type has to be ignorable, and the receive loop switches on the
/// raw string so a server that grows a new message never becomes a parse failure.</summary>
public static class SessionMessageTypes
{
    /// <summary>Server-initiated: "start sending KeepAlive, my timeout is <c>Data</c> seconds".</summary>
    public const string ForceKeepAlive = "ForceKeepAlive";
    /// <summary>Both directions. Inbound it is the server echoing ours back and means nothing.</summary>
    public const string KeepAlive = "KeepAlive";
    public const string Play = "Play";
    public const string Playstate = "Playstate";
    public const string GeneralCommand = "GeneralCommand";
    public const string LibraryChanged = "LibraryChanged";
    public const string UserDataChanged = "UserDataChanged";
    /// <summary>A SyncPlay group's transport command — the whole group unpauses, pauses, seeks or
    /// stops together.</summary>
    public const string SyncPlayCommand = "SyncPlayCommand";
    /// <summary>A SyncPlay group lifecycle event: membership, queue and state.</summary>
    public const string SyncPlayGroupUpdate = "SyncPlayGroupUpdate";
}

/// <summary>What a <see cref="SyncPlayCommandMessage.Command"/> can be (Jellyfin's
/// <c>SendCommandType</c>). A participant has to act on all four.</summary>
public static class SyncPlayCommandTypes
{
    public const string Unpause = "Unpause";
    public const string Pause = "Pause";
    public const string Stop = "Stop";
    public const string Seek = "Seek";
}

/// <summary>The nine <see cref="SyncPlayGroupUpdateMessage.Type"/> values (Jellyfin's
/// <c>GroupUpdateType</c>). Constants rather than an enum for the same reason
/// <see cref="SessionMessageTypes"/> is: the payload union is discriminated on this string, and a
/// value this client does not know has to be ignorable rather than a parse failure.</summary>
public static class SyncPlayGroupUpdateTypes
{
    /// <summary>Payload is a <see cref="SyncPlayGroupInfo"/>.</summary>
    public const string GroupJoined = "GroupJoined";
    /// <summary>Payload is the group id as a string.</summary>
    public const string GroupLeft = "GroupLeft";
    /// <summary>Payload is the group id as a string.</summary>
    public const string NotInGroup = "NotInGroup";
    /// <summary>Payload is a <see cref="SyncPlayQueueUpdate"/>.</summary>
    public const string PlayQueue = "PlayQueue";
    /// <summary>Payload is a <see cref="SyncPlayStateUpdate"/>.</summary>
    public const string StateUpdate = "StateUpdate";
    /// <summary>Payload is the user's NAME, not an id — the server sends what it would display.</summary>
    public const string UserJoined = "UserJoined";
    /// <summary>Payload is the user's NAME. See <see cref="UserJoined"/>.</summary>
    public const string UserLeft = "UserLeft";
    /// <summary>Payload is the group id as a string.</summary>
    public const string GroupDoesNotExist = "GroupDoesNotExist";
    /// <summary>Payload is the group id as a string.</summary>
    public const string LibraryAccessDenied = "LibraryAccessDenied";
}

/// <summary>A SyncPlay transport command (Jellyfin's <c>SendCommand</c>): the group is to be at
/// <see cref="PositionTicks"/> at the instant <see cref="When"/>.
///
/// <para>Hand-written rather than taken from the SDK, which does generate <c>SendCommand</c> — but
/// as an <c>IParsable</c>, so <see cref="JsonSerializer"/> constructs one and populates nothing.
/// <see cref="Command"/> is a string for the same reason <see cref="PlaystateRequestMessage"/>'s
/// is: an unrecognised verb has to be droppable.</para>
///
/// <para>The two instants are <see cref="DateTimeOffset"/> on the wire side and are read through
/// <see cref="WhenUtc"/> / <see cref="EmittedAtUtc"/>, which are UTC by construction. A plain
/// <see cref="DateTime"/> would take its <see cref="DateTimeKind"/> from how the server happened to
/// format the instant — <c>Z</c> gives <see cref="DateTimeKind.Utc"/>, an explicit <c>+00:00</c>
/// gives a LOCAL time — and every comparison in <see cref="SyncPlayScheduler"/> is against a UTC
/// now. A whole-timezone error in the due instant is the kind of thing that looks like the feature
/// simply not working.</para></summary>
public sealed record SyncPlayCommandMessage(
    Guid? GroupId,
    Guid? PlaylistItemId,
    DateTimeOffset? When,
    long? PositionTicks,
    string? Command,
    DateTimeOffset? EmittedAt)
{
    /// <summary>The instant the command is to take effect, in server UTC.</summary>
    public DateTime? WhenUtc => When?.UtcDateTime;

    /// <summary>When the server produced the command, in server UTC. Older than the instant this
    /// client joined means the command was aimed at the group before this client was in it.</summary>
    public DateTime? EmittedAtUtc => EmittedAt?.UtcDateTime;
}

/// <summary>A group as the server describes it on join (Jellyfin's <c>GroupInfoDto</c>).
/// <see cref="Participants"/> are display NAMES, not ids.</summary>
public sealed record SyncPlayGroupInfo(
    Guid? GroupId,
    string? GroupName,
    string? State,
    IReadOnlyList<string>? Participants,
    DateTimeOffset? LastUpdatedAt);

/// <summary>One entry of a group's play queue. <see cref="PlaylistItemId"/> is minted by the server
/// per entry: it is not the item id and it does not survive a new queue.
///
/// <para>The library item arrives under one of two names. The SDK's generated <c>QueueItem</c>
/// calls it <c>Id</c>; the server source calls it <c>ItemId</c>. Both are read and
/// <see cref="Item"/> answers whichever turned up, because the cost of picking the wrong one is a
/// queue whose entries silently point at nothing — and every field here is optional anyway, since
/// the server omits nulls.</para>
///
/// <para><see cref="PlaylistItemId"/> is a string on the server, while the request DTOs that
/// echo it are <c>Guid</c>. Parsing it here accepts Jellyfin's compact ids and dashed ids, while
/// an invalid string costs only this field. The two library ids use the shared
/// <see cref="LenientGuidConverter"/> because Jellyfin serializes its <c>Guid</c> fields in
/// compact <c>N</c> format too.</para></summary>
public sealed record SyncPlayQueueItem(Guid? ItemId, Guid? Id, string? PlaylistItemId)
{
    /// <summary>The library item this entry plays.</summary>
    public Guid? Item => ItemId ?? Id;

    /// <summary>The server-minted queue entry id, or null when it is absent or is not an id at
    /// all. This is what every <c>Ready</c> and <c>Buffering</c> has to echo.</summary>
    public Guid? PlaylistItem
        => Guid.TryParse(PlaylistItemId, out var id) && id != Guid.Empty ? id : null;
}

/// <summary>A group's queue (Jellyfin's <c>PlayQueueUpdate</c>). <see cref="LastUpdate"/> is a
/// monotonic guard — an update no newer than the last applied one is to be dropped.</summary>
public sealed record SyncPlayQueueUpdate(
    string? Reason,
    DateTimeOffset? LastUpdate,
    IReadOnlyList<SyncPlayQueueItem>? Playlist,
    int? PlayingItemIndex,
    long? StartPositionTicks,
    bool? IsPlaying,
    string? ShuffleMode,
    string? RepeatMode);

/// <summary>A group's state change (Jellyfin's <c>GroupStateUpdate</c>). <see cref="State"/> is one
/// of <c>Idle</c>, <c>Waiting</c>, <c>Paused</c>, <c>Playing</c>.</summary>
public sealed record SyncPlayStateUpdate(string? State, string? Reason);

/// <summary>A SyncPlay group update (Jellyfin's <c>GroupUpdate</c>).
///
/// <para><see cref="Data"/> stays a <see cref="JsonElement"/> because the payload is a union of
/// nine shapes discriminated by <see cref="Type"/> — four records and five bare strings — and
/// there is no one record it could be read into. It is narrowed by the accessors below, which is
/// the same split <see cref="SessionMessageReader"/> already makes for the envelope: read the
/// discriminator first, deserialize the payload only once something knows what it is.</para></summary>
public sealed record SyncPlayGroupUpdateMessage(Guid? GroupId, string? Type, JsonElement Data)
{
    /// <summary>The <c>GroupJoined</c> payload, or null when this update is not one.</summary>
    public SyncPlayGroupInfo? GroupInfo() => SessionMessageReader.ReadPayload<SyncPlayGroupInfo>(Data);

    /// <summary>The <c>PlayQueue</c> payload, or null when this update is not one.</summary>
    public SyncPlayQueueUpdate? Queue() => SessionMessageReader.ReadPayload<SyncPlayQueueUpdate>(Data);

    /// <summary>The <c>StateUpdate</c> payload, or null when this update is not one.</summary>
    public SyncPlayStateUpdate? State() => SessionMessageReader.ReadPayload<SyncPlayStateUpdate>(Data);

    /// <summary>The five string-payload updates: a user name for <c>UserJoined</c> and
    /// <c>UserLeft</c>, a group id for the other three. Null for anything that is not a JSON
    /// string, so a caller that asks the wrong question gets nothing rather than
    /// <c>"[object]"</c>.</summary>
    public string? Text() => Data.ValueKind == JsonValueKind.String ? Data.GetString() : null;
}

/// <summary>A remote "play this here" request (Jellyfin's <c>PlayRequest</c>).
///
/// <para>Every property is optional, and that is not defensiveness: the server serializes with
/// <c>DefaultIgnoreCondition = WhenWritingNull</c>, so a field it has no value for is absent from
/// the frame entirely rather than present as null.</para></summary>
public sealed record PlayRequestMessage(
    IReadOnlyList<Guid>? ItemIds,
    string? PlayCommand,
    long? StartPositionTicks,
    int? StartIndex,
    string? MediaSourceId,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    string? ControllingUserId);

/// <summary>A remote transport command (Jellyfin's <c>PlaystateRequest</c>): Play, Pause,
/// PlayPause, Stop, Seek, NextTrack, PreviousTrack, Rewind, FastForward.
///
/// <para><see cref="Command"/> is a string, not the SDK enum, for two reasons: Kiota does not
/// generate <c>PlaystateCommand</c> at all (this message is websocket-only and never appears in
/// the OpenAPI document), and an unrecognised command must be droppable rather than fatal.</para></summary>
public sealed record PlaystateRequestMessage(
    string? Command,
    long? SeekPositionTicks,
    string? ControllingUserId);

/// <summary>A remote <c>GeneralCommand</c> — volume, track selection, message display. The
/// argument bag is the interesting half; <see cref="Name"/> says which keys it carries.</summary>
public sealed record GeneralCommandMessage(
    string? Name,
    string? ControllingUserId,
    IReadOnlyDictionary<string, string?>? Arguments)
{
    /// <summary>Case-insensitive argument lookup. The dictionary arrives from
    /// <see cref="JsonSerializer"/> with the default ordinal comparer, so a plain indexer would
    /// miss a server that ever changed the case of a key.</summary>
    public string? Argument(string key)
    {
        if (Arguments is null)
            return null;
        if (Arguments.TryGetValue(key, out var exact))
            return exact;
        foreach (var pair in Arguments)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    /// <summary>The numeric arguments (<c>Volume</c>, <c>Index</c>, <c>TimeoutMs</c>) still arrive
    /// as JSON strings, because the server's <c>GeneralCommand.Arguments</c> is a
    /// <c>Dictionary&lt;string, string&gt;</c> whatever the value means. Parsed invariantly: the
    /// server does not format these for the client's locale.</summary>
    public int? IntArgument(string key)
        => int.TryParse(Argument(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

/// <summary>A coalesced library change (Jellyfin's <c>LibraryUpdateInfo</c>). The server batches
/// these on a 30 s timer and sends one per user, so a handler must be idempotent and must not
/// assume one message per item.</summary>
public sealed record LibraryUpdateMessage(
    IReadOnlyList<string>? FoldersAddedTo,
    IReadOnlyList<string>? FoldersRemovedFrom,
    IReadOnlyList<string>? ItemsAdded,
    IReadOnlyList<string>? ItemsRemoved,
    IReadOnlyList<string>? ItemsUpdated,
    IReadOnlyList<string>? CollectionFolders)
{
    /// <summary>Cheap enough to be a property — it counts, it does not parse.</summary>
    public bool IsEmpty
        => Count(FoldersAddedTo) + Count(FoldersRemovedFrom) + Count(ItemsAdded)
            + Count(ItemsRemoved) + Count(ItemsUpdated) + Count(CollectionFolders) == 0;

    /// <summary>Every item id the message touches, de-duplicated. Methods rather than properties:
    /// each call allocates and parses, and a property that does that per read is the kind of thing
    /// a caller repeats inside a loop without noticing.</summary>
    public IReadOnlyList<Guid> AffectedItemIds() => Parse(ItemsAdded, ItemsRemoved, ItemsUpdated);

    /// <summary>Every folder id the message touches, de-duplicated. See
    /// <see cref="AffectedItemIds"/> for why this is a method.</summary>
    public IReadOnlyList<Guid> AffectedFolderIds()
        => Parse(FoldersAddedTo, FoldersRemovedFrom, CollectionFolders);

    private static int Count(IReadOnlyList<string>? values) => values?.Count ?? 0;

    /// <summary>The ids arrive dashless (<c>Guid.ToString("N")</c>); <see cref="Guid.TryParse"/>
    /// accepts that form as well as the dashed one. Anything else is skipped rather than thrown
    /// on — one unparseable id must not cost the whole batch.</summary>
    private static IReadOnlyList<Guid> Parse(params IReadOnlyList<string>?[] sources)
    {
        var ids = new List<Guid>();
        foreach (var source in sources)
        {
            if (source is null)
                continue;
            foreach (var value in source)
                if (Guid.TryParse(value, out var id) && id != Guid.Empty && !ids.Contains(id))
                    ids.Add(id);
        }
        return ids;
    }
}

/// <summary>One item's user data in a <c>UserDataChanged</c> batch (Jellyfin's
/// <c>UserItemDataDto</c>).</summary>
public sealed record UserItemDataMessage(
    Guid? ItemId,
    string? Key,
    bool? Played,
    bool? IsFavorite,
    long? PlaybackPositionTicks,
    int? PlayCount,
    double? PlayedPercentage,
    int? UnplayedItemCount,
    DateTimeOffset? LastPlayedDate);

/// <summary>A batch of user-data changes for one user (Jellyfin's <c>UserDataChangeInfo</c>).
/// <see cref="UserId"/> is a string on the wire and is left one here: it is only ever compared,
/// and a guid parse would be another way for a frame to fail.</summary>
public sealed record UserDataChangeMessage(
    string? UserId,
    IReadOnlyList<UserItemDataMessage>? UserDataList);

/// <summary>Splits the session socket's envelope — <c>{ "MessageType", "Data", "MessageId" }</c> —
/// and deserializes payloads out of it.</summary>
public static class SessionMessageReader
{
    /// <summary>One instance for the process. A fresh <see cref="JsonSerializerOptions"/> per
    /// message is the documented .NET performance trap (each one rebuilds and caches its own
    /// metadata), and this path runs on every frame the server sends — including a KeepAlive echo
    /// every 30 seconds for the life of the session.
    ///
    /// <para><see cref="JsonNumberHandling.AllowReadingFromString"/> because the server's own
    /// numeric fields are plain numbers but plugin-authored frames are not reliably so; case
    /// insensitivity because the server writes PascalCase and this client should not break if that
    /// ever changes. Jellyfin writes Guid fields in compact <c>N</c> format; the shared
    /// <see cref="LenientGuidConverter"/> accepts those and dashed ids in outer and nested
    /// payloads, while malformed ids still reject the payload.</para></summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientGuidConverter() },
    };

    /// <summary>Reads the envelope. False for anything unparseable, which is the whole point: a
    /// malformed or unexpected frame must cost one dropped message, never the receive loop and
    /// with it every remote command that would have followed.
    ///
    /// <para><paramref name="data"/> is <see cref="JsonValueKind.Undefined"/> when the frame
    /// carries no payload — <c>KeepAlive</c> is a complete, valid message with nothing in
    /// it.</para></summary>
    public static bool TryRead(string frame, out string messageType, out JsonElement data)
    {
        messageType = "";
        data = default;
        if (string.IsNullOrWhiteSpace(frame))
            return false;
        try
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryProperty(document.RootElement, "MessageType", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() is not { Length: > 0 } name)
                return false;
            messageType = name;
            // Cloned because the document that owns the backing buffer is disposed on the way out
            // of this method, and an un-cloned JsonElement is only valid while its document lives.
            if (TryProperty(document.RootElement, "Data", out var payload))
                data = payload.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Deserializes an envelope payload, or null when it is absent or does not fit the
    /// shape. Same contract as <see cref="TryRead"/>: never throws at the caller.</summary>
    public static T? ReadPayload<T>(JsonElement data) where T : class
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        try
        {
            return data.Deserialize<T>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}
