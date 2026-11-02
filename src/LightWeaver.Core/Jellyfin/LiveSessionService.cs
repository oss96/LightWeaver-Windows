using LightWeaver.Diagnostics;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Process-lifetime owner of the live session sockets — one per WARM profile, not just the active
/// one, so a remote "play on" aimed at a backgrounded profile still lands. Every event is
/// re-raised with the profile key that produced it: with two profiles warm, a command from one
/// server must not be applied against the other's session.
/// </summary>
public sealed class LiveSessionService : IDisposable
{
    private readonly Func<string, JellyfinService?> _resolveSession;
    private readonly object _lock = new();
    private readonly Dictionary<string, SessionSocket> _sockets = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>The resolver is the app view model's warm-session lookup; a profile whose session
    /// has been closed resolves to null and is simply not started.</summary>
    public LiveSessionService(Func<string, JellyfinService?> resolveSession)
        => _resolveSession = resolveSession;

    public event Action<string, PlayRequestMessage>? PlayRequested;
    public event Action<string, PlaystateRequestMessage>? PlaystateRequested;
    public event Action<string, GeneralCommandMessage>? GeneralCommandReceived;
    public event Action<string, LibraryUpdateMessage>? LibraryChanged;
    public event Action<string, UserDataChangeMessage>? UserDataChanged;
    public event Action<string, SyncPlayCommandMessage>? SyncPlayCommandReceived;
    public event Action<string, SyncPlayGroupUpdateMessage>? SyncPlayGroupUpdated;

    /// <summary>The profile's socket is up, on every connection and not just the first. What a
    /// subscriber does with it is ask the server again for whatever it missed while the socket was
    /// away.</summary>
    public event Action<string>? Connected;

    /// <summary>The profile's token was refused; its socket has stopped for good.</summary>
    public event Action<string>? AuthenticationRejected;

    /// <summary>The profile's socket has been closed from this side — a logout, or shutdown. The
    /// session goes with it, so a subscriber holding anything keyed to that profile has to drop it
    /// here: every call it would make resolves the session by profile key, and once that lookup
    /// answers null they all become silent no-ops.</summary>
    public event Action<string>? ProfileStopped;

    /// <summary>Advertises the client's capabilities, then opens the profile's socket. Idempotent:
    /// a profile that activates again — every warm switch back to it does — keeps the socket it
    /// already has rather than gaining a second one.</summary>
    public void StartProfile(string profileKey)
    {
        if (_disposed)
            return;
        if (_resolveSession(profileKey) is not { IsConnected: true } service)
        {
            AppLog.Detail("session", $"event=live_start outcome=noop reason=no_session session={AppLog.ShortHash(profileKey)}");
            return;
        }

        SessionSocket socket;
        lock (_lock)
        {
            if (_sockets.ContainsKey(profileKey))
                return;
            socket = new SessionSocket(service);
            _sockets[profileKey] = socket;
        }
        socket.PlayRequested += message => PlayRequested?.Invoke(profileKey, message);
        socket.PlaystateRequested += message => PlaystateRequested?.Invoke(profileKey, message);
        socket.GeneralCommandReceived += message => GeneralCommandReceived?.Invoke(profileKey, message);
        socket.LibraryChanged += message => LibraryChanged?.Invoke(profileKey, message);
        socket.UserDataChanged += message => UserDataChanged?.Invoke(profileKey, message);
        socket.SyncPlayCommandReceived += message => SyncPlayCommandReceived?.Invoke(profileKey, message);
        socket.SyncPlayGroupUpdated += message => SyncPlayGroupUpdated?.Invoke(profileKey, message);
        socket.Connected += () => Connected?.Invoke(profileKey);
        socket.AuthenticationRejected += () => AuthenticationRejected?.Invoke(profileKey);
        _ = StartCoreAsync(profileKey, service, socket);
    }

    /// <summary>Closes one profile's socket. Called from the session-closed path, where it has to
    /// run BEFORE the service is disposed — the socket authenticates off that service.</summary>
    public void StopProfile(string profileKey)
    {
        SessionSocket? socket;
        lock (_lock)
            if (!_sockets.Remove(profileKey, out socket))
                return;
        AppLog.Detail("session", $"event=live_stop session={AppLog.ShortHash(profileKey)}");
        socket.Dispose();
        RaiseProfileStopped(profileKey);
    }

    public void StopAll()
    {
        List<KeyValuePair<string, SessionSocket>> sockets;
        lock (_lock)
        {
            sockets = [.. _sockets];
            _sockets.Clear();
        }
        foreach (var (profileKey, socket) in sockets)
        {
            socket.Dispose();
            RaiseProfileStopped(profileKey);
        }
    }

    /// <summary>The one event raised from this class rather than re-raised out of a socket
    /// handler, so it is the one that has no <see cref="SessionSocket.Raise{T}"/> around it. It
    /// needs the same guard and more: this runs on the UI THREAD — <c>MainWindow</c>'s
    /// <c>SessionClosed</c> handler calls <see cref="StopProfile"/>, and <see cref="Dispose"/>
    /// runs at exit — so a throwing subscriber would unwind a logout half done, with one socket
    /// disposed, the rest of the profiles never stopped, and the exception surfacing as a
    /// crash from a session that ended correctly.</summary>
    private void RaiseProfileStopped(string profileKey)
    {
        try
        {
            ProfileStopped?.Invoke(profileKey);
        }
        catch (Exception ex)
        {
            // The type, not the message: a handler exception can carry item titles or a URL.
            AppLog.Info("session", $"event=handler outcome=failure kind=profile_stopped "
                + $"error={ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopAll();
    }

    /// <summary>Capabilities first, socket second. The server routes remote commands at the
    /// <c>SessionInfo</c> this POST updates, so a socket that opens before it is briefly a session
    /// that can be reached but advertises nothing it can do. Best-effort by contract:
    /// <see cref="JellyfinService.ReportCapabilitiesAsync"/> swallows its own failures, and a
    /// server that rejects the POST must still get its socket.
    ///
    /// <para>Nobody awaits the returned task, so it observes its own failures: anything that
    /// escaped would reach the unobserved-task handler and write a crash file, burning a
    /// crash-retention slot on what is normally a logout race. <c>CloseSession</c> raises
    /// <c>SessionClosed</c> and disposes the service immediately after, so a logout landing during
    /// the capabilities POST comes back as an <see cref="ObjectDisposedException"/> — which is not
    /// in <see cref="JellyfinService.ReportCapabilitiesAsync"/>'s own filter, and is
    /// benign.</para></summary>
    private async Task StartCoreAsync(string profileKey, JellyfinService service, SessionSocket socket)
    {
        try
        {
            var reported = await service.ReportCapabilitiesAsync().ConfigureAwait(false);
            // The await is long enough for a logout to have closed this profile in the meantime, and
            // starting a socket on a service that is on its way to Dispose leaks a reconnect ladder.
            lock (_lock)
                if (!_sockets.TryGetValue(profileKey, out var current) || !ReferenceEquals(current, socket))
                    return;
            AppLog.Info("session", $"event=live_start outcome=success session={AppLog.ShortHash(profileKey)} "
                + $"capabilities={(reported ? "reported" : "unreported")}");
            socket.Start();
        }
        catch (Exception ex)
        {
            // By type, never by message: an SDK failure can carry the request URL.
            AppLog.Detail("session", $"event=live_start outcome=failure "
                + $"session={AppLog.ShortHash(profileKey)} error={ex.GetType().Name}");
        }
    }
}
