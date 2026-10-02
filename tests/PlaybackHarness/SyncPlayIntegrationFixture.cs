using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.ViewModels;

// Guest-only: real shell and network awaits. Synthetic session, no saved credentials.
internal static class SyncPlayIntegrationFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = UiFixtureApplication.Create();
        MainWindow? window = null;
        using var server = new LiveSessionFixture.FakeJellyfinServer();
        using var service = new JellyfinService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var firstEntry = Guid.NewGuid();
        var secondEntry = Guid.NewGuid();
        var negotiation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logoutMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }
        var firstPath = $"/Items/{first:D}";
        var secondPath = $"/Items/{second:D}";
        var thirdPath = $"/Items/{third:D}";
        server.ResponseOverride = async request =>
        {
            if (request.Path.Equals(secondPath, StringComparison.OrdinalIgnoreCase))
                await metadata.Task;
            if (request.Path.Equals(thirdPath, StringComparison.OrdinalIgnoreCase))
            {
                await logoutMetadata.Task;
                return Json(new { Id = third, Name = "Logout film", Type = "Movie", RunTimeTicks = 300000000L });
            }
            if (request.Path.Equals(firstPath, StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals(secondPath, StringComparison.OrdinalIgnoreCase))
                return Json(new { Id = request.Path.Equals(firstPath, StringComparison.OrdinalIgnoreCase) ? first : second,
                    Name = "Integration film", Type = "Movie", RunTimeTicks = 300000000L });
            if (request.Path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == "POST") await negotiation.Task;
                return Json(new { PlaySessionId = "integration-session", MediaSources = new[] {
                    new { Id = "integration-source", SupportsDirectPlay = true } } });
            }
            if (request.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase))
                return Json(new { Items = new[] { new { Id = first, Name = "First film", Type = "Movie" },
                    new { Id = second, Name = "Second film", Type = "Movie" } } });
            if (request.Path.Equals("/Videos/ActiveEncodings", StringComparison.OrdinalIgnoreCase))
                return "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            return null;
        };
        try
        {
            if (!SyncPlayBindingFixture.TryBuildClip(out var clip))
                throw new InvalidOperationException("The integration fixture requires the SyncPlay clip");
            var credentials = new SavedCredentials(server.Origin, "TEST", "synthetic-integration-token", server.UserId);
            var connect = service.ReconnectAsync(credentials);
            Wait(() => connect.IsCompleted);
            Check(connect.GetAwaiter().GetResult(), "synthetic integration session connects");
            window = new MainWindow { Width = 700, Height = 480 };
            window.Show();
            Pump();
            Check(app.Windows.OfType<MainWindow>().Count() == 1,
                "integration fixture owns exactly one production MainWindow");
            var model = Field<AppViewModel>(window, "_app");
            // Activate through the production warm-session path; appdata was isolated by Program.
            Call(model, "Activate", credentials, service);
            var client = Field<SyncPlayClient>(window, "_syncPlay");
            var vm = Field<PlayerViewModel>(window, "_playerViewModel");
            var group = Guid.NewGuid();
            var join = (Task<bool>)Call(window, "JoinSyncPlayGroupAsync", group)!;
            Wait(() => join.IsCompleted);
            Check(join.GetAwaiter().GetResult() && !client.IsInGroup && vm.SyncPlayPending,
                "HTTP join success remains pending until socket membership");
            var leave = (Task)Call(window, "LeaveSyncPlayGroupAsync")!;
            Wait(() => leave.IsCompleted);
            leave.GetAwaiter().GetResult();
            Check(!vm.SyncPlayPending && !client.IsMembershipPending,
                "lost GroupJoined can be cancelled after HTTP success");

            Join();
            Wait(() => vm.SyncPlay is not null);
            Queue(first, firstEntry, 120000000L);
            Wait(() => server.Requests.Any(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")));
            Check(Field<long>(window, "_pendingResumeTicks") == 0
                && Field<SyncPlayAction?>(window, "_syncPlayDeferredAction") is
                    { Kind: SyncPlayActionKind.Seek, TargetTicks: 120000000L },
                "group resume waits for binding settlement instead of a FileLoaded local seek");
            var sequence = Field<int>(window, "_playbackSequence");
            Wait(() => Field<MpvPlayer?>(window, "_player") is not null);
            var player = Field<MpvPlayer>(window, "_player");
            var generation = player.LoadGeneration;
            Set(vm, "_durationSeconds", 30d);
            Call(window, "ArmSyncPlayLoad", player.LoadGeneration);
            Check(Field<bool>(Field<SyncPlayPlayerBinding>(window, "_syncPlayBinding"), "_startPaused"),
                "every group load is armed paused pending the server action");

            // QueueChanged is queued on the dispatcher, while the client's item changes immediately.
            // Deliver outgoing EOF in that gap: it must not advance the new item.
            Queue(second, secondEntry);
            var binding = Field<SyncPlayPlayerBinding>(window, "_syncPlayBinding");
            Call(binding, "OnPlaybackRestarted", player.LoadGeneration);
            Check(!Field<bool>(binding, "_settled"),
                "outgoing restart cannot settle readiness for incoming item before UI dispatch");
            Call(window, "OnSyncPlayEndReached");
            Check(server.CountOf("/SyncPlay/NextItem") == 0
                && Field<int>(window, "_syncPlayEofGeneration") == -1,
                "outgoing EOF cannot advance the incoming queue item before UI dispatch");
            Wait(() => server.CountOf(secondPath) > 0);
            Check(Field<int>(window, "_playbackSequence") > sequence,
                "next queue entry invalidates old negotiation before metadata returns");
            negotiation.SetResult();
            Wait(() => server.CountOf("/Videos/ActiveEncodings") > 0);
            Check(Field<object?>(window, "_pendingLoad") is null
                && Field<MpvPlayer?>(window, "_player")?.LoadGeneration == generation,
                "superseded negotiation is discarded without loading old playback");
            Wait(() => vm.QueueRows.Any(row => row.Display == "Second film"));
            Check(vm.QueueRows.Any(row => row.Display == "Second film"),
                "queue titles resolve before the selected item's metadata");

            leave = (Task)Call(window, "LeaveSyncPlayGroupAsync")!;
            Wait(() => leave.IsCompleted);
            leave.GetAwaiter().GetResult();
            Pump();
            var afterLeave = Field<int>(window, "_playbackSequence");
            var posts = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            metadata.SetResult();
            Pump(TimeSpan.FromSeconds(1));
            Check(Field<int>(window, "_playbackSequence") == afterLeave
                && server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) == posts,
                "metadata completing after leave cannot start a new group load");

            Join();
            Wait(() => vm.SyncPlay is not null);
            negotiation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var priorNegotiations = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            Queue(first, Guid.NewGuid());
            Wait(() => server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) > priorNegotiations);
            var empty = new SyncPlayQueueUpdate("RemoveItems", DateTimeOffset.UtcNow,
                [], -1, 0, false, null, null);
            Call(client, "ApplyQueue", ReadUpdate("PlayQueue", JsonSerializer.Serialize(empty)));
            Wait(() => model.State == AppState.Browse);
            var emptyGeneration = player.LoadGeneration;
            var discarded = server.CountOf("/Videos/ActiveEncodings");
            negotiation.SetResult();
            Wait(() => server.CountOf("/Videos/ActiveEncodings") > discarded);
            Check(client.IsInGroup && client.PlaylistItemId is null
                && player.LoadGeneration == emptyGeneration && Field<object?>(window, "_pendingLoad") is null,
                "empty group queue stops outgoing playback and discards its delayed negotiation");

            // Keep a real outgoing file while the replacement's item lookup is held.
            // The sequence no longer owns A in that interval, but the loaded entry still does.
            negotiation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            priorNegotiations = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            var loadedEntry = Guid.NewGuid();
            Queue(first, loadedEntry, 50000000L);
            Wait(() => server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) > priorNegotiations);
            var loadedGeneration = player.LoadFile(clip);
            Call(window, "ArmSyncPlayLoad", loadedGeneration);
            Wait(() => Field<bool>(binding, "_settled") && !Field<bool>(binding, "_readyPending"));
            Wait(() => binding.ReadState().PositionSeconds > 4.5);
            Wait(() => server.CountOf("/SyncPlay/Ready") > 0);
            var context = client.ReportContext;
            var readyReports = server.CountOf("/SyncPlay/Ready");
            var bufferReports = server.CountOf("/SyncPlay/Buffering");
            Set(binding, "_readyPending", true);
            Join();
            Call(binding, "OnPlaybackRestarted", loadedGeneration);
            Call(binding, "OnBufferingChanged", true);
            Check(client.ReportContext != context && binding.ReadState() == SyncPlayPlayerState.Idle
                && Field<SyncPlayReportContext?>(binding, "_reportContext") == context
                && Field<bool>(binding, "_readyPending")
                && server.CountOf("/SyncPlay/Ready") == readyReports
                && server.CountOf("/SyncPlay/Buffering") == bufferReports,
                "restored membership rejects old player reports before dispatcher delivery");
            Wait(() => Field<SyncPlayReportContext?>(binding, "_reportContext") == client.ReportContext);
            Wait(() => server.CountOf("/SyncPlay/Ready") > readyReports);
            Check(player.LoadGeneration == loadedGeneration
                && Field<Guid?>(window, "_syncPlayLoadedEntry") == loadedEntry
                && binding.ReadState() != SyncPlayPlayerState.Idle,
                "same-group restore renews Ready context without reloading the settled file");

            var unpausePosts = server.CountOf("/SyncPlay/Unpause");
            vm.TogglePauseCommand.Execute(null);
            Wait(() => server.CountOf("/SyncPlay/Unpause") > unpausePosts);
            Check(player.GetPropertyString("pause") == "yes",
                "VM unpause posts to the group without changing local pause before the echo");
            Call(window, "OnSyncPlayActionRequired", new SyncPlayAction(SyncPlayActionKind.UnpauseNow));
            Wait(() => !player.Pause && player.GetPropertyString("pause") == "no");
            Check(model.State == AppState.Playing,
                "group unpause echo changes the real loaded player's pause state");
            var pausePosts = server.CountOf("/SyncPlay/Pause");
            vm.TogglePauseCommand.Execute(null);
            Wait(() => server.CountOf("/SyncPlay/Pause") > pausePosts);
            Check(player.GetPropertyString("pause") == "no",
                "VM pause posts to the group without changing local pause before the echo");
            Call(window, "OnSyncPlayActionRequired", new SyncPlayAction(SyncPlayActionKind.Pause));
            Wait(() => player.Pause && player.GetPropertyString("pause") == "yes");
            Check(player.TryReadPosition(out var beforeSeek), "group pause echo leaves a readable loaded position");
            var seekPosts = server.CountOf("/SyncPlay/Seek");
            vm.Seek(12);
            Wait(() => server.CountOf("/SyncPlay/Seek") > seekPosts);
            using (var body = JsonDocument.Parse(server.Requests.Last(r => r.Path.EndsWith("/SyncPlay/Seek")).Body))
                Check(body.RootElement.EnumerateObject().First(p => p.Name.Equals("PositionTicks", StringComparison.OrdinalIgnoreCase))
                        .Value.GetInt64() == 120000000L
                    && player.GetPropertyString("pause") == "yes" && player.TryReadPosition(out var afterSeek)
                    && Math.Abs(afterSeek - beforeSeek) < 0.2,
                    "VM seek posts the exact group target without changing local position before the echo");
            Call(window, "OnSyncPlayActionRequired", new SyncPlayAction(SyncPlayActionKind.Seek, TargetTicks: 120000000L));
            Wait(() => player.TryReadPosition(out var position) && Math.Abs(position - 12) < 0.2
                && Field<bool>(binding, "_settled"));
            Check(player.GetPropertyString("pause") == "yes", "group seek echo changes the real loaded position");
            var stopPosts = server.CountOf("/SyncPlay/Stop");
            Call(window, "RequestStop", "back");
            Wait(() => server.CountOf("/SyncPlay/Stop") > stopPosts);
            Check(model.State == AppState.Playing && player.LoadGeneration == loadedGeneration
                && Field<Guid?>(window, "_syncPlayLoadedEntry") == loadedEntry,
                "Back posts group Stop without stopping local playback before the echo");
            Call(window, "OnSyncPlayActionRequired", new SyncPlayAction(SyncPlayActionKind.Stop));
            Check(model.State == AppState.Browse && client.IsInGroup
                && Field<SyncPlayReportContext?>(binding, "_reportContext") is null,
                "group Stop echo stops local playback while preserving group membership");

            priorNegotiations = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            loadedEntry = Guid.NewGuid();
            Queue(first, loadedEntry, 50000000L);
            Wait(() => server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) > priorNegotiations);
            loadedGeneration = player.LoadFile(clip);
            Call(window, "ArmSyncPlayLoad", loadedGeneration);
            Wait(() => Field<bool>(binding, "_settled") && !Field<bool>(binding, "_readyPending")
                && binding.ReadState().PositionSeconds > 4.5);
            metadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataRequests = server.CountOf(secondPath);
            Queue(second, Guid.NewGuid());
            Wait(() => server.CountOf(secondPath) > metadataRequests);
            Check(model.State == AppState.Playing && Field<int>(window, "_syncPlayPlaybackSequence") == -1
                && Field<Guid?>(window, "_syncPlayLoadedEntry") == loadedEntry,
                "replacement metadata wait retains outgoing file ownership after invalidation");
            Call(window, "OnSyncPlayActionRequired", new SyncPlayAction(SyncPlayActionKind.Seek, TargetTicks: 50000000L));
            Call(client, "ApplyQueue", ReadUpdate("PlayQueue", JsonSerializer.Serialize(empty with { LastUpdate = DateTimeOffset.UtcNow })));
            Wait(() => model.State == AppState.Browse);
            Check(client.IsInGroup && vm.QueueRows.Count == 0
                && Field<Guid?>(window, "_syncPlayLoadedEntry") is null
                && Field<Guid?>(window, "_syncPlayRequestedEntry") is null
                && Field<SyncPlayAction?>(window, "_syncPlayDeferredAction") is null
                && Field<SyncPlayReportContext?>(binding, "_reportContext") is null,
                "empty queue during replacement metadata stops A and clears pending work without leaving");
            var emptySequence = Field<int>(window, "_playbackSequence");
            posts = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            discarded = server.CountOf("/Videos/ActiveEncodings");
            negotiation.SetResult();
            metadata.SetResult();
            Wait(() => server.CountOf("/Videos/ActiveEncodings") > discarded);
            Pump(TimeSpan.FromSeconds(1));
            Check(model.State == AppState.Browse && client.IsInGroup
                && Field<int>(window, "_playbackSequence") == emptySequence
                && server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) == posts,
                "replacement metadata arriving after an empty queue cannot revive group playback");

            Queue(third, Guid.NewGuid());
            Wait(() => server.CountOf(thirdPath) > 0);
            var logout = model.LogoutAsync();
            Wait(() => logout.IsCompleted);
            logout.GetAwaiter().GetResult();
            Pump();
            var afterLogout = Field<int>(window, "_playbackSequence");
            posts = server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo"));
            logoutMetadata.SetResult();
            Pump(TimeSpan.FromSeconds(1));
            Check(!client.IsInGroup && vm.SyncPlay is null && model.State == AppState.Login
                && Field<int>(window, "_playbackSequence") == afterLogout
                && server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/PlaybackInfo")) == posts,
                "metadata completing after logout cannot revive playback or group membership");
            Console.WriteLine($"RESULT: PASS ({assertions} sync play integration assertions)");
            return 0;

            void Queue(Guid item, Guid entry, long ticks = 0)
            {
                var queue = new { Reason = "NewPlaylist", LastUpdate = DateTimeOffset.UtcNow,
                    Playlist = new[] { new { ItemId = item.ToString("N"), PlaylistItemId = entry.ToString("N") } },
                    PlayingItemIndex = 0, StartPositionTicks = ticks, IsPlaying = false };
                Call(client, "ApplyQueue", ReadUpdate("PlayQueue", JsonSerializer.Serialize(queue)));
            }

            void Join()
                => Call(client, "ApplyJoin", model.ActiveSessionKey!, ReadUpdate("GroupJoined",
                    JsonSerializer.Serialize(new { GroupId = group.ToString("N"), GroupName = "Fixture group",
                        State = "Waiting", Participants = new[] { "TEST" } })));

            SyncPlayGroupUpdateMessage ReadUpdate(string type, string payload)
            {
                if (!SessionMessageReader.TryRead(LiveSessionFixture.GroupUpdate(group, type, payload), out _, out var data)
                    || SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is not { } update)
                    throw new InvalidOperationException("Compact integration group update did not parse");
                return update;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT: FAIL (sync play integration): {ex}");
            return 1;
        }
        finally
        {
            negotiation.TrySetResult();
            metadata.TrySetResult();
            logoutMetadata.TrySetResult();
            window?.Close();
            app.Shutdown();
        }
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static string Json(object value)
    {
        var body = JsonSerializer.Serialize(value);
        return $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }
    private static void Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Integration response did not arrive");
            Pump();
        }
    }
    private static void Pump(TimeSpan? duration = null)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
