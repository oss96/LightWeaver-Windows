using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Updates;

// Revert and release-lookup failures are logged under AppPaths; keep those logs out of an app's real profile.
var appData = Path.Combine(Path.GetTempPath(), "lw-updates-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", appData);
if (!string.Equals(LightWeaver.AppPaths.Root, Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run: app data resolves to {LightWeaver.AppPaths.Root}, not the test folder. "
        + "Only Debug builds honour LIGHTWEAVER_TEST_APPDATA_ROOT; run this test without -c Release.");
    return 1;
}
L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// The Debug build's fixture endpoint: a loopback API host, and the package hash it vouches for.
const string Api = "http://127.0.0.1:9/";
var package = Encoding.UTF8.GetBytes("installer for 1.6.0");
Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_API", Api);
Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_SHA256", Convert.ToHexString(SHA256.HashData(package)));

string Release(string tag, bool prerelease = false, bool asset = true, long? size = null) => $$"""
    {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"{{Api}}releases/{{tag}}",
     "assets":[{{(asset ? $$"""{"name":"LightWeaver-{{tag}}-setup.exe","browser_download_url":"{{Api}}download/{{tag}}","size":{{size ?? package.Length}}}""" : "")}}]}
    """;

HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

var staging = Directory.CreateTempSubdirectory("lw-revert-tests-").FullName;
var current = new Version(2, 0, 0);

// --- The installer's record -----------------------------------------------------------------------
var app = Directory.CreateTempSubdirectory("lw-revert-app-").FullName;
Check(UpdateRevert.PreviousVersion(app, current) is null, "no record, nothing to revert to");
File.WriteAllText(Path.Combine(app, UpdateRevert.RecordFileName), "1.6.0\r\n");
Check(UpdateRevert.PreviousVersion(app, current) == new Version(1, 6, 0), "the recorded version, whitespace trimmed");
File.WriteAllText(Path.Combine(app, UpdateRevert.RecordFileName), "2.0.0");
Check(UpdateRevert.PreviousVersion(app, current) is null, "the same version is not offered");
File.WriteAllText(Path.Combine(app, UpdateRevert.RecordFileName), "2.1.0");
Check(UpdateRevert.PreviousVersion(app, current) is null, "a newer version is not offered as a revert");
File.WriteAllText(Path.Combine(app, UpdateRevert.RecordFileName), "1.6");
Check(UpdateRevert.PreviousVersion(app, current) is null, "a record that is not a three-part version is ignored");

// --- The release by version --------------------------------------------------------------------
GitHubUpdateClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(new HttpClient(new Fake(answer)));
string? asked = null;
var found = await Client(r => { asked = r.RequestUri!.AbsolutePath; return Json(Release("1.6.0")); }).GetReleaseAsync(new Version(1, 6, 0), CancellationToken.None);
Check(asked == "/releases/tags/1.6.0", $"the release is asked for by its tag ({asked})");
Check(found is { AssetName: "LightWeaver-1.6.0-setup.exe", SetupAssetUri: not null } && found.ExpectedSize == package.Length, "an older release comes back with its setup asset");
Check(await Client(_ => Json("{}", HttpStatusCode.NotFound)).GetReleaseAsync(new Version(1, 6, 0), CancellationToken.None) is null, "a missing release is null");
Check(await Client(_ => Json(Release("1.6.0", prerelease: true))).GetReleaseAsync(new Version(1, 6, 0), CancellationToken.None) is null, "a prerelease is never a revert target");
Check(await Client(_ => Json(Release("1.5.0"))).GetReleaseAsync(new Version(1, 6, 0), CancellationToken.None) is null, "a release whose tag is another version is refused");

// --- The newest release, unchanged by the shared reader -------------------------------------------
var latest = await Client(_ => Json($"[{Release("9.0.0")}]")).GetLatestAsync(CancellationToken.None);
Check(latest is { Version.Major: 9, AssetName: "LightWeaver-9.0.0-setup.exe" }, "the update check still finds a newer release and its asset");
Check(await Client(_ => Json($"[{Release("0.1.0")}]")).GetLatestAsync(CancellationToken.None) is null, "and nothing when the newest is not newer");

// --- The running version is the app's one version (Directory.Build.props) --------------------------
var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (!File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent!;
var appVersion = XDocument.Load(Path.Combine(repo.FullName, "Directory.Build.props")).Descendants("Version").Single().Value;
Check(GitHubUpdateClient.CurrentVersion.ToString(3) == appVersion, $"the update check compares against the app version {appVersion} ({GitHubUpdateClient.CurrentVersion.ToString(3)})");

// --- Download, verification and the failures ------------------------------------------------------
var launcher = new RecordingLauncher();
UpdateRevert Revert(Func<HttpRequestMessage, HttpResponseMessage> api, Func<HttpRequestMessage, HttpResponseMessage> download, string? into = null)
    => new(Client(api), launcher, new Fake(download), into ?? staging);
async Task<RevertFailure?> FailureOf(UpdateRevert revert)
{
    try
    {
        using var staged = await revert.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None);
        return null;
    }
    catch (UpdateRevertException ex)
    {
        return ex.Reason;
    }
}
var setupPath = Path.Combine(staging, "LightWeaver-1.6.0-setup.exe");

var reports = new List<double>();
var good = Revert(_ => Json(Release("1.6.0")), _ => Bytes(package));
using (var staged = await good.DownloadAsync(new Version(1, 6, 0), new SyncProgress(reports.Add), CancellationToken.None))
{
    Check(staged.Path == setupPath, "the verified installer is staged under its release name");
    Check(File.ReadAllBytes(staged.Path).SequenceEqual(package), "with exactly the downloaded bytes");
    Check(reports.Count > 0 && reports[^1] == 1, "progress reaches 100 %");
    var replaced = false;
    try
    {
        File.WriteAllBytes(staged.Path, Encoding.UTF8.GetBytes("swapped in after verification"));
        replaced = true;
    }
    catch (IOException)
    {
    }
    Check(!replaced, "the verified installer cannot be replaced while it is held");
    var deleted = false;
    try
    {
        File.Delete(staged.Path);
        deleted = !File.Exists(staged.Path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
    }
    Check(!deleted, "nor deleted");
    Check(await FailureOf(good) == RevertFailure.Busy, "a second revert while one holds its installer is refused");
}
Check(await FailureOf(good) is null, "once released, a revert can run again");

using (var redirected = await Revert(_ => Json(Release("1.6.0")), r => r.RequestUri!.AbsolutePath.StartsWith("/download")
    ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Api + "storage/1.6.0") } }
    : Bytes(package)).DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
    Check(File.Exists(redirected.Path), "a redirect to an allowed host is followed");
Check(await FailureOf(Revert(_ => Json(Release("1.6.0")), _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/setup.exe") } })) == RevertFailure.Download,
    "a redirect to another host is refused");
Check(await FailureOf(Revert(_ => Json("{}", HttpStatusCode.NotFound), _ => Bytes(package))) == RevertFailure.NoRelease, "a release that is gone says so");
Check(await FailureOf(Revert(_ => Json(Release("1.6.0", asset: false)), _ => Bytes(package))) == RevertFailure.NoInstaller, "a release without a setup says so");
Check(await FailureOf(Revert(_ => Json(Release("1.6.0", size: package.Length + 5)), _ => Bytes(package))) == RevertFailure.Download, "a download shorter than the release declares fails");
Check(await FailureOf(Revert(_ => Json(Release("1.6.0")), _ => Bytes(Encoding.UTF8.GetBytes("tampered installer!")))) == RevertFailure.Verification,
    "an installer that does not verify is refused");
Check(!File.Exists(setupPath), "a refused installer is deleted");
Check(await FailureOf(Revert(_ => throw new HttpRequestException("offline"), _ => Bytes(package))) == RevertFailure.Download, "no connection is a download failure");
var blocked = Path.Combine(staging, "not-a-folder");
File.WriteAllText(blocked, "");
Check(await FailureOf(Revert(_ => Json(Release("1.6.0")), _ => Bytes(package), into: blocked)) == RevertFailure.Download, "a staging folder that cannot be created is a download failure");
using (File.Open(setupPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    Check(await FailureOf(good) == RevertFailure.Download, "a locked installer file is a download failure");
Check(await FailureOf(good) is null, "and every failure releases the revert for a retry");

// --- Install --------------------------------------------------------------------------------------
var settings = new AppSettings { UpdatePolicy = UpdatePolicy.AutoDownloadAndInstall };
var saves = 0;
async Task<RevertFailure?> InstallFailureOf(UpdateRevert revert, StagedRevert staged, Func<bool> save)
{
    try
    {
        await revert.InstallAsync(staged, settings, save, CancellationToken.None);
        return null;
    }
    catch (UpdateRevertException ex)
    {
        return ex.Reason;
    }
}
using (var staged = await good.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
{
    Check(await InstallFailureOf(good, staged, () => { saves++; return true; }) is null, "the installer starts");
    Check(launcher.Launched == staged.Path && launcher.Silent == false && launcher.Relaunch == false, "with its own window, not silently");
    Check(settings.UpdatePolicy == UpdatePolicy.ManualInstall && saves == 1, "updates are set to manual first, so the old version does not update itself back");
}

settings.UpdatePolicy = UpdatePolicy.AutoDownloadAndInstall;
using (var ui = new SingleThreadContext())
using (var staged = await good.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
{
    int? savedOn = null;
    var failure = await ui.RunAsync(() => InstallFailureOf(good, staged, () => { savedOn = Environment.CurrentManagedThreadId; return true; }));
    Check(failure is null && savedOn == ui.ThreadId, "the settings are saved on the caller's context, so a UI thread can apply them");
}

var throwing = new RecordingLauncher();
var unsavable = new UpdateRevert(Client(_ => Json(Release("1.6.0"))), throwing, new Fake(_ => Bytes(package)), staging);
settings.UpdatePolicy = UpdatePolicy.AutoDownloadAndInstall;
var savedPolicies = new List<UpdatePolicy>();
using (var staged = await unsavable.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
{
    var failure = await InstallFailureOf(unsavable, staged, () =>
    {
        savedPolicies.Add(settings.UpdatePolicy);
        return savedPolicies.Count == 1 ? throw new InvalidOperationException("applied on the wrong thread") : true;
    });
    Check(failure == RevertFailure.SettingsNotSaved, "a save that throws stops the revert as not saved");
    Check(throwing.Launched is null && settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall, "with no installer started and the policy put back");
    Check(savedPolicies.SequenceEqual([UpdatePolicy.ManualInstall, UpdatePolicy.AutoDownloadAndInstall]), "and the old policy saved again, in case the throw came after the write");
}

var refusing = new RecordingLauncher();
var unsaved = new UpdateRevert(Client(_ => Json(Release("1.6.0"))), refusing, new Fake(_ => Bytes(package)), staging);
settings.UpdatePolicy = UpdatePolicy.AutoDownloadAndInstall;
using (var staged = await unsaved.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
{
    Check(await InstallFailureOf(unsaved, staged, () => false) == RevertFailure.SettingsNotSaved, "when the manual policy cannot be saved, the revert stops");
    Check(refusing.Launched is null, "and no installer starts");
    Check(settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall, "and the policy stays as it was");
}

var failing = new RecordingLauncher { Result = false };
var unlaunched = new UpdateRevert(Client(_ => Json(Release("1.6.0"))), failing, new Fake(_ => Bytes(package)), staging);
settings.UpdatePolicy = UpdatePolicy.AutoDownload;
using (var staged = await unlaunched.DownloadAsync(new Version(1, 6, 0), null, CancellationToken.None))
{
    Check(await InstallFailureOf(unlaunched, staged, () => true) == RevertFailure.Launch, "a launch that fails reports it");
    Check(settings.UpdatePolicy == UpdatePolicy.AutoDownload, "and puts the update policy back");
}

// --- Settings › Updates and the update toast -------------------------------------------------------
var nine = new Version(9, 0, 0);
UpdateInfo Found(bool installer = true) => new(nine, installer ? new Uri(Api + "download/9.0.0") : null, new Uri(Api + "releases/9.0.0"),
    installer ? "LightWeaver-9.0.0-setup.exe" : null);
UpdatePane Pane(UpdatePhase phase, UpdatePolicy policy = UpdatePolicy.ManualInstall, UpdateInfo? update = null, long done = 0, long? total = null, string? detail = null)
    => UpdatePane.For(new UpdateStatus(phase, update, done, total, detail), policy);
Check(Pane(UpdatePhase.Idle) is { Status: "Not checked yet", Action: UpdateAction.Check, CanCheck: true }, "before the first check, Check for updates is the action");
Check(Pane(UpdatePhase.Checking) is { Status: "Checking…", CanCheck: false, ActionEnabled: false }, "a running check cannot be started again");
Check(Pane(UpdatePhase.Available, update: Found()) is { Status: "Version 9.0.0 is available", Action: UpdateAction.DownloadAndInstall, ActionLabel: "Download and install", CanCheck: true },
    "notify only: a found update offers Download and install");
Check(Pane(UpdatePhase.Available, UpdatePolicy.AutoDownload, Found()) is { Action: UpdateAction.Download, ActionLabel: "Download" }, "the download policies offer Download");
Check(Pane(UpdatePhase.Available, update: Found(installer: false)).Action == UpdateAction.OpenReleasePage, "a release without an installer offers its page");
Check(Pane(UpdatePhase.Downloading, update: Found(), done: 42, total: 100) is { Status: "Downloading… 42 %", Progress: 0.42, ActionEnabled: false, CanCheck: false },
    "a download shows its progress and blocks both buttons");
Check(Pane(UpdatePhase.ReadyToInstall, update: Found()) is { Status: "Version 9.0.0 is verified and ready to install.", Action: UpdateAction.InstallNow },
    "a staged update offers Install now");
Check(Pane(UpdatePhase.ReadyToInstall, UpdatePolicy.AutoDownloadAndInstall, Found()).Status == "Version 9.0.0 is ready — it will install when LightWeaver closes.",
    "under automatic install it says it installs on exit");
Check(Pane(UpdatePhase.Error, update: Found(), detail: "The update download failed.") is { Status: "The update download failed.", IsError: true, Action: UpdateAction.DownloadAndInstall, ActionLabel: "Retry" },
    "a failed download retries the download");
Check(Pane(UpdatePhase.Error, detail: "LightWeaver could not reach the update server.") is { IsError: true, Action: UpdateAction.Check, ActionLabel: "Retry" },
    "a failed check retries the check");

var notices = new UpdateNotices();
const UpdateNoticePlace Away = UpdateNoticePlace.Elsewhere;
Check(notices.Next(new(UpdatePhase.Available, Found()), UpdatePolicy.AutoDownload, Away) is null, "an update the policy downloads by itself is not announced when found");
Check(notices.Next(new(UpdatePhase.Available, Found()), UpdatePolicy.ManualInstall, UpdateNoticePlace.Login) is null, "on Login a found update waits");
Check(notices.Next(new(UpdatePhase.Available, Found()), UpdatePolicy.ManualInstall, Away) is { Message: "Version 9.0.0 is available", Ready: false },
    "and is announced on the next page");
Check(notices.Next(new(UpdatePhase.Available, Found()), UpdatePolicy.ManualInstall, Away) is null, "once per version");
Check(notices.Next(new(UpdatePhase.ReadyToInstall, Found()), UpdatePolicy.AutoDownloadAndInstall, UpdateNoticePlace.UpdatesPane) is null
    && notices.Next(new(UpdatePhase.ReadyToInstall, Found()), UpdatePolicy.AutoDownloadAndInstall, Away) is null, "a ready update Settings › Updates showed is not toasted later");
Check(new UpdateNotices().Next(new(UpdatePhase.ReadyToInstall, Found()), UpdatePolicy.AutoDownloadAndInstall, Away) is { Ready: true }, "elsewhere it is");
Check(notices.Next(new(UpdatePhase.Error, Found()), UpdatePolicy.ManualInstall, Away) is null, "a failure is not announced");

// --- The updater as v2 runs it: the folder tidied on start, each policy, and the installer on exit ---
var installer = Encoding.UTF8.GetBytes("installer for 9.0.0");
Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_SHA256", Convert.ToHexString(SHA256.HashData(installer)));
using var feed = new LoopbackServer();
feed.Answer = path => path.StartsWith("/download/", StringComparison.Ordinal) ? ("application/octet-stream", installer)
    : ("application/json", Encoding.UTF8.GetBytes($$"""
        [{"tag_name":"9.0.0","draft":false,"prerelease":false,"html_url":"{{feed.Url}}releases/9.0.0",
          "assets":[{"name":"LightWeaver-9.0.0-setup.exe","browser_download_url":"{{feed.Url}}download/9.0.0","size":{{installer.Length}}}]}]
        """));
Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_API", feed.Url);
var updates = Path.Combine(LightWeaver.AppPaths.Root, "updates");
Directory.CreateDirectory(updates);
File.WriteAllText(Path.Combine(updates, "LightWeaver-1.0.0-setup.exe"), "an installed version's setup");
File.WriteAllText(Path.Combine(updates, "LightWeaver-9.0.0-setup.exe.part"), "a part nothing records");
async Task<UpdatePhase> Settled(UpdateService service)
{
    for (var i = 0; i < 200 && service.Status.Phase is UpdatePhase.Checking or UpdatePhase.Available or UpdatePhase.Downloading; i++) await Task.Delay(50);
    return service.Status.Phase;
}

var policy = new AppSettings { UpdatePolicy = UpdatePolicy.ManualInstall };
var exits = new RecordingLauncher();
using (var service = new UpdateService(policy, new GitHubUpdateClient(), exits))
{
    Check(Directory.GetFiles(updates).Length == 0, "creating the updater clears the updates folder of stale setups and parts");
    await service.CheckNowAsync();
    Check(service.Status is { Phase: UpdatePhase.Available, Update.DisplayVersion: "9.0.0" } && Directory.GetFiles(updates).Length == 0,
        "notify only: a check finds the update and downloads nothing");
    Check(!service.ShouldLaunchInstallOnNormalExit, "and nothing installs on exit");
    Check(await service.DownloadAndInstallInteractiveAsync() && service.Status.Phase == UpdatePhase.Installing, "Download and install downloads, verifies and queues the installer");
    Check(service.TryLaunchInstallAfterExit() && exits.Silent == false, "which starts with its own window once the app has closed");
}

policy.UpdatePolicy = UpdatePolicy.AutoDownloadAndInstall;
var swapped = true;
exits = new RecordingLauncher
{
    During = path =>
    {
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("swapped in after verification"));
        }
        catch (IOException)
        {
            swapped = false;
        }
    },
};
using (var service = new UpdateService(policy, new GitHubUpdateClient(), exits))
{
    Check(service.Status.Phase == UpdatePhase.ReadyToInstall, "a verified update staged earlier is ready again after a restart");
    Check(service.ShouldLaunchInstallOnNormalExit && service.TryLaunchInstallAfterExit(), "automatic install: a normal exit starts it");
    Check(exits.Silent == true && exits.Relaunch == false, "silently, without starting the app again");
    Check(!swapped, "the verified installer cannot be replaced until it has started");
}

Directory.Delete(updates, recursive: true);
policy.UpdatePolicy = UpdatePolicy.AutoDownload;
exits = new RecordingLauncher();
using (var service = new UpdateService(policy, new GitHubUpdateClient(), exits))
{
    await service.CheckNowAsync();
    Check(await Settled(service) == UpdatePhase.ReadyToInstall, "automatic download: a check downloads and verifies the update by itself");
    Check(!service.ShouldLaunchInstallOnNormalExit, "but leaves installing to the user");
    Check(await service.InstallNowAsync() && service.TryLaunchInstallAfterExit() && exits.Silent == false && exits.Relaunch == true,
        "Install now starts the installer once the app has closed");
}

Directory.Delete(staging, recursive: true);
Directory.Delete(app, recursive: true);
Console.WriteLine($"Updates: {checks} checks passed");
if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
return 0;

/// <summary>A loopback HTTP server that answers each GET from <see cref="Answer"/>, for the release feed and the setup.</summary>
sealed class LoopbackServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackServer()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public Func<string, (string Type, byte[] Body)> Answer { get; set; } = _ => ("text/plain", []);

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    private async Task AcceptAsync()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }
            var (type, body) = Answer(request.Split(' ').ElementAtOrDefault(1) ?? "/");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        }
    }

    public void Dispose() => _listener.Stop();
}

sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
}

sealed class RecordingLauncher : IUpdateInstallerLauncher
{
    public string? Launched { get; private set; }
    public bool? Silent { get; private set; }
    public bool? Relaunch { get; private set; }
    public bool Result { get; init; } = true;
    public Action<string>? During { get; init; }

    public bool Launch(string installerPath, bool silent, bool relaunch)
    {
        During?.Invoke(installerPath);
        Launched = installerPath;
        Silent = silent;
        Relaunch = relaunch;
        return Result;
    }
}

sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

/// <summary>Runs posted work on one dedicated thread, the way a UI dispatcher does.</summary>
sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Starts <paramref name="work"/> on the context's thread and completes with its result.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                result.SetResult(await work());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        }, null);
        return result.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
