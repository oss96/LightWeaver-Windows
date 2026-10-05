using System.Windows.Threading;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.Settings;

/// <summary>Real mpv HTTP requests on loopback, with null video/audio outputs and no window.
/// The server returns 404 after capturing the headers; media decoding is irrelevant here.</summary>
internal static class HttpHeaderFixture
{
    public static void Run(Action<string, Action> test)
    {
        test("mpv sends one complete Authorization header, replaces it and clears it on the next load", () =>
        {
            var previousNoVideo = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO");
            var previousNoAudio = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO");
            try
            {
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
                using var server = new LiveSessionFixture.FakeJellyfinServer();
                using var service = new JellyfinService();
                if (!service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-mpv-token-a",
                        server.UserId)).GetAwaiter().GetResult())
                    throw new InvalidOperationException("The synthetic mpv session could not reconnect.");
                using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
                    new AppSettings { HardwareDecoding = false });
                CheckLoad("/header-first", service.AuthorizationHeader);
                CheckLoad("/header-replaced", service.AuthorizationHeader.Replace("synthetic-mpv-token-a",
                    "synthetic-mpv-token-b", StringComparison.Ordinal));
                CheckLoad("/header-cleared", null);

                void CheckLoad(string path, string? header)
                {
                    player.SetHttpHeaders(header);
                    player.LoadFile(server.Origin + path);
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!server.Requests.Any(request => request.Path == path) && DateTime.UtcNow < deadline)
                        Pump();
                    var request = server.Requests.FirstOrDefault(request => request.Path == path)
                        ?? throw new InvalidOperationException($"mpv did not issue the {path} HTTP request.");
                    if (request.Header("Authorization") != header)
                        throw new InvalidOperationException($"{path}: mpv did not send the complete current Authorization value.");
                    var headerCount = request.Headers!.Count(value => value.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                    if (headerCount != (header is null ? 0 : 1))
                        throw new InvalidOperationException($"{path}: mpv sent an unexpected number of Authorization fields.");
                    if (header is not null && request.Headers!.Any(value => value.StartsWith("Device=", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("Token=", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"{path}: mpv split metadata into separate HTTP fields.");
                    Console.WriteLine($"HTTP-HEADER EVIDENCE: path={path} authorization_fields={headerCount} complete=True");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", previousNoVideo);
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", previousNoAudio);
            }
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
