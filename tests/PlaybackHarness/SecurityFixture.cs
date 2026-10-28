using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightWeaver.Imaging;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Downloads;
using System.Windows.Threading;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver;
using LightWeaver.ViewModels;

/// <summary>Windowless contracts for the 2026-10-16 security review: server-controlled values must
/// not choose file paths, launch targets, request hosts or crash the client.</summary>
internal static class SecurityFixture
{
    public const string Mode = "--security";

    public static void Run(Action<string, Action> test)
    {
        test("a server container value cannot leave the download folder", () =>
        {
            Expect(DownloadPaths.Extension(@"mkv\..\..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x") == "mkvAppDataRo",
                "a traversal container must collapse to letters and digits");
            Expect(DownloadPaths.Extension("mov,mp4,m4a") == "mov", "the first container token is the extension");
            Expect(DownloadPaths.Extension(@"..\..") == "mkv", "a container with no usable characters falls back to mkv");
            Expect(DownloadPaths.Extension(null) == "mkv", "a missing container falls back to mkv");
            Expect(DownloadPaths.Extension("mp4:evil") == "mp4evil", "an alternate data stream separator is dropped");

            var id = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "lw-security-downloads");
            Expect(DownloadPaths.IsContained(Item(id, Path.Combine(folder, $"{id:N}_Movie-0123456789ab.mkv"))),
                "a normal download path is contained");
            Expect(!DownloadPaths.IsContained(Item(id, Path.Combine(folder, $"{id:N}_Movie.mkv", "..", "..", "Startup", "x.mkv"))),
                "a path with parent segments is refused");
            Expect(!DownloadPaths.IsContained(Item(id, Path.Combine(folder, "evil.exe"))),
                "a file not named after its item is refused");
            Expect(!DownloadPaths.IsContained(Item(id, $"{id:N}_Movie.mkv")), "a relative path is refused");
            Expect(!DownloadPaths.IsContained(Item(id, Path.Combine(folder, $"{id:N}_Movie.mkv:stream"))),
                "an alternate data stream is refused");
        });

        test("only absolute http and https trailer links reach the shell", () =>
        {
            foreach (var good in new[] { "https://www.youtube.com/watch?v=abc", "http://example.com/trailer" })
                Expect(WebLinks.TryParse(good, out _), "a web link is accepted: " + good);
            foreach (var bad in new[]
                     {
                         @"C:\Windows\System32\calc.exe", @"\\attacker\share\x.exe", "file:///C:/Windows/System32/calc.exe",
                         "ms-settings:", "search-ms:query=x", "javascript:alert(1)", "https://user:pass@example.com/",
                         "/relative/path", "", null,
                     })
                Expect(!WebLinks.TryParse(bad, out _), "a non-web link is refused: " + bad);

            var picked = JellyfinService.PickTrailerUrl(
            [
                new MediaUrl { Url = @"\\attacker\share\youtube.exe" },
                new MediaUrl { Url = "https://www.youtube.com/watch?v=abc" },
            ]);
            Expect(picked == "https://www.youtube.com/watch?v=abc", "the UNC trailer is skipped, got " + picked);
        });

        test("server subtitle and transcode URLs stay on the server's origin", () =>
        {
            const string server = "https://media.example:8920/jellyfin";
            Expect(JellyfinService.SameOriginUrl(server, "/Videos/1/2/Subtitles/3/0/Stream.srt?ApiKey=k")
                    == "https://media.example:8920/jellyfin/Videos/1/2/Subtitles/3/0/Stream.srt?ApiKey=k",
                "a server-relative path resolves under the server address");
            Expect(JellyfinService.SameOriginUrl(server, "https://MEDIA.example:8920/jellyfin/x.vtt") is not null,
                "an absolute URL on the same origin is kept");
            foreach (var bad in new[]
                     {
                         @"\\attacker\share\x.srt", "file:///C:/Users/me/secret.txt", @"C:\Users\me\secret.txt",
                         "https://attacker.example/x.srt", "http://media.example:8920/x.srt", "https://media.example/x.srt",
                         "https://media.example:8920@attacker.example/x.srt", @"https://media.example:8920\@attacker.example/",
                         "", null,
                     })
            {
                var resolved = JellyfinService.SameOriginUrl(server, bad);
                Expect(resolved is null || new Uri(resolved).Host == "media.example" && new Uri(resolved).Port == 8920,
                    $"an off-origin value is refused: {bad} -> {resolved}");
            }
            Expect(JellyfinService.SameOriginUrl("http://jellyfin.example:8096", "@attacker.example/x") is null,
                "a user-info suffix cannot move the host");
            Expect(JellyfinService.SameOriginUrl(server, "/../other-app/x.srt") is null
                    && JellyfinService.SameOriginUrl(server, "https://media.example:8920/other-app/x.srt") is null,
                "a path that climbs out of the server's base path is refused");
        });

        test("an image URL carries a token only for the server that owns it", () =>
        {
            Expect(AppViewModel.ServerOwnsUrl("http://jellyfin.example:8096", "http://jellyfin.example:8096/Items/1/Images/Primary"),
                "the server's own image is owned");
            Expect(AppViewModel.ServerOwnsUrl("https://host.example/jellyfin", "https://host.example/jellyfin/Items/1/Images/Primary"),
                "a sub-path server owns its images");
            foreach (var foreign in new[]
                     {
                         "http://jellyfin.example:80960/Items/1", "http://jellyfin.example.attacker.example:8096/Items/1",
                         "https://jellyfin.example:8096/Items/1", "http://jellyfin.example:8096@attacker.example/Items/1",
                         "https://image.tmdb.org/t/p/w500/poster.jpg",
                     })
                Expect(!AppViewModel.ServerOwnsUrl("http://jellyfin.example:8096", foreign), "a foreign URL is not owned: " + foreign);
            Expect(!AppViewModel.ServerOwnsUrl("https://host.example/jellyfin", "https://host.example/jellyfin2/Items/1"),
                "a sibling path is not owned");
        });

        test("an update installer must carry the release's own version", () =>
        {
            var release = new Version(1, 8, 0);
            Expect(UpdatePackageVerifier.MatchesVersion("1.8.0", release), "the matching version is accepted");
            Expect(UpdatePackageVerifier.MatchesVersion(" 1.8.0 ", release), "padding from the version resource is ignored");
            foreach (var other in new[] { "1.7.0", "1.8.1", "2.8.0", "", "   ", null, "1.8.0-beta" })
                Expect(!UpdatePackageVerifier.MatchesVersion(other, release), "a different version is refused: " + other);
        });

        test("mpv runs no yt-dlp hook", () =>
        {
            var previousNoVideo = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO");
            var previousNoAudio = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO");
            try
            {
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
                using var player = MpvPlayer.Create(0, new LightWeaver.Threading.WpfUiDispatcher(Dispatcher.CurrentDispatcher), new AppSettings { HardwareDecoding = false });
                Expect(player.GetPropertyString("ytdl") == "no", "ytdl is " + player.GetPropertyString("ytdl"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", previousNoVideo);
                Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", previousNoAudio);
            }
        });

        test("trickplay dimensions that would overflow the sheet arithmetic are refused", () =>
        {
            Expect(new TrickplayInfo("source", 320, 180, 10, 10, 500, 10_000).IsUsable, "Jellyfin's default bucket is usable");
            foreach (var bad in new[]
                     {
                         new TrickplayInfo("source", 320, 180, 65_536, 65_536, 500, 10_000),
                         new TrickplayInfo("source", 320, 180, 46_341, 46_341, 500, 10_000),
                         new TrickplayInfo("source", int.MaxValue, 180, 10, 10, 500, 10_000),
                         new TrickplayInfo("source", 320, 180, 0, 10, 500, 10_000),
                         new TrickplayInfo("source", 320, 180, 10, 10, 0, 10_000),
                         new TrickplayInfo("source", 320, 180, 10, 10, 500, 0),
                     })
                Expect(!bad.IsUsable, "an overflowing or empty bucket is refused: " + bad);
            using var service = new JellyfinService();
            var preview = new TrickplayProvider(service, Guid.NewGuid(),
                    new TrickplayInfo("source", 320, 180, 65_536, 65_536, 500, 10_000))
                .GetPreviewAsync(42).GetAwaiter().GetResult();
            Expect(preview is null, "an unusable bucket yields no preview instead of throwing");
        });

        test("an oversized image response or bitmap is refused before it fills memory", () =>
        {
            Expect(ImageLimits.DecodedPixels(320, 180, 0) == 57_600, "a full-size decode counts every pixel");
            Expect(ImageLimits.DecodedPixels(8000, 6000, 400) == 400 * 300, "a scaled decode counts the scaled pixels");
            Expect(ImageLimits.DecodedPixels(100, 1_000_000, 50) == 50L * 500_000, "a tall image stays tall when scaled");
            Expect(ImageLimits.DecodedPixels(60_000, 60_000, 0) > ImageLimits.MaxDecodedPixels, "a 3.6 gigapixel image exceeds the cap");
            Expect(ImageLimits.DecodeWidth(1, 300) == 1 && ImageLimits.DecodedPixels(1, 65_535, 300) == 65_535,
                "a 1 px wide image is never scaled up to the card width");

            using var server = new StaticHttpServer();
            server.Routes["/small.png"] = Png(64, 64);
            server.Routes["/huge.png"] = Png(7000, 6000);
            server.Routes["/sliver.png"] = Png(1, 20_000);
            server.Routes["/padded.png"] = Padded(Png(64, 64), ImageLimits.MaxImageBytes);
            server.Routes["/flood.png"] = Padded(Png(64, 64), ImageLimits.MaxImageBytes + 1);
            Expect(ImageCache.GetImageAsync(server.Origin + "/small.png").GetAwaiter().GetResult() is not null,
                "a normal image still loads");
            Expect(ImageCache.GetImageAsync(server.Origin + "/huge.png").GetAwaiter().GetResult() is null,
                "a 42 megapixel image is refused at decode");
            Expect(ImageCache.GetImageAsync(server.Origin + "/huge.png", 400).GetAwaiter().GetResult() is not null,
                "the same image decoded at card width is within the cap");
            var sliver = ImageCache.GetImageAsync(server.Origin + "/sliver.png", 300).GetAwaiter().GetResult();
            Expect(sliver is { PixelWidth: 1, PixelHeight: 20_000 },
                $"a 1x20000 image at card width decodes at its own size, got {sliver?.PixelWidth}x{sliver?.PixelHeight}");
            Expect(ImageCache.GetImageAsync(server.Origin + "/padded.png").GetAwaiter().GetResult() is not null,
                "a valid image padded to exactly the byte cap still loads");
            Expect(ImageCache.GetImageAsync(server.Origin + "/flood.png").GetAwaiter().GetResult() is null,
                "the same image one byte past the cap is refused");
        });

        test("a remote Play is limited in size and keeps its start item", () =>
        {
            var ids = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).ToList();
            var limited = RemoteCommand.LimitPlayRequest(new PlayRequestMessage(ids, "PlayNow", 0, 9_000, null, null, null, null));
            Expect(limited.ItemIds!.Count == RemoteCommand.MaxPlayItems, "the queue is capped, got " + limited.ItemIds.Count);
            Expect(limited.ItemIds[limited.StartIndex!.Value] == ids[9_000], "the start item is still the one the server named");
            var tail = RemoteCommand.LimitPlayRequest(new PlayRequestMessage(ids, "PlayNow", 0, 9_999, null, null, null, null));
            Expect(tail.ItemIds![tail.StartIndex!.Value] == ids[9_999] && tail.ItemIds.Count == RemoteCommand.MaxPlayItems,
                "a start at the end keeps a full window ending there");
            var small = new PlayRequestMessage(ids.Take(3).ToList(), "PlayNow", 0, 1, null, null, null, null);
            Expect(ReferenceEquals(RemoteCommand.LimitPlayRequest(small), small), "a normal request is untouched");
        });

        test("a dashboard message is one bounded line", () =>
        {
            Expect(RemoteCommand.MessageText("Hi", "there") == "Hi \u2014 there", "header and text are joined");
            Expect(RemoteCommand.MessageText("  ", "Only text ") == "Only text", "a blank half is dropped");
            Expect(RemoteCommand.MessageText("Line one\r\nLine two", null) == "Line one Line two", "line breaks collapse");
            var flood = RemoteCommand.MessageText(new string('x', 100_000), null);
            Expect(flood.Length == RemoteCommand.MaxMessageLength && flood.EndsWith('\u2026'), "a flood is cut to the cap");
            Expect(RemoteCommand.MessageText(null, "  ") == "", "an empty message stays empty");
        });

        test("logging out asks the server to end the session, without waiting on a dead server", () =>
        {
            using var server = new LiveSessionFixture.FakeJellyfinServer();
            using var service = new JellyfinService();
            if (!service.ReconnectAsync(new SavedCredentials(server.Origin, "TEST", "synthetic-revoke-token", server.UserId))
                    .GetAwaiter().GetResult())
                throw new InvalidOperationException("The synthetic session could not reconnect.");
            service.RevokeServerSessionAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var logout = server.Requests.FirstOrDefault(request => request.Path == "/Sessions/Logout");
            Expect(logout is { Method: "POST" }, "the server received POST /Sessions/Logout");
            Expect(logout!.Header("Authorization")?.Contains("synthetic-revoke-token", StringComparison.Ordinal) == true,
                "the logout carries the session's own token");

            var never = new TaskCompletionSource<string?>();
            server.ResponseOverride = capture => capture.Path == "/Sessions/Logout" ? never.Task : Task.FromResult<string?>(null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            service.RevokeServerSessionAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            Expect(watch.Elapsed < TimeSpan.FromSeconds(5), "a server that never answers is abandoned at the timeout, took " + watch.Elapsed);
            never.TrySetResult(null);
        });

        test("the OMDB key is sealed to this Windows user in settings.json", () =>
        {
            var json = JsonSerializer.Serialize(new AppSettings { OmdbApiKey = "synthetic-omdb-key" });
            Expect(!json.Contains("synthetic-omdb-key", StringComparison.Ordinal), "the key is not written in plain text");
            Expect(json.Contains("\"OmdbApiKeyProtected\"", StringComparison.Ordinal), "the sealed key is written");
            Expect(!json.Contains("\"OmdbApiKey\"", StringComparison.Ordinal), "no plain-text property is written");
            Expect(JsonSerializer.Deserialize<AppSettings>(json)!.OmdbApiKey == "synthetic-omdb-key", "the sealed key reads back");

            var legacyJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [nameof(AppSettings.OmdbApiKey)] = "synthetic-legacy-key",
            });
            var legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson)!;
            Expect(legacy.OmdbApiKey == "synthetic-legacy-key" && legacy.ReadPlainTextOmdbKey,
                "a plain-text key from an older version is kept and flagged for an immediate sealed save");
            Expect(!JsonSerializer.Deserialize<AppSettings>(json)!.ReadPlainTextOmdbKey, "a sealed key needs no re-save");
            Expect(!JsonSerializer.Serialize(legacy).Contains("synthetic-legacy-key", StringComparison.Ordinal),
                "and is sealed on the next save");
            Expect(JsonSerializer.Deserialize<AppSettings>("{\"OmdbApiKeyProtected\":\"not-a-dpapi-blob\"}")!.OmdbApiKey == "",
                "a value that does not unseal reads as no key");
            Expect(!JsonSerializer.Serialize(new AppSettings()).Contains("OmdbApiKey", StringComparison.Ordinal),
                "no key writes nothing");

            var settingsPath = Path.Combine(AppPaths.Root, "settings.json");
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(settingsPath, legacyJson);
            Expect(SettingsStore.Load().OmdbApiKey == "synthetic-legacy-key", "the store loads a legacy plain-text key");
            var resaved = File.ReadAllText(settingsPath);
            Expect(!resaved.Contains("synthetic-legacy-key", StringComparison.Ordinal) && resaved.Contains("OmdbApiKeyProtected", StringComparison.Ordinal),
                "loading a legacy file rewrites it with the key sealed");
            File.Delete(settingsPath);
        });

        test("a bare host that will connect over HTTP shows the plain-HTTP warning", () =>
        {
            foreach (var plain in new[] { "jellyfin.example:8096", "  jellyfin.example ", "http://jellyfin.example", "HTTP://jellyfin.example" })
                Expect(JellyfinService.UsesPlainHttp(plain), "plain HTTP is flagged: " + plain);
            foreach (var safe in new[] { "https://jellyfin.example", "HTTPS://jellyfin.example:8920", "", "   " })
                Expect(!JellyfinService.UsesPlainHttp(safe), "no warning for: '" + safe + "'");
        });
    }

    private static byte[] Padded(byte[] image, int length)
    {
        var padded = new byte[length];
        image.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] Png(int width, int height)
    {
        var stride = (width + 7) / 8;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite, null, new byte[stride * height], stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Loopback HTTP/1.1 server answering each GET with a fixed body, or 404.</summary>
    private sealed class StaticHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public StaticHttpServer()
        {
            _listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptAsync);
        }

        public string Origin { get; }
        public ConcurrentDictionary<string, byte[]> Routes { get; } = new();

        public void Dispose() => _listener.Stop();

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync() ?? "";
                    while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                    var path = requestLine.Split(' ') is [_, var target, ..] ? target : "/";
                    var found = Routes.TryGetValue(path, out var body);
                    body ??= [];
                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch (IOException) { }
            }
        }
    }

    private static DownloadItem Item(Guid id, string path) => new()
    {
        ItemId = id,
        ServerUrl = "https://jellyfin.example",
        Title = "Movie",
        FilePath = path,
    };

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
