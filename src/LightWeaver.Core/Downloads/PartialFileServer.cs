using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightWeaver.Downloads;

/// <summary>State of one published download, sampled every time a reader reaches the write head.
/// The provider returns null once the download is gone, which ends the response.</summary>
/// <param name="Status">Whether more bytes are still coming.</param>
/// <param name="TotalBytes">Expected size, or -1 when the server never reported one (a
/// transcoded download). On Completed this is the byte count actually written.</param>
public readonly record struct PartialFileState(DownloadStatus Status, long TotalBytes);

/// <summary>
/// Serves the partial file of a still-running download over loopback HTTP, so playback can start
/// on bytes that are still arriving instead of waiting for the download to finish.
///
/// <para><b>Why a socket and not the file.</b> mpv cannot follow a growing local file:
/// <c>stream_file</c> returns 0 at the write head and mpv reads that as end-of-stream, so a
/// direct <c>loadfile</c> ends playback at whatever byte had arrived when it opened. Over HTTP
/// that same moment is indistinguishable from a slow server — the response just has no bytes for
/// you yet — and every HTTP client already waits. That wait is the whole reason this class
/// exists; see <see cref="WaitForMoreAsync"/>.</para>
///
/// <para><b>Reach.</b> <see cref="IPAddress.Loopback"/> on an ephemeral port, never
/// <see cref="IPAddress.Any"/>. A request is never mapped onto the filesystem: it must carry a
/// token issued by <see cref="Publish"/>, and the token is the only thing that names a file. It
/// is therefore a capability and is never logged — records carry
/// <see cref="Diagnostics.AppLog.ShortHash"/> of it instead.</para>
///
/// <para><b>Why the token rides in the query</b> (<c>/?t=…</c>) and not the path. It is the one
/// place <see cref="Diagnostics.AppLog"/> can protect: its redaction strips the query and fragment
/// off every <c>http(s)://</c> match before any named-secret rule runs, and keeps the path. And
/// this is not a verbose-only concern — mpv's own warn/error lines are breadcrumbed into the
/// incident ring, and that ring is written into <c>crash-*.log</c> and <c>playback-*.log</c> on an
/// ordinary run, so an http-demuxer error naming this URL reaches a shareable file exactly when a
/// partial stream fails. A path token would be in it verbatim. mpv and ffmpeg are indifferent
/// between the two forms: the whole request target goes on the request line either way, neither
/// form ends in an extension so demuxer probing is unchanged, and Range, <c>http-header-fields</c>
/// and reconnect all work off that same target.</para>
///
/// <para>The listener starts with the first publication and stops with the last, so an install
/// that never streams a running download never opens a socket. A publication is retired when it is
/// withdrawn and also when a reader has been handed the completed file, so it does not outlive the
/// playback that needed it.</para>
/// </summary>
public sealed class PartialFileServer : IDisposable
{
    /// <summary>How long a reader may sit at the write head with no new bytes before its response
    /// is closed. Deliberately longer than <see cref="DownloadManager"/>'s own 90 s per-read stall
    /// bound: a real stall fails the download, and the failed state is what normally ends the wait.
    /// This is the backstop for the case where no state change ever arrives, so a wedged download
    /// cannot pin a socket for the life of the process.</summary>
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Write-head poll interval. The file grows in 80 KB writes at whatever the link
    /// does, so a finer poll buys nothing and a coarser one shows up as a hitch in playback.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Request-head cap: a client that never sends the blank line is dropped rather than
    /// read forever.</summary>
    private const int MaxRequestBytes = 8192;

    /// <summary>How long a connection may take to send its request head. The byte cap above only
    /// stops a client that sends too MUCH; a client that sends nothing at all would otherwise sit
    /// in the read until the listener is torn down.</summary>
    private static readonly TimeSpan DefaultHeadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Concurrent connections served at once. This server has exactly one real client —
    /// mpv, with one stream connection plus the odd HEAD or seek reconnect overlapping it — so the
    /// headroom is for that overlap and nothing else. Anything past it is refused at accept, which
    /// keeps a local process from pinning unbounded sockets and file handles on us.
    /// <para>Internal so the harness asserts against the cap itself; a copy of the number in the
    /// fixture would keep passing after the cap moved.</para></summary>
    internal const int MaxConnections = 8;

    /// <summary>Refusals are recorded at most this often. Every miss is a request that had no
    /// business being here, but one line per miss lets a loopback port scanner push the incident
    /// ring out of the log — and that ring is the context a real playback failure is read with.</summary>
    private const long RefusalLogIntervalMs = 10_000;

    private const int CopyBufferBytes = 64 * 1024;

    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    /// <summary>The zero-length chunk that ends a chunked body. Writing it is the only thing that
    /// tells a client the message is COMPLETE; withholding it is how an abnormal end is reported,
    /// so it belongs to exactly one code path (<see cref="WaitOutcome.Delivered"/>).</summary>
    private static readonly byte[] ChunkTerminator = "0\r\n\r\n"u8.ToArray();

    /// <summary>Query parameter carrying the capability, as issued by <see cref="Publish"/>.</summary>
    private const string TokenParameter = "t=";

    private sealed record Publication(Guid ItemId, string FilePath, Func<PartialFileState?> State);

    /// <summary>Keyed on the token's canonical text and matched ORDINALLY, not parsed back into a
    /// Guid: Guid.TryParseExact accepts any casing, so an uppercased token opened the same
    /// publication (caught by the harness). A capability is the exact string that was issued.</summary>
    private readonly ConcurrentDictionary<string, Publication> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _tokenByItem = new();
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _headTimeout;
    private readonly object _gate = new();
    private readonly object _refusalGate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private int _connections;
    private long _lastRefusalLogMs = long.MinValue / 2;
    private int _suppressedRefusals;
    private bool _disposed;

    /// <param name="stallTimeout">Write-head bound; the harness shortens it to make the bound
    /// observable in a test run.</param>
    /// <param name="headTimeout">Request-head bound, shortened by the harness for the same reason.</param>
    public PartialFileServer(TimeSpan? stallTimeout = null, TimeSpan? headTimeout = null)
    {
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _headTimeout = headTimeout ?? DefaultHeadTimeout;
    }

    /// <summary>Publishes one download's partial file and returns the loopback URL that serves it.
    /// Re-publishing an item issues a FRESH token and drops the old one, which also cuts loose any
    /// reader left over from a previous playback of the same item.</summary>
    public string Publish(Guid itemId, string filePath, Func<PartialFileState?> state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = Guid.NewGuid().ToString("N");
        int port;
        // The listener start and the registration are one step. Registering outside the gate left
        // a window where a concurrent Withdraw saw an empty table, stopped the listener, and this
        // method still returned a URL naming the port it had just closed. No caller is off the UI
        // thread today, but the rest of the class is written to be thread-safe and this is the one
        // invariant that was not.
        lock (_gate)
        {
            if (_listener is null)
            {
                _cts = new CancellationTokenSource();
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listener = listener;
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = AcceptLoopAsync(listener, _cts.Token);
                Diagnostics.AppLog.Detail("partial-stream",
                    FormattableString.Invariant($"event=listen outcome=success port={_port}"));
            }
            port = _port;
            if (_tokenByItem.TryGetValue(itemId, out var previous))
                _byToken.TryRemove(previous, out _);
            _byToken[token] = new Publication(itemId, filePath, state);
            _tokenByItem[itemId] = token;
        }
        Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
            $"event=publish outcome=success item={itemId:N} token={Diagnostics.AppLog.ShortHash(token)}"));
        return FormattableString.Invariant($"http://127.0.0.1:{port}/?{TokenParameter}{token}");
    }

    /// <summary>Retires an item's token. Live readers notice on their next write-head poll and
    /// close; the listener shuts down once nothing is published.</summary>
    public void Withdraw(Guid itemId)
    {
        string? token;
        lock (_gate)
        {
            if (!_tokenByItem.TryRemove(itemId, out token))
                return;
            _byToken.TryRemove(token, out _);
            StopIfIdleLocked();
        }
        Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
            $"event=withdraw outcome=success item={itemId:N} token={Diagnostics.AppLog.ShortHash(token)}"));
    }

    /// <summary>Retires a publication whose reader has been handed the whole completed file.
    /// Token-checked, not just item-checked: a replay publishes the same item again under a fresh
    /// token, and retiring THAT one from an outgoing reader's thread would cut the playback that
    /// replaced it.</summary>
    private void WithdrawDelivered(Guid itemId, string token)
    {
        lock (_gate)
        {
            if (!_tokenByItem.TryGetValue(itemId, out var current)
                || !string.Equals(current, token, StringComparison.Ordinal))
                return;
            _tokenByItem.TryRemove(itemId, out _);
            _byToken.TryRemove(token, out _);
            StopIfIdleLocked();
        }
        Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
            $"event=delivered outcome=withdrawn item={itemId:N} token={Diagnostics.AppLog.ShortHash(token)}"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Under the gate with the stop, for the same reason Publish registers under it: the table
        // and the listener are one piece of state, and a half-torn-down server must not be visible.
        lock (_gate)
        {
            _byToken.Clear();
            _tokenByItem.Clear();
            StopLocked();
        }
    }

    private void StopIfIdleLocked()
    {
        if (_byToken.IsEmpty)
            StopLocked();
    }

    private void StopLocked()
    {
        // Captured before the cancel, not re-read after it. Monitor is reentrant, so a continuation
        // that runs inline on Cancel() and re-enters this object holds the same lock and can clear
        // the field between these lines — a hazard nothing reaches today, and exactly the kind that
        // only ever shows up as a NullReferenceException in a crash file.
        var listener = _listener;
        if (listener is null)
            return;
        _listener = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        listener.Stop();
        Diagnostics.AppLog.Detail("partial-stream", "event=idle outcome=stopped");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    // Dropped without a response: a client this far over the cap is not a media
                    // player, and writing it a status only tells it the port is worth holding.
                    Interlocked.Decrement(ref _connections);
                    RecordRefusal("too_many_connections");
                    client.Dispose();
                    continue;
                }
                _ = ServeAsync(client, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException
            or ObjectDisposedException or InvalidOperationException)
        {
            // The listener was stopped; every path here already recorded why. InvalidOperationException
            // belongs in the list: AcceptTcpClientAsync throws it on a listener that has already been
            // Stop()ed, and this task is fire-and-forget — anything that escapes reaches
            // TaskScheduler.UnobservedTaskException, which App.OnStartup turns into a crash file and
            // spends one of the ten slots a real crash needs.
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                string? head;
                // The head read is the one place a connection can be silent and still be waited
                // on, so it gets its own bound. Everything after it is answered immediately or is
                // the body, which has the write-head bound of its own.
                using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    headCts.CancelAfter(_headTimeout);
                    head = await ReadHeadAsync(stream, headCts.Token).ConfigureAwait(false);
                }
                if (head is null)
                    return;
                var lines = head.Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length < 2)
                {
                    await WriteStatusAsync(stream, "400 Bad Request", null, ct).ConfigureAwait(false);
                    return;
                }
                var method = request[0];
                if (method is not ("GET" or "HEAD"))
                {
                    await WriteStatusAsync(stream, "405 Method Not Allowed", null, ct).ConfigureAwait(false);
                    return;
                }
                // The target is matched against issued tokens and NOTHING else. It is never
                // combined with a directory, so no request can name a file this server was not
                // handed, and no path form is served at all.
                if (TokenFromTarget(request[1]) is not { } token
                    || !_byToken.TryGetValue(token, out var publication))
                {
                    RecordRefusal("unknown_token");
                    await WriteStatusAsync(stream, "404 Not Found", null, ct).ConfigureAwait(false);
                    return;
                }
                string? range = null;
                for (var i = 1; i < lines.Length; i++)
                    if (lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = lines[i][6..].Trim();
                await ServePublicationAsync(stream, client, token, publication, method, range, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException
                or OperationCanceledException or ObjectDisposedException
                or InvalidOperationException)
            {
                // A reader that walks away mid-response is the normal end of a playback, not a fault.
                // InvalidOperationException for the same reason as the accept loop's: GetStream()
                // throws it on a socket torn down between accept and here, and an escape from this
                // fire-and-forget task is written to disk as a crash.
            }
            finally
            {
                Interlocked.Decrement(ref _connections);
            }
        }
    }

    /// <summary>The capability out of a raw request target, or null when there is none to find.
    ///
    /// <para>Ordinal and UNDECODED, on the target exactly as it arrived. Neither <see cref="Uri"/>
    /// nor a query parser may be used here: both percent-decode, and <see cref="Uri"/> case-folds
    /// parts of the target as well, so a re-encoded or re-cased variant of an issued token would
    /// open the publication. The property being kept is "the capability is the exact string that
    /// was issued" — the case-insensitivity of <c>Guid.TryParseExact</c> already broke it once.</para>
    /// </summary>
    private static string? TokenFromTarget(string target)
    {
        var query = target.IndexOf('?');
        if (query < 0)
            return null;
        // Split on '&' rather than requiring the parameter to come first: an extra parameter is
        // legal in a URL and must not decide whether the token matches.
        foreach (var part in target[(query + 1)..].Split('&'))
            if (part.StartsWith(TokenParameter, StringComparison.Ordinal))
                return part[TokenParameter.Length..];
        return null;
    }

    /// <summary>One refusal line per <see cref="RefusalLogIntervalMs"/>, carrying the count it
    /// stands for so a burst is still visible as a burst.</summary>
    private void RecordRefusal(string reason)
    {
        int suppressed;
        lock (_refusalGate)
        {
            var now = Environment.TickCount64;
            if (now - _lastRefusalLogMs < RefusalLogIntervalMs)
            {
                _suppressedRefusals++;
                return;
            }
            _lastRefusalLogMs = now;
            suppressed = _suppressedRefusals;
            _suppressedRefusals = 0;
        }
        Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
            $"event=request outcome=refused reason={reason} suppressed={suppressed}"));
    }

    private async Task ServePublicationAsync(NetworkStream stream, TcpClient client, string token,
        Publication publication, string method, string? range, CancellationToken ct)
    {
        var tokenHash = Diagnostics.AppLog.ShortHash(token);
        if (publication.State() is not { } state)
        {
            Diagnostics.AppLog.Detail("partial-stream",
                $"event=request outcome=gone token={tokenHash}");
            await WriteStatusAsync(stream, "404 Not Found", null, ct).ConfigureAwait(false);
            return;
        }
        // FileShare.ReadWrite because the engine holds the file open for writing; FileShare.Delete
        // so this handle is not the reason a cancel cannot delete the partial file.
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(publication.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Detail("partial-stream",
                $"event=request outcome=unreadable token={tokenHash} error={ex.GetType().Name}");
            await WriteStatusAsync(stream, "404 Not Found", null, ct).ConfigureAwait(false);
            return;
        }
        using (handle)
        {
            var available = RandomAccess.GetLength(handle);
            // The complete length, or -1 when there is none to give. A transcoded download has no
            // reported total, and inventing one would make every Content-Length a lie the client
            // only discovers by running short.
            var known = state.TotalBytes > 0 ? state.TotalBytes
                : state.Status == DownloadStatus.Completed ? available
                : -1;
            var (start, requestedEnd, ranged) = ParseRange(range);
            // Two different refusals, and they are refusals for different reasons.
            //
            // start >= known is past the end of the complete resource: permanently unsatisfiable,
            // and a 416 is the only honest answer.
            //
            // start > available is past the WRITE HEAD — those bytes are coming, just not yet — so
            // in principle a start a few bytes past it could be served by waiting. It is refused
            // anyway, because the rule has to be simple and the cost of waiting is not paid here:
            // a resume position converted to a byte offset can land gigabytes past a prefix, and
            // parking on it looks to the client exactly like a hang, for the full stall bound,
            // before anything happens. "On disk now, or 416" is predictable and answers instantly;
            // the client re-asks, which for a growing file is what it should do anyway. Exactly AT
            // the head still serves: those bytes are one write away and the body waits for them,
            // which is the case this whole class exists for.
            if (start < 0 || (requestedEnd >= 0 && requestedEnd < start)
                || (known >= 0 && start >= known)
                || start > available)
            {
                Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
                    $"event=request outcome=unsatisfiable token={tokenHash} start={start} known={known}"));
                await WriteStatusAsync(stream, "416 Range Not Satisfiable",
                    FormattableString.Invariant($"Content-Range: bytes */{(known >= 0 ? known : available)}\r\n"),
                    ct).ConfigureAwait(false);
                return;
            }

            // The last byte this response will deliver; -1 means "until the download ends".
            //
            // A bounded Range on an unsized download is HONOURED rather than flattened to -1. It
            // used to be flattened, so `bytes=100-199` on a transcoded download produced a body
            // that ran unbounded past 199 — a protocol lie, whatever the odds of a client asking
            // (Accept-Ranges is not advertised without a complete length). Honouring it also gives
            // the response a length it can declare: the client named the last byte, so the body is
            // end - start + 1 long whatever the complete resource turns out to be.
            var end = known >= 0
                ? (requestedEnd >= 0 ? Math.Min(requestedEnd, known - 1) : known - 1)
                : requestedEnd;
            // "The Range was honoured", not "start > 0": a bytes=0-N range gets a body cut short at
            // N, and answering that 200 tells the client the whole resource is N+1 bytes long. With
            // no complete length to compare against, any bounded range is short of the whole by
            // construction — an unsized response is one that has no end.
            var partial = ranged && (start > 0 || (known >= 0 ? end < known - 1 : end >= 0));
            // A body with no last byte is chunk-framed. "No Content-Length, connection closes" IS
            // the framing of such a response, so a wait ended mid-film — the download paused from
            // the Downloads screen, failed on a LAN stall, removed, or past the stall bound — is
            // indistinguishable on the wire from the download having finished. Chunked framing can
            // state the difference: a normal end writes the terminating zero chunk (the Delivered
            // branch in CopyAsync) and an abnormal end closes without it, which is a truncated
            // message rather than an end of stream.
            //
            // MEASURED, and written down because it is NOT the result this was changed for: ffmpeg
            // does not turn that truncation into an error. It logs nothing and reports a clean end
            // of stream, exactly as it did under the old framing — so mpv ends the file rather than
            // failing it either way, and MainWindow's EndReached guard is what actually produces
            // the fallback. The framing stays because it is the honest answer and the only way any
            // client can tell the two ends apart; it is not what carries the feature.
            var chunked = end < 0;
            var header = new StringBuilder();
            header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            header.Append("Content-Type: application/octet-stream\r\n");
            if (known >= 0)
                header.Append("Accept-Ranges: bytes\r\n");
            if (chunked)
                header.Append("Transfer-Encoding: chunked\r\n");
            else
                header.Append(FormattableString.Invariant($"Content-Length: {end - start + 1}\r\n"));
            if (partial)
            {
                // Unknown complete length: `*` per RFC 7233. With no last byte either, the range
                // names what exists at this instant rather than a promise — the body runs past it.
                var completeLength = known >= 0 ? FormattableString.Invariant($"{known}") : "*";
                var lastByte = chunked ? Math.Max(start, available - 1) : end;
                header.Append(FormattableString.Invariant(
                    $"Content-Range: bytes {start}-{lastByte}/{completeLength}\r\n"));
            }
            header.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), ct).ConfigureAwait(false);
            Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
                $"event=request outcome=success token={tokenHash} method={method.ToLowerInvariant()} status={(partial ? 206 : 200)} start={start} known={known} on_disk={available}"));
            if (method == "HEAD")
                return;

            await CopyAsync(stream, client, handle, token, publication, start, end, chunked,
                toEndOfResource: known >= 0 && end == known - 1, tokenHash, ct).ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(NetworkStream stream, TcpClient client, SafeFileHandle handle,
        string token, Publication publication, long start, long end, bool chunked,
        bool toEndOfResource, string tokenHash, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferBytes];
        var position = start;
        var idle = Stopwatch.StartNew();
        // Whether this response ever sat at the write head. It is what separates a reader that
        // followed the download to its end — a playout, whose publication is spent — from one that
        // merely asked for a range that happened to run to the last byte, which is what ffmpeg does
        // when it reads a container's index. Only the first retires the publication.
        var followed = false;
        while (end < 0 || position <= end)
        {
            var want = end < 0 ? buffer.Length : (int)Math.Min(buffer.Length, end - position + 1);
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, want), position, ct)
                .ConfigureAwait(false);
            if (read > 0)
            {
                await WriteBodyAsync(stream, chunked, buffer.AsMemory(0, read), ct)
                    .ConfigureAwait(false);
                position += read;
                idle.Restart();
                continue;
            }
            var outcome = await WaitForMoreAsync(client, token, publication, position, idle,
                tokenHash, ct).ConfigureAwait(false);
            if (outcome == WaitOutcome.Ended)
                return;     // abnormal: the framing is left unterminated on purpose
            if (outcome == WaitOutcome.Delivered)
            {
                // The download finished and every byte of it has gone out, so the message is
                // complete and says so. This is the ONLY exit that closes the framing.
                //
                // Written BEFORE the publication is retired, and that order is load-bearing:
                // retiring the last publication stops the listener, which cancels ct — so a
                // terminator written after it is cancelled, and the response that had just
                // delivered a whole file went out looking truncated. Caught by the fixture.
                if (chunked)
                    await stream.WriteAsync(ChunkTerminator, ct).ConfigureAwait(false);
                WithdrawDelivered(publication.ItemId, token);
                return;
            }
            followed = true;
        }
        if (followed && toEndOfResource)
            WithdrawDelivered(publication.ItemId, token);
    }

    /// <summary>One body write, chunk-framed when the response declared chunked encoding: the size
    /// in hex, CRLF, the bytes, CRLF (RFC 9112 §7.1).</summary>
    private static async Task WriteBodyAsync(NetworkStream stream, bool chunked,
        ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!chunked)
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            return;
        }
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(FormattableString.Invariant($"{data.Length:X}\r\n")), ct)
            .ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.WriteAsync(Crlf, ct).ConfigureAwait(false);
    }

    /// <summary>Why a write-head wait ended. For an unsized body this decides the framing the
    /// response closes with, which is the only thing that tells the client which end it was.</summary>
    private enum WaitOutcome
    {
        /// <summary>More bytes may still be coming; poll again.</summary>
        Continue,
        /// <summary>The download finished and the whole of it has gone out. A normal end.</summary>
        Delivered,
        /// <summary>Nothing more is coming: paused, failed, removed, withdrawn, stalled past the
        /// bound, or the reader left. An abnormal end.</summary>
        Ended,
    }

    /// <summary>
    /// The write head. A local read returning 0 here means "not yet", not "never" — returning EOF
    /// is exactly the mpv behaviour this server exists to avoid — so the response holds and polls
    /// the file's length instead.
    /// </summary>
    /// <returns><see cref="WaitOutcome.Continue"/> to keep polling, or the reason the response
    /// ends — which for a chunked body decides whether it is terminated or cut off.</returns>
    private async Task<WaitOutcome> WaitForMoreAsync(TcpClient client, string token,
        Publication publication, long position, Stopwatch idle, string tokenHash,
        CancellationToken ct)
    {
        if (!_byToken.TryGetValue(token, out _) || publication.State() is not { } state)
            return WaitOutcome.Ended;   // withdrawn, or the download was removed under us
        switch (state.Status)
        {
            case DownloadStatus.Completed:
                // Completion is persisted while the engine's FileStream is still open, so up to one
                // of its buffers can be unflushed at this moment. TotalBytes is the count actually
                // written, so it — not the length on disk — decides whether the tail is still owed.
                if (state.TotalBytes <= 0 || position >= state.TotalBytes)
                {
                    // End of file, and the whole of it has gone out: retire the publication here.
                    // Nothing else in the app observes EOF — the player sits paused at the end when
                    // there is no Up Next — so without this the listening socket and a live token
                    // outlast the playback for as long as the user leaves the app open, which is
                    // precisely what the reach note on this class promises they do not.
                    //
                    // No tail is left draining: TotalBytes is the count actually written, so this
                    // is reached only once the last byte has been handed to the socket. The one
                    // case it gives up is a reader that buffered to EOF far AHEAD of playback
                    // (possible only for a file smaller than the configured demuxer budget, which
                    // the user sets in Settings → Storage and can raise to 1 GiB) and then seeks:
                    // that request is refused and the player falls back to the server stream at
                    // its live position.
                    //
                    // The retirement itself is the caller's, after it has closed the framing; see
                    // the Delivered branch in CopyAsync for why the order matters.
                    return WaitOutcome.Delivered;
                }
                break;
            case DownloadStatus.Downloading:
            case DownloadStatus.Queued:
                break;
            default:
                // Paused or Failed: nothing more is coming, and pretending otherwise would hold the
                // socket until the stall bound for a download the user stopped on purpose.
                Diagnostics.AppLog.Detail("partial-stream", FormattableString.Invariant(
                    $"event=wait outcome=ended token={tokenHash} status={state.Status.ToString().ToLowerInvariant()} position={position}"));
                return WaitOutcome.Ended;
        }
        if (idle.Elapsed >= _stallTimeout)
        {
            Diagnostics.AppLog.Error("partial-stream", FormattableString.Invariant(
                $"event=wait outcome=stalled token={tokenHash} position={position} waited_ms={idle.ElapsedMilliseconds}"));
            return WaitOutcome.Ended;
        }
        // Nothing is being written to the socket while we wait, so a client that closed would
        // otherwise go unnoticed until the bound expires.
        if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0)
            return WaitOutcome.Ended;
        await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        return WaitOutcome.Continue;
    }

    /// <summary>Byte range from a <c>Range:</c> header. Returns start -1 for a header this server
    /// will not answer, which the caller turns into a 416.</summary>
    private static (long Start, long End, bool Ranged) ParseRange(string? range)
    {
        if (range is null)
            return (0, -1, false);
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return (-1, -1, true);
        var parts = range[6..].Split('-');
        // A suffix range ("bytes=-500") counts back from a complete length this server may not
        // have, and no client on this path asks for one.
        if (parts.Length != 2 || parts[0].Length == 0
            || !long.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var start))
            return (-1, -1, true);
        var end = -1L;
        if (parts[1].Length > 0 && !long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out end))
            return (-1, -1, true);
        return (start, parts[1].Length > 0 ? end : -1, true);
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            var scan = Math.Max(0, used - 3);
            used += read;
            for (var i = scan; i + 3 < used; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return Encoding.ASCII.GetString(buffer, 0, i);
        }
        return null;
    }

    private static Task WriteStatusAsync(NetworkStream stream, string status, string? extraHeader,
        CancellationToken ct)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{extraHeader}Content-Length: 0\r\nConnection: close\r\n\r\n"), ct)
            .AsTask();
}
