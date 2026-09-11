using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;

namespace LightWeaver.Downloads;

/// <summary>
/// The download engine (Phase 7 M20). In-memory state is the live source of truth
/// (progress ticks never hit disk); only status transitions persist to the
/// <see cref="DownloadStore"/> index — Velly's exact strategy. Resumability rides
/// HTTP Range: an existing partial file continues from its byte length (206 +
/// Content-Range), so pause/cancel/app-kill never lose progress. At most
/// <c>maxParallel()</c> downloads run at once; the rest wait as Queued.
/// </summary>
public sealed class DownloadManager
{
    /// <summary>Set once by MainWindow; the card badge converter reads it.</summary>
    public static DownloadManager? Instance { get; private set; }

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // big files; cancellation governs lifetime
    };

    /// <summary>How long a single read may make no progress before the transfer is failed (B18).
    /// Generous on purpose — a slow server or a stalled transcode is not a dead connection — but
    /// finite, which the infinite client timeout above left it not being.</summary>
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(90);

    private readonly DownloadStore _store = new();
    private readonly ConcurrentDictionary<Guid, DownloadItem> _live = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly Func<int> _maxParallel;
    private readonly Func<DownloadItem, string?> _urlBuilder;
    private readonly Func<DownloadItem, string?> _authHeader;
    private readonly object _pumpLock = new();

    /// <summary>Any state or progress change; marshal to the dispatcher before touching UI.</summary>
    public event Action? DownloadsChanged;
    /// <summary>A download reached Completed (badge broadcast / toast).</summary>
    public event Action<DownloadItem>? Completed;

    // Test hooks (verify skill): throttle makes pause/concurrency observable on a fast
    // LAN; the log file gives the suite assertable transition/resume evidence.
    private static readonly int ThrottleKbps =
        int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_DOWNLOAD_THROTTLE_KBPS"),
            out var v) && v > 0 ? v : 0;
    private static readonly string? LogPath =
        Environment.GetEnvironmentVariable("LIGHTWEAVER_DOWNLOAD_LOG");

    /// <summary>urlBuilder/authHeader resolve per item (against its recorded server's
    /// warm session since the multi-account rework); null = that server has no live
    /// session right now, and the download pauses cleanly.</summary>
    public DownloadManager(Func<int> maxParallel, Func<DownloadItem, string?> urlBuilder,
        Func<DownloadItem, string?> authHeader)
    {
        _maxParallel = maxParallel;
        _urlBuilder = urlBuilder;
        _authHeader = authHeader;
        foreach (var item in _store.LoadAll())
            _live[item.ItemId] = item;
        Instance = this;
    }

    public IReadOnlyList<DownloadItem> Snapshot()
        => _live.Values.OrderBy(i => i.AddedTimestampMs).ToList();

    public bool IsTracked(Guid itemId) => _live.ContainsKey(itemId);

    /// <summary>The completed download for an item, with the Velly self-heal: a
    /// completed entry whose file vanished is dropped so playback falls back to the
    /// server instead of erroring on a missing path.</summary>
    public DownloadItem? GetCompletedDownload(Guid itemId)
    {
        if (!_live.TryGetValue(itemId, out var item) || item.Status != DownloadStatus.Completed)
            return null;
        if (!File.Exists(item.FilePath))
        {
            _live.TryRemove(itemId, out _);
            _store.Remove(itemId);
            RaiseChanged();
            return null;
        }
        return item;
    }

    public bool IsDownloaded(Guid itemId) => GetCompletedDownload(itemId) is not null;

    public void Enqueue(DownloadItem item)
    {
        if (_active.ContainsKey(item.ItemId))
            return;   // already downloading
        Log($"enqueue {item.ItemId:N} \"{item.Title}\" ({item.Resolution})");
        var queued = item with { Status = DownloadStatus.Queued, ErrorMessage = null };
        Detail("enqueue", queued);
        Persist(queued);
        Pump();
    }

    public void Pause(Guid itemId)
    {
        if (_active.TryRemove(itemId, out var cts))
            cts.Cancel();
        if (_live.TryGetValue(itemId, out var item)
            && item.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
        {
            Log($"pause {itemId:N}");
            var paused = item with { Status = DownloadStatus.Paused };
            Detail("pause", paused);
            Persist(paused);
        }
        Pump();
    }

    public void Resume(Guid itemId)
    {
        if (_live.TryGetValue(itemId, out var item)
            && item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
        {
            Log($"resume {itemId:N}");
            var requeued = item with { Status = DownloadStatus.Queued, ErrorMessage = null };
            Detail("resume", requeued);
            Persist(requeued);
            Pump();
        }
    }

    /// <summary>Cancel/delete: stop the job, drop the entry, remove the file. Runs under
    /// the pump lock — a cancelled task's finally-Pump must not start new work while a
    /// removal is mid-flight (it would orphan a running job outside _active).</summary>
    public void Remove(Guid itemId)
    {
        lock (_pumpLock)
        {
            if (_active.TryRemove(itemId, out var cts))
                cts.Cancel();
            if (_live.TryRemove(itemId, out var item))
            {
                Log($"remove {itemId:N}");
                Detail("remove", item);
                TryDeleteFile(item.FilePath);
                _store.Remove(itemId);
            }
        }
        RaiseChanged();
        Pump();
    }

    public void PauseAll()
    {
        var ids = _live.Values
            .Where(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
            .Select(i => i.ItemId).ToList();
        BatchDetail("pause_all", ids.Count);
        foreach (var id in ids)
            Pause(id);
    }

    /// <summary>Drop Completed entries from the list, keeping the files on disk.</summary>
    public void ClearCompleted()
    {
        var completed = _live.Values.Where(i => i.Status == DownloadStatus.Completed).ToList();
        BatchDetail("clear_completed", completed.Count);
        foreach (var item in completed)
        {
            _live.TryRemove(item.ItemId, out _);
            _store.Remove(item.ItemId);
        }
        RaiseChanged();
    }

    public void DeleteAll()
    {
        // The whole teardown holds the pump lock: without it, a just-cancelled task's
        // finally-Pump can start a Queued item mid-iteration — an orphaned download
        // outside _active whose open handle makes its file undeletable (found live).
        lock (_pumpLock)
        {
            BatchDetail("delete_all", _live.Count, _active.Count);
            foreach (var cts in _active.Values)
                cts.Cancel();
            _active.Clear();
            foreach (var item in _live.Values)
                TryDeleteFile(item.FilePath);
            _live.Clear();
            _store.Save([]);
        }
        RaiseChanged();
    }

    /// <summary>Restart recovery: anything the last session left Downloading/Queued
    /// re-queues and continues from its partial file's byte offset.</summary>
    public void ResumeInterrupted()
    {
        var interrupted = _live.Values
            .Where(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
            .ToList();
        if (interrupted.Count == 0)
            return;
        Log($"resume-interrupted count={interrupted.Count}");
        BatchDetail("resume_interrupted", interrupted.Count);
        foreach (var item in interrupted)
            Persist(item with { Status = DownloadStatus.Queued });
        Pump();
    }

    /// <summary>Starts Queued items while slots are free (at most maxParallel active).</summary>
    private void Pump()
    {
        lock (_pumpLock)
        {
            while (_active.Count < Math.Max(1, _maxParallel()))
            {
                var next = _live.Values
                    .Where(i => i.Status == DownloadStatus.Queued && !_active.ContainsKey(i.ItemId))
                    .OrderBy(i => i.AddedTimestampMs)
                    .FirstOrDefault();
                if (next is null)
                    return;
                var cts = new CancellationTokenSource();
                _active[next.ItemId] = cts;
                _ = Task.Run(() => RunDownloadAsync(next.ItemId, cts.Token));
            }
        }
    }

    private async Task RunDownloadAsync(Guid itemId, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!_live.TryGetValue(itemId, out var item))
                return;
            // Pause can land between Pump adding us to _active and this task getting scheduled.
            // Persisting Downloading unconditionally overwrote that Paused verdict, and since the
            // very next await throws OperationCanceledException (swallowed by design) the item was
            // left Downloading, outside _active, and invisible to Pump — which only starts Queued.
            // Terminal stuck state, recoverable only by pausing and resuming again (B16).
            if (ct.IsCancellationRequested)
                return;
            // Held rather than discarded: the lifecycle records below must report the state this
            // task has actually moved the item to. Logging `item` there said status=Queued on
            // event=start, i.e. the state it had just left.
            var running = item with { Status = DownloadStatus.Downloading, ErrorMessage = null };
            Persist(running);

            var url = _urlBuilder(item);
            var auth = _authHeader(item);
            if (url is null || auth is null)
                throw new InvalidOperationException("No signed-in session for this server.");

            Directory.CreateDirectory(Path.GetDirectoryName(item.FilePath)!);
            var startByte = File.Exists(item.FilePath) ? new FileInfo(item.FilePath).Length : 0L;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", auth);
            if (startByte > 0)
                request.Headers.TryAddWithoutValidation("Range", $"bytes={startByte}-");
            Log($"start {itemId:N} from byte {startByte}");
            Detail("start", running, FormattableString.Invariant($"resume_from={startByte}"));

            using var response = await Http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

            var partial = response.StatusCode == HttpStatusCode.PartialContent;
            var contentLength = response.Content.Headers.ContentLength ?? -1L;
            var totalBytes = partial
                ? response.Content.Headers.ContentRange?.Length
                  ?? (contentLength > 0 ? startByte + contentLength : -1L)
                : contentLength;
            Log($"http {(int)response.StatusCode} {itemId:N} total={totalBytes}");
            // http_status, NOT status: every record already carries the item's own `status=`, and
            // two fields with the same name in one key=value line is a parsing trap - a reader
            // taking the first match would report the HTTP code as the download state, or vice
            // versa. Same reasoning for response_bytes_total against the item's bytes_total.
            Detail("http", running, FormattableString.Invariant(
                $"http_status={(int)response.StatusCode} partial={(partial ? "true" : "false")} response_bytes_total={totalBytes}"));

            // 206 appends to the partial file; a 200 (server ignored the Range, or a
            // fresh start) rewrites from scratch.
            await using (var output = new FileStream(item.FilePath,
                partial && startByte > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read))
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                var totalRead = partial ? startByte : 0L;
                var lastNotify = totalRead;
                // NOTHING inside the loop below logs. The 512 KB progress notification is the
                // narrowest granularity anything here runs at, and a record per notification is
                // ~2000 lines for a 1 GB file - it would blow the rolling file's one retained
                // generation on a single download. Byte progress is UI state, not diagnostics; the
                // lifecycle records around the loop are what a failure needs.
                while (true)
                {
                    // Per-read stall timeout (B18). The client's overall Timeout is deliberately
                    // Infinite (big files, cancellation governs lifetime), but that left no bound
                    // on a single read either: a connection that goes silent without a FIN — AP
                    // roam, server container restart, the SYN-loss network — parked here forever
                    // with no progress, no failure and no retry, showing whatever percentage it
                    // had reached. A linked CTS bounds the read without touching cancellation
                    // semantics: a real pause/cancel still surfaces as OperationCanceledException
                    // and is swallowed as before, while a stall becomes a Failed item the existing
                    // Range resume can pick up.
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        readCts.CancelAfter(ReadStallTimeout);
                        try
                        {
                            read = await input.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException(FormattableString.Invariant(
                                $"connection stalled: no data for {ReadStallTimeout.TotalSeconds:0}s at byte {totalRead}"));
                        }
                    }
                    if (read <= 0)
                        break;
                    ct.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;
                    if (totalRead - lastNotify >= 512 * 1024)
                    {
                        UpdateProgressInMemory(itemId, totalRead, totalBytes);
                        lastNotify = totalRead;
                    }
                    if (ThrottleKbps > 0)
                        await Task.Delay(Math.Max(1, read / ThrottleKbps), ct).ConfigureAwait(false);
                }

                // A clean end-of-stream is not the same as a complete file. With chunked or
                // connection-close framing a truncated response can end the read loop without
                // throwing, and marking that Completed was actively harmful: it also rewrote
                // TotalBytes = totalRead, making the record self-consistent so nothing downstream
                // could notice, and PlayItem prefers a completed download — so the item played a
                // corrupt local file INSTEAD of falling back to the server (B15). The partial file
                // is deliberately left in place: the existing Range resume is the right recovery.
                if (totalBytes > 0 && totalRead < totalBytes)
                    throw new IOException(FormattableString.Invariant(
                        $"truncated transfer: got {totalRead} of {totalBytes} bytes"));

                Log($"complete {itemId:N} bytes={totalRead}");
                // Info, not Detail: a completed download is sparse, is worth having in the incident
                // ring, and is the one download event a non-verbose app.log should be able to show.
                Diagnostics.AppLog.Info("downloads", FormattableString.Invariant(
                    $"event=complete item={itemId:N} bytes_done={totalRead} elapsed_ms={started.ElapsedMilliseconds}"));
                if (_live.TryGetValue(itemId, out var done))
                {
                    var completed = done with
                    {
                        Status = DownloadStatus.Completed,
                        Progress = 1,
                        BytesDownloaded = totalRead,
                        TotalBytes = totalRead,
                    };
                    Persist(completed);
                    Completed?.Invoke(completed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // pause/cancel decided the item's fate; the partial file stays for Range resume
            if (_live.TryGetValue(itemId, out var cancelled))
                Detail("cancelled", cancelled, FormattableString.Invariant(
                    $"elapsed_ms={started.ElapsedMilliseconds}"));
        }
        catch (Exception ex)
        {
            Log($"failed {itemId:N}: {ex.Message}");
            // The env-var log above is test instrumentation and off in a shipped build; the
            // failure still reaches the user only as a red row with a one-line message.
            //
            // The EXCEPTION OBJECT is deliberately not passed to AppLog (M3). An IO failure on this
            // path names the file it could not write, and a download's filename is built from the
            // media title - so the message and its stack are exactly the leak the structural
            // contract exists to prevent. Redaction masks the user directory, not the title. The
            // type, the byte count and the state are what a failed transfer is diagnosed from.
            if (_live.TryGetValue(itemId, out var item))
            {
                Diagnostics.AppLog.Error("downloads", FormattableString.Invariant(
                    $"event=failed item={itemId:N} bytes_done={item.BytesDownloaded} bytes_total={item.TotalBytes} elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}"));
                Persist(item with { Status = DownloadStatus.Failed, ErrorMessage = ex.Message });
            }
            else
            {
                Diagnostics.AppLog.Error("downloads", FormattableString.Invariant(
                    $"event=failed item={itemId:N} outcome=untracked elapsed_ms={started.ElapsedMilliseconds} error={ex.GetType().Name}"));
            }
        }
        finally
        {
            _active.TryRemove(itemId, out _);
            Pump();
        }
    }

    private void UpdateProgressInMemory(Guid itemId, long bytesDownloaded, long totalBytes)
    {
        if (!_live.TryGetValue(itemId, out var item))
            return;
        _live[itemId] = item with
        {
            Progress = totalBytes > 0 ? (double)bytesDownloaded / totalBytes : 0,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes,
        };
        RaiseChanged();
    }

    /// <summary>Status transition: in-memory + disk index (progress ticks skip the disk).</summary>
    private void Persist(DownloadItem item)
    {
        _live[item.ItemId] = item;
        _store.AddOrUpdate(item);
        RaiseChanged();
    }

    private void RaiseChanged() => DownloadsChanged?.Invoke();

    private static void TryDeleteFile(string path)
    {
        // A cancel races the download task's open FileStream: cancellation is
        // observed at the next read/write, so the handle may close a beat later.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// One lifecycle record per state transition of one download.
    ///
    /// <para>What is structural here and what is not: the item GUID, the status, the byte counts and
    /// whether the download is transcoded are all bounded facts. <c>Title</c>, <c>Subtitle</c>,
    /// <c>PosterUrl</c>, <c>ServerUrl</c>, <c>FilePath</c> and <c>Resolution</c> are not, and none of
    /// them is logged — <c>Resolution</c> least of all, because its default comes from
    /// <c>AppSettings.DefaultDownloadResolution</c>, a free-form deserialized string. What it is
    /// worth knowing is captured as <c>transcoded=true|false</c>, which <c>MaxWidth</c> decides.</para>
    /// </summary>
    private static void Detail(string eventName, DownloadItem item, string? extra = null)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant(
            $"event={eventName} item={item.ItemId:N} status={StatusToken(item.Status)} transcoded={(item.IsTranscoded ? "true" : "false")} bytes_done={item.BytesDownloaded} bytes_total={item.TotalBytes}");
        if (extra is not null)
            line += " " + extra;
        Diagnostics.AppLog.Detail("downloads", line);
    }

    /// <summary>`DownloadStatus.Downloading` → `downloading`. The record vocabulary is lower_snake
    /// throughout and an enum's PascalCase name was the one field that slipped; every member is a
    /// single word, so lowercasing is the whole transform. The index on disk keeps the enum's own
    /// spelling — that is a serialization format, not a log field.</summary>
    private static string StatusToken(DownloadStatus status)
        => status.ToString().ToLowerInvariant();

    /// <summary>A whole-queue action, recorded once with the number of items it covered rather than
    /// once per item (a batch of 8 episodes is one decision, not eight).</summary>
    private static void BatchDetail(string eventName, int count, int? active = null)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant($"event={eventName} count={count}");
        if (active is { } activeCount)
            line += FormattableString.Invariant($" active={activeCount}");
        Diagnostics.AppLog.Detail("downloads", line);
    }

    private static void Log(string line)
    {
        if (LogPath is null)
            return;
        try
        {
            File.AppendAllText(LogPath,
                FormattableString.Invariant($"{DateTime.Now:HH:mm:ss.fff} {line}\r\n"));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Invariant KB/MB/GB display (locale commas broke parsing before — M21).</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => FormattableString.Invariant($"{bytes} B"),
        < 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => FormattableString.Invariant($"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => FormattableString.Invariant($"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };
}
