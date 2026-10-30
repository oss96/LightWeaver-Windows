using System.Net;
using System.Net.Sockets;
using System.Text;
using LightWeaver.Jellyfin;
using LightWeaver.Login;
using LightWeaver.ViewModels;

// Before anything resolves AppPaths: the sign-out checks save credentials, and they must land in
// a temporary folder, never in an app's real profile. Release builds ignore the override, so the
// root is checked before anything touches app data.
var appData = Path.Combine(Path.GetTempPath(), "lw-login-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", appData);
if (!string.Equals(LightWeaver.AppPaths.Root, Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run: app data resolves to {LightWeaver.AppPaths.Root}, not the test folder. "
        + "Only Debug builds honour LIGHTWEAVER_TEST_APPDATA_ROOT; run this test without -c Release.");
    return 1;
}

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// --- Address candidates (design 12a: https, then http, then port 8096) -----------------------
Check(ServerAddress.Candidates("media.home.lan").SequenceEqual(["https://media.home.lan", "http://media.home.lan", "http://media.home.lan:8096"]),
    "bare host tries https, http, then :8096");
Check(ServerAddress.Candidates("192.0.2.10:8096").SequenceEqual(["https://192.0.2.10:8096", "http://192.0.2.10:8096"]),
    "an explicit port is kept and not replaced by 8096");
Check(ServerAddress.Candidates(" https://media.example.com/ ").SequenceEqual(["https://media.example.com"]), "a full URL is used as typed, trimmed");
Check(ServerAddress.Candidates("HTTP://Host:80/jellyfin/").SequenceEqual(["http://host/jellyfin"]), "a full URL keeps its sub-path");
Check(ServerAddress.Candidates("host/jellyfin").SequenceEqual(["https://host/jellyfin", "http://host/jellyfin", "http://host:8096/jellyfin"]),
    "a sub-path survives the port fallback");
Check(ServerAddress.Candidates("[fe80::1]:8920").SequenceEqual(["https://[fe80::1]:8920", "http://[fe80::1]:8920"]), "bracketed IPv6 with a port");
Check(ServerAddress.Candidates("[fe80::1]").Count == 3, "bracketed IPv6 without a port gets the 8096 fallback");
Check(ServerAddress.Candidates("").Count == 0 && ServerAddress.Candidates("   ").Count == 0 && ServerAddress.Candidates(null).Count == 0, "empty input");
Check(ServerAddress.Candidates("not an address").Count == 0, "text with spaces is not an address");
Check(ServerAddress.Display("http://media.home.lan:8096") == "media.home.lan:8096", "display drops the scheme, keeps the port");
Check(ServerAddress.Display("https://host/jellyfin/") == "host/jellyfin", "display keeps the sub-path");

// --- Probing ----------------------------------------------------------------------------------
var routes = new Dictionary<string, (HttpStatusCode Status, string Body)>
{
    ["http://media.home.lan:8096/System/Info/Public"] = (HttpStatusCode.OK, """{"ServerName":"Home Server","Id":"abc123","Version":"10.11.0"}"""),
    ["https://web.example/System/Info/Public"] = (HttpStatusCode.OK, "<html>not jellyfin</html>"),
    ["http://web.example/System/Info/Public"] = (HttpStatusCode.OK, """{"Something":"else"}"""),
    ["http://media.home.lan:8096/QuickConnect/Enabled"] = (HttpStatusCode.OK, "true"),
    ["http://media.home.lan:8096/Users/Public"] = (HttpStatusCode.OK,
        """[{"Name":"Mara","Id":"6b4d57c3a0d34f5e9a1b2c3d4e5f6a7b","PrimaryImageTag":"t1","HasPassword":true},{"Name":"Kids","Id":"0f0e0d0c0b0a49088776655443322110","HasPassword":false},{"Name":"","Id":"x"}]"""),
};
var requested = new List<string>();
var probe = new ServerProbe(new HttpClient(new FakeHandler(routes, requested)));

var found = await probe.FindAsync("media.home.lan");
Check(found is { Url: "http://media.home.lan:8096", Name: "Home Server" }, "finds the server on the third candidate");
Check(requested.Take(3).SequenceEqual(["https://media.home.lan/System/Info/Public", "http://media.home.lan/System/Info/Public", "http://media.home.lan:8096/System/Info/Public"]),
    "candidates are tried in order: " + string.Join(", ", requested));
Check(await probe.FindAsync("web.example") is null, "a web server that is not Jellyfin is not a server");
Check(await probe.FindAsync("nothing.here") is null, "nothing answering is not a server");
Check(await probe.QuickConnectEnabledAsync("http://media.home.lan:8096"), "Quick Connect enabled");
Check(!await probe.QuickConnectEnabledAsync("http://nothing.here"), "Quick Connect unknown counts as off");
var users = await probe.PublicUsersAsync("http://media.home.lan:8096");
Check(users.Select(u => u.Name).SequenceEqual(["Mara", "Kids"]), "public users, malformed entries dropped");
Check(users[0].ImageUrl("http://s", 80) == "http://s/Users/6b4d57c3a0d34f5e9a1b2c3d4e5f6a7b/Images/Primary?tag=t1&maxWidth=80&quality=90" && users[1].ImageUrl("http://s", 80) is null,
    "avatar URL only for users with a picture");
Check(!users[1].HasPassword && users[0].HasPassword, "password flag read");

// --- Saved servers ----------------------------------------------------------------------------
SavedCredentials Profile(string url, string user, string? name = null) => new(url, user, "token-" + user, Guid.NewGuid(), name);
var mara = Profile("http://media.home.lan:8096", "Mara", "Home Server");
var jonah = Profile("http://MEDIA.home.lan:8096/", "Jonah");
var cabin = Profile("http://192.0.2.20:8096", "Mara");
var servers = SavedServers.From(new CredentialProfiles([mara, cabin, jonah], 2));
Check(servers.Count == 2, "profiles on one server group into one saved server");
Check(servers[0] is { Name: "Home Server" } && ReferenceEquals(servers[0].Profile, jonah) && servers[0].Profiles.Count == 2,
    "the active profile is the one a click signs in as");
Check(servers[1] is { Name: "192.0.2.20:8096" } && ReferenceEquals(servers[1].Profile, cabin), "a server without a saved name shows its address");
Check(SavedServers.From(new CredentialProfiles([mara, jonah], 1))[0].Name == "Home Server", "any profile's server name labels the group");
Check(SavedServers.From(new CredentialProfiles([], -1)).Count == 0, "first launch: no saved servers");

// --- Sign-out order: local first, the server revocation behind it -----------------------------
using (var server = new SessionServer())
{
    var userId = Guid.NewGuid();
    var signedIn = new SavedCredentials(server.Origin, "Mara", "token-mara", userId, "Home Server");
    var other = new SavedCredentials("http://cabin.invalid:8096", "Jonah", "token-jonah", Guid.NewGuid(), "Cabin");
    CredentialStore.Save(other);
    var app = new AppViewModel { RevokesServerSessions = true };
    server.UserId = userId;
    Check(await app.SwitchProfileAsync(signedIn) && app.ActiveSessionKey is not null, "the stand-in server signs Mara in");

    app.SignOut();
    Check(app.State == AppState.Login && app.ActiveSessionKey is null, "sign-out shows Login before the server answers the revocation");
    Check(await server.LogoutArrived.Task.WaitAsync(TimeSpan.FromSeconds(10)) == "token-mara" && !app.LastRevocation.IsCompleted,
        "the revocation carries Mara's token and is still waiting on the server");
    Check(CredentialStore.LoadProfiles().Profiles.Select(p => p.Username).SequenceEqual(["Jonah"]), "only Mara's profile is removed");

    app.SignOut();
    Check(CredentialStore.LoadProfiles().Profiles.Select(p => p.Username).SequenceEqual(["Jonah"]),
        "a second sign-out with no session leaves the next saved profile alone");

    server.ReleaseLogout.SetResult();
    await app.LastRevocation.WaitAsync(TimeSpan.FromSeconds(10));
    Check(app.LastRevocation.IsCompletedSuccessfully, "the revocation finishes after the session was closed and disposed");
}
// --- One device per profile: signing in on one profile keeps the other signed in ---------------
var shared = CredentialStore.GetOrCreateDeviceId();
using (var first = JellyfinService.ForSignIn())
using (var second = JellyfinService.ForSignIn())
{
    string DeviceOf(JellyfinService service) => service.AuthorizationHeader.Split("DeviceId=\"")[1].Split('"')[0];
    Check(DeviceOf(first) != DeviceOf(second) && DeviceOf(first) != shared && DeviceOf(second) != shared,
        "every sign-in speaks as a device of its own");
}
var legacy = new SavedCredentials("http://media.home.lan:8096", "Mara", "token-legacy", Guid.NewGuid());
Check(CredentialStore.DeviceIdFor(legacy) == shared, "a profile saved before per-profile devices keeps the shared id its token belongs to");
Check(CredentialStore.DeviceIdFor(legacy with { DeviceId = "lan-device" }) == "lan-device", "a profile with its own device uses it");
CredentialStore.Save(new SavedCredentials("http://cabin.invalid:8096", "Jonah", "token-jonah", Guid.NewGuid(), "Cabin", "cabin-device"));
Check(CredentialStore.LoadProfiles().Profiles.Single(p => p.Username == "Jonah").DeviceId == "cabin-device",
    "the profile's device survives saving and loading");
using (var server = new SessionServer())
{
    var userId = Guid.NewGuid();
    server.UserId = userId;
    var app = new AppViewModel();
    Check(await app.SwitchProfileAsync(new SavedCredentials(server.Origin, "Mara", "token-lan", userId, "Home", "lan-device"))
        && server.MeAuthorization?.Contains("DeviceId=\"lan-device\"", StringComparison.Ordinal) == true,
        "a saved profile reconnects as its own device");
    Check(await new AppViewModel().SwitchProfileAsync(new SavedCredentials(server.Origin, "Mara", "token-old", userId, "Home"))
        && server.MeAuthorization?.Contains($"DeviceId=\"{shared}\"", StringComparison.Ordinal) == true,
        "an older profile reconnects as the shared device its token was issued to");

    using var service = new JellyfinService("lan-device");
    await service.ReconnectAsync(new SavedCredentials(server.Origin, "Mara", "token-lan", userId, "Home", "lan-device"));
    await service.DiscardUnplayedDecisionAsync(new PlaybackDecision("http://unused", true, "play session/1", null));
    Check(server.EncodingTeardown == "DELETE /Videos/ActiveEncodings?deviceId=lan-device&playSessionId=play%20session%2F1 Token=\"token-lan\"",
        "a discarded decision ends its server encoding as the profile's device: " + server.EncodingTeardown);
}
try { Directory.Delete(appData, recursive: true); } catch (IOException) { }

Console.WriteLine($"{checks} checks passed.");
return 0;

/// <summary>A Jellyfin stand-in on a loopback port: answers /Users/Me, records the encoding
/// teardown, and holds /Sessions/Logout open until <see cref="ReleaseLogout"/> is set, reporting
/// the token it carried.</summary>
sealed class SessionServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public SessionServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Origin = $"http://localhost:{port}";
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _ = ServeAsync();
    }

    public string Origin { get; }
    public Guid UserId { get; set; }
    public string? MeAuthorization { get; private set; }
    public string? EncodingTeardown { get; private set; }
    public TaskCompletionSource<string?> LogoutArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseLogout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            _ = RespondAsync(context);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            switch (context.Request.Url!.AbsolutePath)
            {
                case "/Users/Me":
                    MeAuthorization = context.Request.Headers["Authorization"];
                    var body = Encoding.UTF8.GetBytes("{\"Id\":\"" + UserId.ToString("N") + "\",\"Name\":\"Mara\"}");
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(body);
                    break;
                case "/Videos/ActiveEncodings":
                    var authorization = context.Request.Headers["Authorization"] ?? "";
                    EncodingTeardown = $"{context.Request.HttpMethod} {context.Request.Url.PathAndQuery} "
                        + authorization[Math.Max(0, authorization.IndexOf("Token=", StringComparison.Ordinal))..];
                    response.StatusCode = 204;
                    break;
                case "/Sessions/Logout":
                    var token = context.Request.Headers["Authorization"] is { } header && header.IndexOf("Token=\"", StringComparison.Ordinal) is var at and >= 0
                        ? header[(at + 7)..].TrimEnd('"')
                        : null;
                    LogoutArrived.TrySetResult(token);
                    await ReleaseLogout.Task;
                    response.StatusCode = 204;
                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        ReleaseLogout.TrySetResult();
        _listener.Close();
    }
}

sealed class FakeHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes, List<string> requested) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        requested.Add(url);
        if (!routes.TryGetValue(url, out var route)) throw new HttpRequestException("No such host is known.");
        return Task.FromResult(new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") });
    }
}
