using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

// Exercises the real SessionSocket, the real JellyfinService and the real envelope reader against
// a hand-rolled server. No WPF window, no mpv, no saved credentials; the token and every server
// response are synthetic.
//
// The server is a raw TcpListener rather than an HttpListener, and that is the point of it: the
// upgrade handshake is written out by hand, so the assertions can be made against the actual
// request line and headers the client put on the wire. HttpListener's WebSocket support goes
// through HTTP.sys, which accepts the upgrade before any of that is observable.
internal static class LiveSessionFixture
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            count++;
            Console.WriteLine("PASS: " + name);
        }

        try
        {
            CheckUriDerivation(Check);
            CheckPayloadParsing(Check);
            CheckSyncPlayGuidParsing(Check);
            CheckSyncPlayParsing(Check);
            CheckCommandResolution(Check);
            CheckStreamResolution(Check);
            CheckPlayTargeting(Check);
            CheckQueueInsertion(Check);
            await CheckFolderInvalidationAsync(Check).ConfigureAwait(false);
            await CheckFolderIndexSurvivalAsync(Check).ConfigureAwait(false);
            await CheckTransportAsync(Check).ConfigureAwait(false);
            await CheckSyncPlayTransportAsync(Check).ConfigureAwait(false);
            await CheckRejectedTokenAsync(Check).ConfigureAwait(false);
            Console.WriteLine($"RESULT: PASS ({count} live session assertions)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: live session: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void CheckUriDerivation(Action<bool, string> check)
    {
        check(SessionSocket.BuildSocketUri("http://h:8096")?.ToString() == "ws://h:8096/socket",
            "a bare origin derives ws://host:port/socket");
        check(SessionSocket.BuildSocketUri("https://h/jellyfin")?.ToString() == "wss://h/jellyfin/socket",
            "a https sub-path install derives wss and keeps its path prefix");
        check(SessionSocket.BuildSocketUri("http://h/jellyfin/")?.ToString() == "ws://h/jellyfin/socket",
            "a trailing slash does not double up before /socket");
        check(SessionSocket.BuildSocketUri("http://h:8096")?.Query.Length == 0,
            "the derived endpoint carries no query string");
        check(SessionSocket.BuildSocketUri(null) is null && SessionSocket.BuildSocketUri("ftp://h") is null,
            "a missing or non-http server URL derives no endpoint");
    }

    // Every frame below is shaped the way the server actually writes them: PascalCase names, enums
    // as strings, nulls omitted rather than written, and library ids dashless.
    private static void CheckPayloadParsing(Action<bool, string> check)
    {
        var item = Guid.NewGuid();
        var user = Guid.NewGuid();

        check(SessionMessageReader.TryRead(
                $$"""{"MessageType":"Play","Data":{"ItemIds":["{{item:D}}"],"StartPositionTicks":12300000000,"PlayCommand":"PlayNow","ControllingUserId":"{{user:D}}"},"MessageId":"{{Guid.NewGuid():D}}"}""",
                out var type, out var data)
            && type == SessionMessageTypes.Play
            && SessionMessageReader.ReadPayload<PlayRequestMessage>(data) is
                { PlayCommand: "PlayNow", StartPositionTicks: 12300000000, MediaSourceId: null, AudioStreamIndex: null } play
            && play.ItemIds is [var only] && only == item,
            "a Play frame parses and its omitted fields stay null");

        check(SessionMessageReader.TryRead(
                $$"""{"MessageType":"Playstate","Data":{"Command":"Seek","SeekPositionTicks":6000000000,"ControllingUserId":"{{user:D}}"},"MessageId":"{{Guid.NewGuid():D}}"}""",
                out type, out data)
            && type == SessionMessageTypes.Playstate
            && SessionMessageReader.ReadPayload<PlaystateRequestMessage>(data) is
                { Command: "Seek", SeekPositionTicks: 6000000000 },
            "a Playstate frame parses its command and seek target");

        check(SessionMessageReader.TryRead(
                // Three '$' rather than two: the argument bag closes with '}}', which at two would
                // be read as the end of an interpolation hole.
                $$$"""{"MessageType":"GeneralCommand","Data":{"Name":"SetVolume","ControllingUserId":"{{{user:D}}}","Arguments":{"Volume":"37"}},"MessageId":"{{{Guid.NewGuid():D}}}"}""",
                out type, out data)
            && type == SessionMessageTypes.GeneralCommand
            && SessionMessageReader.ReadPayload<GeneralCommandMessage>(data) is { Name: "SetVolume" } command
            && command.Argument("volume") == "37" && command.IntArgument("Volume") == 37
            && command.Argument("Index") is null && command.IntArgument("Index") is null,
            "a GeneralCommand frame parses string arguments case-insensitively and as numbers");

        check(SessionMessageReader.TryRead(
                $$"""{"MessageType":"LibraryChanged","Data":{"FoldersAddedTo":[],"FoldersRemovedFrom":[],"ItemsAdded":[],"ItemsRemoved":["{{item:N}}","not-a-guid"],"ItemsUpdated":[],"CollectionFolders":["{{user:N}}"],"IsEmpty":false},"MessageId":"{{Guid.NewGuid():D}}"}""",
                out type, out data)
            && type == SessionMessageTypes.LibraryChanged
            && SessionMessageReader.ReadPayload<LibraryUpdateMessage>(data) is { IsEmpty: false } library
            && library.AffectedItemIds() is [var removed] && removed == item
            && library.AffectedFolderIds() is [var folder] && folder == user,
            "a LibraryChanged frame parses dashless ids and skips junk");

        check(SessionMessageReader.TryRead(
                """{"MessageType":"LibraryChanged","Data":{"FoldersAddedTo":[],"FoldersRemovedFrom":[],"ItemsAdded":[],"ItemsRemoved":[],"ItemsUpdated":[],"CollectionFolders":[],"IsEmpty":true},"MessageId":"x"}""",
                out _, out data)
            && SessionMessageReader.ReadPayload<LibraryUpdateMessage>(data) is { IsEmpty: true },
            "an empty LibraryChanged batch reports itself empty");

        check(SessionMessageReader.TryRead(
                $$"""{"MessageType":"UserDataChanged","Data":{"UserId":"{{user:D}}","UserDataList":[{"Key":"4188","ItemId":"{{item:D}}","Played":true,"PlayCount":2,"PlaybackPositionTicks":0,"IsFavorite":false,"PlayedPercentage":100.0,"LastPlayedDate":"2026-09-15T10:00:00.0000000Z"}]},"MessageId":"{{Guid.NewGuid():D}}"}""",
                out type, out data)
            && type == SessionMessageTypes.UserDataChanged
            && SessionMessageReader.ReadPayload<UserDataChangeMessage>(data) is { } changed
            && changed.UserDataList is [{ Played: true, PlayCount: 2, IsFavorite: false, Key: "4188" } entry]
            && entry.ItemId == item,
            "a UserDataChanged batch parses its per-item rows");

        check(SessionMessageReader.TryRead("""{"MessageType":"KeepAlive","MessageId":"x"}""", out type, out data)
            && type == SessionMessageTypes.KeepAlive && data.ValueKind == JsonValueKind.Undefined,
            "a payload-free KeepAlive frame is a valid envelope");

        check(!SessionMessageReader.TryRead("{ this is not json", out _, out _)
            && !SessionMessageReader.TryRead("""{"Data":{"ItemIds":[]}}""", out _, out _)
            && !SessionMessageReader.TryRead("[]", out _, out _)
            && !SessionMessageReader.TryRead("", out _, out _),
            "malformed and envelope-less frames are rejected rather than thrown on");

        check(SessionMessageReader.TryRead("""{"MessageType":"Play","Data":"not-an-object"}""", out _, out data)
            && SessionMessageReader.ReadPayload<PlayRequestMessage>(data) is null,
            "a payload of the wrong shape yields no message instead of an exception");
    }

    private static void CheckSyncPlayGuidParsing(Action<bool, string> check)
    {
        var group = Guid.NewGuid();
        var entry = Guid.NewGuid();
        var media = Guid.NewGuid();
        T? Read<T>(string json) where T : class
        {
            using var document = JsonDocument.Parse(json);
            return SessionMessageReader.ReadPayload<T>(document.RootElement);
        }

        foreach (var format in new[] { "N", "D" })
        {
            var joined = Read<SyncPlayGroupUpdateMessage>($$"""{"GroupId":"{{group.ToString(format)}}","Type":"GroupJoined","Data":{"GroupId":"{{group.ToString(format)}}"} }""");
            check(joined?.GroupId == group, $"a {format}-format outer SyncPlay group id parses");
            check(joined?.GroupInfo()?.GroupId == group, $"a {format}-format nested GroupJoined id parses");
            var command = Read<SyncPlayCommandMessage>($$"""{"GroupId":"{{group.ToString(format)}}","PlaylistItemId":"{{entry.ToString(format)}}","Command":"Pause"}""");
            check(command?.GroupId == group && command.PlaylistItemId == entry,
                $"{format}-format SyncPlay command group and playlist ids parse");
            foreach (var alias in new[] { "ItemId", "Id" })
            {
                var queue = Read<SyncPlayQueueUpdate>($$"""{"Playlist":[{"{{alias}}":"{{media.ToString(format)}}","PlaylistItemId":"{{entry.ToString(format)}}"}]}""");
                check(queue?.Playlist is [{ } item] && item.Item == media && item.PlaylistItem == entry,
                    $"a {format}-format queue {alias} resolves the library item and playlist entry");
            }
        }

        foreach (var fields in new[] { "", "\"GroupId\":null," })
        {
            check(Read<SyncPlayGroupUpdateMessage>($$"""{ {{fields}}"Type":"GroupJoined","Data":{ } }""") is { GroupId: null },
                "an omitted or null outer group id remains optional");
            check(Read<SyncPlayGroupInfo>("{" + fields.TrimEnd(',') + "}") is { GroupId: null },
                "an omitted or null nested GroupJoined id remains optional");
        }
        check(Read<SyncPlayCommandMessage>("{}") is { GroupId: null, PlaylistItemId: null }
            && Read<SyncPlayCommandMessage>("""{"GroupId":null,"PlaylistItemId":null}""") is { GroupId: null, PlaylistItemId: null },
            "omitted and null command ids remain optional");
        check(Read<SyncPlayQueueUpdate>("""{"Playlist":[{}, {"ItemId":null,"Id":null,"PlaylistItemId":null}]}""")
                is { Playlist: [{ Item: null, PlaylistItem: null }, { Item: null, PlaylistItem: null }] },
            "omitted and null queue ids remain optional");

        foreach (var invalid in new[] { "\"not-a-guid\"", "17", "true", "{}", "[]" })
        {
            check(Read<SyncPlayGroupUpdateMessage>($$"""{"GroupId":{{invalid}},"Type":"GroupJoined","Data":{ } }""") is null,
                $"invalid outer group id {invalid} rejects its payload");
            check(Read<SyncPlayGroupInfo>($$"""{"GroupId":{{invalid}}}""") is null,
                $"invalid nested GroupJoined id {invalid} rejects its payload");
            foreach (var field in new[] { "GroupId", "PlaylistItemId" })
                check(Read<SyncPlayCommandMessage>($$"""{"{{field}}":{{invalid}},"Command":"Pause"}""") is null,
                    $"invalid command {field} {invalid} rejects its payload");
            foreach (var alias in new[] { "ItemId", "Id" })
                check(Read<SyncPlayQueueUpdate>($$"""{"Playlist":[{"{{alias}}":{{invalid}}}]}""") is null,
                    $"invalid queue {alias} {invalid} rejects its payload");
        }
    }

    // SyncPlay's two frames, shaped the way the server writes them. The command carries instants,
    // which is the half that can go wrong silently: every one of them is server UTC, and a client
    // that read one as local time would be a whole timezone out of step with the group.
    private static void CheckSyncPlayParsing(Action<bool, string> check)
    {
        var group = Guid.NewGuid();
        var item = Guid.NewGuid();
        var when = new DateTime(2026, 9, 15, 20, 30, 0, DateTimeKind.Utc);

        check(SessionMessageReader.TryRead(
                SyncCommand(group, item, "Unpause", "2026-09-15T20:30:00.0000000Z",
                    "2026-09-15T20:29:59.5000000Z", 6000000000),
                out var type, out var data)
            && type == SessionMessageTypes.SyncPlayCommand
            && SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(data) is
                { Command: "Unpause", PositionTicks: 6000000000 } unpause
            && unpause.GroupId == group && unpause.PlaylistItemId == item
            && unpause.WhenUtc is { Kind: DateTimeKind.Utc } whenUtc && whenUtc.Ticks == when.Ticks
            && unpause.EmittedAtUtc is { Kind: DateTimeKind.Utc } emitted
            && emitted.Ticks == when.AddMilliseconds(-500).Ticks,
            "a SyncPlayCommand frame parses and its instants round-trip as UTC");

        check(new[] { "Unpause", "Pause", "Stop", "Seek" }.All(command =>
                SessionMessageReader.TryRead(
                    SyncCommand(group, item, command, "2026-09-15T20:30:00.0000000Z",
                        "2026-09-15T20:29:59.5000000Z", 6000000000), out _, out var each)
                && SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(each)?.Command == command),
            "all four SendCommand verbs parse");

        check(SessionMessageReader.TryRead(
                SyncCommand(group, item, "Stop", "2026-09-15T20:30:00.0000000Z", "2026-09-15T20:29:59.5000000Z", null),
                out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(data) is
                { Command: "Stop", PositionTicks: null },
            "an omitted PositionTicks stays null rather than becoming zero");

        check(SessionMessageReader.TryRead(
                SyncCommand(group, item, "Fastforward", "2026-09-15T20:30:00.0000000Z",
                    "2026-09-15T20:29:59.5000000Z", 0), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(data) is { Command: "Fastforward" },
            "a verb this client does not know parses rather than failing the frame");

        var info = $$"""{"GroupId":"{{group:N}}","GroupName":"Movie night","State":"Waiting","Participants":["alice","bob"],"LastUpdatedAt":"2026-09-15T20:29:00.0000000Z"}""";
        check(SessionMessageReader.TryRead(GroupUpdate(group, "GroupJoined", info), out type, out data)
            && type == SessionMessageTypes.SyncPlayGroupUpdate
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is
                { Type: SyncPlayGroupUpdateTypes.GroupJoined } joined
            && joined.GroupId == group
            && joined.GroupInfo() is { GroupName: "Movie night", State: "Waiting" } joinedInfo
            && joinedInfo.Participants is ["alice", "bob"],
            "a GroupJoined update parses the group it joined");

        var media = Guid.NewGuid();
        var queue = $$"""{"Reason":"NewPlaylist","LastUpdate":"2026-09-15T20:29:10.0000000Z","Playlist":[{"ItemId":"{{media:N}}","PlaylistItemId":"{{item:N}}"}],"PlayingItemIndex":0,"StartPositionTicks":6000000000,"IsPlaying":false,"ShuffleMode":"Sorted","RepeatMode":"RepeatNone"}""";
        check(SessionMessageReader.TryRead(GroupUpdate(group, "PlayQueue", queue), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is
                { Type: SyncPlayGroupUpdateTypes.PlayQueue } queued
            && queued.Queue() is { Reason: "NewPlaylist", PlayingItemIndex: 0, IsPlaying: false,
                StartPositionTicks: 6000000000, ShuffleMode: "Sorted", RepeatMode: "RepeatNone" } update
            && update.LastUpdate?.UtcDateTime.Ticks
                == new DateTime(2026, 9, 15, 20, 29, 10, DateTimeKind.Utc).Ticks
            && update.Playlist is [{ } entry] && entry.PlaylistItem == item && entry.Item == media,
            "a PlayQueue update parses its playlist and its monotonic guard");

        // Playlist ids are strings on the server; both spellings must resolve to the same entry.
        var dashless = $$"""{"Reason":"NewPlaylist","Playlist":[{"ItemId":"{{media:N}}","PlaylistItemId":"{{item:N}}"}],"PlayingItemIndex":0}""";
        check(SessionMessageReader.TryRead(GroupUpdate(group, "PlayQueue", dashless), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data)?.Queue() is
                { Playlist: [{ } dashlessEntry] }
            && dashlessEntry.PlaylistItem == item && dashlessEntry.Item == media,
            "a dashless playlist item id parses as the same entry rather than dropping the queue");

        // And an id that is not one at all costs the field, not the payload: everything else in
        // the update still has to arrive, because the queue is what the player binding loads from.
        var unparseable = $$"""{"Reason":"NewPlaylist","Playlist":[{"ItemId":"{{media:N}}","PlaylistItemId":"not-an-id"}],"PlayingItemIndex":0}""";
        check(SessionMessageReader.TryRead(GroupUpdate(group, "PlayQueue", unparseable), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data)?.Queue() is
                { Reason: "NewPlaylist", PlayingItemIndex: 0, Playlist: [{ } junkEntry] }
            && junkEntry.PlaylistItem is null && junkEntry.Item == media,
            "a playlist item id that is not an id costs that field, not the whole queue update");

        // The SDK's generated QueueItem calls the library item Id; the server source calls it
        // ItemId. Whichever this server writes, the entry has to end up pointing at the item.
        var renamed = $$"""{"Reason":"NewPlaylist","Playlist":[{"Id":"{{media:N}}","PlaylistItemId":"{{item:N}}"}],"PlayingItemIndex":0}""";
        check(SessionMessageReader.TryRead(GroupUpdate(group, "PlayQueue", renamed), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data)?.Queue()?.Playlist
                is [{ } renamedEntry]
            && renamedEntry.Item == media && renamedEntry.PlaylistItem == item,
            "a playlist entry that names its item Id rather than ItemId still points at the item");

        check(SessionMessageReader.TryRead(
                GroupUpdate(group, "StateUpdate", """{"State":"Playing","Reason":"Unpause"}"""), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is
                { Type: SyncPlayGroupUpdateTypes.StateUpdate } stated
            && stated.State() is { State: "Playing", Reason: "Unpause" },
            "a StateUpdate update parses the group's new state");

        // The five string-payload members. UserJoined and UserLeft carry a display NAME, the other
        // three a group id — which is why nothing here tries to parse the payload as a guid.
        check(SessionMessageReader.TryRead(GroupUpdate(group, "UserJoined", "\"alice\""), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is
                { Type: SyncPlayGroupUpdateTypes.UserJoined } userJoined
            && userJoined.Text() == "alice" && userJoined.GroupInfo() is null,
            "a UserJoined update parses the user's name");
        check(SessionMessageReader.TryRead(GroupUpdate(group, "UserLeft", "\"bob\""), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data)?.Text() == "bob",
            "a UserLeft update parses the user's name");
        foreach (var member in new[] { "GroupLeft", "NotInGroup", "GroupDoesNotExist", "LibraryAccessDenied" })
            check(SessionMessageReader.TryRead(GroupUpdate(group, member, $"\"{group:D}\""), out _, out var each)
                && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(each) is { } message
                && message.Type == member && message.Text() == group.ToString("D"),
                $"a {member} update parses the group it names");

        check(SessionMessageReader.TryRead(GroupUpdate(group, "CreateGroupDenied", "\"stale\""), out _, out data)
            && SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is
                { Type: "CreateGroupDenied" } unknown
            && unknown.GroupInfo() is null && unknown.Queue() is null && unknown.State() is null
            && unknown.Text() == "stale",
            "an update type this client does not know parses and narrows to nothing");
    }

    /// <summary>A <c>SyncPlayCommand</c> frame. A null <paramref name="positionTicks"/> omits the
    /// field entirely, which is what the server does with a null.</summary>
    internal static string SyncCommand(Guid group, Guid item, string command, string when,
        string emittedAt, long? positionTicks)
    {
        var position = positionTicks is { } ticks
            ? FormattableString.Invariant($"\"PositionTicks\":{ticks},")
            : "";
        return $$"""{"MessageType":"SyncPlayCommand","Data":{"GroupId":"{{group:N}}","PlaylistItemId":"{{item:N}}","When":"{{when}}",{{position}}"Command":"{{command}}","EmittedAt":"{{emittedAt}}"},"MessageId":"{{Guid.NewGuid():N}}"}""";
    }

    /// <summary>A <c>SyncPlayGroupUpdate</c> frame around an already-serialized payload — the union
    /// has nine shapes and five of them are not objects at all.</summary>
    internal static string GroupUpdate(Guid group, string type, string payload)
        => $$"""{"MessageType":"SyncPlayGroupUpdate","Data":{"GroupId":"{{group:N}}","Type":"{{type}}","Data":{{payload}}},"MessageId":"{{Guid.NewGuid():N}}"}""";

    // Both SyncPlay frames over the wire, through the real SessionSocket and the real
    // LiveSessionService. The profile key is the point of the second one: two warm profiles mean
    // two groups on two servers, and a command applied against the wrong one would move the wrong
    // playback. Its own server, for the same reason CheckRejectedTokenAsync has one.
    private static async Task CheckSyncPlayTransportAsync(Action<bool, string> check)
    {
        const string profileKey = "sync-play-profile";
        using var server = new FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the sync play fixture session connects");
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        using var live = new LiveSessionService(key => key == profileKey ? service : null);
        var commands = Channel.CreateUnbounded<(string Profile, SyncPlayCommandMessage Message)>();
        var updates = Channel.CreateUnbounded<(string Profile, SyncPlayGroupUpdateMessage Message)>();
        live.SyncPlayCommandReceived += (profile, message) => commands.Writer.TryWrite((profile, message));
        live.SyncPlayGroupUpdated += (profile, message) => updates.Writer.TryWrite((profile, message));
        live.StartProfile(profileKey);

        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var group = Guid.NewGuid();
        var item = Guid.NewGuid();
        // The two frames this client cannot act on go FIRST, so everything after them is evidence
        // that they cost one message rather than the receive loop.
        await connection.SendAsync(GroupUpdate(group, "CreateGroupDenied", "\"stale\"")).ConfigureAwait(false);
        await connection.SendAsync(SyncCommand(group, item, "Fastforward",
            "2026-09-15T20:30:00.0000000Z", "2026-09-15T20:29:59.5000000Z", 0)).ConfigureAwait(false);
        await connection.SendAsync(GroupUpdate(group, "StateUpdate",
            """{"State":"Playing","Reason":"Unpause"}""")).ConfigureAwait(false);
        await connection.SendAsync(SyncCommand(group, item, "Unpause",
            "2026-09-15T20:30:00.0000000Z", "2026-09-15T20:29:59.5000000Z", 6000000000)).ConfigureAwait(false);

        async Task<(string Profile, SyncPlayGroupUpdateMessage Message)> NextUpdateAsync() => await updates.Reader
            .ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).AsTask().ConfigureAwait(false);
        async Task<(string Profile, SyncPlayCommandMessage Message)> NextCommandAsync() => await commands.Reader
            .ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).AsTask().ConfigureAwait(false);

        var unknownUpdate = await NextUpdateAsync().ConfigureAwait(false);
        check(unknownUpdate.Message.Type == "CreateGroupDenied" && unknownUpdate.Message.GroupInfo() is null,
            "an unknown group update type reaches the handler and narrows to nothing");
        var known = await NextUpdateAsync().ConfigureAwait(false);
        check(known.Profile == profileKey && known.Message.State() is { State: "Playing" }
            && known.Message.GroupId == group,
            "a SyncPlayGroupUpdate arrives on the profile that received it");

        var unknownCommand = await NextCommandAsync().ConfigureAwait(false);
        check(unknownCommand.Message.Command == "Fastforward",
            "an unknown SendCommand verb reaches the handler instead of failing the frame");
        var command = await NextCommandAsync().ConfigureAwait(false);
        check(command.Profile == profileKey && command.Message is { Command: "Unpause", PositionTicks: 6000000000 }
            && command.Message.PlaylistItemId == item
            && command.Message.WhenUtc is { Kind: DateTimeKind.Utc },
            "a SyncPlayCommand arrives on the profile that received it");
    }

    // What a frame MEANS, once it has parsed. Everything here goes through the real envelope
    // reader first rather than hand-building the message records: an argument bag that arrives as
    // JSON strings is exactly where this mapping can go wrong, and a record built in C# would have
    // the ints already.
    private static void CheckCommandResolution(Action<bool, string> check)
    {
        static RemoteAction General(string json)
        {
            SessionMessageReader.TryRead(
                $$"""{"MessageType":"GeneralCommand","Data":{{json}},"MessageId":"x"}""",
                out _, out var data);
            return RemoteCommand.Resolve(SessionMessageReader.ReadPayload<GeneralCommandMessage>(data));
        }

        static RemoteAction Playstate(string json)
        {
            SessionMessageReader.TryRead(
                $$"""{"MessageType":"Playstate","Data":{{json}},"MessageId":"x"}""",
                out _, out var data);
            return RemoteCommand.Resolve(SessionMessageReader.ReadPayload<PlaystateRequestMessage>(data));
        }

        check(General("""{"Name":"SetVolume","Arguments":{"Volume":"37"}}""")
                is { Kind: RemoteActionKind.SetVolume, Number: 37 },
            "SetVolume resolves its string argument to a level");
        check(General("""{"Name":"SetVolume","Arguments":{"Volume":"150"}}""")
                is { Kind: RemoteActionKind.SetVolume, Number: 100 }
            && General("""{"Name":"SetVolume","Arguments":{"Volume":"-5"}}""")
                is { Kind: RemoteActionKind.SetVolume, Number: 0 },
            "a level outside 0-100 is clamped rather than passed on");
        check(General("""{"Name":"SetVolume","Arguments":{"Volume":"abc"}}""").Kind == RemoteActionKind.Noop,
            "an unparseable level is a no-op, not a silent SetVolume(0)");
        check(General("""{"Name":"SetVolume"}""").Kind == RemoteActionKind.Noop,
            "SetVolume without an argument bag is a no-op");
        check(General("""{"Name":"abc","Arguments":{}}""").Kind == RemoteActionKind.Noop
            && General("""{"Name":null,"Arguments":{}}""").Kind == RemoteActionKind.Noop,
            "an unknown or absent command name is a no-op");
        check(General("""{"Name":"ToggleMute","Arguments":{}}""").Kind == RemoteActionKind.ToggleMute
            && General("""{"Name":"VolumeUp","Arguments":{}}""").Kind == RemoteActionKind.VolumeUp
            && General("""{"Name":"ToggleFullscreen","Arguments":{}}""").Kind == RemoteActionKind.ToggleFullscreen,
            "the argument-free general commands resolve by name");
        check(General("""{"Name":"DisplayMessage","Arguments":{"Header":"Hi","Text":"there"}}""")
                is { Kind: RemoteActionKind.DisplayMessage, Header: "Hi", Text: "there" },
            "DisplayMessage carries its header and text");
        check(General("""{"Name":"SetSubtitleStreamIndex","Arguments":{"Index":"-1"}}""")
                is { Kind: RemoteActionKind.SetSubtitleStreamIndex, Number: -1 }
            && General("""{"Name":"SetAudioStreamIndex","Arguments":{"Index":"3"}}""")
                is { Kind: RemoteActionKind.SetAudioStreamIndex, Number: 3 }
            && General("""{"Name":"SetAudioStreamIndex","Arguments":{}}""").Kind == RemoteActionKind.Noop,
            "a stream index resolves as itself, including the negative 'no subtitles'");

        // 6000000000 ticks is 600 s. A wrong divisor here seeks to the wrong place with nothing in
        // the log to say so, which is the whole reason the conversion is not at the call site.
        check(Playstate("""{"Command":"Seek","SeekPositionTicks":6000000000}""")
                is { Kind: RemoteActionKind.Seek, Number: 600 },
            "Seek converts ticks to seconds");
        check(Playstate("""{"Command":"Seek"}""").Kind == RemoteActionKind.Noop,
            "Seek without a target is a no-op");
        check(Playstate("""{"Command":"Play"}""").Kind == RemoteActionKind.Unpause
            && Playstate("""{"Command":"Unpause"}""").Kind == RemoteActionKind.Unpause
            && Playstate("""{"Command":"Pause"}""").Kind == RemoteActionKind.Pause
            && Playstate("""{"Command":"PlayPause"}""").Kind == RemoteActionKind.PlayPause,
            "the legacy Play command resolves to Unpause alongside the current names");
        check(Playstate("""{"Command":"Stop"}""").Kind == RemoteActionKind.Stop
            && Playstate("""{"Command":"NextTrack"}""").Kind == RemoteActionKind.NextTrack
            && Playstate("""{"Command":"FastForward"}""").Kind == RemoteActionKind.FastForward
            && Playstate("""{"Command":"Nonsense"}""").Kind == RemoteActionKind.Noop,
            "the remaining transport commands resolve by name and an unknown one does not");

        check(RemoteCommand.ResolvePlay("PlayNow") == RemotePlayKind.PlayNow
            && RemoteCommand.ResolvePlay("PlayNext") == RemotePlayKind.PlayNext
            && RemoteCommand.ResolvePlay("PlayLast") == RemotePlayKind.PlayLast,
            "the three supported play commands resolve");
        check(RemoteCommand.ResolvePlay("PlayInstantMix") == RemotePlayKind.Noop
            && RemoteCommand.ResolvePlay("PlayShuffle") == RemotePlayKind.Noop
            && RemoteCommand.ResolvePlay(null) == RemotePlayKind.Noop,
            "the unadvertised play commands resolve to no-ops");
    }

    // Which SOURCE a remote stream index is resolved against. The projection GetMediaStreamsAsync
    // returns always describes the item's DEFAULT source; the dashboard's track list comes from
    // the source that is playing. Resolving one against the other picks whatever stream the other
    // file happens to have at that index — a different track, reported as a success.
    private static void CheckStreamResolution(Action<bool, string> check)
    {
        static MediaStreamChoice Stream(int index, int ordinal, string display)
            => new(index, ordinal, display, "eng", display, IsDefault: false, IsExternal: false);

        // Source A, the item's default: two audio streams at indices 1 and 3.
        var defaultSource = new MediaSourceStreams("source-a",
            [Stream(0, 0, "video")],
            [Stream(1, 0, "A first"), Stream(3, 1, "A second")],
            [Stream(4, 0, "A subs")],
            []);

        check(RemoteCommand.ResolveStream(defaultSource, "source-a", audio: true, 3)
                is { Kind: RemoteStreamKind.Stream, Choice.Display: "A second" },
            "an index resolves to its own row when the playing source is the one projected");
        check(RemoteCommand.ResolveStream(defaultSource, null, audio: true, 3)
                is { Kind: RemoteStreamKind.Stream, Choice.Display: "A second" },
            "with no live decision to contradict it the projection is taken as the playing source");
        check(RemoteCommand.ResolveStream(defaultSource, "source-a", audio: true, 9)
                .Kind == RemoteStreamKind.Unknown,
            "an index no stream carries is unknown rather than a nearby row");
        check(RemoteCommand.ResolveStream(null, "source-a", audio: true, 3)
                .Kind == RemoteStreamKind.Unknown,
            "a failed stream fetch is unknown, not a guess");

        // The blocker: playing the 4K remux (source B) while the projection still describes A.
        check(RemoteCommand.ResolveStream(defaultSource, "source-b", audio: true, 3)
                .Kind != RemoteStreamKind.Stream,
            "an index for the playing source is never answered from the default source's list");
        check(RemoteCommand.ResolveStream(defaultSource, "source-b", audio: false, 4)
                .Kind != RemoteStreamKind.Stream,
            "the same holds for a subtitle index");
        // "No subtitles" is the one index that needs no source at all, so a mismatch must not
        // take it away: it means the same thing in every file.
        check(RemoteCommand.ResolveStream(defaultSource, "source-b", audio: false, -1)
                .Kind == RemoteStreamKind.Off,
            "the negative 'no subtitles' index still resolves when the sources differ");
    }

    // Which SESSION a remote PlayNext / PlayLast is allowed to queue onto. There is one queue and
    // one playback session: an item queued from a backgrounded profile would be negotiated and
    // progress-reported under the ACTIVE account, and on another server its id means nothing.
    private static void CheckPlayTargeting(Action<bool, string> check)
    {
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayNow, playing: false,
                ownsPlayback: false) == RemotePlayKind.PlayNow,
            "PlayNow is a cast and stays valid from a backgrounded profile");
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.Noop, playing: true,
                ownsPlayback: true) == RemotePlayKind.Noop,
            "an unsupported play command stays a no-op");
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayNext, playing: false,
                ownsPlayback: true) == RemotePlayKind.PlayNow
            && RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayLast, playing: false,
                ownsPlayback: true) == RemotePlayKind.PlayNow,
            "with nothing playing, queueing becomes playing");
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayNext, playing: true,
                ownsPlayback: true) == RemotePlayKind.PlayNext
            && RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayLast, playing: true,
                ownsPlayback: true) == RemotePlayKind.PlayLast,
            "the profile that owns the playback queues onto it");

        // A single item started from the detail view clears the queue, so gating this on "a queue
        // is active" made every play-next during a film a play-now that stopped the film.
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayNext, playing: true,
                ownsPlayback: true) != RemotePlayKind.PlayNow,
            "play next during a single item does not interrupt what is playing");

        // The blocker: profile B is warm and its socket is live, A is playing.
        check(RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayNext, playing: true,
                ownsPlayback: false) == RemotePlayKind.Noop
            && RemoteCommand.ResolvePlayTarget(RemotePlayKind.PlayLast, playing: true,
                ownsPlayback: false) == RemotePlayKind.Noop,
            "a profile that does not own the playback cannot queue onto it");
    }

    // The queue half of a remote PlayNext / PlayLast. Pure state, so it is asserted directly.
    private static void CheckQueueInsertion(Action<bool, string> check)
    {
        static MediaItem Item(string name) => new() { Id = Guid.NewGuid(), Name = name };
        static (PlayQueue Queue, Func<int> Changes) Fresh()
        {
            var queue = new PlayQueue();
            var changes = 0;
            queue.Changed += () => changes++;
            return (queue, () => changes);
        }

        var (empty, emptyChanges) = Fresh();
        empty.InsertNext([Item("a"), Item("b")]);
        check(empty.Count == 2 && empty.CurrentIndex == 0 && empty.Current?.Name == "a"
            && emptyChanges() == 1,
            "InsertNext on an empty queue behaves as Set and raises Changed once");

        var (appendEmpty, appendEmptyChanges) = Fresh();
        appendEmpty.Append([Item("a")]);
        check(appendEmpty.Count == 1 && appendEmpty.CurrentIndex == 0 && appendEmptyChanges() == 1,
            "Append on an empty queue behaves as Set and raises Changed once");

        var (queue, changes) = Fresh();
        var third = Item("c");
        queue.Set([Item("a"), Item("b"), third], 1);
        var playing = queue.Current;
        queue.InsertNext([Item("d"), Item("e")]);
        check(queue.Count == 5 && queue.CurrentIndex == 1 && ReferenceEquals(queue.Current, playing)
            && queue.Items[2].Name == "d" && queue.Items[3].Name == "e"
            && ReferenceEquals(queue.Items[4], third),
            "InsertNext lands after the playing entry without moving CurrentIndex");
        check(changes() == 2, "the mid-queue InsertNext raised Changed exactly once after the Set");

        queue.Append([Item("f")]);
        check(queue.Count == 6 && queue.Items[5].Name == "f" && queue.CurrentIndex == 1
            && changes() == 3,
            "Append lands at the end, leaves CurrentIndex alone and raises Changed once");

        queue.InsertNext([]);
        queue.Append([]);
        check(queue.Count == 6 && changes() == 3,
            "an empty insertion changes nothing and raises nothing");
    }

    // The deleted-items fix, at the layer that can be asserted offscreen: an entry that was
    // readable stops being readable, both for one named folder and for the blanket case where the
    // server named items but no folder at all.
    private static async Task CheckFolderInvalidationAsync(Action<bool, string> check)
    {
        var profile = $"{Guid.NewGuid():N}@http://fixture.invalid";
        var other = $"{Guid.NewGuid():N}@http://fixture.invalid";
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        static FolderCacheEntry<MediaItem> Entry(string name) => new(
            [new MediaItem { Id = Guid.NewGuid(), Name = name }], 1, Complete: true, Truncated: false,
            DateTime.UtcNow, BrowseFolderCache.CurrentSchema);

        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(profile, first), Entry("one"))
            .ConfigureAwait(false);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(profile, first))
                .ConfigureAwait(false) is { Items: [{ Name: "one" }] },
            "a stored folder entry reads back");

        BrowseFolderCache.Invalidate(profile, first);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(profile, first))
                .ConfigureAwait(false) is null,
            "Invalidate drops the folder entry it names");

        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(profile, first), Entry("one"))
            .ConfigureAwait(false);
        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(profile, second), Entry("two"))
            .ConfigureAwait(false);
        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(other, first), Entry("theirs"))
            .ConfigureAwait(false);
        BrowseFolderCache.Invalidate(profile, first);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(profile, second))
                .ConfigureAwait(false) is { Items: [{ Name: "two" }] },
            "invalidating one folder leaves the profile's other folders alone");

        await BrowseFolderCache.InvalidateProfileAsync(profile).ConfigureAwait(false);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(profile, second))
                .ConfigureAwait(false) is null,
            "the blanket invalidation reaches a folder no message named");
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(other, first))
                .ConfigureAwait(false) is { Items: [{ Name: "theirs" }] },
            "the blanket invalidation stops at the profile it was given");
    }

    // The index is a DIRECTORY, not a projection — but it lives in the cache the 30-day age sweep
    // walks, and that sweep can only judge a file by its own mtime. An index rewritten only when a
    // NEW folder id appears goes stale under a settled library and is swept away from under the
    // entries it lists; the blanket invalidation then enumerates nothing, deletes nothing, and
    // logs that it worked.
    private static async Task CheckFolderIndexSurvivalAsync(Action<bool, string> check)
    {
        var settled = $"{Guid.NewGuid():N}@http://fixture.invalid";
        var visited = Guid.NewGuid();
        var abandoned = Guid.NewGuid();

        static FolderCacheEntry<MediaItem> Entry(string name) => new(
            [new MediaItem { Id = Guid.NewGuid(), Name = name }], 1, Complete: true, Truncated: false,
            DateTime.UtcNow, BrowseFolderCache.CurrentSchema);

        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(settled, visited), Entry("visited"))
            .ConfigureAwait(false);
        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(settled, abandoned), Entry("abandoned"))
            .ConfigureAwait(false);
        // Asserted rather than assumed: everything below depends on the index being a file in the
        // directory the sweep walks, under the same name rule as every other entry.
        check(File.Exists(CachePath(IndexKey(settled))),
            "the folder index is an ordinary entry in the swept cache directory");

        // Forty days on with nothing new in the library: age every file this profile owns, then
        // re-store the one folder the user keeps opening. That store is all a settled library ever
        // does, and its folder id is already listed.
        Backdate(BrowseFolderCache.Key(settled, visited));
        Backdate(BrowseFolderCache.Key(settled, abandoned));
        Backdate(IndexKey(settled));
        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(settled, visited), Entry("visited"))
            .ConfigureAwait(false);

        LightWeaver.Imaging.MetadataCache.EvictOlderThan(TimeSpan.FromDays(30));
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(settled, abandoned))
                .ConfigureAwait(false) is null,
            "the age sweep drops a folder entry nobody opened for thirty days");
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(settled, visited))
                .ConfigureAwait(false) is { Items: [{ Name: "visited" }] },
            "the age sweep keeps the folder that was re-stored");

        await BrowseFolderCache.InvalidateProfileAsync(settled).ConfigureAwait(false);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(settled, visited))
                .ConfigureAwait(false) is null,
            "the blanket invalidation still reaches a folder after a sweep ran over the index");

        // The upgrade path: every entry written before the index existed is invisible to the
        // blanket invalidation until its folder is next STORED, which on a settled library is
        // exactly what does not happen to the folders that matter most.
        var upgraded = $"{Guid.NewGuid():N}@http://fixture.invalid";
        var legacy = Guid.NewGuid();
        await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key(upgraded, legacy), Entry("legacy"))
            .ConfigureAwait(false);
        File.Delete(CachePath(IndexKey(upgraded)));
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(upgraded, legacy))
                .ConfigureAwait(false) is { Items: [{ Name: "legacy" }] },
            "an entry stored before the index existed still reads back");
        await BrowseFolderCache.InvalidateProfileAsync(upgraded).ConfigureAwait(false);
        check(await BrowseFolderCache.ReadAsync(BrowseFolderCache.Key(upgraded, legacy))
                .ConfigureAwait(false) is null,
            "reading an entry lists it, so the blanket invalidation reaches an upgraded install's folders");
    }

    /// <summary>Ages one cache entry past any plausible sweep cutoff. Throws when the key has no
    /// file, which is the point: a fixture that backdated nothing would assert nothing.</summary>
    private static void Backdate(string key)
        => File.SetLastWriteTimeUtc(CachePath(key), DateTime.UtcNow - TimeSpan.FromDays(40));

    /// <summary>The file behind a cache key, through the cache's OWN name rule rather than a copy
    /// of it — the layout is SHA-1 of the key, and a fixture that hashed the key itself would keep
    /// passing after the real rule moved.</summary>
    private static string CachePath(string key)
    {
        var name = typeof(LightWeaver.Imaging.DiskJsonCache)
            .GetMethod("CacheFileName", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, [key]) as string
            ?? throw new InvalidOperationException(
                "DiskJsonCache has no private static CacheFileName; the on-disk layout moved, so "
                + "this fixture is testing nothing.");
        return Path.Combine(LightWeaver.AppPaths.Root, "metacache", name);
    }

    /// <summary>The profile index's own cache key, read off the production method so a change to
    /// its shape cannot leave this fixture backdating a file nothing uses.</summary>
    private static string IndexKey(string profileKey)
        => typeof(BrowseFolderCache)
            .GetMethod("IndexKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, [profileKey]) as string
        ?? throw new InvalidOperationException(
            "BrowseFolderCache has no private static IndexKey; the profile index was renamed, so "
            + "this fixture is testing nothing.");

    private static async Task CheckTransportAsync(Action<bool, string> check)
    {
        using var server = new FakeJellyfinServer();
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the production service accepts the fixture session");
        var me = await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(me.Path.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase),
            "the fixture session is validated against Users/Me");

        check(await service.ReportCapabilitiesAsync().ConfigureAwait(false), "capabilities post succeeds");
        var capabilities = await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(capabilities.Method == "POST" && capabilities.Path == "/Sessions/Capabilities/Full",
            "capabilities go to Sessions/Capabilities/Full");
        using (var body = JsonDocument.Parse(capabilities.Body))
        {
            var root = body.RootElement;
            var media = Strings(root, "PlayableMediaTypes");
            var commands = Strings(root, "SupportedCommands");
            check(root.GetProperty("SupportsMediaControl").GetBoolean()
                && root.GetProperty("SupportsPersistentIdentifier").GetBoolean(),
                "capabilities claim media control and a persistent identifier");
            check(media.Contains("Video") && media.Contains("Audio"),
                "capabilities advertise both playable media types");
            string[] expected =
            [
                "SetVolume", "VolumeUp", "VolumeDown", "Mute", "Unmute", "ToggleMute",
                "SetAudioStreamIndex", "SetSubtitleStreamIndex", "PlayNext", "DisplayMessage",
                "ToggleFullscreen", "Play",
            ];
            check(expected.All(commands.Contains) && commands.Count == expected.Length,
                "capabilities advertise exactly the twelve general commands");
            // Named explicitly because getting this wrong is silent: these are PlaystateCommands,
            // the GeneralCommandType enum has no such members, and the server would simply ignore
            // whatever a client invented for them.
            check(!commands.Contains("Pause") && !commands.Contains("Stop") && !commands.Contains("Seek"),
                "transport commands are not advertised as general commands");
        }

        server.CapabilitiesStatus = 500;
        var rejectedCapabilities = await service.ReportCapabilitiesAsync().ConfigureAwait(false);
        await server.NextRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(!rejectedCapabilities, "a rejected capabilities post reports failure instead of throwing");
        server.CapabilitiesStatus = 204;

        // Short base backoff so the reconnect leg does not have to sit through the production 2 s.
        using var socket = new SessionSocket(service, TimeSpan.FromMilliseconds(150));
        var commandsSeen = Channel.CreateUnbounded<GeneralCommandMessage>();
        socket.GeneralCommandReceived += message => commandsSeen.Writer.TryWrite(message);
        socket.Start();

        var upgrade = await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(upgrade.Path.EndsWith("/socket", StringComparison.Ordinal), "the upgrade targets /socket");
        check(upgrade.Header("Authorization")?.StartsWith("MediaBrowser Token=\"", StringComparison.Ordinal) == true,
            "the upgrade authenticates with the MediaBrowser Authorization header");
        check(upgrade.Query.Length == 0
            && !upgrade.Target.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            && !upgrade.Target.Contains("api_key", StringComparison.OrdinalIgnoreCase),
            "the upgrade carries no ApiKey or api_key query parameter");

        var connection = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // Polled, not read once: the server publishes the connection the moment it has written the
        // 101, which is before the client has finished reading it.
        check(await SettlesAsync(() => socket.IsConnected, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "the client reports the established connection");

        // Data is the server's socket timeout in SECONDS; one second here makes the T/2 interval
        // 500 ms, so two keep-alives are a bounded wait rather than a minute of it.
        await connection.SendAsync("""{"MessageType":"ForceKeepAlive","Data":1,"MessageId":"force-1"}""").ConfigureAwait(false);
        check(await connection.ReceiveAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                == """{"MessageType":"KeepAlive"}""",
            "ForceKeepAlive produces an immediate client KeepAlive");
        check(await connection.ReceiveAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                == """{"MessageType":"KeepAlive"}""",
            "the client keeps sending KeepAlive on the armed interval");

        async Task<GeneralCommandMessage> NextCommandAsync() => await commandsSeen.Reader
            .ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).AsTask().ConfigureAwait(false);

        await connection.SendAsync("{\"MessageType\":\"GeneralCom").ConfigureAwait(false);
        await connection.SendAsync(Command("ToggleMute")).ConfigureAwait(false);
        check((await NextCommandAsync().ConfigureAwait(false)).Name == "ToggleMute",
            "a malformed frame does not kill the receive loop");

        // Bigger than the socket's 16 KiB receive buffer, so the loop only sees it whole if it
        // accumulates until EndOfMessage — the one path the malformed-frame leg above cannot
        // reach, because that frame arrives complete in a single ReceiveAsync.
        var filler = new string('x', 40 * 1024);
        await connection.SendAsync(Command("DisplayMessage", filler)).ConfigureAwait(false);
        var assembled = await NextCommandAsync().ConfigureAwait(false);
        check(assembled.Name == "DisplayMessage" && assembled.Argument("Text") == filler,
            "a frame spanning several reads is reassembled intact");

        // Past MaxFrameBytes. Dropping it is the easy half; the half worth asserting is that the
        // loop stays in phase afterwards, since a frame boundary it got wrong would make every
        // later command unreadable rather than just this one.
        await connection.SendAsync(Command("Oversized", new string('y', 5 * 1024 * 1024))).ConfigureAwait(false);
        await connection.SendAsync(Command("Mute")).ConfigureAwait(false);
        check((await NextCommandAsync().ConfigureAwait(false)).Name == "Mute",
            "an oversized frame is dropped without desynchronising the ones after it");

        // A subscriber that throws costs one message, not the connection: this used to unwind the
        // receive loop and take the reconnect ladder with it for the life of the process, leaving
        // nothing behind but an unobserved-task crash file.
        socket.GeneralCommandReceived += message =>
        {
            if (message.Name == "Boom")
                throw new InvalidOperationException("subscriber fault");
        };
        await connection.SendAsync(Command("Boom")).ConfigureAwait(false);
        check((await NextCommandAsync().ConfigureAwait(false)).Name == "Boom",
            "a frame still reaches the subscribers ahead of one that throws");
        await connection.SendAsync(Command("VolumeUp")).ConfigureAwait(false);
        check((await NextCommandAsync().ConfigureAwait(false)).Name == "VolumeUp",
            "a subscriber exception costs one message, not the socket");

        // The cache-invalidation path, over the wire. The legs that cover it elsewhere call
        // ReadPayload and BrowseFolderCache directly, so nothing until now drove a LibraryChanged
        // frame through the dispatch that joins them — which is the path all three of the
        // silent-failure defects in this feature lived on, and none of them announced itself.
        //
        // The empty batch goes first, and the assertion that the frame which arrives is the
        // populated one is what says it never raised: the server coalesces library changes on a
        // 30 s timer and sends the tick per user whether or not anything happened, so the frame
        // that must NOT reach a subscriber is the one that arrives most often.
        var libraryUpdates = Channel.CreateUnbounded<LibraryUpdateMessage>();
        socket.LibraryChanged += message => libraryUpdates.Writer.TryWrite(message);
        var changedItem = Guid.NewGuid();
        var changedFolder = Guid.NewGuid();
        await connection.SendAsync(LibraryUpdate()).ConfigureAwait(false);
        await connection.SendAsync(LibraryUpdate(changedItem, changedFolder)).ConfigureAwait(false);
        var library = await libraryUpdates.Reader
            .ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).AsTask().ConfigureAwait(false);
        check(!library.IsEmpty
            && library.AffectedItemIds() is [var affectedItem] && affectedItem == changedItem
            && library.AffectedFolderIds() is [var affectedFolder] && affectedFolder == changedFolder,
            "a populated LibraryChanged frame reaches the handler with its ids, and an empty one does not");

        connection.Abort();
        var reconnect = await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        check(reconnect.Path.EndsWith("/socket", StringComparison.Ordinal),
            "an abrupt close is followed by a reconnect");
        var resumed = await server.NextConnectionAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // The timer is disarmed when a connection ends, so the replacement socket is silent until
        // the server asks again. If the second ForceKeepAlive were ignored the session would look
        // healthy and then be reaped sixty seconds later with no client-side sign of why.
        await resumed.SendAsync("""{"MessageType":"ForceKeepAlive","Data":1,"MessageId":"force-2"}""").ConfigureAwait(false);
        check(await resumed.ReceiveAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                == """{"MessageType":"KeepAlive"}""",
            "the keep-alive re-arms on the reconnected socket");
        socket.Dispose();
    }

    /// <summary>A GeneralCommand frame as the server writes it, optionally with a <c>Text</c>
    /// argument — the only one long enough to size a frame with. Three '$' for the same reason as
    /// the parsing checks above: the argument bag closes with '}}'.</summary>
    private static string Command(string name, string? text = null)
        => text is null
            ? $$$"""{"MessageType":"GeneralCommand","Data":{"Name":"{{{name}}}","Arguments":{}},"MessageId":"{{{name}}}"}"""
            : $$$"""{"MessageType":"GeneralCommand","Data":{"Name":"{{{name}}}","Arguments":{"Text":"{{{text}}}"}},"MessageId":"{{{name}}}"}""";

    /// <summary>A LibraryChanged frame as the server writes it: every list present, ids dashless,
    /// and the <c>IsEmpty</c> flag the server puts on its coalescing tick. No arguments is that
    /// tick — the batch where the timer fired and nothing had changed.</summary>
    private static string LibraryUpdate(Guid? item = null, Guid? folder = null)
        => $$"""{"MessageType":"LibraryChanged","Data":{"FoldersAddedTo":[{{Id(folder)}}],"FoldersRemovedFrom":[],"ItemsAdded":[],"ItemsRemoved":[{{Id(item)}}],"ItemsUpdated":[],"CollectionFolders":[],"IsEmpty":{{(item is null && folder is null ? "true" : "false")}}},"MessageId":"{{Guid.NewGuid():D}}"}""";

    private static string Id(Guid? id) => id is { } value ? $"\"{value:N}\"" : "";

    /// <summary>A server of its own rather than a flag flipped on the one above: a socket that is
    /// being torn down can still have a connect in flight, and a leftover upgrade landing in the
    /// shared channel would make "no further attempt" mean nothing.</summary>
    private static async Task CheckRejectedTokenAsync(Action<bool, string> check)
    {
        using var server = new FakeJellyfinServer { RejectUpgrades = true };
        using var service = new JellyfinService();
        check(await service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "revoked-fixture-token", server.UserId))
                .ConfigureAwait(false),
            "the rejecting fixture still establishes an ordinary session");

        using var socket = new SessionSocket(service, TimeSpan.FromMilliseconds(100));
        var rejections = 0;
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.AuthenticationRejected += () =>
        {
            Interlocked.Increment(ref rejections);
            rejected.TrySetResult();
        };
        socket.Start();

        await server.NextUpgradeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // Fifteen backoff intervals. A socket that retried a dead token would have been back
        // several times over by now.
        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
        check(Volatile.Read(ref rejections) == 1, "a refused upgrade raises AuthenticationRejected exactly once");
        check(server.UpgradeCount == 1, "a refused upgrade is never retried");
        socket.Dispose();
    }

    private static async Task<bool> SettlesAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }

    private static List<string> Strings(JsonElement root, string name)
        => root.GetProperty(name).EnumerateArray().Select(value => value.GetString() ?? "").ToList();

    internal sealed record HttpCapture(string Method, string Path, string Body);

    internal sealed record UpgradeCapture(string Target, string Path, string Query, IReadOnlyList<string> Headers)
    {
        public string? Header(string name) => Headers
            .FirstOrDefault(header => header.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?[(name.Length + 1)..].Trim();
    }

    internal sealed class ServerConnection(WebSocket socket, TcpClient client)
    {
        public Task SendAsync(string json) => socket
            .SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<string?> ReceiveAsync(TimeSpan timeout)
        {
            var buffer = new byte[8192];
            var assembled = new List<byte>();
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                assembled.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(assembled.ToArray());
            }
        }

        /// <summary>Linger zero, so the client sees a reset rather than a courteous close frame —
        /// which is what a server restart or a dropped LAN actually looks like.</summary>
        public void Abort()
        {
            client.Client.Close(0);
            client.Dispose();
        }
    }

    /// <summary>Internal rather than private: <c>SyncPlayFixture</c> drives the production
    /// <c>ServerClock</c> against this same server. One hand-rolled Jellyfin, grown a route at a
    /// time, beats a second one that drifts out of step with it.</summary>
    internal sealed class FakeJellyfinServer : IDisposable
    {
        private const string UpgradeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Channel<HttpCapture> _requests = Channel.CreateUnbounded<HttpCapture>();

        /// <summary>Every request, kept as well as queued. The channel answers "what came next",
        /// which is the wrong question for "exactly one Join was posted for that outage" — a leg
        /// that drained the channel looking for a second one could only ever prove it by waiting
        /// for a timeout.</summary>
        private readonly List<HttpCapture> _received = [];
        private readonly Channel<UpgradeCapture> _upgrades = Channel.CreateUnbounded<UpgradeCapture>();
        private readonly Channel<ServerConnection> _connections = Channel.CreateUnbounded<ServerConnection>();
        private int _upgradeCount;

        public FakeJellyfinServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = AcceptAsync(_cancellation.Token);
        }

        public string Origin { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public int CapabilitiesStatus { get; set; } = 204;

        /// <summary>Status for every <c>/SyncPlay/*</c> route except <c>Ping</c>. 403 is what a
        /// revoked <c>SyncPlayAccess</c> — or a session the server no longer has in the group —
        /// looks like, and it is the ONLY sign of either: there is no socket message for it.</summary>
        public int SyncPlayStatus { get; set; } = 204;

        /// <summary>How far ahead of this machine the fake server's clock runs. A skew a real LAN
        /// would never show, so a clock measurement that silently stopped reading the answer cannot
        /// pass for a good one.</summary>
        public TimeSpan ClockSkew { get; set; }

        /// <summary>How long <c>GET /GetUtcTime</c> sits on the answer. The clock believes the
        /// measurement with the SMALLEST delay, so a slow answer is the only way from outside to
        /// tell a client that emptied its measurement window from one that kept it: a new
        /// measurement that cannot win on delay changes the offset only if the old ones are
        /// gone.</summary>
        public TimeSpan ClockDelay { get; set; }

        /// <summary>How long <c>POST /SyncPlay/Join</c> sits on the answer. A restore is the one
        /// call with a race worth driving deterministically — a leave landing inside its round trip
        /// re-joins this session server-side — and a round trip that lasts seconds is how a fixture
        /// gets to stand in the middle of one.</summary>
        public TimeSpan JoinDelay { get; set; }
        public Task? BufferingResponseGate { get; set; }
        public Func<HttpCapture, Task<string?>>? ResponseOverride { get; set; }

        /// <summary>The single group <c>GET /SyncPlay/List</c> answers with.</summary>
        public Guid ListedGroupId { get; } = Guid.NewGuid();

        /// <summary>Settable, not <c>init</c>: a leg that revokes a token mid-session needs the
        /// upgrade that ESTABLISHED the socket to have been accepted first.</summary>
        public bool RejectUpgrades { get; set; }
        public int UpgradeCount => Volatile.Read(ref _upgradeCount);

        /// <summary>Every request so far, oldest first.</summary>
        public IReadOnlyList<HttpCapture> Requests
        {
            get
            {
                lock (_received)
                    return [.. _received];
            }
        }

        /// <summary>How many requests have reached one route. The counting form of
        /// <see cref="Requests"/>, which is what the "exactly one" assertions want.</summary>
        public int CountOf(string pathSuffix)
        {
            lock (_received)
                return _received.Count(request => request.Path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<HttpCapture> NextRequestAsync(TimeSpan timeout)
            => await _requests.Reader.ReadAsync(new CancellationTokenSource(timeout).Token).AsTask().ConfigureAwait(false);

        public async Task<UpgradeCapture> NextUpgradeAsync(TimeSpan timeout)
            => await _upgrades.Reader.ReadAsync(new CancellationTokenSource(timeout).Token).AsTask().ConfigureAwait(false);

        public async Task<ServerConnection> NextConnectionAsync(TimeSpan timeout)
            => await _connections.Reader.ReadAsync(new CancellationTokenSource(timeout).Token).AsTask().ConfigureAwait(false);

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
        }

        private async Task AcceptAsync(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                _ = ServeAsync(client, cancellation);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken cancellation)
        {
            var keepOpen = false;
            try
            {
                var stream = client.GetStream();
                if (await ReadHeadAsync(stream, cancellation).ConfigureAwait(false) is not { Count: > 0 } head)
                    return;
                var parts = head[0].Split(' ');
                var method = parts[0];
                var target = parts.Length > 1 ? parts[1] : "/";
                var split = target.IndexOf('?', StringComparison.Ordinal);
                var path = split < 0 ? target : target[..split];
                var query = split < 0 ? "" : target[(split + 1)..];
                var headers = head.Skip(1).ToList();

                if (headers.Any(header => header.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase)
                        && header.Contains("websocket", StringComparison.OrdinalIgnoreCase)))
                {
                    Interlocked.Increment(ref _upgradeCount);
                    _upgrades.Writer.TryWrite(new UpgradeCapture(target, path, query, headers));
                    if (RejectUpgrades)
                    {
                        await WriteAsync(stream, "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                            cancellation).ConfigureAwait(false);
                        return;
                    }
                    var key = new UpgradeCapture(target, path, query, headers).Header("Sec-WebSocket-Key") ?? "";
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + UpgradeGuid)));
                    await WriteAsync(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
                        + $"Connection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n", cancellation).ConfigureAwait(false);
                    var negotiated = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                        keepAliveInterval: Timeout.InfiniteTimeSpan);
                    _connections.Writer.TryWrite(new ServerConnection(negotiated, client));
                    keepOpen = true;
                    return;
                }

                var length = headers
                    .FirstOrDefault(header => header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    ?[15..].Trim();
                var body = "";
                if (int.TryParse(length, out var bytes) && bytes > 0)
                {
                    var payload = new byte[bytes];
                    await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
                    body = Encoding.UTF8.GetString(payload);
                }
                var capture = new HttpCapture(method, path, body);
                lock (_received)
                    _received.Add(capture);
                _requests.Writer.TryWrite(capture);
                // Before the answer is composed, so the instants in it are the ones a client would
                // have got from a server that was genuinely this slow.
                if (ClockDelay > TimeSpan.Zero && path.EndsWith("/GetUtcTime", StringComparison.OrdinalIgnoreCase))
                    await Task.Delay(ClockDelay, cancellation).ConfigureAwait(false);
                if (JoinDelay > TimeSpan.Zero && path.EndsWith("/SyncPlay/Join", StringComparison.OrdinalIgnoreCase))
                    await Task.Delay(JoinDelay, cancellation).ConfigureAwait(false);
                if (BufferingResponseGate is { } gate && path.EndsWith("/SyncPlay/Buffering", StringComparison.OrdinalIgnoreCase))
                    await gate.WaitAsync(cancellation).ConfigureAwait(false);
                var response = ResponseOverride is { } respond
                    ? await respond(capture).ConfigureAwait(false) : null;
                await WriteAsync(stream, response ?? Respond(path), cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                // An upgraded connection now belongs to the WebSocket wrapped around its stream.
                if (!keepOpen)
                    client.Dispose();
            }
        }

        private string Respond(string path)
        {
            if (path.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase))
            {
                // Policy included because SyncPlayAccess lives on it and there is no other way to
                // read the level: denial is a bare 403 with no socket message behind it.
                var json = JsonSerializer.Serialize(new
                {
                    Id = UserId,
                    Name = "TEST",
                    Policy = new { SyncPlayAccess = "CreateAndJoinGroups" },
                });
                return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
            }
            if (path.EndsWith("/GetUtcTime", StringComparison.OrdinalIgnoreCase))
            {
                // Both instants round-tripped in ISO 8601 with the Z the server writes. They are
                // read a moment apart on purpose: the gap is the server-side processing time the
                // NTP delay has to take back out.
                var received = DateTime.UtcNow + ClockSkew;
                var json = JsonSerializer.Serialize(new
                {
                    RequestReceptionTime = received.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    ResponseTransmissionTime = (DateTime.UtcNow + ClockSkew)
                        .ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                });
                return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
            }
            if (path.EndsWith("/SyncPlay/Ping", StringComparison.OrdinalIgnoreCase))
                return "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            if (path.EndsWith("/SyncPlay/List", StringComparison.OrdinalIgnoreCase))
            {
                // PascalCase, like every other body here and like the real server: Kiota keys its
                // deserializers camelCase and falls back on the first character, so a fixture that
                // wrote camelCase would be testing a shape the server never sends.
                var json = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        GroupId = ListedGroupId,
                        GroupName = "Movie night",
                        State = "Playing",
                        Participants = new[] { "alice", "bob" },
                    },
                });
                return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
            }
            // Every other SyncPlay route answers the same way, so the switch below is the whole
            // access story: 403 is what a revoked SyncPlayAccess looks like, and it is the ONLY
            // sign of one — 12.0 has no socket message for a refusal.
            if (path.Contains("/SyncPlay/", StringComparison.OrdinalIgnoreCase))
                return SyncPlayStatus is >= 200 and < 300
                    ? "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 {SyncPlayStatus} Forbidden\r\nContent-Type: application/json\r\n"
                        + "Content-Length: 2\r\nConnection: close\r\n\r\n{}";
            if (path.EndsWith("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase))
                return CapabilitiesStatus is >= 200 and < 300
                    ? "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 {CapabilitiesStatus} Server Error\r\nContent-Type: application/json\r\n"
                        + "Content-Length: 2\r\nConnection: close\r\n\r\n{}";
            return "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }

        private static Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellation)
            => stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellation).AsTask();

        /// <summary>Reads the request head a byte at a time, deliberately. A buffered reader would
        /// swallow whatever followed the blank line, and on the upgrade path that buffer is handed
        /// straight to <see cref="WebSocket.CreateFromStream"/>.</summary>
        private static async Task<List<string>?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellation)
        {
            var one = new byte[1];
            var head = new List<byte>();
            while (true)
            {
                if (await stream.ReadAsync(one.AsMemory(0, 1), cancellation).ConfigureAwait(false) == 0)
                    return null;
                head.Add(one[0]);
                var n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                    break;
                if (n > 16384)
                    return null;
            }
            return [.. Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];
        }
    }
}
