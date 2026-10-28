using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using LightWeaver.Settings;

namespace LightWeaver.Updates;

/// <summary>Why reverting an update stopped.</summary>
public enum RevertFailure
{
    NoRelease,
    NoInstaller,
    Download,
    Verification,
    SettingsNotSaved,
    Launch,
    Busy,
}

/// <summary>Reverting an update failed for <see cref="Reason"/>.</summary>
public sealed class UpdateRevertException(RevertFailure reason, Exception? inner = null)
    : Exception(reason.ToString(), inner)
{
    public RevertFailure Reason { get; } = reason;
}

/// <summary>A downloaded and verified installer, held open without write or delete sharing so the
/// file at <see cref="Path"/> cannot change until it has started. Disposing releases it and ends
/// the revert operation.</summary>
public sealed class StagedRevert : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal StagedRevert(string path, Version version, string sha256, string? debugSha256, FileStream hold, Action release)
    {
        Path = path;
        Version = version;
        Sha256 = sha256;
        DebugSha256 = debugSha256;
        Hold = hold;
        _release = release;
    }

    public string Path { get; }
    internal Version Version { get; }
    internal string Sha256 { get; }
    internal string? DebugSha256 { get; }
    internal FileStream Hold { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hold.Dispose();
        _release();
    }
}

/// <summary>Reverts an update to the version installed before it. The installer records that version
/// next to the app; its setup comes from its own GitHub release, is verified like an update and
/// installs over this version, keeping the login and settings. One revert runs at a time.</summary>
public sealed class UpdateRevert
{
    /// <summary>The installer's record of the version it replaced, in the app folder.</summary>
    public const string RecordFileName = "previous-version.txt";

    private const int RedirectLimit = 5;
    private static int s_active;
    private readonly GitHubUpdateClient _client;
    private readonly IUpdateInstallerLauncher _launcher;
    private readonly HttpMessageHandler? _downloadHandler;
    private readonly string _stagingDirectory;

    public UpdateRevert(GitHubUpdateClient? client = null, IUpdateInstallerLauncher? launcher = null,
        HttpMessageHandler? downloadHandler = null, string? stagingDirectory = null)
    {
        _client = client ?? new GitHubUpdateClient();
        _launcher = launcher ?? new UpdateInstallerLauncher();
        _downloadHandler = downloadHandler;
        _stagingDirectory = stagingDirectory ?? System.IO.Path.Combine(AppPaths.Root, "updates");
    }

    /// <summary>The version installed before <paramref name="current"/>; null when none is recorded
    /// or the record is not an older version.</summary>
    public static Version? PreviousVersion(string appDirectory, Version current)
    {
        string text;
        try
        {
            text = File.ReadAllText(System.IO.Path.Combine(appDirectory, RecordFileName)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return GitHubUpdateClient.TryParseThreePartVersion(text, out var version) && version < current ? version : null;
    }

    /// <summary>Downloads the setup of <paramref name="version"/>'s release, pins it and verifies the
    /// pinned file. <paramref name="progress"/> reports 0 to 1. Throws
    /// <see cref="UpdateRevertException"/> for every expected failure, and
    /// <see cref="RevertFailure.Busy"/> while another revert holds its installer.</summary>
    public async Task<StagedRevert> DownloadAsync(Version version, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref s_active, 1, 0) != 0) throw new UpdateRevertException(RevertFailure.Busy);
        string? path = null;
        FileStream? hold = null;
        var handedOver = false;
        try
        {
            var release = await _client.GetReleaseAsync(version, cancellationToken).ConfigureAwait(false)
                ?? throw new UpdateRevertException(RevertFailure.NoRelease);
            if (release is not { SetupAssetUri: { } asset, AssetName: { } name, ExpectedSize: { } size })
                throw new UpdateRevertException(RevertFailure.NoInstaller);
            Directory.CreateDirectory(_stagingDirectory);
            path = System.IO.Path.Combine(_stagingDirectory, name);
            await FetchAsync(asset, path, size, progress, cancellationToken).ConfigureAwait(false);

            hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await HashAsync(hold, cancellationToken).ConfigureAwait(false);
            if (!await UpdatePackageVerifier.VerifyAsync(path, release.Version, release.DebugSha256, cancellationToken).ConfigureAwait(false))
            {
                Diagnostics.AppLog.Error("updates", $"event=revert outcome=failure reason=verification to_version={release.DisplayVersion}");
                throw new UpdateRevertException(RevertFailure.Verification);
            }
            Diagnostics.AppLog.Info("updates", $"event=revert outcome=ready from_version={GitHubUpdateClient.CurrentVersion.ToString(3)} to_version={release.DisplayVersion}");
            handedOver = true;
            return new StagedRevert(path, release.Version, sha256, release.DebugSha256, hold, End);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException
            or UnauthorizedAccessException or TaskCanceledException or CryptographicException)
        {
            throw new UpdateRevertException(RevertFailure.Download, ex);
        }
        finally
        {
            if (!handedOver)
            {
                hold?.Dispose();
                if (path is not null) TryDelete(path);
                End();
            }
        }
    }

    /// <summary>Checks the pinned installer once more off the caller's thread, sets updates to manual
    /// so the older version does not update itself straight back, and starts the installer with its
    /// own window. The policy change and <paramref name="saveSettings"/> run on the caller's context.
    /// Nothing starts unless the manual policy was saved; a failed start puts the old policy back.</summary>
    public async Task InstallAsync(StagedRevert staged, AppSettings settings, Func<bool> saveSettings, CancellationToken cancellationToken)
    {
        bool intact;
        try
        {
            intact = await Task.Run(() => IsIntactAsync(staged, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new UpdateRevertException(RevertFailure.Verification, ex);
        }
        if (!intact) throw new UpdateRevertException(RevertFailure.Verification);

        var policy = settings.UpdatePolicy;
        settings.UpdatePolicy = UpdatePolicy.ManualInstall;
        if (!Save(saveSettings, out var unsaved))
        {
            settings.UpdatePolicy = policy;
            if (unsaved is not null) Save(saveSettings, out _);
            throw new UpdateRevertException(RevertFailure.SettingsNotSaved, unsaved);
        }
        var launched = false;
        Exception? failure = null;
        try
        {
            launched = _launcher.Launch(staged.Path, silent: false, relaunch: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            failure = ex;
            Diagnostics.AppLog.Error("updates", $"event=revert outcome=failure reason=launch error={ex.GetType().Name}");
        }
        if (launched) return;
        settings.UpdatePolicy = policy;
        Save(saveSettings, out _);
        throw new UpdateRevertException(RevertFailure.Launch, failure);
    }

    private static void End() => Interlocked.Exchange(ref s_active, 0);

    private static async Task<bool> IsIntactAsync(StagedRevert staged, CancellationToken cancellationToken)
        => string.Equals(await HashAsync(staged.Hold, cancellationToken).ConfigureAwait(false), staged.Sha256, StringComparison.OrdinalIgnoreCase)
            && await UpdatePackageVerifier.VerifyAsync(staged.Path, staged.Version, staged.DebugSha256, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs the caller's save; one that throws has not saved, and <paramref name="failure"/> holds why.</summary>
    private static bool Save(Func<bool> saveSettings, out Exception? failure)
    {
        failure = null;
        try
        {
            return saveSettings();
        }
        catch (Exception ex)
        {
            failure = ex;
            Diagnostics.AppLog.Error("updates", $"event=revert outcome=failure reason=settings error={ex.GetType().Name}");
            return false;
        }
    }

    private static async Task<string> HashAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Streams the asset to <paramref name="path"/>, following only redirects to the release
    /// hosts, and fails unless exactly <paramref name="size"/> bytes arrive.</summary>
    private async Task FetchAsync(Uri asset, string path, long size, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var handler = _downloadHandler is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
        using var http = new HttpClient(_downloadHandler ?? handler!, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        var uri = asset;
        HttpResponseMessage? response = null;
        for (var hop = 0; hop <= RedirectLimit; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400) break;
            var next = response.Headers.Location is { } location ? new Uri(uri, location) : null;
            response.Dispose();
            response = null;
            if (next is null || !GitHubUpdateClient.IsApprovedDownloadUri(next))
                throw new HttpRequestException("redirect to an unapproved endpoint");
            uri = next;
        }
        using (response)
        {
            if (response is null) throw new HttpRequestException("redirect limit");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != size) throw new IOException("size mismatch");
            long received = 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            var buffer = new byte[1024 * 128];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (read > size - received) throw new IOException("longer than declared");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report((double)received / size);
            }
            if (received != size) throw new IOException("shorter than declared");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Detail("updates", $"event=revert_cleanup outcome=failure error={ex.GetType().Name}");
        }
    }
}
