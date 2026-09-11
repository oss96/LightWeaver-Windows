using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using LightWeaver.Settings;

namespace LightWeaver.Updates;

/// <summary>Shared single-flight update coordinator with persistent resumable staging.</summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StalePartAge = TimeSpan.FromDays(7);
    private const int MetadataWriteAttempts = 6;

    /// <summary>Download progress is recorded in 10% buckets — 0, 10, … 100 — so one transfer costs
    /// eleven records at most however many times the byte loop reports in.</summary>
    private const int ProgressRecordLimit = 11;
#if DEBUG
    private static int _metadataReplaceFailureConsumed;
#endif
    private readonly AppSettings _settings;
    private readonly GitHubUpdateClient _client;
    private readonly IUpdateInstallerLauncher _launcher;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _stagingDirectory = Path.Combine(AppPaths.Root, "updates");
    private readonly System.Threading.Timer _timer;
    private readonly object _automaticCancellationLock = new();
    private UpdateStatus _status = new(UpdatePhase.Idle, null);
    private DateTimeOffset? _lastCheckedUtc;
    private CancellationTokenSource? _automaticDownloadCancellation;
    private bool _automaticDownloadRestartRequested;
    private bool _disposed;
    private bool _installerLaunched;
    private bool? _queuedInstallSilent;
    private UpdateInfo? _stagedUpdate;
    private bool _started;
    private int _progressBucket = -1;
    private int _progressRecords;

    public UpdateService(AppSettings settings, GitHubUpdateClient? client = null, IUpdateInstallerLauncher? launcher = null)
    {
        _settings = settings;
        _client = client ?? new GitHubUpdateClient();
        _launcher = launcher ?? new UpdateInstallerLauncher();
        DeleteObsoleteCheckStamp();
        CleanupStaleStaging();
        RestorePendingUpdate();
        _timer = new System.Threading.Timer(_ => _ = CheckAsync("timer"), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<UpdateStatus>? StatusChanged;
    public UpdateStatus Status => _status;
    public bool HasReadyUpdate => _status.Phase == UpdatePhase.ReadyToInstall && _status.Update is not null;
    public bool ShouldLaunchInstallOnNormalExit => _queuedInstallSilent is not null
        || (_settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall && _stagedUpdate is not null
            && _status.Phase != UpdatePhase.Downloading);
    public Task CheckNowAsync() => CheckAsync("manual");
    public void Start()
    {
        if (_disposed || _started)
        {
            Detail($"event=start outcome=noop reason={(_disposed ? "disposed" : "already_started")}");
            return;
        }
        _started = true;
        Detail(FormattableString.Invariant(
            $"event=start outcome=success interval_minutes={(long)AutomaticCheckInterval.TotalMinutes}"));
        _timer.Change(AutomaticCheckInterval, AutomaticCheckInterval);
        _ = CheckAsync("startup");
    }

    public void OnPolicyChanged()
    {
        if (_disposed) return;
        var policy = PolicyToken(_settings.UpdatePolicy);
        if (_settings.UpdatePolicy == UpdatePolicy.ManualInstall)
        {
            bool cancelling;
            lock (_automaticCancellationLock)
            {
                _automaticDownloadRestartRequested = false;
                cancelling = _automaticDownloadCancellation is not null;
                _automaticDownloadCancellation?.Cancel();
            }
            // Recorded outside the lock: a verbose line is file IO, and this lock is also taken by
            // the download task's unwind.
            Detail($"event=policy policy={policy} action={(cancelling ? "cancel_download" : "noop")}");
            SetStatus(_status);
            return;
        }
        SetStatus(_status);
        var restart = false;
        lock (_automaticCancellationLock)
            if (_automaticDownloadCancellation?.IsCancellationRequested == true)
            {
                _automaticDownloadRestartRequested = true;
                restart = true;
            }
        var resume = _status.Phase is UpdatePhase.Available or UpdatePhase.Error
            && _status.Update?.SetupAssetUri is not null ? _status.Update : null;
        Detail($"event=policy policy={policy} phase={PhaseToken(_status.Phase)} action={(restart
            ? "restart_requested" : resume is not null ? "resume_download" : "noop")}");
        if (resume is not null)
            _ = DownloadAutomaticAsync(resume);
    }

    private async Task CheckAsync(string trigger)
    {
        if (_disposed || !await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            // The single-flight gate is the whole reason a manual Check now can look like it did
            // nothing, so the skip is a record rather than a silent return.
            Detail($"event=check trigger={trigger} outcome=noop reason={(_disposed ? "disposed" : "busy")}");
            return;
        }
        var started = Stopwatch.StartNew();
        var fromVersion = GitHubUpdateClient.CurrentVersion.ToString(3);
        Detail($"event=check trigger={trigger} from_version={fromVersion}");
        UpdateInfo? automaticUpdate = null;
        var staged = _status;
        try
        {
            SetStatus(new(UpdatePhase.Checking, _status.Update, LastCheckedUtc: _lastCheckedUtc));
            var update = await _client.GetLatestAsync(CancellationToken.None).ConfigureAwait(false);
            _lastCheckedUtc = DateTimeOffset.UtcNow;
            if (staged.Phase == UpdatePhase.ReadyToInstall && staged.Update is { } stagedUpdate
                && (update is null || update.Version <= stagedUpdate.Version))
            {
                Detail(FormattableString.Invariant(
                    $"event=check trigger={trigger} outcome=current reason=staged from_version={fromVersion} staged_version={stagedUpdate.DisplayVersion} elapsed_ms={started.ElapsedMilliseconds}"));
                SetStatus(staged);
                return;
            }
            if (update is null)
            {
                Detail(FormattableString.Invariant(
                    $"event=check trigger={trigger} outcome=current from_version={fromVersion} elapsed_ms={started.ElapsedMilliseconds}"));
                SetStatus(new(UpdatePhase.UpToDate, null, LastCheckedUtc: _lastCheckedUtc));
                return;
            }
            RetireOlderStagedUpdate(update.Version);
            Detail(FormattableString.Invariant(
                $"event=check trigger={trigger} outcome=available from_version={fromVersion} to_version={update.DisplayVersion} asset={(update.SetupAssetUri is null ? "absent" : "present")} elapsed_ms={started.ElapsedMilliseconds}"));
            SetStatus(new(UpdatePhase.Available, update,
                Detail: update.SetupAssetUri is null
                    ? $"Version {update.DisplayVersion} is available, but it has no Windows installer asset."
                    : null,
                LastCheckedUtc: _lastCheckedUtc));
            if (_settings.UpdatePolicy is UpdatePolicy.AutoDownload or UpdatePolicy.AutoDownloadAndInstall
                && update.SetupAssetUri is not null)
                automaticUpdate = update;
        }
        catch (TaskCanceledException ex)
        {
            RecordCheckFailure(trigger, "timeout", ex, started);
            SetCheckError(staged, "The update check timed out. Try again.");
        }
        catch (JsonException ex)
        {
            RecordCheckFailure(trigger, "invalid_feed", ex, started);
            SetCheckError(staged, "The update server returned invalid release information.");
        }
        catch (HttpRequestException ex)
        {
            RecordCheckFailure(trigger, "unreachable", ex, started);
            SetCheckError(staged, "LightWeaver could not reach the update server.");
        }
        catch (IOException ex)
        {
            RecordCheckFailure(trigger, "read_error", ex, started);
            SetCheckError(staged, "LightWeaver could not read the update response.");
        }
        finally
        {
            _operationGate.Release();
            if (automaticUpdate is not null && !_disposed && _settings.UpdatePolicy != UpdatePolicy.ManualInstall)
                _ = DownloadAutomaticAsync(automaticUpdate);
        }
    }

    public async Task DownloadAsync()
    {
        var update = _status.Update;
        if (update?.SetupAssetUri is null || _disposed || !await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            Detail($"event=download trigger=manual outcome=noop reason={DownloadRefusalReason(update)}");
            return;
        }
        try { await DownloadCoreAsync(update, "manual", CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or CryptographicException or OperationCanceledException)
        { SetDownloadError(update, ex); }
        finally { _operationGate.Release(); }
    }

    public async Task<bool> DownloadAndInstallInteractiveAsync()
    {
        var update = _status.Update;
        if (update?.SetupAssetUri is null || _disposed || !await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            Detail($"event=download trigger=manual_install outcome=noop reason={DownloadRefusalReason(update)}");
            return false;
        }
        try
        {
            await DownloadCoreAsync(update, "manual_install", CancellationToken.None).ConfigureAwait(false);
            return QueueStagedInstaller(silent: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or CryptographicException or OperationCanceledException)
        {
            SetDownloadError(update, ex);
            return false;
        }
        finally { _operationGate.Release(); }
    }

    public Task<bool> InstallNowAsync() => Task.FromResult(QueueStagedInstaller(
        silent: _settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall));

    public bool TryLaunchSilentInstallOnExit()
    {
        // Same predicate, negated, so the refusal can name itself instead of returning a bare false.
        if (_settings.UpdatePolicy != UpdatePolicy.AutoDownloadAndInstall || _stagedUpdate is null
            || _status.Phase == UpdatePhase.Downloading)
        {
            Detail($"event=exit_install outcome=noop reason=policy policy={PolicyToken(_settings.UpdatePolicy)} phase={PhaseToken(_status.Phase)}");
            return false;
        }
        return LaunchStagedInstaller(silent: true);
    }

    public bool TryLaunchInstallAfterExit()
    {
        if (_queuedInstallSilent is not { } silent)
        {
            Detail("event=exit_install route=policy");
            return TryLaunchSilentInstallOnExit();
        }
        _queuedInstallSilent = null;
        Detail($"event=exit_install route=queued silent={(silent ? "true" : "false")}");
        return LaunchStagedInstaller(silent);
    }

    public void OpenReleasePage()
    {
        if (_status.Update is not { } update) return;
        // The release page URI is never recorded, only the version it belongs to.
        try
        {
            Process.Start(new ProcessStartInfo(update.ReleasePageUri.AbsoluteUri) { UseShellExecute = true });
            Detail($"event=release_page outcome=success to_version={update.DisplayVersion}");
        }
        catch (Win32Exception ex) { Detail($"event=release_page outcome=failure error={ex.GetType().Name}"); }
    }

    private async Task DownloadAutomaticAsync(UpdateInfo update)
    {
        if (_disposed || _settings.UpdatePolicy == UpdatePolicy.ManualInstall)
        {
            Detail($"event=download trigger=automatic outcome=noop reason={(_disposed ? "disposed" : "policy")}");
            return;
        }
        CancellationTokenSource? claimed = null;
        lock (_automaticCancellationLock)
            if (_automaticDownloadCancellation is null)
                _automaticDownloadCancellation = claimed = new CancellationTokenSource();
        if (claimed is null)
        {
            Detail("event=download trigger=automatic outcome=noop reason=in_flight");
            return;
        }
        var cancellation = claimed;
        try
        {
            await _operationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try { await DownloadCoreAsync(update, "automatic", cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The one `cancelled` in this file that is real: a caller-owned token, cancelled by
                // the policy switching to Manual. A transfer TIMEOUT is a failure and is recorded as
                // one by SetDownloadError.
                Detail("event=download trigger=automatic outcome=cancelled reason=policy stage=transfer");
                SetStatus(new(UpdatePhase.Available, update, Detail: "Download paused.", LastCheckedUtc: _lastCheckedUtc));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or CryptographicException or OperationCanceledException)
            { SetDownloadError(update, ex); }
            finally { _operationGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Detail("event=download trigger=automatic outcome=cancelled reason=policy stage=gate");
            lock (_automaticCancellationLock)
                if (ReferenceEquals(_automaticDownloadCancellation, cancellation))
                    SetStatus(new(UpdatePhase.Available, update, Detail: "Download paused.", LastCheckedUtc: _lastCheckedUtc));
        }
        finally
        {
            UpdateInfo? restart = null;
            lock (_automaticCancellationLock)
            {
                if (ReferenceEquals(_automaticDownloadCancellation, cancellation))
                {
                    _automaticDownloadCancellation = null;
                    if (!_disposed && _automaticDownloadRestartRequested
                        && _settings.UpdatePolicy != UpdatePolicy.ManualInstall
                        && _status.Update?.SetupAssetUri is not null)
                        restart = _status.Update;
                    _automaticDownloadRestartRequested = false;
                }
            }
            cancellation.Dispose();
            if (restart is not null)
            {
                Detail($"event=download trigger=automatic action=restart to_version={restart.DisplayVersion}");
                _ = DownloadAutomaticAsync(restart);
            }
        }
    }

    private async Task DownloadCoreAsync(UpdateInfo update, string trigger, CancellationToken cancellationToken)
    {
        if (update.SetupAssetUri is null || update.AssetName is null)
            throw new InvalidOperationException("The selected release has no approved installer asset.");
        Directory.CreateDirectory(_stagingDirectory);
        var started = Stopwatch.StartNew();
        var finalPath = Path.Combine(_stagingDirectory, update.AssetName);
        var partPath = finalPath + ".part";
        var partialPath = Path.Combine(_stagingDirectory, "partial.json");
        DeleteOtherPartialPackages(update.AssetName);
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        var expectedDownloadSize = update.ExpectedSize ?? ReadMatchingPartialExpectedSize(partialPath, update.AssetName);
        if (expectedDownloadSize is { } expectedSize && existing > expectedSize)
        {
            Detail(FormattableString.Invariant(
                $"event=partial outcome=stale bytes_done={existing} bytes_total={expectedSize}"));
            File.Delete(partPath);
            existing = 0;
        }
        WriteMetadata(partialPath, "partial", new(update.DisplayVersion, update.AssetName, update.SetupAssetUri.AbsoluteUri,
            update.ReleasePageUri.AbsoluteUri, DateTimeOffset.UtcNow, existing, expectedDownloadSize,
            PackageSha256: null, DebugSha256: update.DebugSha256));
        // Reset here rather than in the funnel: this is where a transfer BEGINS, so a second
        // download in one session gets its own eleven buckets, and a resume starts at the bucket it
        // actually resumes from instead of back-filling the ones it never fetched.
        _progressBucket = -1;
        _progressRecords = 0;
        Detail(FormattableString.Invariant(
            $"event=download_start trigger={trigger} to_version={update.DisplayVersion} resume={(existing > 0 ? "true" : "false")} bytes_done={existing} bytes_total={BytesToken(expectedDownloadSize)}"));

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromMinutes(10));
        var downloadToken = timeoutCancellation.Token;
        if (expectedDownloadSize is { } completedSize && existing == completedSize)
        {
            Detail(FormattableString.Invariant(
                $"event=download outcome=noop reason=already_complete bytes_done={existing}"));
            SetStatus(new(UpdatePhase.Downloading, update, existing, completedSize, LastCheckedUtc: _lastCheckedUtc));
            await CompleteStagedDownloadAsync(update, partPath, finalPath, partialPath, existing, completedSize,
                downloadToken).ConfigureAwait(false);
            return;
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        var downloadUri = update.SetupAssetUri;
        HttpResponseMessage? response = null;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400) break;
            var next = response.Headers.Location is { } location ? new Uri(downloadUri, location) : null;
            response.Dispose();
            response = null;
            if (next is null || !GitHubUpdateClient.IsApprovedDownloadUri(next))
            {
                // Hop number only. Neither the rejected target nor the approved one is recorded.
                Detail(FormattableString.Invariant(
                    $"event=redirect outcome=failure reason={(next is null ? "no_location" : "endpoint_not_allowed")} hop={redirects + 1}"));
                throw new HttpRequestException("Update download redirected to an unapproved endpoint.");
            }
            Detail(FormattableString.Invariant($"event=redirect outcome=success hop={redirects + 1}"));
            downloadUri = next;
        }
        using (response)
        {
            if (response is null)
            {
                Detail("event=download outcome=failure reason=redirect_limit");
                throw new HttpRequestException("Update download exceeded the redirect limit.");
            }
            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                Detail(FormattableString.Invariant(
                    $"event=resume outcome=fallback reason=range_ignored bytes_discarded={existing}"));
                existing = 0;
            }
            response.EnsureSuccessStatusCode();
            if (existing > 0 && (response.StatusCode != HttpStatusCode.PartialContent
                || response.Content.Headers.ContentRange?.From != existing))
            {
                Detail(FormattableString.Invariant(
                    $"event=resume outcome=failure reason=range_invalid bytes_done={existing} status={(int)response.StatusCode}"));
                throw new HttpRequestException("The update server returned an invalid Range response.");
            }
            long? total = expectedDownloadSize
                ?? (response.Content.Headers.ContentLength is { } length ? existing + length : null);
            if (expectedDownloadSize is { } declaredSize
                && (response.Content.Headers.ContentLength is { } responseLength
                    && responseLength != declaredSize - existing
                    || response.StatusCode == HttpStatusCode.PartialContent
                    && response.Content.Headers.ContentRange?.Length != declaredSize))
            {
                Detail(FormattableString.Invariant(
                    $"event=download outcome=failure reason=size_mismatch bytes_done={existing} bytes_total={declaredSize}"));
                throw new IOException("The update response size did not match the release metadata.");
            }
            SetStatus(new(UpdatePhase.Downloading, update, existing, total, LastCheckedUtc: _lastCheckedUtc));
            long downloaded = existing;
            await using (var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false))
            await using (var output = new FileStream(partPath, existing == 0 ? FileMode.Create : FileMode.Append,
                FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    if (total is { } maximum && read > maximum - downloaded)
                        throw new IOException("The update response exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    downloaded += read;
                    // Nothing is recorded in this loop: the only record it can produce is the 10%
                    // bucket the status funnel below decides on.
                    WriteMetadata(partialPath, "partial", new(update.DisplayVersion, update.AssetName, update.SetupAssetUri.AbsoluteUri,
                        update.ReleasePageUri.AbsoluteUri, DateTimeOffset.UtcNow, downloaded, expectedDownloadSize ?? total,
                        PackageSha256: null, DebugSha256: update.DebugSha256));
                    SetStatus(new(UpdatePhase.Downloading, update, downloaded, total, LastCheckedUtc: _lastCheckedUtc));
                }
                await output.FlushAsync(downloadToken).ConfigureAwait(false);
            }
            if (total is not null && downloaded != total)
            {
                Detail(FormattableString.Invariant(
                    $"event=download outcome=failure reason=length_mismatch bytes_done={downloaded} bytes_total={BytesToken(total)}"));
                throw new IOException("Downloaded update length did not match the response.");
            }
            Detail(FormattableString.Invariant(
                $"event=download outcome=success trigger={trigger} bytes_done={downloaded} bytes_total={BytesToken(total)} elapsed_ms={started.ElapsedMilliseconds}"));
            await CompleteStagedDownloadAsync(update, partPath, finalPath, partialPath, downloaded, total,
                downloadToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteStagedDownloadAsync(UpdateInfo update, string partPath, string finalPath,
        string partialPath, long downloaded, long? total, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        if (!await UpdatePackageVerifier.VerifyAsync(partPath, update.DebugSha256, cancellationToken).ConfigureAwait(false))
        {
            // Error, not Detail: a package that reached this machine and cannot be trusted is worth a
            // line in a non-verbose app.log. The digest that failed to match is not recorded.
            Diagnostics.AppLog.Error("updates", FormattableString.Invariant(
                $"event=promote outcome=failure reason=verification to_version={update.DisplayVersion} bytes_done={downloaded}"));
            DeleteFailedPartial(partPath, partialPath);
            throw new CryptographicException("Update package did not pass integrity verification.");
        }
        var packageSha256 = await UpdatePackageVerifier.ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
        File.Move(partPath, finalPath, overwrite: true);
        File.Delete(partialPath);
        DeleteOtherStagedPackages(update.AssetName!);
        WriteMetadata(PendingPath, "pending", new(update.DisplayVersion, update.AssetName!, update.SetupAssetUri!.AbsoluteUri,
            update.ReleasePageUri.AbsoluteUri, DateTimeOffset.UtcNow, downloaded, update.ExpectedSize ?? total,
            packageSha256, update.DebugSha256));
        Detail(FormattableString.Invariant(
            $"event=promote outcome=success to_version={update.DisplayVersion} bytes={downloaded} elapsed_ms={started.ElapsedMilliseconds}"));
        var stagedUpdate = update with { PackageSha256 = packageSha256 };
        _stagedUpdate = stagedUpdate;
        // Info: with the installer staged on disk this is the updater event an incident file should
        // carry even if verbose was off for the rest of the run.
        Diagnostics.AppLog.Info("updates", FormattableString.Invariant(
            $"event=ready to_version={update.DisplayVersion} bytes={downloaded}"));
        SetStatus(new(UpdatePhase.ReadyToInstall, stagedUpdate, downloaded, total, LastCheckedUtc: _lastCheckedUtc));
    }

    private static void DeleteFailedPartial(string partPath, string partialPath)
    {
        try
        {
            if (File.Exists(partPath)) File.Delete(partPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
            Detail("event=discard_partial outcome=applied");
        }
        catch (IOException ex) { RecordSwallowed("discard_partial", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("discard_partial", ex); }
    }

    private static long? ReadMatchingPartialExpectedSize(string partialPath, string assetName)
    {
        try
        {
            if (!File.Exists(partialPath)) return null;
            var metadata = JsonSerializer.Deserialize<UpdateStageMetadata>(File.ReadAllText(partialPath));
            return metadata?.AssetName == assetName && metadata.ExpectedSize is > 0
                ? metadata.ExpectedSize : null;
        }
        catch (IOException ex) { RecordSwallowed("partial_metadata", ex); return null; }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("partial_metadata", ex); return null; }
        catch (JsonException ex) { RecordSwallowed("partial_metadata", ex); return null; }
    }

    private bool QueueStagedInstaller(bool silent)
    {
        if (!ValidateStagedInstaller())
        {
            Detail($"event=install_queue outcome=failure reason=revalidation silent={(silent ? "true" : "false")}");
            return false;
        }
        _queuedInstallSilent = silent;
        Detail($"event=install_queue outcome=success silent={(silent ? "true" : "false")} to_version={VersionToken(_stagedUpdate)}");
        SetStatus(_status with { Phase = UpdatePhase.Installing, Detail = null });
        return true;
    }

    private bool LaunchStagedInstaller(bool silent)
    {
        if (_installerLaunched || _stagedUpdate is not { AssetName: { } assetName } update)
        {
            Detail($"event=launch outcome=noop reason={(_installerLaunched ? "already_launched" : "no_staged_package")}");
            return false;
        }
        var path = Path.Combine(_stagingDirectory, assetName);
        try
        {
            if (!ValidateStagedInstaller())
            {
                Detail("event=launch outcome=failure reason=revalidation");
                return false;
            }
            SetStatus(new(UpdatePhase.Installing, update, LastCheckedUtc: _lastCheckedUtc));
            _installerLaunched = _launcher.Launch(path, silent);
            if (!_installerLaunched)
            {
                Detail($"event=launch outcome=failure reason=launcher_declined silent={(silent ? "true" : "false")}");
                SetStatus(new(UpdatePhase.ReadyToInstall, update, LastCheckedUtc: _lastCheckedUtc));
            }
            else
                // Info: the handoff is the last thing this process does for the update, and the
                // installer's own log is the only trace after it. The launch PATH is never recorded.
                Diagnostics.AppLog.Info("updates",
                    $"event=launch outcome=success silent={(silent ? "true" : "false")} to_version={update.DisplayVersion}");
            return _installerLaunched;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
            or CryptographicException)
        {
            // Type only: a launch failure's message names the installer path.
            Diagnostics.AppLog.Error("updates",
                $"event=launch outcome=failure to_version={update.DisplayVersion} error={ex.GetType().Name}");
            SetStatus(new(UpdatePhase.Error, update, Detail: "Installer could not be started.", LastCheckedUtc: _lastCheckedUtc));
            return false;
        }
    }

    private bool ValidateStagedInstaller()
    {
        if (_stagedUpdate is not { AssetName: { } assetName } update)
        {
            Detail("event=revalidate outcome=noop reason=no_staged_package");
            return false;
        }
        var path = Path.Combine(_stagingDirectory, assetName);
        try
        {
            // The two conditions are split only so the failure can name itself; the short-circuit
            // order is unchanged, so a missing/mismatched digest still skips the trust check.
            if (update.PackageSha256 is not { Length: > 0 } expectedHash
                || !string.Equals(UpdatePackageVerifier.ComputeSha256Async(path, CancellationToken.None).GetAwaiter().GetResult(),
                    expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.AppLog.Error("updates",
                    $"event=revalidate outcome=failure reason=hash to_version={update.DisplayVersion}");
                return DiscardStagedInstaller(update, assetName);
            }
            if (!UpdatePackageVerifier.VerifyAsync(path, update.DebugSha256, CancellationToken.None).GetAwaiter().GetResult())
            {
                Diagnostics.AppLog.Error("updates",
                    $"event=revalidate outcome=failure reason=verification to_version={update.DisplayVersion}");
                return DiscardStagedInstaller(update, assetName);
            }
            Detail($"event=revalidate outcome=success to_version={update.DisplayVersion}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Diagnostics.AppLog.Error("updates",
                $"event=revalidate outcome=failure to_version={update.DisplayVersion} error={ex.GetType().Name}");
            return DiscardStagedInstaller(update, assetName);
        }
    }

    /// <summary>The shared body of every revalidation refusal: forget the package, remove it, and
    /// leave the UI on the explicit error rather than a silent no-op.</summary>
    private bool DiscardStagedInstaller(UpdateInfo update, string assetName)
    {
        _stagedUpdate = null;
        DeletePendingAndInstaller(assetName);
        SetStatus(new(UpdatePhase.Error, update,
            Detail: "The staged installer failed revalidation and was not started.", LastCheckedUtc: _lastCheckedUtc));
        return false;
    }

    private string PendingPath => Path.Combine(_stagingDirectory, "pending.json");

    private void DeleteObsoleteCheckStamp()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "update-check.txt");
            if (!File.Exists(path)) return;
            File.Delete(path);
            Detail("event=stamp outcome=applied");
        }
        catch (IOException ex) { RecordSwallowed("stamp", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("stamp", ex); }
    }

    private void RestorePendingUpdate()
    {
        try
        {
            if (!File.Exists(PendingPath))
            {
                Detail("event=restore outcome=noop reason=absent");
                return;
            }
            var metadata = JsonSerializer.Deserialize<UpdateStageMetadata>(File.ReadAllText(PendingPath));
            if (metadata is null || !Version.TryParse(metadata.Version, out var version) || version <= GitHubUpdateClient.CurrentVersion
                || metadata.AssetName != $"LightWeaver-{version.ToString(3)}-setup.exe"
                || !File.Exists(Path.Combine(_stagingDirectory, metadata.AssetName)))
            { Detail("event=restore outcome=failure reason=stale"); DeletePendingAndInstaller(metadata?.AssetName); return; }
            if (!Uri.TryCreate(metadata.AssetUrl, UriKind.Absolute, out var asset)
                || !Uri.TryCreate(metadata.ReleaseUrl, UriKind.Absolute, out var page))
            { Detail("event=restore outcome=failure reason=uri_shape"); DeletePendingAndInstaller(metadata.AssetName); return; }
            if (!GitHubUpdateClient.IsApprovedDownloadUri(asset)
                || !GitHubUpdateClient.IsApprovedDownloadUri(page)
                || metadata.PackageSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            { Detail("event=restore outcome=failure reason=untrusted_metadata"); DeletePendingAndInstaller(metadata.AssetName); return; }
            var installerPath = Path.Combine(_stagingDirectory, metadata.AssetName);
            // Split from the trust check below only so each refusal can name itself; the digest
            // itself is never recorded, only whether it matched.
            if (!string.Equals(UpdatePackageVerifier.ComputeSha256Async(installerPath, CancellationToken.None)
                    .GetAwaiter().GetResult(), metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
            { Detail("event=restore outcome=failure reason=hash_mismatch"); DeletePendingAndInstaller(metadata.AssetName); return; }
            if (!UpdatePackageVerifier.VerifyAsync(installerPath, metadata.DebugSha256, CancellationToken.None)
                    .GetAwaiter().GetResult())
            { Detail("event=restore outcome=failure reason=verification"); DeletePendingAndInstaller(metadata.AssetName); return; }
            var stagedUpdate = new UpdateInfo(version, asset, page, metadata.AssetName, metadata.ExpectedSize,
                metadata.PackageSha256, metadata.DebugSha256);
            _stagedUpdate = stagedUpdate;
            Detail(FormattableString.Invariant(
                $"event=restore outcome=success to_version={version.ToString(3)} bytes_total={BytesToken(metadata.ExpectedSize)}"));
            SetStatus(new(UpdatePhase.ReadyToInstall,
                stagedUpdate,
                metadata.BytesDownloaded, metadata.ExpectedSize, LastCheckedUtc: _lastCheckedUtc));
        }
        catch (IOException ex) { RecordSwallowed("restore", ex); DeletePendingAndInstaller(null); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("restore", ex); }
        catch (JsonException ex) { RecordSwallowed("restore", ex); DeletePendingAndInstaller(null); }
    }

    private void RetireOlderStagedUpdate(Version availableVersion)
    {
        if (_stagedUpdate is not { } staged || staged.Version >= availableVersion) return;
        Detail(FormattableString.Invariant(
            $"event=retire outcome=applied staged_version={staged.Version.ToString(3)} to_version={availableVersion.ToString(3)}"));
        _stagedUpdate = null;
        DeletePendingAndInstaller(staged.AssetName);
    }

    private void CleanupStaleStaging()
    {
        // Counts, not names: one record for the whole sweep, however many files it removed.
        var partsDeleted = 0;
        var installersDeleted = 0;
        var metadataDeleted = false;
        try
        {
            if (!Directory.Exists(_stagingDirectory))
            {
                Detail("event=cleanup outcome=noop reason=absent");
                return;
            }
            var pendingAssetName = ReadPendingAssetNameForCleanup();
            var partialPath = Path.Combine(_stagingDirectory, "partial.json");
            var partialAssetName = ReadPartialAssetNameForCleanup(partialPath);
            foreach (var part in Directory.EnumerateFiles(_stagingDirectory, "*.part"))
            {
                var name = Path.GetFileName(part);
                if (!TryParseAssetVersion(name, ".part", out var version)
                    || version <= GitHubUpdateClient.CurrentVersion
                    || DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > StalePartAge
                    || !string.Equals(name, partialAssetName + ".part", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    partsDeleted++;
                }
            }
            foreach (var installer in Directory.EnumerateFiles(_stagingDirectory, "LightWeaver-*-setup.exe"))
            {
                var name = Path.GetFileName(installer);
                if (!TryParseAssetVersion(name, string.Empty, out var version)
                    || version <= GitHubUpdateClient.CurrentVersion
                    || !string.Equals(name, pendingAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(installer);
                    installersDeleted++;
                }
            }
            if (File.Exists(partialPath)
                && (partialAssetName is null
                    || !File.Exists(Path.Combine(_stagingDirectory, partialAssetName + ".part"))))
            {
                File.Delete(partialPath);
                metadataDeleted = true;
            }
            Detail(FormattableString.Invariant(
                $"event=cleanup outcome={(partsDeleted + installersDeleted > 0 || metadataDeleted ? "applied" : "noop")} parts_deleted={partsDeleted} installers_deleted={installersDeleted} partial_metadata_deleted={(metadataDeleted ? "true" : "false")}"));
        }
        catch (IOException ex) { RecordSwallowed("cleanup", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("cleanup", ex); }
    }

    private string? ReadPartialAssetNameForCleanup(string partialPath)
    {
        try
        {
            if (!File.Exists(partialPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(partialPath) > StalePartAge)
                return null;
            var metadata = JsonSerializer.Deserialize<UpdateStageMetadata>(File.ReadAllText(partialPath));
            if (metadata is null || !Version.TryParse(metadata.Version, out var version)
                || version <= GitHubUpdateClient.CurrentVersion
                || metadata.AssetName != $"LightWeaver-{version.ToString(3)}-setup.exe")
                return null;
            return metadata.AssetName;
        }
        catch (IOException ex) { RecordSwallowed("partial_metadata", ex); return null; }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("partial_metadata", ex); return null; }
        catch (JsonException ex) { RecordSwallowed("partial_metadata", ex); return null; }
    }

    private static bool TryParseAssetVersion(string name, string trailingSuffix, out Version version)
    {
        version = new Version(0, 0, 0);
        const string prefix = "LightWeaver-";
        var suffix = "-setup.exe" + trailingSuffix;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || name.Length <= prefix.Length + suffix.Length)
            return false;
        return Version.TryParse(name[prefix.Length..^suffix.Length], out version!);
    }

    private string? ReadPendingAssetNameForCleanup()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            var metadata = JsonSerializer.Deserialize<UpdateStageMetadata>(File.ReadAllText(PendingPath));
            return metadata?.AssetName is { } name && Path.GetFileName(name) == name ? name : null;
        }
        catch (IOException ex) { RecordSwallowed("pending_metadata", ex); return null; }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("pending_metadata", ex); return null; }
        catch (JsonException ex) { RecordSwallowed("pending_metadata", ex); return null; }
    }

    private void DeletePendingAndInstaller(string? assetName)
    {
        var metadataDeleted = false;
        var installerDeleted = false;
        try
        {
            if (File.Exists(PendingPath))
            {
                File.Delete(PendingPath);
                metadataDeleted = true;
            }
            if (assetName is { Length: > 0 })
            {
                var installer = Path.Combine(_stagingDirectory, assetName);
                if (File.Exists(installer))
                {
                    File.Delete(installer);
                    installerDeleted = true;
                }
            }
            if (metadataDeleted || installerDeleted)
                Detail($"event=discard_pending outcome=applied metadata={(metadataDeleted ? "true" : "false")} installer={(installerDeleted ? "true" : "false")}");
        }
        catch (IOException ex) { RecordSwallowed("discard_pending", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("discard_pending", ex); }
    }

    private void DeleteOtherStagedPackages(string retainedAssetName)
    {
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_stagingDirectory, "LightWeaver-*-setup.exe*"))
            {
                var name = Path.GetFileName(path);
                if (!string.Equals(name, retainedAssetName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, retainedAssetName + ".part", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            if (deleted > 0)
                Detail(FormattableString.Invariant($"event=cleanup_packages outcome=applied count={deleted}"));
        }
        catch (IOException ex) { RecordSwallowed("cleanup_packages", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("cleanup_packages", ex); }
    }

    private void DeleteOtherPartialPackages(string retainedAssetName)
    {
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_stagingDirectory, "LightWeaver-*-setup.exe.part"))
                if (!string.Equals(Path.GetFileName(path), retainedAssetName + ".part", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    deleted++;
                }
            if (deleted > 0)
                Detail(FormattableString.Invariant($"event=cleanup_partials outcome=applied count={deleted}"));
        }
        catch (IOException ex) { RecordSwallowed("cleanup_partials", ex); }
        catch (UnauthorizedAccessException ex) { RecordSwallowed("cleanup_partials", ex); }
    }

    private void SetDownloadError(UpdateInfo update, Exception exception)
    {
        var (detail, reason) = exception switch
        {
            CryptographicException => ("The downloaded installer failed package verification.", "verification"),
            OperationCanceledException => ("The update download timed out or was cancelled.", "timeout"),
            HttpRequestException => ("The update installer could not be downloaded.", "transport"),
            UnauthorizedAccessException => ("LightWeaver could not write to its update staging folder.", "access"),
            IOException => ("The update installer could not be saved.", "io"),
            _ => ("The update download failed.", "unknown"),
        };
        // failure, never cancelled: the caller-owned cancellation legs are taken by their own
        // `when (cancellation.IsCancellationRequested)` filters, so an OperationCanceledException
        // that reaches here is the ten-minute transfer timeout. Type only — an IO or HTTP message
        // here names the staging path or the asset URI.
        Detail($"event=download outcome=failure reason={reason} to_version={update.DisplayVersion} error={exception.GetType().Name}");
        SetStatus(new(UpdatePhase.Error, update, Detail: detail, LastCheckedUtc: _lastCheckedUtc));
    }

    private void SetCheckError(UpdateStatus priorStatus, string detail)
    {
        if (priorStatus.Phase == UpdatePhase.ReadyToInstall)
        {
            // A failed refresh must not demote a package that is already staged and verified.
            Detail($"event=retain_staged outcome=applied reason=check_failed to_version={VersionToken(priorStatus.Update)}");
            SetStatus(priorStatus with { Detail = detail });
            return;
        }
        SetStatus(new(UpdatePhase.Error, priorStatus.Update, Detail: detail, LastCheckedUtc: _lastCheckedUtc));
    }

    private static void WriteMetadata(string path, string kind, UpdateStageMetadata metadata)
    {
        // Antivirus, an indexer, or another app process can briefly hold the destination open on
        // Windows. MoveFileEx then reports ACCESS_DENIED even though the directory is writable.
        // Metadata is replaced for every download chunk, so treating that harmless sharing window
        // as fatal makes an otherwise healthy resumable transfer stop.
        //
        // A unique temporary name also keeps two LightWeaver processes from colliding before the
        // replace. The completed JSON remains atomic: readers see the prior file or the new file,
        // never a partially-written one. The bounded sub-second retry absorbs only transient
        // sharing failures; persistent permissions/storage errors still reach the caller.
        var temp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(metadata);
        var failures = 0;
        string? lastError = null;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temp, json);
#if DEBUG
                    if (Environment.GetEnvironmentVariable(
                            "LIGHTWEAVER_UPDATE_METADATA_REPLACE_FAIL_ONCE_RECORD") is { Length: > 0 } record
                        && Interlocked.CompareExchange(ref _metadataReplaceFailureConsumed, 1, 0) == 0)
                    {
                        File.WriteAllText(record, "simulated transient metadata replacement denial");
                        throw new UnauthorizedAccessException(
                            "Simulated transient update-metadata replacement denial.");
                    }
#endif
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < MetadataWriteAttempts
                                           && ex is IOException or UnauthorizedAccessException)
                {
                    // Counted here, recorded once below. Recording per attempt would put up to
                    // MetadataWriteAttempts - 1 lines on a path the byte loop reaches every 128 KiB.
                    failures++;
                    lastError = ex.GetType().Name;
                    Thread.Sleep(20 << (attempt - 1));
                }
            }
        }
        finally
        {
            // The only record on a path the byte loop can reach, and it fires only when a
            // replacement actually failed: exactly one per call however many attempts it took, where
            // the first-attempt success writes nothing at all. Coalesced rather than per attempt
            // because metadata is replaced on every 128 KiB read, so an antivirus that
            // fails-then-succeeds on each replace would otherwise multiply its burst by five. In the
            // `finally` so the give-up path reports its attempt count too, instead of leaving the
            // caller's own failure record as the only trace. `kind`, not the path — see the class
            // privacy note.
            if (failures > 0)
                Detail(FormattableString.Invariant(
                    $"event=metadata outcome=failure kind={kind} attempts={failures} error={lastError}"));
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException ex) { RecordSwallowed("metadata_temp", ex); }
            catch (UnauthorizedAccessException ex) { RecordSwallowed("metadata_temp", ex); }
        }
    }

    // ---- Structural verbose records -----------------------------------------------------
    //
    // What is structural here and what is not. Versions are public release facts and are logged
    // (`from_version`, `to_version`, `staged_version`); byte counts, phases, outcomes, retry
    // attempts and deletion counts are bounded. Deliberately absent, and none of it is a judgement
    // call: the release FEED and any part of it (the tag is attacker-supplied text), any URI, host
    // or endpoint, every SHA-256 digest — the fixture hash, the package hash and the Authenticode
    // thumbprint alike — the staging/partial/pending/launch PATHS, and the signer's identity. Where
    // one of those decides something, the DECISION is recorded and the value is not: `asset=present`,
    // `endpoint=production`, `mode=authenticode`, `reason=hash_mismatch`, `kind=pending`.
    //
    // Caught exceptions carry `error=<TypeName>` only. Every failure on this path has a message that
    // names either the update URI or a file under the staging directory, so neither the message nor
    // the exception object is ever handed to AppLog.
    //
    // Volume. These call sites are async continuations, so file IO is acceptable — but per-tick IO is
    // not, and the status funnel below IS the per-128 KB progress callback. That is why exactly two
    // things are coalesced there: the phase record fires only on a real phase change, and progress is
    // bucketed to 10% steps with a hard cap of ProgressRecordLimit per transfer.

    private static void Detail(string record) => Diagnostics.AppLog.Detail("updates", record);

    /// <summary>A failed check is `failure`, never `cancelled`: nothing here owns a cancellation
    /// token — <see cref="CheckAsync"/> passes <see cref="CancellationToken.None"/> — so a
    /// <see cref="TaskCanceledException"/> is HttpClient's own timeout. Verbose-only on purpose: an
    /// offline machine fails this check on every launch, which is ordinary rather than an
    /// incident.</summary>
    private static void RecordCheckFailure(string trigger, string reason, Exception exception, Stopwatch started) =>
        Detail(FormattableString.Invariant(
            $"event=check trigger={trigger} outcome=failure reason={reason} error={exception.GetType().Name} elapsed_ms={started.ElapsedMilliseconds}"));

    /// <summary>The best-effort staging cleanups used to swallow these with an empty catch, so a
    /// staging folder the app could not tidy left no trace whatsoever.</summary>
    private static void RecordSwallowed(string eventName, Exception exception) =>
        Detail($"event={eventName} outcome=failure error={exception.GetType().Name}");

    /// <summary>Mirrors the refusal condition's short-circuit order, so the reason is the one that
    /// actually stopped the download.</summary>
    private string DownloadRefusalReason(UpdateInfo? update) => update is null ? "no_update"
        : update.SetupAssetUri is null ? "no_asset" : _disposed ? "disposed" : "busy";

    private static string BytesToken(long? value) =>
        value is { } bytes ? FormattableString.Invariant($"{bytes}") : "unknown";

    private static string VersionToken(UpdateInfo? update) => update?.DisplayVersion ?? "none";

    /// <summary>Explicit rather than <c>ToString().ToLowerInvariant()</c>: the field vocabulary is
    /// lower_snake_case and that transform would emit <c>readytoinstall</c>/<c>uptodate</c>.</summary>
    private static string PhaseToken(UpdatePhase phase) => phase switch
    {
        UpdatePhase.Idle => "idle",
        UpdatePhase.Checking => "checking",
        UpdatePhase.UpToDate => "up_to_date",
        UpdatePhase.Available => "available",
        UpdatePhase.Downloading => "downloading",
        UpdatePhase.ReadyToInstall => "ready_to_install",
        UpdatePhase.Installing => "installing",
        UpdatePhase.Error => "error",
        _ => "unknown",
    };

    private static string PolicyToken(UpdatePolicy policy) => policy switch
    {
        UpdatePolicy.ManualInstall => "manual",
        UpdatePolicy.AutoDownload => "auto_download",
        UpdatePolicy.AutoDownloadAndInstall => "auto_download_install",
        _ => "unknown",
    };

    /// <summary>The single phase-transition funnel: every state change in this file goes through
    /// here, which is what makes one record per transition possible. It is also the progress
    /// callback of <see cref="DownloadCoreAsync"/>'s byte loop, so see
    /// <see cref="RecordStatus"/> for what it does NOT record.</summary>
    private void SetStatus(UpdateStatus value)
    {
        var previous = _status;
        _status = value;
        RecordStatus(previous, value);
        StatusChanged?.Invoke(value);
    }

    /// <summary>Two coalesced records:
    /// <list type="bullet">
    /// <item>the phase transition, only when the phase actually CHANGED. <see cref="OnPolicyChanged"/>
    ///   re-raises the current status unchanged and the byte loop re-raises <c>Downloading</c> per
    ///   128 KB read, so an unconditional record here is hundreds of identical lines per
    ///   transfer.</item>
    /// <item>download progress, in 10% buckets, recorded only when the bucket ADVANCES and never more
    ///   than <see cref="ProgressRecordLimit"/> times per transfer (the bucket state is reset where a
    ///   transfer begins, in <see cref="DownloadCoreAsync"/>). A resumed transfer therefore starts at
    ///   the bucket it resumes from rather than back-filling the ones it never fetched, and a transfer
    ///   whose total size is unknown gets no bucket records at all rather than a division by
    ///   zero.</item>
    /// </list></summary>
    private void RecordStatus(UpdateStatus previous, UpdateStatus value)
    {
        // Ahead of the formatting and the IO: the byte loop reaches this once per 128 KB.
        if (!Diagnostics.AppLog.Verbose)
            return;
        if (previous.Phase != value.Phase)
            Detail($"event=phase from={PhaseToken(previous.Phase)} to={PhaseToken(value.Phase)} version={VersionToken(value.Update)}");
        if (value.Phase != UpdatePhase.Downloading || value.TotalBytes is not { } total || total <= 0
            || _progressRecords >= ProgressRecordLimit)
            return;
        var bucket = (int)Math.Clamp(value.BytesDownloaded * 10 / total, 0, 10);
        if (bucket <= _progressBucket)
            return;
        _progressBucket = bucket;
        _progressRecords++;
        Detail(FormattableString.Invariant(
            $"event=download_progress percent={bucket * 10} bytes_done={value.BytesDownloaded} bytes_total={total}"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        lock (_automaticCancellationLock) _automaticDownloadCancellation?.Cancel();
        // The semaphore remains alive until outstanding async operations have released it.
    }
}
