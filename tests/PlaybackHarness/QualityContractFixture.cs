using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LightWeaver.Jellyfin;
using LightWeaver.Player;

// Exercises the production SDK serializer over HTTP. No WPF window, mpv, or saved
// credentials are opened. The token and all server responses are synthetic.
internal static class QualityContractFixture
{
    public static async Task<int> RunAsync()
    {
        using var listener = new HttpListener();
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var service = new JellyfinService();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var count = 0;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        async Task<HttpListenerContext> Next() =>
            await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        static async Task Reply(HttpListenerContext context, string body, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        static async Task<JsonDocument> Body(HttpListenerContext context) =>
            JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync());
        static JsonElement Field(JsonElement value, string name) =>
            value.EnumerateObject().Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        string Decision(bool direct, string session) => JsonSerializer.Serialize(new
        {
            PlaySessionId = session,
            MediaSources = new[] { new { Id = "source-a", SupportsDirectPlay = direct,
                TranscodingUrl = direct ? null : "/Videos/fixture/master.m3u8" } }
        });
        try
        {
            // Use the production pure snapshot function without constructing a WPF window.
            var capture = typeof(LightWeaver.MainWindow).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "CaptureQualityTrackSnapshot" && method.GetParameters().Length == 5);
            var english = new MediaStreamChoice(3, 0, "English", "eng", "English", true, false);
            var french = new MediaStreamChoice(4, -1, "French", "fra", "French", false, true);
            var streams = new MediaSourceStreams("source-a", [], [], [english, french], []);
            var burned = new PlaybackDecision("http://fixture.test/video", true, "burned-session",
                "source-a", SubtitleStreamIndex: 3, SubtitleIsBurnedIn: true);
            object Snapshot(IReadOnlyList<MpvTrack> tracks, PlaybackDecision selectedDecision,
                object? previous = null, bool? explicitOff = null) =>
                capture.Invoke(null, [tracks, selectedDecision, streams, previous, explicitOff])!;
            static bool IsOff(object snapshot) => (bool)snapshot.GetType().GetProperty("SubtitleOff")!.GetValue(snapshot)!;
            static int? SubtitleIndex(object snapshot) =>
                ((MediaStreamChoice?)snapshot.GetType().GetProperty("SourceSubtitle")!.GetValue(snapshot))?.Index;
            var initialBurn = Snapshot([], burned);
            Check(!IsOff(initialBurn) && SubtitleIndex(initialBurn) == 3,
                "initial server-selected burned subtitle survives a quality snapshot");
            MpvTrack[] externalRows = [new(1, "sub", "French", "fra", false, false, false),
                new(2, "sub", "German", "deu", false, false, false)];
            var repeatedBurn = Snapshot(externalRows, burned, initialBurn);
            Check(!IsOff(repeatedBurn) && SubtitleIndex(repeatedBurn) == 3,
                "unselected external subtitle rows do not erase the burned source selection");
            Check(IsOff(Snapshot(externalRows, burned, repeatedBurn, true)),
                "explicit subtitles off supersedes the previous burned selection");
            Check(SubtitleIndex(Snapshot([externalRows[0] with { Selected = true }], burned, null, false)) == 4,
                "a newly selected external subtitle supersedes the burned source selection");
            Check(IsOff(Snapshot(externalRows, burned with { SubtitleIsBurnedIn = false })),
                "unselected ordinary subtitle rows remain off");

            var reconnect = service.ReconnectAsync(new SavedCredentials(origin, "TEST", "synthetic-fixture-token", user));
            var request = await Next();
            Check(request.Request.Url!.AbsolutePath.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase), "synthetic reconnect reaches Users/Me");
            await Reply(request, JsonSerializer.Serialize(new { Id = user, Name = "TEST" }));
            Check(await reconnect, "production service accepts synthetic session");

            var burnRequest = service.NegotiatePlaybackAsync(item, 1_000_000, true);
            request = await Next();
            await Reply(request, JsonSerializer.Serialize(new
            {
                PlaySessionId = "server-selected-burn",
                MediaSources = new[] { new { Id = "source-a", SupportsDirectPlay = false,
                    TranscodingUrl = "/Videos/fixture/master.m3u8", DefaultSubtitleStreamIndex = 3,
                    MediaStreams = new[] { new { Index = 3, Type = "Subtitle", DeliveryMethod = "Encode" } } } }
            }));
            var serverBurn = await burnRequest;
            Check(serverBurn is { SubtitleStreamIndex: 3, SubtitleIsBurnedIn: true },
                "negotiation retains the server-selected burned subtitle and delivery method");

            var pending = service.NegotiatePlaybackAsync(item, 4_000_000, true, 2, -1, "source-a");
            request = await Next();
            Check(request.Request.HttpMethod == "POST" && request.Request.Url!.AbsolutePath.EndsWith("/PlaybackInfo"), "quality negotiation posts PlaybackInfo");
            using (var body = await Body(request))
            {
                var value = body.RootElement;
                Check(Field(value, "MaxStreamingBitrate").GetInt32() == 4_000_000, "wire bitrate is 4 Mbps");
                Check(!Field(value, "EnableDirectPlay").GetBoolean() && !Field(value, "EnableDirectStream").GetBoolean()
                    && Field(value, "EnableTranscoding").GetBoolean(), "forced request disables both direct modes and enables transcoding");
                Check(Field(value, "AudioStreamIndex").GetInt32() == 2 && Field(value, "SubtitleStreamIndex").GetInt32() == -1
                    && Field(value, "MediaSourceId").GetString() == "source-a", "wire request retains source, audio and subtitles-off selection");
                Check(Field(Field(value, "DeviceProfile"), "MaxStreamingBitrate").GetInt32() == 4_000_000, "device profile matches bitrate cap");
            }
            await Reply(request, Decision(false, "session-transcode"));
            var decision = await pending;
            Check(decision is { IsTranscode: true, PlaySessionId: "session-transcode", MediaSourceId: "source-a" }
                && decision.Url == origin + "/Videos/fixture/master.m3u8", "server transcode decision retains session and source");

            var discard = service.DiscardUnplayedDecisionAsync(decision!);
            request = await Next();
            Check(request.Request.HttpMethod == "DELETE" && request.Request.Url!.AbsolutePath.EndsWith("/Videos/ActiveEncodings"), "discard uses encoding teardown without a stopped report");
            Check(request.Request.QueryString["playSessionId"] == "session-transcode"
                && !string.IsNullOrWhiteSpace(request.Request.QueryString["deviceId"]), "discard targets the negotiated session and device");
            await Reply(request, "", 204);
            await discard;

            pending = service.NegotiatePlaybackAsync(item, 0);
            request = await Next();
            using (var body = await Body(request))
            {
                Check(Field(body.RootElement, "MaxStreamingBitrate").GetInt32() == 1_000_000_000, "unlimited sends explicit 1 Gbps");
                Check(Field(body.RootElement, "EnableDirectPlay").GetBoolean() && Field(body.RootElement, "EnableDirectStream").GetBoolean(), "automatic mode permits direct playback and streaming");
            }
            await Reply(request, Decision(true, "session-direct"));
            var directDecision = await pending;
            Check(directDecision is { IsTranscode: false }
                && directDecision.Url.Contains("mediaSourceId=source-a", StringComparison.Ordinal),
                "direct response pins the negotiated media source in its URL");

            pending = service.NegotiatePlaybackAsync(item, 1_000_000, true);
            request = await Next();
            await Reply(request, "{}", 400);
            Check(await pending is null, "failed negotiation produces no replacement decision");

            pending = service.NegotiatePlaybackAsync(item, 1_000_000, true);
            request = await Next();
            await Reply(request, Decision(true, "session-no-transcode"));
            Check(await pending is null, "forced transcode never silently falls back to direct playback");

            discard = service.DiscardUnplayedDecisionAsync(decision!);
            request = await Next();
            await Reply(request, "{}", 400);
            // Named explicitly rather than written as Check(true, ...). The old form did prove
            // something - reaching the line at all meant the await had not thrown - but nothing
            // in it SAID so, and an assertion a reader cannot evaluate is indistinguishable from
            // one that tests nothing.
            var teardownThrew = false;
            try { await discard; } catch { teardownThrew = true; }
            Check(!teardownThrew, "a rejected teardown never throws into the playback path");

            var reporter = new PlaybackReporter(() => service.Client, () => (12.5, true));
            reporter.Start(item, 125_000_000, "paused-session", "source-a", true, isPaused: true);
            request = await Next();
            using (var body = await Body(request))
            {
                Check(Field(body.RootElement, "IsPaused").GetBoolean(), "replacement start reports the actual paused state");
                Check(Field(body.RootElement, "PositionTicks").GetInt64() == 125_000_000,
                    "replacement start reports preserved position");
            }
            await Reply(request, "", 204);
            var stop = reporter.Stop();
            request = await Next();
            using (var body = await Body(request))
            {
                Check(Field(body.RootElement, "PlaySessionId").GetString() == "paused-session"
                    && Field(body.RootElement, "PositionTicks").GetInt64() == 125_000_000,
                    "stopped report retains outgoing session and position");
            }
            await Reply(request, "", 204);
            await stop;
            Check(!reporter.IsActive && reporter.Stop().IsCompletedSuccessfully,
                "a second stop is an already-completed no-op");

            reporter.Start(item, 0, "delayed-start-session", "source-a");
            var heldStart = await Next();
            var queuedStop = reporter.Stop();
            var nextRequest = Next();
            await Task.Delay(250);
            Check(!queuedStop.IsCompleted && !nextRequest.IsCompleted,
                "stop waits while its outgoing start response is pending");
            await Reply(heldStart, "", 204);
            request = await nextRequest;
            using (var body = await Body(request))
            {
                Check(request.Request.Url!.AbsolutePath.EndsWith("/Stopped")
                    && Field(body.RootElement, "PlaySessionId").GetString() == "delayed-start-session",
                    "the queued stop retains its outgoing session after start completes");
            }
            await Reply(request, "", 204);
            await queuedStop;

            Console.WriteLine($"RESULT: PASS ({count} quality contract assertions)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: quality contract: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
