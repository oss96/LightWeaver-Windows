using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Player;

internal static class RemoteBufferFixture
{
    /// <summary>The remote endpoint is the operator's own server, never hard-coded here.</summary>
    private static string? RemoteServer =>
        Environment.GetEnvironmentVariable("LIGHTWEAVER_REMOTE_SERVER") is { } value
            && !string.IsNullOrWhiteSpace(value) ? value.Trim().TrimEnd('/') : null;

    /// <summary>Reports the skip and returns true when no remote server is configured. An absent
    /// server is not a failure: the fixture simply has nothing to reach.</summary>
    public static bool TrySkip()
    {
        if (RemoteServer is not null) return false;
        Console.WriteLine("SKIP: remote Jellyfin playback needs LIGHTWEAVER_REMOTE_SERVER "
            + "(the base URL of a reachable server).");
        return true;
    }

    public static void Run()
    {
        // The caller gates this on TrySkip(), so reaching Run() without a server is a wiring bug.
        // Throwing rather than returning keeps a test that did nothing from being reported as passed.
        if (RemoteServer is not { } remote)
            throw new InvalidOperationException(
                "RemoteBufferFixture.Run was called without LIGHTWEAVER_REMOTE_SERVER; gate it on TrySkip().");
        // Raw mpv capture includes HTTP headers. Never inherit it in an authenticated test.
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_LOG", null);
        // Runtime-only DPAPI access. Never print the credential payload or request headers.
        var encrypted = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "LightWeaver-Debug", "credentials.dat"));
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        SavedCredentials credentials;
        try
        {
            using var doc = JsonDocument.Parse(plain);
            var profiles = doc.RootElement.TryGetProperty("Profiles", out var list)
                ? list.EnumerateArray().Select(x => x.Deserialize<SavedCredentials>()!).ToArray()
                : [doc.RootElement.Deserialize<SavedCredentials>()!];
            credentials = profiles.Single(x => x.Username.Equals("test", StringComparison.OrdinalIgnoreCase));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var localInfo = Get(http, credentials.ServerUrl.TrimEnd('/') + "/System/Info/Public");
        using var remoteInfo = Get(http, remote + "/System/Info/Public");
        var serverId = localInfo.RootElement.GetProperty("Id").GetString();
        if (string.IsNullOrEmpty(serverId) || serverId != remoteInfo.RootElement.GetProperty("Id").GetString())
            throw new InvalidOperationException("Remote server identity differs; no credentials sent.");
        var auth = $"MediaBrowser Token=\"{credentials.AccessToken}\"";
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        using var items = Get(http, $"{remote}/Items?UserId={credentials.UserId:N}&Recursive=true&IncludeItemTypes=Movie&Limit=1");
        var itemId = items.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString();
        var app = new App();
        app.InitializeComponent();
        var main = new MainWindow();
        try
        {
            main.Show();
            main.PlayUrl($"{remote}/Videos/{itemId}/stream?static=true", auth);
            var player = (MpvPlayer)typeof(MainWindow).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            player.ApplyVideoBuffer(new LightWeaver.Settings.AppSettings { VideoBufferMiB = 32 });
            var until = DateTime.UtcNow.AddSeconds(40);
            double position = 0;
            long cacheBytes = 0;
            do
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                double.TryParse(player.GetPropertyString("time-pos"), CultureInfo.InvariantCulture, out position);
                var state = player.GetPropertyString("demuxer-cache-state");
                if (!string.IsNullOrEmpty(state))
                {
                    using var cache = JsonDocument.Parse(state);
                    if (cache.RootElement.TryGetProperty("fw-bytes", out var bytes)) cacheBytes = Math.Max(cacheBytes, bytes.GetInt64());
                }
            } while ((position < 5 || cacheBytes == 0) && DateTime.UtcNow < until);
            if (position < 5 || cacheBytes == 0 || player.GetPropertyString("demuxer-max-bytes") != "33554432")
                throw new InvalidOperationException("Remote playback failed to advance with an active configured cache.");
            Console.WriteLine($"REMOTE EVIDENCE: position={position:F1}s cacheObservedBytes={cacheBytes} bufferMiB=32");
        }
        finally { main.Close(); app.Shutdown(); }
    }

    private static JsonDocument Get(HttpClient http, string url)
    {
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote fixture HTTP status {(int)response.StatusCode}.");
        return JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }
}
