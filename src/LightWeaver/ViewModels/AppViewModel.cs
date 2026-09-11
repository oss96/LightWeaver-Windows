using System.IO;
using LightWeaver.Jellyfin;

namespace LightWeaver.ViewModels;

public enum AppState
{
    Loading,
    Login,
    Browse,
    Playing,
}

/// <summary>
/// Top-level application state: which shell view is active and the Jellyfin sessions.
/// MainWindow listens to <see cref="StateChanged"/> and swaps views accordingly.
///
/// Warm multi-account sessions (Phase 7): every profile that has connected this run
/// keeps its <see cref="JellyfinService"/> alive in <see cref="_sessions"/>, keyed by
/// the same profile key the per-profile Home layout uses. Switching to a warm profile
/// swaps <see cref="Jellyfin"/> instantly (no network on the switch path) and
/// revalidates the token in the background; only never-connected profiles pay the
/// blocking reconnect.
/// </summary>
public sealed class AppViewModel : ObservableObject
{
    private AppState _state = AppState.Loading;

    private readonly Dictionary<string, JellyfinService> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The active profile's session. In the Login state this is a fresh
    /// "scratch" service so an in-progress login (or Quick Connect handshake) can
    /// never mutate a warm session's server URL/token.</summary>
    public JellyfinService Jellyfin { get; private set; } = new();

    /// <summary>Profile key of the active warm session; null while logged out.</summary>
    public string? ActiveSessionKey { get; private set; }

    /// <summary>The playback queue (Phase 5 M1); empty = single-item playback.</summary>
    public PlayQueue Queue { get; } = new();

    /// <summary>
    /// The in-flight playback-stop report, if any. Views that re-read UserData
    /// right after playback (resume position) await this so their GET can't
    /// outrun the stop POST. Publish through <see cref="PublishStopReport"/>.
    /// </summary>
    public Task? PendingStopReport { get; set; }

    /// <summary>
    /// Records an in-flight stop report. Outstanding reports <b>compose</b> rather than replace:
    /// a queue advance stops item A and starts B, so when B is later stopped A's POST may still be
    /// in flight, and a view awaiting "the pending report" has to wait for both or it can read
    /// A's pre-stop resume position back (B23). Assigning the field directly loses the earlier one
    /// — which is what four of the five stop sites effectively did by dropping the task entirely.
    /// </summary>
    public void PublishStopReport(Task? stop)
    {
        if (stop is null)
            return;
        PendingStopReport = PendingStopReport is { IsCompleted: false } prior
            ? Task.WhenAll(prior, stop)
            : stop;
    }

    /// <summary>
    /// Raised after the detail view toggles an item's watched/favorite state, carrying
    /// the refreshed <see cref="MediaItem"/>. Cached list views (LibraryView) replace the
    /// matching entry so their card badges update on back-navigation without a refetch.
    /// </summary>
    public event Action<MediaItem>? ItemUserDataChanged;

    /// <summary>Broadcast a refreshed item to any cached list views (see <see cref="ItemUserDataChanged"/>).</summary>
    public void NotifyItemUserDataChanged(MediaItem item) => ItemUserDataChanged?.Invoke(item);

    public event Action<AppState>? StateChanged;

    /// <summary>A session became active (warm or freshly connected). Raised BEFORE the
    /// state bounce so the shell can swap its per-profile UI caches first.</summary>
    public event Action<string>? SessionActivated;

    /// <summary>A warm session was closed (logout / invalidated token) — the shell
    /// drops its cached views for the key.</summary>
    public event Action<string>? SessionClosed;

    /// <summary>A warm session's background revalidation found the token revoked.
    /// Carries the profile's display name; the shell surfaces a toast.</summary>
    public event Action<string>? SessionExpired;

    public AppState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                StateChanged?.Invoke(value);
        }
    }

    /// <summary>One key format app-wide: the HomeLayoutStore per-profile key.</summary>
    public static string SessionKey(SavedCredentials c)
        => Settings.HomeLayoutStore.ProfileKey(c.UserId, c.ServerUrl);

    /// <summary>Whether switching to this profile would be instant (no reconnect).</summary>
    public bool IsSessionWarm(SavedCredentials c) => _sessions.ContainsKey(SessionKey(c));

    /// <summary>The connected session for a server URL — active first, then any warm
    /// one. Lets in-flight downloads keep their originating server's auth across
    /// profile switches. (Profiles sharing a server URL share whichever session is
    /// found first; item access is equivalent for download purposes.)</summary>
    public JellyfinService? FindSessionByServer(string serverUrl)
    {
        if (Jellyfin.IsConnected
            && string.Equals(Jellyfin.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
            return Jellyfin;
        return _sessions.Values.FirstOrDefault(s => s.IsConnected
            && string.Equals(s.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The connected session that owns an absolute asset URL — matched by
    /// <c>ServerUrl</c> PREFIX, so a server hosted under a sub-path (<c>http://host/jellyfin</c>)
    /// resolves as well as a bare origin, and the longest match wins when two profiles differ only
    /// by path. Null when no warm session owns it.
    ///
    /// <para>Exists for the image cache (B14): images were fetched with whichever profile was
    /// ACTIVE, so with two servers warm an uncached poster from server A requested while profile B
    /// was active went to A carrying B's token — 401, and a failed fetch caches nothing, so the
    /// card stayed blank and re-failed every time. Playback pins via <c>_playbackJf</c> and
    /// downloads via <see cref="FindSessionByServer"/>; this is the same rule for assets.</para></summary>
    public JellyfinService? FindSessionForUrl(string url)
    {
        JellyfinService? best = null;
        foreach (var s in _sessions.Values.Append(Jellyfin))
        {
            if (!s.IsConnected || s.ServerUrl is not { Length: > 0 } su
                || !url.StartsWith(su, StringComparison.OrdinalIgnoreCase))
                continue;
            if (su.Length > (best?.ServerUrl?.Length ?? 0))
                best = s;
        }
        return best;
    }

    /// <summary>Registers the service as the profile's warm session and makes it
    /// active. The credential save keeps the on-disk ActiveIndex in step.</summary>
    private void Activate(SavedCredentials profile, JellyfinService service)
    {
        var key = SessionKey(profile);
        _sessions[key] = service;
        Jellyfin = service;
        ActiveSessionKey = key;
        CredentialStore.Save(profile);
        SessionActivated?.Invoke(key);
    }

    private void CloseSession(string key)
    {
        if (!_sessions.Remove(key, out var closed))
            return;
        if (ReferenceEquals(closed, Jellyfin))
            ActiveSessionKey = null;
        closed.Logout();
        // Raise BEFORE disposing (B28). SessionClosed fires synchronously, and while it runs the
        // Jellyfin property can still reference `closed` — both callers reassign it only after this
        // returns. Disposing first meant any handler that touched _app.Jellyfin would hit a disposed
        // HttpClient. Logout() has already cleared the token, so what handlers see is a
        // signed-out-but-usable service, which is the accurate state. Nothing subscribed does touch
        // it today; the point is that the invariant no longer depends on that staying true.
        SessionClosed?.Invoke(key);
        closed.Dispose();
    }

    /// <summary>Swaps in a fresh scratch service for the login form, disposing the outgoing one if
    /// it was itself a scratch (i.e. never promoted to a warm session).
    ///
    /// <para>Every <see cref="JellyfinService"/> owns an HttpClient over a SocketsHttpHandler, and
    /// only warm sessions were ever disposed — via <see cref="CloseSession"/>. A scratch service
    /// whose login was abandoned was simply dropped, so each "Add server… then back out" leaked a
    /// connection pool to finalization (B27).</para></summary>
    private void ReplaceWithScratchSession()
    {
        var outgoing = Jellyfin;
        Jellyfin = new JellyfinService();
        // Identity check against the warm set: a service that IS a warm session belongs to
        // _sessions and is CloseSession's to dispose, not ours.
        if (!_sessions.Values.Any(s => ReferenceEquals(s, outgoing)))
            outgoing.Dispose();
    }

    /// <summary>Try to restore the previous session; lands on Browse or Login.</summary>
    public async Task StartupAsync()
    {
        var saved = CredentialStore.Load();
        if (saved is not null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var service = new JellyfinService();
            if (await service.ReconnectAsync(saved))
            {
                SessionLog($"cold-connect {SessionKey(saved)} {sw.ElapsedMilliseconds}ms");
                Activate(saved, service);
                State = AppState.Browse;
                return;
            }
            service.Dispose();
        }
        State = AppState.Login;
    }

    public async Task<string?> LoginAsync(string serverUrl, string username, string password)
    {
        var result = await Jellyfin.LoginAsync(serverUrl, username, password);
        if (!result.Success)
            return result.Error ?? "Login failed.";
        Activate(result.Credentials!, Jellyfin);
        State = AppState.Browse;
        return null;
    }

    /// <summary>Removes the active profile and its warm session; lands on the next
    /// saved profile (instant when warm) or the login form when none remain.</summary>
    public async Task LogoutAsync()
    {
        // Only the active profile goes; other saved servers/users stay switchable.
        CredentialStore.RemoveActive();
        if (ActiveSessionKey is { } key)
        {
            SessionLog($"logout {key}");
            CloseSession(key);
        }
        if (CredentialStore.Load() is { } next && await SwitchProfileAsync(next))
            return;
        ReplaceWithScratchSession();   // scratch for the login form; disposes the outgoing one (B27)
        State = AppState.Login;
    }

    /// <summary>Finishes a login that happened outside LoginAsync (Quick Connect).</summary>
    public void CompleteExternalLogin(SavedCredentials credentials)
    {
        Activate(credentials, Jellyfin);
        State = AppState.Browse;
    }

    /// <summary>Switches to a saved profile. A warm session activates instantly (its
    /// token revalidates in the background); a cold one validates first, as before.
    /// The state is bounced through Loading so Browse re-raises even when already
    /// browsing — unless playback is running, which keeps playing under the profile
    /// that started it (the browse layer swaps beneath and shows on return).</summary>
    public async Task<bool> SwitchProfileAsync(SavedCredentials profile)
    {
        var key = SessionKey(profile);
        if (_sessions.TryGetValue(key, out var warm))
        {
            SessionLog($"warm-activate {key}");
            Activate(profile, warm);
            BounceToBrowse();
            _ = RevalidateAsync(key, warm, profile);
            return true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var service = new JellyfinService();
        if (!await service.ReconnectAsync(profile))
        {
            service.Dispose();
            return false;
        }
        SessionLog($"cold-connect {key} {sw.ElapsedMilliseconds}ms");
        Activate(profile, service);
        BounceToBrowse();
        return true;
    }

    private void BounceToBrowse()
    {
        if (State == AppState.Playing)
            return;   // playback continuity: the browse layer shows on return
        State = AppState.Loading;
        State = AppState.Browse;
    }

    /// <summary>Background token check after a warm activate — the ReconnectAsync
    /// semantics without blocking the switch. A revoked token closes the session and
    /// falls back to the login form (only when that profile is still the active one).</summary>
    private async Task RevalidateAsync(string key, JellyfinService session, SavedCredentials profile)
    {
        var valid = await session.ValidateAsync();
        SessionLog(valid ? $"revalidate-ok {key}" : $"revalidate-invalid {key}");
        if (valid || ActiveSessionKey != key)
        {
            if (!valid)
                CloseSession(key);   // no longer active: drop it quietly
            return;
        }
        CloseSession(key);
        SessionExpired?.Invoke(profile.ServerName ?? profile.ServerUrl);
        ReplaceWithScratchSession();
        State = AppState.Login;
    }

    /// <summary>Shows the login form without touching saved profiles ("Add server…").
    /// The scratch service keeps the handshake off the warm sessions.</summary>
    public void BeginAddServer()
    {
        ReplaceWithScratchSession();
        ActiveSessionKey = null;
        State = AppState.Login;
    }

    public void EnterPlayback() => State = AppState.Playing;

    public void LeavePlayback() => State = AppState.Browse;

    /// <summary>Test hook: LIGHTWEAVER_SESSION_LOG=&lt;path&gt; appends switch/lifecycle
    /// lines (warm-activate / cold-connect / revalidate / logout) for the verify suite.</summary>
    private static void SessionLog(string line)
    {
        // The app's own diagnostics get every one of these regardless of the env hook: session
        // lifecycle is the single most useful context for a later crash file ("it died just after
        // a profile switch"), and the ring is memory-only so this costs nothing on a clean run.
        // Note the ordering — the env-var test-hook below returns early, so an AppLog call placed
        // after it would only ever run under the test suite.
        Diagnostics.AppLog.Info("session", StructuralSessionLine(line));
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_SESSION_LOG") is not { Length: > 0 } path)
            return;
        try
        {
            File.AppendAllText(path, FormattableString.Invariant(
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"));
        }
        catch (IOException)
        {
            // logging is best-effort
        }
    }

    /// <summary>
    /// The env-hook line above is a legacy test protocol that includes its profile key
    /// (<c>{userId:N}@{serverUrl}</c>). The product log gets the same lifecycle meaning without the
    /// key — but no longer without an IDENTITY: dropping it entirely (M2) made a multi-profile
    /// session log unreadable, because <c>warm_activate</c> / <c>revalidate_invalid</c> pairs could
    /// not be attributed to a profile and a switch back and forth looked like the same event twice.
    ///
    /// <para><c>session=</c> is <see cref="Diagnostics.AppLog.ShortHash"/> of that key: stable for
    /// the life of the profile, distinct per profile, and carrying neither the server URL nor the
    /// user id. The same token appears on the Home-layout records, which are keyed by the same
    /// string.</para>
    /// </summary>
    private static string StructuralSessionLine(string line)
    {
        var firstSpace = line.IndexOf(' ');
        var name = (firstSpace < 0 ? line : line[..firstSpace]).Replace('-', '_');
        var elapsed = System.Text.RegularExpressions.Regex.Match(line, @"\s(?<ms>\d+)ms$");
        // Everything between the event name and an optional trailing "<n>ms" is the profile key.
        var keyEnd = elapsed.Success ? elapsed.Index : line.Length;
        var key = firstSpace < 0 || firstSpace + 1 >= keyEnd
            ? string.Empty
            : line[(firstSpace + 1)..keyEnd];
        var session = Diagnostics.AppLog.ShortHash(key.Trim());
        return elapsed.Success
            ? $"event={name} session={session} elapsed_ms={elapsed.Groups["ms"].Value}"
            : $"event={name} session={session}";
    }
}
