using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LightWeaver.Diagnostics;

namespace LightWeaver.Jellyfin;

/// <summary>
/// One live session socket for one <see cref="JellyfinService"/>: connects to the server's
/// <c>/socket</c> endpoint, keeps it alive, and re-raises what arrives as plain events. It owns a
/// background task and knows nothing about the UI or the player — routing is
/// <see cref="LiveSessionService"/>'s job and acting on a command is the caller's.
///
/// <para>The socket lands on the same server-side <c>SessionInfo</c> as
/// <see cref="PlaybackReporter"/> because it authenticates with the same token: the server fills
/// the device id from the token's device record, so there is no separate handshake.</para>
/// </summary>
public sealed class SessionSocket : IDisposable
{
    /// <summary>Assembled-frame ceiling. Nothing the server legitimately sends comes close — the
    /// biggest is a coalesced LibraryChanged, which is a list of 32-character ids — and an
    /// unbounded accumulator is how one hostile or broken frame becomes an OOM.</summary>
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Receive buffer, and the size the frame assembler is built and rebuilt at.</summary>
    private const int FrameBufferBytes = 16 * 1024;

    /// <summary>An assembler that grew past this is released instead of reused.
    /// <see cref="ArrayBufferWriter{T}.Clear"/> keeps capacity, so one frame near
    /// <see cref="MaxFrameBytes"/> — dropped or not — would pin megabytes per socket for the rest
    /// of the process, which is the allocation the cap exists to bound. Everything this protocol
    /// sends routinely fits well under this, so the reuse that is worth having is kept.</summary>
    private const int RetainedFrameBytes = 64 * 1024;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>A connection that lasted this long counts as healthy, so the next drop starts the
    /// ladder over instead of inheriting the delay of the outage that preceded it.</summary>
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(60);

    /// <summary>Used only when a <c>ForceKeepAlive</c> carries no usable timeout. The server's own
    /// default is 60 s, so half of it is the interval this would have produced anyway.</summary>
    private static readonly TimeSpan FallbackKeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Floor on the keep-alive interval: the timeout comes off the wire, and a server
    /// that ever reported a fraction of a second would otherwise turn this into a send loop.</summary>
    private static readonly TimeSpan MinKeepAliveInterval = TimeSpan.FromMilliseconds(200);

    private static readonly ReadOnlyMemory<byte> KeepAliveFrame =
        Encoding.UTF8.GetBytes("{\"MessageType\":\"KeepAlive\"}");

    private readonly JellyfinService _service;
    private readonly TimeSpan _baseBackoff;
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>Concurrent sends on one <see cref="ClientWebSocket"/> throw
    /// (<c>InvalidOperationException</c>, "already one outstanding 'SendAsync' call"), and the
    /// keep-alive timer sends from a thread-pool thread that knows nothing about the caller.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>A <see cref="System.Threading.Timer"/>, deliberately not a <c>DispatcherTimer</c>:
    /// the keep-alive has to fire while the UI thread is busy, and the server drops the socket
    /// sixty seconds after the last one it received.</summary>
    private readonly System.Threading.Timer _keepAlive;

    private ClientWebSocket? _socket;
    private Task? _loop;
    private bool _started;
    private bool _disposed;
    /// <summary>Only ever touched from the receive loop, so no interlock (see <see cref="Raise"/>).</summary>
    private bool _handlerFaultLogged;
    private volatile bool _isConnected;

    public SessionSocket(JellyfinService service, TimeSpan? baseBackoff = null)
    {
        _service = service;
        // Injectable so a fixture does not have to sit through a 2 s reconnect to prove the ladder
        // exists.
        _baseBackoff = baseBackoff is { TotalMilliseconds: > 0 } value ? value : TimeSpan.FromSeconds(2);
        _keepAlive = new System.Threading.Timer(_ => _ = SendKeepAliveAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<PlayRequestMessage>? PlayRequested;
    public event Action<PlaystateRequestMessage>? PlaystateRequested;
    public event Action<GeneralCommandMessage>? GeneralCommandReceived;
    public event Action<LibraryUpdateMessage>? LibraryChanged;
    public event Action<UserDataChangeMessage>? UserDataChanged;
    public event Action<SyncPlayCommandMessage>? SyncPlayCommandReceived;
    public event Action<SyncPlayGroupUpdateMessage>? SyncPlayGroupUpdated;

    /// <summary>The socket is up. Raised on the transition, so a reconnect after a drop raises it
    /// again — which is what makes it a resynchronisation signal rather than a status bit: a
    /// subscriber that missed messages while the socket was away learns here that it has to ask
    /// for them again.
    ///
    /// <para>Raised on the receive-loop thread just BEFORE the loop starts, so a subscriber that
    /// blocks holds up the first message. Do work asynchronously.</para></summary>
    public event Action? Connected;

    /// <summary>The token was refused. Terminal: nothing reconnects after this.</summary>
    public event Action? AuthenticationRejected;

    public bool IsConnected => _isConnected;

    /// <summary>Derives the socket endpoint from a normalized server URL. Null when the URL is
    /// missing or is not http/https.
    ///
    /// <para><c>/socket</c> is APPENDED to the existing path rather than replacing it, so a
    /// sub-path install (<c>http://host/jellyfin</c>) resolves to
    /// <c>ws://host/jellyfin/socket</c> — a rooted <c>/socket</c> 404s there. No query string
    /// either: the token rides the <c>Authorization</c> header, both because Jellyfin 12 disables
    /// the legacy <c>api_key</c> parameter outright and because
    /// <see cref="AppLog"/>'s URL redaction only matches <c>https?://</c>, so a <c>ws://</c> URI
    /// with a query would reach a log file intact.</para></summary>
    public static Uri? BuildSocketUri(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            return null;
        var scheme = server.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss"
            : server.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? "ws"
            : null;
        if (scheme is null)
            return null;
        // Authority rather than Host + Port: it brackets an IPv6 literal and already omits the
        // port when it is the default — and http/https share their defaults with ws/wss.
        var path = server.AbsolutePath.TrimEnd('/') + "/socket";
        return Uri.TryCreate($"{scheme}://{server.Authority}{path}", UriKind.Absolute, out var socket)
            ? socket
            : null;
    }

    /// <summary>Starts the connect/receive loop. Idempotent — a second call is a no-op, so a
    /// profile that activates twice does not end up with two sockets on one session.</summary>
    public void Start()
    {
        if (_disposed || _started)
            return;
        _started = true;
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        _keepAlive.Dispose();
        // Abort rather than a courteous close handshake: this runs on the shutdown path, and a
        // server that has stopped answering must not be able to hold the process open.
        _socket?.Abort();
        try
        {
            // Bounded, because this join runs on the UI thread: StopProfile from the
            // session-closed Dispatcher.Invoke, and _live.Dispose() from OnClosed. So the receive
            // loop must never block on the dispatcher. Today it touches it nowhere and unwinds in
            // milliseconds; the first handler that marshals with Dispatcher.Invoke deadlocks
            // against this wait and makes every logout and every exit cost the full second per
            // profile. Marshal with BeginInvoke instead, or move this join off the UI thread.
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Best-effort join. The loop swallows its own transport failures; anything that
            // reaches here is already on its way down.
        }
        // The token source and the send gate are deliberately left undisposed: the receive loop
        // may still be unwinding through both, and disposing them under it converts a clean
        // shutdown into an ObjectDisposedException on a thread nobody is watching. Same rule as
        // Updates/UpdateService.
    }

    private enum ConnectionOutcome
    {
        /// <summary>Never got a socket. Transient — back off and try again.</summary>
        Failed,
        /// <summary>Connected, then lost it. Back off and try again.</summary>
        Established,
        /// <summary>401/403. Terminal.</summary>
        Rejected,
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        var backoff = _baseBackoff;
        while (!cancellation.IsCancellationRequested)
        {
            var lifetime = Stopwatch.StartNew();
            var outcome = await RunOnceAsync(cancellation).ConfigureAwait(false);
            if (outcome == ConnectionOutcome.Rejected)
            {
                // A revoked token is terminal, and reconnecting against it is the standing repo
                // rule's failure mode: nothing here may mint or re-authorise a token, so a retry
                // ladder against a 401 is a loop that can only ever lose.
                AppLog.Info("session", "event=socket outcome=rejected reason=auth");
                Raise(AuthenticationRejected, "rejected");
                return;
            }
            if (cancellation.IsCancellationRequested)
                return;
            if (outcome == ConnectionOutcome.Established && lifetime.Elapsed >= StableConnection)
                backoff = _baseBackoff;
            try
            {
                await Task.Delay(Jitter(backoff), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromMilliseconds(Math.Min(
                backoff.TotalMilliseconds * 2,
                Math.Max(MaxBackoff.TotalMilliseconds, _baseBackoff.TotalMilliseconds)));
        }
    }

    private async Task<ConnectionOutcome> RunOnceAsync(CancellationToken cancellation)
    {
        if (!_service.IsConnected || BuildSocketUri(_service.ServerUrl) is not { } uri)
            return ConnectionOutcome.Failed;

        using var socket = new ClientWebSocket();
        // Without this the failure path carries no status at all and a dead token is
        // indistinguishable from a refused connection — which is exactly the difference between
        // stopping and retrying forever.
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", _service.AuthorizationHeader);
        try
        {
            await socket.ConnectAsync(uri, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConnectionOutcome.Failed;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException
            or IOException or SocketException or InvalidOperationException)
        {
            var status = socket.HttpStatusCode;
            // Detail, not Info: an offline or sleeping server fails this every backoff tick for as
            // long as it is away, and the breadcrumb ring holds 500 entries in total — a failure
            // that repeats on a timer must not be what evicts the events an incident needs.
            AppLog.Detail("session", FormattableString.Invariant(
                $"event=socket outcome=connect_failure endpoint={Endpoint(uri)} status={(int)status} error={ex.GetType().Name}"));
            return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ConnectionOutcome.Rejected
                : ConnectionOutcome.Failed;
        }

        _socket = socket;
        _isConnected = true;
        AppLog.Info("session", $"event=socket outcome=connected endpoint={Endpoint(uri)}");
        Raise(Connected, "connected");
        try
        {
            await ReceiveLoopAsync(socket, cancellation).ConfigureAwait(false);
            return ConnectionOutcome.Established;
        }
        finally
        {
            _isConnected = false;
            // Disarmed before the reference is dropped, so a tick in flight finds a socket that is
            // at worst already disposed (which SendKeepAliveAsync catches) rather than the next
            // connection's.
            _keepAlive.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _socket = null;
            AppLog.Detail("session", "event=socket outcome=closed");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[FrameBufferBytes];
        var assembled = new ArrayBufferWriter<byte>(FrameBufferBytes);
        var dropping = false;
        while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException
                or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                AppLog.Detail("session", $"event=receive outcome=failure error={ex.GetType().Name}");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return;
            // Text only. A binary frame is not something this protocol produces, and decoding one
            // as UTF-8 would at best waste a parse.
            if (result.MessageType != WebSocketMessageType.Text)
                dropping = true;
            else if (assembled.WrittenCount + result.Count > MaxFrameBytes)
                dropping = true;
            else
                assembled.Write(buffer.AsSpan(0, result.Count));

            if (!result.EndOfMessage)
                continue;
            if (dropping)
                AppLog.Detail("session", FormattableString.Invariant(
                    $"event=frame outcome=dropped bytes={assembled.WrittenCount}"));
            else
                Dispatch(Encoding.UTF8.GetString(assembled.WrittenSpan));
            if (assembled.Capacity > RetainedFrameBytes)
                assembled = new ArrayBufferWriter<byte>(FrameBufferBytes);
            else
                assembled.Clear();
            dropping = false;
        }
    }

    private void Dispatch(string frame)
    {
        if (!SessionMessageReader.TryRead(frame, out var messageType, out var data))
        {
            // Recorded and dropped, never thrown. One frame this client cannot read is the server
            // talking about something it does not handle; letting it out of here would take the
            // receive loop — and with it every command that would have followed — down with it.
            // Whatever a SUBSCRIBER throws is held to the same rule, which is why the events below
            // are raised through Raise rather than invoked here.
            AppLog.Detail("session", FormattableString.Invariant(
                $"event=frame outcome=unparsed chars={frame.Length}"));
            return;
        }

        switch (messageType)
        {
            case SessionMessageTypes.ForceKeepAlive:
                ArmKeepAlive(data);
                break;
            case SessionMessageTypes.KeepAlive:
                // The server's echo of ours. It carries no information and exists only so the
                // exchange is symmetrical.
                break;
            case SessionMessageTypes.Play:
                if (SessionMessageReader.ReadPayload<PlayRequestMessage>(data) is { } play)
                    Raise(PlayRequested, play, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            case SessionMessageTypes.Playstate:
                if (SessionMessageReader.ReadPayload<PlaystateRequestMessage>(data) is { } playstate)
                    Raise(PlaystateRequested, playstate, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            case SessionMessageTypes.GeneralCommand:
                if (SessionMessageReader.ReadPayload<GeneralCommandMessage>(data) is { } command)
                    Raise(GeneralCommandReceived, command, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            case SessionMessageTypes.LibraryChanged:
                if (SessionMessageReader.ReadPayload<LibraryUpdateMessage>(data) is not { } library)
                    LogUnreadablePayload(messageType);
                else if (library.IsEmpty)
                    // Read fine and carries nothing. The server coalesces library changes on a 30 s
                    // timer and sends the tick per user, so an empty one is ordinary — but it is
                    // still a message that produced no action, and that is the thing this log is for.
                    AppLog.Detail("session", $"event=frame outcome=empty kind={messageType}");
                else
                    Raise(LibraryChanged, library, messageType);
                break;
            case SessionMessageTypes.UserDataChanged:
                if (SessionMessageReader.ReadPayload<UserDataChangeMessage>(data) is { } userData)
                    Raise(UserDataChanged, userData, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            case SessionMessageTypes.SyncPlayCommand:
                if (SessionMessageReader.ReadPayload<SyncPlayCommandMessage>(data) is { } syncPlayCommand)
                    Raise(SyncPlayCommandReceived, syncPlayCommand, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            case SessionMessageTypes.SyncPlayGroupUpdate:
                if (SessionMessageReader.ReadPayload<SyncPlayGroupUpdateMessage>(data) is { } groupUpdate)
                    Raise(SyncPlayGroupUpdated, groupUpdate, messageType);
                else
                    LogUnreadablePayload(messageType);
                break;
            default:
                AppLog.Detail("session", "event=frame outcome=ignored");
                break;
        }
    }

    /// <summary>A frame whose envelope read but whose <c>Data</c> did not deserialize into the
    /// shape its type names — a field the server changed the type of, above all.
    ///
    /// <para>This is the one way a message could go missing with the log saying nothing at all: an
    /// unreadable envelope records <c>unparsed</c> and an unknown type records <c>ignored</c>, but
    /// a payload that came out null used to fall straight through the <c>if</c>. That is worth a
    /// line for every message here, and most of all for the two SyncPlay ones, whose entire failure
    /// mode is that nothing happened. The wire type is safe to print: it is only ever one of the
    /// constants above, having matched one exactly.</para></summary>
    private static void LogUnreadablePayload(string kind)
        => AppLog.Detail("session", $"event=frame outcome=unreadable kind={kind}");

    /// <summary>Hands one message to its subscribers on the receive-loop thread and swallows what
    /// they throw.
    ///
    /// <para>Nothing above this catches. An escaping handler exception unwinds
    /// <see cref="ReceiveLoopAsync"/> and then <see cref="RunAsync"/>, which faults <c>_loop</c>
    /// and takes the reconnect ladder with it for the remaining life of the process. What that
    /// looks like from outside is a session that went quiet — indistinguishable from a server that
    /// stopped sending — and the only trace is an unobserved-task crash file, which also burns a
    /// crash-retention slot. One bad subscriber costs one message instead.</para>
    ///
    /// <para>First fault to the breadcrumb ring, the rest verbose-only: a handler that throws on
    /// one message of a kind usually throws on all of them, and the ring holds 500 entries in
    /// total — the same reason the connect-failure log above is Detail.</para></summary>
    private void Raise<T>(Action<T>? handler, T message, string kind)
    {
        if (handler is null)
            return;
        try
        {
            handler(message);
        }
        catch (Exception ex)
        {
            LogHandlerFault(kind, ex);
        }
    }

    /// <summary>The parameterless form, for <see cref="AuthenticationRejected"/>. It runs in
    /// <see cref="RunAsync"/> rather than <see cref="Dispatch"/>, where the ladder is already
    /// terminal, so a throwing subscriber costs no reconnect — but it would still fault
    /// <c>_loop</c> and spend a crash-retention slot on a session that ended correctly.</summary>
    private void Raise(Action? handler, string kind)
    {
        if (handler is null)
            return;
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            LogHandlerFault(kind, ex);
        }
    }

    private void LogHandlerFault(string kind, Exception ex)
    {
        // The type, not the message: a handler exception can carry item titles or a URL.
        var line = $"event=handler outcome=failure kind={kind} error={ex.GetType().Name}";
        if (_handlerFaultLogged)
            AppLog.Detail("session", line);
        else
            AppLog.Info("session", line);
        _handlerFaultLogged = true;
    }

    /// <summary>Arms the keep-alive from a <c>ForceKeepAlive</c>, whose <c>Data</c> is the server's
    /// socket timeout in seconds.
    ///
    /// <para>Half the timeout, and one sent immediately. Only an INBOUND KeepAlive advances the
    /// server's <c>LastKeepAliveDate</c>; at the timeout it disposes the socket without warning,
    /// and it only re-sends <c>ForceKeepAlive</c> inside the last quarter of the window — so one
    /// whole interval of slack has to fit inside the timeout.</para></summary>
    private void ArmKeepAlive(JsonElement data)
    {
        var interval = data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds / 2)
            : FallbackKeepAliveInterval;
        if (interval < MinKeepAliveInterval)
            interval = MinKeepAliveInterval;
        AppLog.Detail("session", FormattableString.Invariant(
            $"event=keep_alive outcome=armed interval_ms={(long)interval.TotalMilliseconds}"));
        _ = SendKeepAliveAsync();
        _keepAlive.Change(interval, interval);
    }

    private async Task SendKeepAliveAsync()
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;
        try
        {
            await _sendGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            await socket.SendAsync(KeepAliveFrame, WebSocketMessageType.Text, true, _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or SocketException
            or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The receive loop is the one that notices a dead socket and reconnects; a failed
            // keep-alive has nothing useful to add and must not surface on a timer thread.
            AppLog.Detail("session", $"event=keep_alive outcome=failure error={ex.GetType().Name}");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>±20% so a server that drops every client at once does not get them all back in the
    /// same millisecond.</summary>
    private static TimeSpan Jitter(TimeSpan value)
        => TimeSpan.FromMilliseconds(value.TotalMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));

    /// <summary>The endpoint as a correlatable token and nothing more. The URI carries the user's
    /// server address, and <see cref="AppLog"/>'s URL rule only matches <c>https?://</c> — a
    /// <c>ws://</c> URI reaches the file exactly as written. <see cref="UriPartial.Path"/> drops
    /// any query before the hash so two endpoints that differ only by one still hash apart.</summary>
    private static string Endpoint(Uri uri) => AppLog.ShortHash(uri.GetLeftPart(UriPartial.Path));
}
