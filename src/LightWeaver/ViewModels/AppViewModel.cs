using LightWeaver.Localization;
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
    private bool _accountOperationPending;

    public bool IsAccountOperationAvailable => !_accountOperationPending;

    // UI-thread gate shared by password login, Quick Connect and every switcher entry point.
    public bool TryBeginAccountOperation()
    {
        if (_accountOperationPending) return false;
        _accountOperationPending = true;
        OnPropertyChanged(nameof(IsAccountOperationAvailable));
        return true;
    }

    public void EndAccountOperation()
    {
        _accountOperationPending = false;
        OnPropertyChanged(nameof(IsAccountOperationAvailable));
    }

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

    /// <summary>Broadcast a refreshed item to any cached list views (see <see cref="ItemUserDataChanged"/>),
    /// and record it in the user-data ledger first so a view opened LATER can apply it too.</summary>
    public void NotifyItemUserDataChanged(MediaItem item)
    {
        RecordUserData(item);
        ItemUserDataChanged?.Invoke(item);
    }

    /// <summary>The fan-out above only reaches views that are alive in the history. A library
    /// opened after a toggle elsewhere renders its folder from the disk cache, which nobody told
    /// about the toggle, and shows the old badge until its identity sweep lands — on a busy LAN a
    /// visible flash, and for a folder that is never swept, until the entry is next rewritten. This
    /// ledger is the in-process memory of every update fanned out, so a cached render can apply
    /// the ones newer than its entry. Only the user-state fields are taken from it (see
    /// <see cref="ApplyUserDataLedger"/>); the entry's own metadata stays.
    /// <para>One profile at a time: ids from another server mean nothing here, so a different
    /// active profile clears it. Bounded, oldest first: past the cap the cache entry has almost
    /// certainly been rewritten by a sweep since.</para></summary>
    private readonly Dictionary<Guid, (MediaItem Item, DateTime AtUtc)> _userDataLedger = [];
    private string? _userDataLedgerProfile;
    private const int UserDataLedgerCapacity = 512;

    private void RecordUserData(MediaItem item)
    {
        if (ActiveSessionKey is not { } profileKey)
            return;
        if (_userDataLedgerProfile != profileKey)
        {
            _userDataLedger.Clear();
            _userDataLedgerProfile = profileKey;
        }
        _userDataLedger[item.Id] = (item, DateTime.UtcNow);
        if (_userDataLedger.Count > UserDataLedgerCapacity)
            _userDataLedger.Remove(_userDataLedger.MinBy(pair => pair.Value.AtUtc).Key);
    }

    /// <summary>Applies every ledger update NEWER than <paramref name="cachedAtUtc"/> to a list
    /// read from a cache written at that time. An update older than the entry is already in it, or
    /// was overtaken by the fresher read that wrote it, so it is left alone. Returns the input
    /// instances where nothing applies, so a caller's reference comparisons stay cheap.</summary>
    public List<MediaItem> ApplyUserDataLedger(IEnumerable<MediaItem> cached, DateTime cachedAtUtc,
        string? profileKey)
    {
        if (_userDataLedger.Count == 0 || profileKey is null || profileKey != _userDataLedgerProfile)
            return cached.ToList();
        return cached.Select(item =>
                _userDataLedger.TryGetValue(item.Id, out var update) && update.AtUtc > cachedAtUtc
                    ? item.WithUserData(update.Item.Played, update.Item.IsFavorite,
                        update.Item.ResumePositionTicks, update.Item.PlayedPercentage)
                    : item)
            .ToList();
    }

    /// <summary>
    /// The server reported that the ACTIVE profile's library changed, after the browse caches for
    /// it have been dropped. The argument is the folder ids the change names; an EMPTY list means
    /// the change could not be attributed to a folder and every live view should reload.
    /// </summary>
    public event Action<IReadOnlyList<Guid>>? LibraryChanged;

    /// <summary>Broadcast a library change to the live views (see <see cref="LibraryChanged"/>).</summary>
    public void NotifyLibraryChanged(IReadOnlyList<Guid> folderIds) => LibraryChanged?.Invoke(folderIds);

    /// <summary>
    /// Items the ACTIVE profile's server no longer has: either it said so (a LibraryChanged
    /// batch's removed ids) or it answered 404 when one was opened. Every live view drops the
    /// cards, and the views that own a cache entry drop them from it too. A set, not one id per
    /// event: a library scan can remove thousands at once and each view scans its list once.
    /// </summary>
    public event Action<IReadOnlySet<Guid>>? ItemsRemoved;

    /// <summary>Broadcast removed items to the live views (see <see cref="ItemsRemoved"/>).</summary>
    public void NotifyItemsRemoved(IReadOnlySet<Guid> itemIds)
    {
        if (itemIds.Count == 0)
            return;
        foreach (var id in itemIds)
            _userDataLedger.Remove(id);
        // Before the fan-out: a view's removal edit of a cache entry must find the tombstone
        // already there when a stale store checks for one after it (see StoreBrowseListAsync).
        RecordRemovals(itemIds);
        ItemsRemoved?.Invoke(itemIds);
    }

    /// <summary>The removal ledger: every item passed to <see cref="NotifyItemsRemoved"/>, with
    /// its time. The fan-out only edits what is on screen and in the cache at that moment; a
    /// request that started before the removal can still answer with the item afterwards, pass
    /// its view's generation check, and paint it back and store it again.
    /// <see cref="DropRemovedItems"/> filters such a response by the time its request started, and
    /// a cache entry by its <c>CachedAtUtc</c>, so a removal newer than the data wins and an item
    /// re-added under the same id later comes back from the next request.
    /// <para>Same scope as the user-data ledger: the active profile only (another one clears it),
    /// bounded, oldest first. A library scan removes thousands at once, so the cap is higher.
    /// Locked: cache stores check it off the UI thread.</para></summary>
    private readonly Dictionary<Guid, DateTime> _removalLedger = [];
    private string? _removalLedgerProfile;
    private readonly object _removalLedgerLock = new();
    private const int RemovalLedgerCapacity = 4096;

    private void RecordRemovals(IReadOnlySet<Guid> itemIds)
    {
        if (ActiveSessionKey is not { } profileKey)
            return;
        var now = DateTime.UtcNow;
        lock (_removalLedgerLock)
        {
            if (_removalLedgerProfile != profileKey)
            {
                _removalLedger.Clear();
                _removalLedgerProfile = profileKey;
            }
            foreach (var id in itemIds)
                _removalLedger[id] = now;
            var excess = _removalLedger.Count - RemovalLedgerCapacity;
            if (excess > 0)
                foreach (var id in _removalLedger.OrderBy(pair => pair.Value).Take(excess)
                             .Select(pair => pair.Key).ToList())
                    _removalLedger.Remove(id);
        }
    }

    /// <summary>The items of a list observed at <paramref name="observedAtUtc"/> (a request's start,
    /// or a cache entry's <c>CachedAtUtc</c>) that no removal newer than that names. Returns a new
    /// list in the same order, with the input instances.</summary>
    public List<MediaItem> DropRemovedItems(IEnumerable<MediaItem> items, DateTime observedAtUtc,
        string? profileKey)
    {
        lock (_removalLedgerLock)
        {
            if (_removalLedger.Count == 0 || profileKey is null || profileKey != _removalLedgerProfile)
                return items.ToList();
            return items.Where(item => !(_removalLedger.TryGetValue(item.Id, out var removedAtUtc)
                    && removedAtUtc > observedAtUtc))
                .ToList();
        }
    }

    /// <summary>The ids among <paramref name="items"/> that a removal newer than
    /// <paramref name="observedAtUtc"/> names: what a store of that list has to take out again.</summary>
    public HashSet<Guid> RemovedSince(IEnumerable<MediaItem> items, DateTime observedAtUtc, string? profileKey)
    {
        lock (_removalLedgerLock)
        {
            if (_removalLedger.Count == 0 || profileKey is null || profileKey != _removalLedgerProfile)
                return [];
            return items.Where(item => _removalLedger.TryGetValue(item.Id, out var removedAtUtc)
                    && removedAtUtc > observedAtUtc)
                .Select(item => item.Id)
                .ToHashSet();
        }
    }

    /// <summary>Stores an ordered browse list (a series' seasons, a season's episodes, a playlist,
    /// an album) fetched by a request that started at <paramref name="observedAtUtc"/>, which it
    /// is stamped with. Under the entry's write gate (<see cref="Imaging.MetadataCache.LockAsync"/>),
    /// like <see cref="RemoveFromBrowseListAsync"/>, and filtered against the removal ledger inside
    /// it: the ledger records a removal before its fan-out edits any entry, so that edit either ran
    /// before this write, which then already filters the item out, or waits for the gate and reads
    /// this write. A removal recorded while the write ran is taken out before the gate is released,
    /// for the list whose view has gone and so will not edit it.</summary>
    public async Task StoreBrowseListAsync(string cacheKey, List<MediaItem> items, DateTime observedAtUtc,
        string? profileKey)
    {
        using (await Imaging.MetadataCache.LockAsync(cacheKey).ConfigureAwait(false))
        {
            var kept = DropRemovedItems(items, observedAtUtc, profileKey);
            var refiltered = items.Count - kept.Count;
            await Imaging.MetadataCache.StoreAsync(cacheKey,
                new Imaging.BrowseCacheEntry<MediaItem>(kept, kept.Count, observedAtUtc)).ConfigureAwait(false);
            while (RemovedSince(kept, observedAtUtc, profileKey) is { Count: > 0 } late)
            {
                kept = kept.Where(item => !late.Contains(item.Id)).ToList();
                refiltered += late.Count;
                await Imaging.MetadataCache.StoreAsync(cacheKey,
                    new Imaging.BrowseCacheEntry<MediaItem>(kept, kept.Count, observedAtUtc)).ConfigureAwait(false);
            }
            if (refiltered > 0)
                Diagnostics.AppLog.Detail("library", $"event=cache_store outcome=refiltered count={refiltered}");
        }
    }

    /// <summary>Takes items out of an ordered browse list entry, keeping its stamp. The read and
    /// the write hold the entry's write gate, so two removals cannot each write back a list that
    /// still has the other's item.</summary>
    public static async Task RemoveFromBrowseListAsync(string cacheKey, IReadOnlySet<Guid> itemIds)
    {
        using (await Imaging.MetadataCache.LockAsync(cacheKey).ConfigureAwait(false))
        {
            var cached = await Imaging.MetadataCache.ReadAsync<Imaging.BrowseCacheEntry<MediaItem>>(cacheKey)
                .ConfigureAwait(false);
            if (cached?.Items is null)
                return;
            var kept = cached.Items.Where(item => !itemIds.Contains(item.Id)).ToList();
            if (kept.Count == cached.Items.Count)
                return;
            await Imaging.MetadataCache.StoreAsync(cacheKey, cached with { Items = kept, TotalCount = kept.Count })
                .ConfigureAwait(false);
        }
    }

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

    /// <summary>The warm session registered under a profile key, or null once it has been closed.
    /// The live session sockets resolve their service this way rather than holding a reference, so
    /// a logout that disposes the service cannot leave one pointing at it.</summary>
    public JellyfinService? FindSessionByKey(string key)
        => _sessions.TryGetValue(key, out var session) ? session : null;

    /// <summary>The connected session that owns an absolute asset URL — same scheme, host and port
    /// as its <c>ServerUrl</c> and under its path at a segment boundary, so a server hosted under a
    /// sub-path (<c>http://host/jellyfin</c>) resolves as well as a bare origin, and the longest
    /// match wins when two profiles differ only by path. Null when no warm session owns it.
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
            if (!s.IsConnected || s.ServerUrl is not { Length: > 0 } su || !ServerOwnsUrl(su, url))
                continue;
            if (su.Length > (best?.ServerUrl?.Length ?? 0))
                best = s;
        }
        return best;
    }

    /// <summary>True when the URL has the server's exact origin and sits under its base path.</summary>
    internal static bool ServerOwnsUrl(string serverUrl, string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var asset) && JellyfinService.OwnsUrl(serverUrl, asset);

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
        var service = Jellyfin;
        var result = await service.LoginAsync(serverUrl, username, password);
        if (!ReferenceEquals(Jellyfin, service))
            return L.Text("AppViewModel.LoginFailed.ad94f0a5");
        if (!result.Success)
            return result.Error ?? L.Text("AppViewModel.LoginFailed.ad94f0a5");
        Activate(result.Credentials!, service);
        State = AppState.Browse;
        return null;
    }

    private static readonly TimeSpan LogoutRevokeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Removes the active profile and its warm session; lands on the next
    /// saved profile (instant when warm) or the login form when none remain.</summary>
    public async Task LogoutAsync()
    {
        // Only the active profile goes; other saved servers/users stay switchable.
        if (ActiveSessionKey is null)
            CredentialStore.RemoveActive();
        else
            EndActiveSession();
        if (CredentialStore.Load() is { } next && await SwitchProfileAsync(next))
            return;
        ReplaceWithScratchSession();   // scratch for the login form; disposes the outgoing one (B27)
        State = AppState.Login;
    }

    /// <summary>Signs out of the active profile only and shows the login form, without moving on
    /// to another saved profile (v2: Login opens with this server preselected). Finishes locally
    /// before returning; the server revocation runs on behind it. Without an active session no
    /// saved profile is removed, so a repeated sign-out cannot take the next one with it.</summary>
    public void SignOut()
    {
        EndActiveSession();
        ReplaceWithScratchSession();
        State = AppState.Login;
    }

    /// <summary>Forgets the active session's profile and closes the session at once, so nothing can
    /// act on it afterwards. The revocation it starts (<see cref="LastRevocation"/>) carries the
    /// token captured beforehand and never holds up the local sign-out.</summary>
    private void EndActiveSession()
    {
        if (ActiveSessionKey is not { } key)
            return;
        CredentialStore.RemoveWhere(p => string.Equals(SessionKey(p), key, StringComparison.OrdinalIgnoreCase));
        SessionLog($"logout {key}");
        if (RevokesServerSessions && _sessions.TryGetValue(key, out var session))
            LastRevocation = session.RevokeServerSessionAsync(LogoutRevokeTimeout);
        CloseSession(key);
    }

    /// <summary>Whether a sign-out ends the session on the server too. Release only: the verify
    /// driver seeds the Debug profile with a COPY of the installed app's credentials.dat, so a
    /// Debug logout that revoked its token would sign the installed app out on the server too.</summary>
    internal bool RevokesServerSessions { get; init; } = !AppPaths.IsDebugEnvironment;

    /// <summary>The revocation the last sign-out started; it logs its own outcome and never
    /// throws. Completed when none was started.</summary>
    internal Task LastRevocation { get; private set; } = Task.CompletedTask;

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

    /// <summary>Reconnects the saved profile and activates it only if <paramref name="stillWanted"/>
    /// holds once the server has answered; playback stays on screen. True when restored, false when
    /// the server refused or could not be reached, null when nothing is saved or it is no longer wanted.</summary>
    public async Task<bool?> RestoreSavedAsync(Func<bool> stillWanted)
    {
        if (CredentialStore.Load() is not { } saved)
            return null;
        var service = new JellyfinService();
        if (!await service.ReconnectAsync(saved))
        {
            service.Dispose();
            return false;
        }
        if (!stillWanted())
        {
            service.Dispose();
            return null;
        }
        SessionLog($"restore {SessionKey(saved)}");
        Activate(saved, service);
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

    /// <summary>No usable session (a saved token was refused): the shell shows sign-in, and the
    /// saved profiles stay.</summary>
    public void RequireSignIn() => State = AppState.Login;

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
