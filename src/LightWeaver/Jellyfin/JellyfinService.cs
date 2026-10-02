using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions;

namespace LightWeaver.Jellyfin;

public sealed record LoginResult(bool Success, string? Error, SavedCredentials? Credentials);

/// <summary>An external (sidecar) subtitle stream, resolved to an absolute URL for mpv.</summary>
public sealed record ExternalSubtitle(string Url, string? Title, string? Language);

/// <summary>One label/value line of an item's media metadata (info panel).</summary>
public sealed record MediaInfoField(string Label, string Value);

/// <summary>One selectable stream of the item's default media source. Index is
/// Jellyfin's global stream index (spans all types); TypeOrdinal is the 0-based
/// position among the EMBEDDED streams of the same type (-1 for external streams),
/// which is what maps onto mpv's per-type 1-based track ids.</summary>
public sealed record MediaStreamChoice(int Index, int TypeOrdinal, string Display,
    string? Language, string? Title, bool IsDefault, bool IsExternal)
{
    public override string ToString() => Display;
}

/// <summary>Selectable streams + summary fields (resolution/aspect/codec/native HDR)
/// of the item's default media source, for the detail view's preselection UI.</summary>
public sealed record MediaSourceStreams(
    string? SourceId,
    List<MediaStreamChoice> Video,
    List<MediaStreamChoice> Audio,
    List<MediaStreamChoice> Subtitles,
    List<MediaInfoField> Summary);

/// <summary>One trickplay resolution bucket (a grid of thumbnails per JPEG sheet).</summary>
public sealed record TrickplayInfo(string MediaSourceId, int Width, int Height,
    int TileWidth, int TileHeight, int ThumbnailCount, int IntervalMs);

/// <summary>One row of a folder identity sweep: everything the incremental browse refresh needs
/// to decide whether a cached item is still current, and nothing else. <see cref="Etag"/> is the
/// server's change token (null when the server does not issue one for the item).</summary>
public sealed record SweepRow(Guid Id, string? Etag, bool Played, bool IsFavorite,
    long ResumePositionTicks, double? PlayedPercentage);

/// <summary>Outcome of playback negotiation: the URL mpv should play plus the
/// server-issued identifiers the progress reports must carry.</summary>
public sealed record PlaybackDecision(string Url, bool IsTranscode, string PlaySessionId,
    string? MediaSourceId, int? AudioStreamIndex = null, int? SubtitleStreamIndex = null,
    bool SubtitleIsBurnedIn = false);

/// <summary>One row of the download resolution picker (Phase 7 M20): the original
/// source (exact size) or a transcode tier (estimated from bitrate × runtime).</summary>
public sealed record DownloadOption(string Label, string? MediaSourceId, bool IsOriginal,
    int? MaxWidth, int? VideoBitRate, long? EstimatedSizeBytes, string? Container, string Resolution);

/// <summary>One alternate version (media source) of an item. Movies have carried several for
/// years; Jellyfin 12 extends them to episodes. <see cref="Label"/> is derived once, at
/// construction; <see cref="Container"/>, <see cref="Width"/> and <see cref="Height"/> are the
/// raw source facts it was derived FROM, carried so a caller can re-label without a second
/// PlaybackInfo round-trip. The picker itself reads only the label plus
/// <see cref="SizeBytes"/>/<see cref="Bitrate"/>, which are what tell two same-named versions
/// apart.</summary>
public sealed record MediaVersion(string Id, string Label, string? Container, int? Bitrate,
    int? Width, int? Height, long? SizeBytes, bool IsDefault)
{
    /// <summary>Right-hand disambiguator: exact size when the server knows it, else the source
    /// bitrate, else nothing. A METHOD, not a computed property: this record is stored in the
    /// metadata disk cache, and a public getter would be serialized into every cached entry.</summary>
    public string Detail()
        => SizeBytes is { } size
            ? Downloads.DownloadManager.FormatBytes(size)
            : Bitrate is > 0
                ? FormattableString.Invariant($"{Bitrate.Value / 1_000_000.0:0.#} Mbps")
                : "";
}

/// <summary>
/// Wraps the Kiota-generated Jellyfin SDK client: connection lifecycle (login,
/// startup reconnect with token validation, logout) and the base URL / auth token
/// needed to hand stream URLs to mpv. Data queries live here too so views never
/// touch the SDK types directly.
/// </summary>
public sealed class JellyfinService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>"No cap" as an explicit number: omitting MaxStreamingBitrate makes the server
    /// fall back to a ~8 Mbps default and transcode everything (verified against Jellyfin 10.11).</summary>
    private const int UnlimitedBitrateBps = 1_000_000_000;
#if DEBUG
    private static int _downloadOptionsFailOnceConsumed;
#endif

    private readonly JellyfinSdkSettings _settings;
    private readonly JellyfinApiClient _client;
    private readonly HttpClient _httpClient;
    /// <summary>Kept because the transcode-teardown route is keyed by device + play session,
    /// and the SDK settings do not hand the device id back out once initialized.</summary>
    private readonly string _deviceId = CredentialStore.GetOrCreateDeviceId();

    public string? ServerUrl { get; private set; }
    public string? AccessToken { get; private set; }
    public Guid UserId { get; private set; }

    public bool IsConnected => AccessToken is not null;

    /// <summary>Header value mpv uses to authenticate stream requests (token stays out of URLs).</summary>
    public string AuthorizationHeader => $"MediaBrowser Token=\"{AccessToken}\"";

    public JellyfinApiClient Client => _client;

    public JellyfinService()
    {
        _settings = new JellyfinSdkSettings();
        _settings.Initialize(
            "LightWeaver",
            typeof(JellyfinService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            Environment.MachineName,
            _deviceId);

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // No proxy: Windows WPAD auto-detection (enabled by default) can stall
            // first requests ~20s when discovery fails. Media servers are LAN-direct.
            UseProxy = false,
            // Measured live: simultaneous connection attempts to the server
            // intermittently lose SYNs and ride the ~21s retransmit ladder. Cut
            // stalled connects fast (paired with WithRetry below) and keep pooled
            // connections warm so bursts reuse instead of reconnecting.
            ConnectTimeout = TimeSpan.FromSeconds(4),
            MaxConnectionsPerServer = 4,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = RequestTimeout,
        };

        _client = new JellyfinApiClient(
            new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(_settings), _settings, _httpClient));
    }

    public async Task<LoginResult> LoginAsync(string serverUrl, string username, string password)
    {
        var url = NormalizeUrl(serverUrl);
        _settings.SetServerUrl(url);
        try
        {
            var auth = await _client.Users.AuthenticateByName.PostAsync(new AuthenticateUserByName
            {
                Username = username,
                Pw = password,
            }).ConfigureAwait(false);

            if (auth?.AccessToken is null || auth.User?.Id is null)
                return new LoginResult(false, "Server returned an incomplete login response.", null);

            ApplySession(url, auth.AccessToken, auth.User.Id.Value);
            var credentials = new SavedCredentials(url, username, auth.AccessToken, auth.User.Id.Value,
                await GetServerNameAsync().ConfigureAwait(false));
            return new LoginResult(true, null, credentials);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("jellyfin", "login failed", ex);
            return new LoginResult(false, FriendlyError(ex), null);
        }
    }

    /// <summary>Display name of the connected server (profile switcher); best-effort.</summary>
    private async Task<string?> GetServerNameAsync()
    {
        try
        {
            var info = await _client.System.Info.Public.GetAsync().ConfigureAwait(false);
            return info?.ServerName;
        }
        catch
        {
            return null;
        }
    }

    // ---- Quick Connect (Phase 5 M7) ----

    // The normalized URL of the in-flight Quick Connect handshake (ServerUrl is
    // only set once a session is established).
    private string? _quickConnectUrl;

    /// <summary>Starts a Quick Connect handshake against the server. Null when the
    /// server has QC disabled or is unreachable.</summary>
    public async Task<(string Code, string Secret)?> InitiateQuickConnectAsync(string serverUrl)
    {
        var url = NormalizeUrl(serverUrl);
        _settings.SetServerUrl(url);
        _quickConnectUrl = url;
        try
        {
            var enabled = await _client.QuickConnect.Enabled.GetAsync().ConfigureAwait(false);
            if (enabled != true)
                return null;
            var result = await _client.QuickConnect.Initiate.PostAsync().ConfigureAwait(false);
            return result is { Code.Length: > 0, Secret.Length: > 0 }
                ? (result.Code, result.Secret)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One Quick Connect poll: null while the code is still unauthorized;
    /// a LoginResult once it is approved (success) or expired (failure).</summary>
    public async Task<LoginResult?> PollQuickConnectAsync(string secret)
    {
        QuickConnectResult? state;
        try
        {
            state = await _client.QuickConnect.Connect.GetAsync(cfg =>
                cfg.QueryParameters.Secret = secret).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return new LoginResult(false, "The Quick Connect code expired. Try again.", null);
        }
        catch
        {
            return null;   // transient network hiccup — keep polling
        }
        if (state?.Authenticated != true)
            return null;

        try
        {
            var auth = await _client.Users.AuthenticateWithQuickConnect.PostAsync(new QuickConnectDto
            {
                Secret = secret,
            }).ConfigureAwait(false);
            if (auth?.AccessToken is null || auth.User?.Id is null || _quickConnectUrl is not { } url)
                return new LoginResult(false, "Server returned an incomplete login response.", null);
            ApplySession(url, auth.AccessToken, auth.User.Id.Value);
            var credentials = new SavedCredentials(url, auth.User.Name ?? "user",
                auth.AccessToken, auth.User.Id.Value, await GetServerNameAsync().ConfigureAwait(false));
            return new LoginResult(true, null, credentials);
        }
        catch (Exception ex)
        {
            return new LoginResult(false, FriendlyError(ex), null);
        }
    }

    /// <summary>Restores a saved session and validates the token with a real request.</summary>
    public async Task<bool> ReconnectAsync(SavedCredentials saved)
    {
        _settings.SetServerUrl(saved.ServerUrl);
        _settings.SetAccessToken(saved.AccessToken);
        try
        {
            var me = await _client.Users.Me.GetAsync().ConfigureAwait(false);
            if (me?.Id is null)
                return false;
            ApplySession(saved.ServerUrl, saved.AccessToken, me.Id.Value);
            return true;
        }
        catch (Exception ex)
        {
            // The documented silent failure: a dead token drops the app back to the login form
            // with no visible cause, and every server-dependent test suite then reports the same
            // uninformative "Home never loaded". This line is the difference between diagnosing
            // it and guessing at the network (see the pipeline notes in CLAUDE.md).
            Diagnostics.AppLog.Error("jellyfin",
                "saved session was rejected; falling back to the login form", ex);
            _settings.SetAccessToken(null);
            return false;
        }
    }

    /// <summary>Probes whether the established session's token is still accepted
    /// (warm-switch background revalidation). Never mutates session state.</summary>
    public async Task<bool> ValidateAsync()
    {
        if (!IsConnected)
            return false;
        try
        {
            var me = await _client.Users.Me.GetAsync().ConfigureAwait(false);
            return me?.Id is not null;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            return false;   // genuinely revoked/expired
        }
        catch
        {
            return true;    // transient network trouble is not an invalid token
        }
    }

    /// <summary>Tells the server what this client can be asked to do, so it appears as a "Play on"
    /// target and routes remote commands at the session the live socket rides. Best-effort by
    /// contract — every failure collapses to false, because capabilities are an enhancement and
    /// must never be able to block or undo a login.
    ///
    /// <para><see cref="ClientCapabilitiesDto.SupportedCommands"/> carries only real
    /// <c>GeneralCommandType</c> values. Play/Pause/Stop/Seek are <c>PlaystateCommand</c>s — a
    /// different enum, delivered on the <c>Playstate</c> socket message and enabled by
    /// <c>SupportsMediaControl</c> instead. Naming them here does not compile, and would not have
    /// turned them on if it did.</para></summary>
    public async Task<bool> ReportCapabilitiesAsync()
    {
        if (!IsConnected)
            return false;
        try
        {
            await _client.Sessions.Capabilities.Full.PostAsync(new ClientCapabilitiesDto
            {
                PlayableMediaTypes = [MediaType.Video, MediaType.Audio],
                SupportsMediaControl = true,
                // The server keys a remembered session off the device id rather than minting a new
                // one per connection, which is what lets a "play on" target survive a restart.
                SupportsPersistentIdentifier = true,
                SupportedCommands =
                [
                    GeneralCommandType.SetVolume,
                    GeneralCommandType.VolumeUp,
                    GeneralCommandType.VolumeDown,
                    GeneralCommandType.Mute,
                    GeneralCommandType.Unmute,
                    GeneralCommandType.ToggleMute,
                    GeneralCommandType.SetAudioStreamIndex,
                    GeneralCommandType.SetSubtitleStreamIndex,
                    GeneralCommandType.PlayNext,
                    GeneralCommandType.DisplayMessage,
                    GeneralCommandType.ToggleFullscreen,
                    GeneralCommandType.Play,
                ],
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or OperationCanceledException)
        {
            Diagnostics.AppLog.Detail("session", $"event=capabilities outcome=failure error={ex.GetType().Name}");
            return false;
        }
    }

    public void Logout()
    {
        AccessToken = null;
        ServerUrl = null;
        UserId = Guid.Empty;
        _settings.SetAccessToken(null);
    }

    /// <summary>Closed warm sessions release their socket pool (one HttpClient per
    /// profile session since the warm-switch rework).</summary>
    public void Dispose() => _httpClient.Dispose();

    // ---- Data queries (views consume MediaItem, never SDK DTOs) ----

    /// <summary>Retries once on transport-level stalls (timeout / connect failure) —
    /// the LAN sometimes drops the first SYN burst; the retry lands on a warm pool.
    ///
    /// <para><b>Reads only, or mutations that are idempotent in effect.</b> The retry trigger
    /// includes <see cref="TaskCanceledException"/>, which is also what the 30 s
    /// <see cref="RequestTimeout"/> raises — so a request that REACHED the server and merely
    /// answered slowly gets sent a second time. That is harmless for a GET, and harmless for
    /// the played/favorite POSTs (setting watched twice leaves it watched), but it is not
    /// harmless for anything that allocates server-side state per call: see
    /// <see cref="NegotiatePlaybackAsync"/>, which is deliberately NOT wrapped (B12).</para></summary>
    private static async Task<T> WithRetry<T>(Func<Task<T>> op,
        [CallerMemberName] string caller = "")
    {
        var operation = OperationToken(caller);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await op().ConfigureAwait(false);
            RequestDetail(operation, "success", started, attempts: 1);
            return result;
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            // The silent retry gets its own greppable record: without one, a LAN that drops the
            // first SYN of every burst looks identical to a healthy one in the log.
            RetryDetail(operation, ex, started);
            try
            {
                var result = await op().ConfigureAwait(false);
                RequestDetail(operation, "success", started, attempts: 2);
                return result;
            }
            catch (Exception retried)
            {
                RequestDetail(operation, "failure", started, attempts: 2, retried);
                throw;
            }
        }
        catch (Exception ex)
        {
            RequestDetail(operation, "failure", started, attempts: 1, ex);
            throw;
        }
    }

    /// <summary>Cancellation-aware read retry. A caller cancellation is terminal: it must
    /// never be mistaken for the transport timeout that the ordinary read helper retries.</summary>
    private static async Task<T> WithRetry<T>(Func<CancellationToken, Task<T>> op,
        CancellationToken cancellationToken, [CallerMemberName] string caller = "")
    {
        var operation = OperationToken(caller);
        cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await op(cancellationToken).ConfigureAwait(false);
            RequestDetail(operation, "success", started, attempts: 1);
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is TaskCanceledException or HttpRequestException)
        {
            // After the cancellation re-check, not before: a cancellation observed here means the
            // retry never fires, and a retry record for an attempt that did not happen is worse
            // than no record at all. The terminal record has to be written HERE, though — a throw
            // out of a catch block is seen by no sibling clause, so leaving it to
            // ThrowIfCancellationRequested alone produced a call with no record at all.
            if (cancellationToken.IsCancellationRequested)
            {
                RequestDetail(operation, "cancelled", started, attempts: 1, ex);
                cancellationToken.ThrowIfCancellationRequested();
            }
            RetryDetail(operation, ex, started);
            try
            {
                var result = await op(cancellationToken).ConfigureAwait(false);
                RequestDetail(operation, "success", started, attempts: 2);
                return result;
            }
            catch (Exception retried)
            {
                RequestDetail(operation,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "failure",
                    started, attempts: 2, retried);
                throw;
            }
        }
        catch (Exception ex)
        {
            RequestDetail(operation,
                cancellationToken.IsCancellationRequested ? "cancelled" : "failure",
                started, attempts: 1, ex);
            throw;
        }
    }

    /// <summary>One record per SERVICE CALL, naming the operation, its outcome, its wall time and
    /// how many attempts it took. Deliberately not the same layer as a view owner's
    /// <c>event=load</c> aggregate: that one says a screen filled, this one says which request paid
    /// for it and whether the transport misbehaved.
    ///
    /// <para>The exception is reduced to its TYPE. A response body or a socket message can carry a
    /// URL, and this record exists on the path that touches the server on every screen.</para></summary>
    private static void RequestDetail(string operation, string outcome,
        System.Diagnostics.Stopwatch started, int attempts, Exception? ex = null)
    {
        // The flag check precedes formatting: this runs on every server call, not just failing ones.
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant(
            $"event=request op={operation} outcome={outcome} elapsed_ms={started.ElapsedMilliseconds} attempts={attempts}");
        if (ex is not null)
            line += $" error={ex.GetType().Name}";
        Diagnostics.AppLog.Detail("jellyfin", line);
    }

    private static void RetryDetail(string operation, Exception cause,
        System.Diagnostics.Stopwatch started)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        Diagnostics.AppLog.Detail("jellyfin", FormattableString.Invariant(
            $"event=retry op={operation} reason={cause.GetType().Name} elapsed_ms={started.ElapsedMilliseconds} attempt=2"));
    }

    /// <summary>`GetResumePagedAsync` → `get_resume_paged`. Cached because
    /// <see cref="CallerMemberNameAttribute"/> hands the same handful of names to every call on
    /// every screen, and the log convention is lower_snake.</summary>
    private static readonly ConcurrentDictionary<string, string> OperationTokens = new();

    private static string OperationToken(string member)
        => OperationTokens.GetOrAdd(member, static name =>
        {
            if (name.EndsWith("Async", StringComparison.Ordinal))
                name = name[..^"Async".Length];
            var token = new StringBuilder(name.Length + 4);
            foreach (var c in name)
            {
                if (char.IsUpper(c) && token.Length > 0)
                    token.Append('_');
                token.Append(char.ToLowerInvariant(c));
            }
            return token.Length == 0 ? "unknown" : token.ToString();
        });

    /// <summary>Library tiles (movies, shows, …).</summary>
    public async Task<List<MediaItem>> GetLibrariesAsync()
    {
        var result = await WithRetry(() => _client.UserViews.GetAsync()).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>The kinds the two Continue Watching queries ask for. Deliberately the same set
    /// as <see cref="MediaItem.IsPlayable"/>, and shared by both queries so the rail and its
    /// see-all view can never drift apart. AudioBook, Trailer, Book and Recording are absent
    /// because <see cref="MediaItem.IsPlayable"/> excludes them, not by separate judgement — a
    /// card the rail offers but a click cannot play is the same defect in a different costume.
    /// </summary>
    /// <remarks>
    /// <para>This is load-bearing, not tidiness. <c>/UserItems/Resume</c> takes an OPT-IN type
    /// filter with no default, so an unconstrained call means "anything the server considers in
    /// progress" — and since Jellyfin 12 that includes CONTAINERS: a Season or Series whose
    /// children are partly watched is in progress by <c>PlayedPercentage</c>, with
    /// <c>PlaybackPositionTicks</c> still 0. Measured against Tower 12.0.0: the unconstrained
    /// query returned 19 items, of which 9 were Seasons and 9 were Series and exactly 1 was an
    /// Episode. Those containers rendered as poster tiles with no progress bar that navigate
    /// instead of playing, and duplicated the Next Up rail, which already listed every one of
    /// those series as a proper episode.</para>
    /// <para>The filter must go in the REQUEST, never in a post-filter over the response.
    /// <see cref="GetResumePagedAsync"/> feeds SectionView, which asks for the next page at
    /// <c>startIndex = items.Count</c> and trusts the server's <c>TotalRecordCount</c>; dropping
    /// items client-side would make the server re-serve them as duplicates, compounding page
    /// after page, and would leave the "N items" label counting rows the grid never shows. The
    /// server applies this filter to the count as well — the same measurement returned
    /// <c>TotalRecordCount</c> 19 unconstrained and 1 constrained — so paging stays
    /// consistent.</para>
    /// </remarks>
    private static readonly BaseItemKind[] ResumeItemTypes =
        [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video,
         BaseItemKind.MusicVideo, BaseItemKind.Audio];

    /// <summary>Continue-watching row. Leaf items only — see <see cref="ResumeItemTypes"/>.</summary>
    public async Task<List<MediaItem>> GetResumeItemsAsync(int limit = 12)
    {
        var result = await WithRetry(() => _client.UserItems.Resume.GetAsync(cfg =>
        {
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.IncludeItemTypes = ResumeItemTypes;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Next episodes of shows in progress.</summary>
    public async Task<List<MediaItem>> GetNextUpAsync(int limit = 12)
    {
        var result = await WithRetry(() => _client.Shows.NextUp.GetAsync(cfg =>
        {
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Recently added across all libraries (flattened, newest first per library).</summary>
    public async Task<List<MediaItem>> GetLatestAsync(IEnumerable<Guid> libraryIds, int limitPerLibrary = 16)
    {
        var all = new List<MediaItem>();
        foreach (var libraryId in libraryIds)
        {
            try
            {
                var items = await WithRetry(() => _client.Items.Latest.GetAsync(cfg =>
                {
                    cfg.QueryParameters.ParentId = libraryId;
                    cfg.QueryParameters.Limit = limitPerLibrary;
                    cfg.QueryParameters.Fields = [ItemFields.Overview];
                })).ConfigureAwait(false);
                if (items is not null)
                    all.AddRange(Map(items));
            }
            catch
            {
                // one broken library must not empty the whole row
            }
        }
        return all;
    }

    /// <summary>Paged Continue Watching — the full see-all view behind the Home rail.
    /// Server order (most recently played first), like the rail.</summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetResumePagedAsync(int startIndex, int limit)
    {
        var result = await WithRetry(() => _client.UserItems.Resume.GetAsync(cfg =>
        {
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.EnableTotalRecordCount = true;
            cfg.QueryParameters.IncludeItemTypes = ResumeItemTypes;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Paged Next Up (see-all view). The dedicated endpoint pages natively.</summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetNextUpPagedAsync(int startIndex, int limit)
    {
        var result = await WithRetry(() => _client.Shows.NextUp.GetAsync(cfg =>
        {
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.EnableTotalRecordCount = true;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Paged Recently Added (see-all view). The rail's /Items/Latest endpoint
    /// cannot page (no StartIndex, no total), so the full view is the equivalent Items
    /// query: newest first, server-wide, movies/series/episodes.</summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetLatestPagedAsync(int startIndex, int limit)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];
            cfg.QueryParameters.SortBy = [ItemSortBy.DateCreated];
            cfg.QueryParameters.SortOrder = [SortOrder.Descending];
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Paged Favorites (see-all view). Same query as the rail, paged.</summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetFavoritesPagedAsync(int startIndex, int limit)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filters = [ItemFilter.IsFavorite];
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];
            cfg.QueryParameters.SortBy = [ItemSortBy.SortName];
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Paged children of a library/folder with sort/filter/genre/letter options
    /// (Phase 5 M3). Defaults reproduce the classic name-sorted listing.
    /// <para><paramref name="nameStartsWith"/> is an <em>exact prefix</em> filter. It used to be
    /// <c>NameStartsWithOrGreater</c>, which is the server's letter-<em>jump</em> parameter: it
    /// returns the requested letter and every letter after it, so clicking M in a library
    /// returned M–Z. Since the letter rail has no scroll or jump mechanism — a click wipes the
    /// list and re-runs this query from index 0 — "or greater" only ever showed up as a filter
    /// that failed to filter. Changed 2026-08-05.</para></summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetItemsAsync(Guid parentId, int startIndex, int limit,
        ItemSortBy sortBy = ItemSortBy.SortName, SortOrder sortOrder = SortOrder.Ascending,
        ItemFilter? filter = null, string? genre = null, string? nameStartsWith = null)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.ParentId = parentId;
            cfg.QueryParameters.SortBy = [sortBy];
            cfg.QueryParameters.SortOrder = [sortOrder];
            if (filter is { } f)
                cfg.QueryParameters.Filters = [f];
            if (genre is { Length: > 0 })
                cfg.QueryParameters.Genres = [genre];
            if (nameStartsWith is { Length: > 0 })
                cfg.QueryParameters.NameStartsWith = nameStartsWith;
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>One page of a folder's children in the CANONICAL order: SortName ascending, no
    /// filter, no genre, no letter. The folder cache prefetches a whole folder through this and
    /// then sorts and filters locally, so every page it stores has to come from one server
    /// ordering — adding a query parameter here reshuffles the cache rather than narrowing it.
    /// The <c>Fields</c> set is the one the local sort and filter need (sort key, date added,
    /// genres) plus the change token the incremental refresh compares.
    /// <para>Cancellable, unlike <see cref="GetItemsAsync"/>: a prefetch outlives the page that
    /// started it, so leaving the folder has to be able to stop it.</para></summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> GetFolderPageAsync(Guid parentId,
        int startIndex, int limit, CancellationToken cancellationToken)
    {
        var result = await WithRetry(token => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.ParentId = parentId;
            cfg.QueryParameters.SortBy = [ItemSortBy.SortName];
            cfg.QueryParameters.SortOrder = [SortOrder.Ascending];
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.EnableTotalRecordCount = true;
            cfg.QueryParameters.Fields = CanonicalBrowseFields;
        }, token), cancellationToken).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Sweeps all pages of a folder's children in canonical order: SortName ascending,
    /// returning only (Id, Etag, UserData). Batches by <see cref="BrowseFolderCache.SweepPageSize"/>.</summary>
    public async Task<(List<SweepRow> Rows, int TotalCount)> SweepFolderAsync(Guid parentId,
        CancellationToken cancellationToken)
    {
        var rows = new List<SweepRow>();
        var startIndex = 0;
        var limit = BrowseFolderCache.SweepPageSize;
        var total = 0;
        while (true)
        {
            var (pageRows, serverTotal) = await SweepFolderAsync(parentId, startIndex, limit, cancellationToken).ConfigureAwait(false);
            total = serverTotal;
            if (pageRows.Count == 0)
                break;
            rows.AddRange(pageRows);
            if (rows.Count >= total || pageRows.Count < limit || rows.Count >= BrowseFolderCache.MaxItems)
                break;
            startIndex = rows.Count;
        }
        return (rows, total);
    }

    /// <summary>Identity-and-user-state sweep of one folder page: the same canonical query as
    /// <see cref="GetFolderPageAsync"/>, stripped to what the incremental refresh actually
    /// compares. No images and no metadata fields beyond the change token, so sweeping a whole
    /// folder costs a fraction of re-fetching it.</summary>
    public async Task<(List<SweepRow> Rows, int TotalCount)> SweepFolderAsync(Guid parentId,
        int startIndex, int limit, CancellationToken cancellationToken)
    {
        var result = await WithRetry(token => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.ParentId = parentId;
            cfg.QueryParameters.SortBy = [ItemSortBy.SortName];
            cfg.QueryParameters.SortOrder = [SortOrder.Ascending];
            cfg.QueryParameters.StartIndex = startIndex;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.EnableTotalRecordCount = true;
            cfg.QueryParameters.EnableImages = false;
            // Spelled out rather than left to the server's default: user state is the whole point
            // of this call, and a sweep that silently lost it would look like a folder in which
            // nobody had ever watched anything.
            cfg.QueryParameters.EnableUserData = true;
            cfg.QueryParameters.Fields = [ItemFields.Etag];
        }, token), cancellationToken).ConfigureAwait(false);
        var rows = result?.Items?
            .Where(d => d.Id is not null)
            .Select(d => new SweepRow(d.Id!.Value, d.Etag,
                d.UserData?.Played ?? false,
                d.UserData?.IsFavorite ?? false,
                d.UserData?.PlaybackPositionTicks ?? 0,
                d.UserData?.PlayedPercentage))
            .ToList() ?? [];
        return (rows, result?.TotalRecordCount ?? 0);
    }

    /// <summary>Refetches items by id, in batches of
    /// <see cref="BrowseFolderCache.RefetchBatchSize"/> — the repair half of the incremental
    /// refresh. Carries the same <c>Fields</c> as <see cref="GetFolderPageAsync"/> so a refetched
    /// item is interchangeable with a prefetched one. An empty id list issues no request.
    /// <para>The server returns matches in its own order, not the caller's; the caller reorders.
    /// Ids it does not know are simply absent from the result.</para></summary>
    public async Task<List<MediaItem>> GetItemsByIdsAsync(IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += BrowseFolderCache.RefetchBatchSize)
        {
            // The SDK's Ids parameter is Guid?[], not Guid[].
            var batch = ids.Skip(offset).Take(BrowseFolderCache.RefetchBatchSize)
                .Select(id => (Guid?)id).ToArray();
            var result = await WithRetry(token => _client.Items.GetAsync(cfg =>
            {
                cfg.QueryParameters.Ids = batch;
                cfg.QueryParameters.EnableTotalRecordCount = false;
                cfg.QueryParameters.EnableUserData = true;
                cfg.QueryParameters.Fields = CanonicalBrowseFields;
            }, token), cancellationToken).ConfigureAwait(false);
            items.AddRange(Map(result?.Items));
        }
        return items;
    }

    /// <summary>The field set a cached folder item must carry: enough for the card, the local
    /// sort keys, the genre filter, and the change token. Shared so a page fetched by
    /// <see cref="GetFolderPageAsync"/> and one repaired by <see cref="GetItemsByIdsAsync"/>
    /// cannot drift apart.</summary>
    private static readonly ItemFields[] CanonicalBrowseFields =
    [
        ItemFields.Overview, ItemFields.SortName, ItemFields.DateCreated,
        ItemFields.Genres, ItemFields.Etag,
    ];

    /// <summary>Paged advanced search across the whole server (Phase 7 M4). Mirrors the paged
    /// <see cref="GetItemsAsync"/> shape but driven by the <see cref="AdvancedSearchQuery"/>
    /// contract (multi-type, multi-genre, person, year range, watched, sort). Recursive.</summary>
    public async Task<(List<MediaItem> Items, int TotalCount)> AdvancedSearchAsync(
        AdvancedSearchQuery q, int startIndex, int limit)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            var p = cfg.QueryParameters;
            p.Recursive = true;
            if (q.Term is { Length: > 0 })
                p.SearchTerm = q.Term;
            if (q.ItemTypes.Count > 0)
                p.IncludeItemTypes = q.ItemTypes.ToArray();
            if (q.Genres.Count > 0)
                p.Genres = q.Genres.ToArray();
            if (q.PersonIds.Count > 0)
                p.PersonIds = q.PersonIds.Select(id => (Guid?)id).ToArray();
            if (q.MinYear is { } min)
                p.MinPremiereDate = new DateTimeOffset(new DateTime(min, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            if (q.MaxYear is { } max)
                p.MaxPremiereDate = new DateTimeOffset(new DateTime(max, 12, 31, 23, 59, 59, DateTimeKind.Utc));
            if (q.Watched is { } w)
                p.Filters = [w];
            p.SortBy = [q.Sort];
            p.SortOrder = [q.Order];
            p.StartIndex = startIndex;
            p.Limit = limit;
            p.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return (Map(result?.Items), result?.TotalRecordCount ?? 0);
    }

    /// <summary>Genre names present in a library (filter dropdown source). A null
    /// <paramref name="parentId"/> returns the server-wide genre set (the Advanced Search
    /// facet); passing a library id scopes to that library (the per-library filter).</summary>
    public async Task<List<string>> GetGenresAsync(Guid? parentId = null)
    {
        var result = await WithRetry(() => _client.Genres.GetAsync(cfg =>
        {
            if (parentId is { } pid)
                cfg.QueryParameters.ParentId = pid;
            cfg.QueryParameters.SortBy = [ItemSortBy.SortName];
        })).ConfigureAwait(false);
        return result?.Items?.Select(g => g.Name).Where(n => n is { Length: > 0 }).Select(n => n!).ToList() ?? [];
    }

    /// <summary>People matching a name prefix — the Advanced Search person type-ahead
    /// (2026-08-05). Returns <see cref="PersonEntry"/> rather than a new type so the typed
    /// picker and the cast-face deep link converge on one shape; <c>Role</c> is null because
    /// a role only means something relative to an item, and this lookup has no item.
    /// <para><c>/Persons</c> takes no <c>StartIndex</c> — it does not page — which is fine for
    /// a suggestion list. <c>UserId</c> is passed so the server applies this user's library
    /// access instead of returning people from libraries they cannot see.</para></summary>
    public async Task<List<PersonEntry>> SearchPeopleAsync(string term, int limit = 8)
    {
        var result = await WithRetry(() => _client.Persons.GetAsync(cfg =>
        {
            cfg.QueryParameters.SearchTerm = term;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.UserId = UserId;
        })).ConfigureAwait(false);
        return result?.Items?
            .Where(d => d.Id is not null && d.Name is { Length: > 0 })
            .Select(d =>
            {
                var tag = d.ImageTags?.AdditionalData?.TryGetValue("Primary", out var t) == true
                    ? t?.ToString()
                    : null;
                return new PersonEntry(d.Id!.Value, d.Name!, null,
                    tag is null ? null : GetPrimaryImageUrl(d.Id!.Value, tag, 64));
            })
            .ToList() ?? [];
    }

    public async Task<List<MediaItem>> GetSeasonsAsync(Guid seriesId)
    {
        var result = await WithRetry(() => _client.Shows[seriesId].Seasons.GetAsync()).ConfigureAwait(false);
        return Map(result?.Items);
    }

    public async Task<List<MediaItem>> GetEpisodesAsync(Guid seriesId, Guid seasonId)
    {
        var result = await WithRetry(() => _client.Shows[seriesId].Episodes.GetAsync(cfg =>
        {
            cfg.QueryParameters.SeasonId = seasonId;
            // Explicit UserId. The comment here used to claim UserData.Played is absent without it
            // ("the M15 unwatched queue filter is wrong without it"); measured against Jellyfin
            // 10.11.11 that is FALSE — the server populates UserData from the access token on this
            // endpoint and on every other one the app calls, with or without the parameter (B17).
            // Kept anyway: it is explicit, it costs nothing, and older servers did require it on
            // the /Shows routes. Do not "tidy" the other queries INTO this shape expecting a
            // behaviour change — there isn't one.
            cfg.QueryParameters.UserId = UserId;
            // Virtual/missing episodes (metadata-only placeholders) are neither
            // playable nor downloadable; hiding them matches Jellyfin's default UX.
            cfg.QueryParameters.IsMissing = false;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>All episodes of a series across seasons, in series order (queue source).</summary>
    public async Task<List<MediaItem>> GetSeriesEpisodesAsync(Guid seriesId)
    {
        var result = await WithRetry(() => _client.Shows[seriesId].Episodes.GetAsync(cfg =>
        {
            cfg.QueryParameters.UserId = UserId;   // explicit, not required — see GetEpisodesAsync (B17)
            cfg.QueryParameters.IsMissing = false; // exclude virtual placeholders (unplayable)
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Children of a server playlist, in playlist order (queue source).</summary>
    public async Task<List<MediaItem>> GetPlaylistItemsAsync(Guid playlistId, int limit = 500)
    {
        var result = await WithRetry(() => _client.Playlists[playlistId].Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.UserId = UserId;
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Album tracks in disc/track order. The generic child browse query sorts by name,
    /// which is not an acceptable queue order for an album.</summary>
    public async Task<List<MediaItem>> GetAlbumTracksAsync(Guid albumId, int limit = 500)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.ParentId = albumId;
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Audio];
            cfg.QueryParameters.SortBy = [ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber, ItemSortBy.SortName];
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.UserId = UserId;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return DistinctTracks(Map(result?.Items));
    }

    /// <summary>All library tracks credited to a music artist. This is a recursive audio query,
    /// not a direct-child browse, so albums below the artist cannot leave Play all empty.</summary>
    public async Task<List<MediaItem>> GetArtistTracksAsync(Guid artistId, int limit = 500)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.AlbumArtistIds = [artistId];
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Audio];
            cfg.QueryParameters.SortBy = [ItemSortBy.Album, ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber, ItemSortBy.SortName];
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.UserId = UserId;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return DistinctTracks(Map(result?.Items));
    }

    private static List<MediaItem> DistinctTracks(IEnumerable<MediaItem> items)
        => items.Where(item => item.Type == BaseItemDto_Type.Audio)
            .DistinctBy(item => item.Id)
            .ToList();

    /// <summary>External subtitle streams of the item's default media source. mpv
    /// authenticates the URLs with the same Authorization header as the video.</summary>
    public async Task<List<ExternalSubtitle>> GetExternalSubtitlesAsync(Guid itemId)
    {
        var info = await WithRetry(() => _client.Items[itemId].PlaybackInfo.GetAsync(cfg =>
            cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
        var source = info?.MediaSources?.FirstOrDefault();
        if (source?.MediaStreams is null)
            return [];

        var subs = new List<ExternalSubtitle>();
        foreach (var stream in source.MediaStreams)
        {
            if (stream.Type != MediaStream_Type.Subtitle || stream.IsExternal != true)
                continue;
            // DeliveryUrl is authoritative when present (server-chosen container/codec);
            // otherwise fall back to the stable subtitle endpoint.
            var url = string.IsNullOrEmpty(stream.DeliveryUrl)
                ? $"/Videos/{itemId}/{source.Id}/Subtitles/{stream.Index}/Stream.vtt"
                : stream.DeliveryUrl;
            if (url.StartsWith('/'))
                url = ServerUrl + url;
            subs.Add(new ExternalSubtitle(url, stream.Title ?? stream.DisplayTitle, stream.Language));
        }
        return subs;
    }

    /// <summary>Human-readable media metadata of the item's default source (container,
    /// per-stream codec/resolution/HDR/bitrate/audio layout) for the info panel.</summary>
    public async Task<List<MediaInfoField>> GetMediaInfoAsync(Guid itemId)
    {
        var info = await WithRetry(() => _client.Items[itemId].PlaybackInfo.GetAsync(cfg =>
            cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
        var source = info?.MediaSources?.FirstOrDefault();
        if (source is null)
            return [];

        var fields = new List<MediaInfoField>();
        if (source.Container is { Length: > 0 } container)
            fields.Add(new MediaInfoField("Container", container.ToUpperInvariant()));
        // Invariant: user-visible decimals must not follow the OS locale ("7,5 GB").
        if (source.Size is > 0)
            fields.Add(new MediaInfoField("Size", FormattableString.Invariant($"{source.Size.Value / 1_073_741_824.0:0.00} GB")));
        if (source.Bitrate is > 0)
            fields.Add(new MediaInfoField("Total bitrate", FormattableString.Invariant($"{source.Bitrate.Value / 1_000_000.0:0.0} Mbps")));

        var streams = source.MediaStreams ?? [];

        var videoIndex = 0;
        foreach (var v in streams.Where(s => s.Type == MediaStream_Type.Video))
        {
            videoIndex++;
            var parts = new List<string>();
            if (v.Codec is { Length: > 0 } codec)
                parts.Add(v.Profile is { Length: > 0 } profile
                    ? $"{codec.ToUpperInvariant()} {profile}"
                    : codec.ToUpperInvariant());
            if (v is { Width: > 0, Height: > 0 })
                parts.Add($"{v.Width}x{v.Height}");
            if (v.RealFrameRate is > 0)
                parts.Add(FormattableString.Invariant($"{v.RealFrameRate.Value:0.###} fps"));
            if (v.VideoRangeType?.ToString() is { Length: > 0 } range)
                parts.Add(range);
            if (v.BitRate is > 0)
                parts.Add(FormattableString.Invariant($"{v.BitRate.Value / 1_000_000.0:0.0} Mbps"));
            if (parts.Count > 0)
                fields.Add(new MediaInfoField(videoIndex == 1 ? "Video" : $"Video {videoIndex}",
                    string.Join(" · ", parts)));
        }

        var audioIndex = 0;
        foreach (var a in streams.Where(s => s.Type == MediaStream_Type.Audio))
        {
            audioIndex++;
            var parts = new List<string>();
            if (a.Codec is { Length: > 0 } codec)
                parts.Add(codec.ToUpperInvariant());
            if (a.ChannelLayout is { Length: > 0 } layout)
                parts.Add(layout);
            else if (a.Channels is > 0)
                parts.Add($"{a.Channels} ch");
            if (a.Language is { Length: > 0 } lang)
                parts.Add($"({lang})");
            if (a.BitRate is > 0)
                parts.Add($"{a.BitRate.Value / 1000} kbps");
            if (parts.Count > 0)
                fields.Add(new MediaInfoField(audioIndex == 1 ? "Audio" : $"Audio {audioIndex}",
                    string.Join(" · ", parts)));
        }

        var subs = streams
            .Where(s => s.Type == MediaStream_Type.Subtitle)
            .Select(s => (s.Language ?? s.Title ?? "unknown") + (s.IsExternal == true ? " (external)" : ""))
            .ToList();
        if (subs.Count > 0)
            fields.Add(new MediaInfoField("Subtitles", string.Join(", ", subs)));

        return fields;
    }

    /// <summary>Selectable streams of the item's default media source plus summary
    /// fields (resolution / aspect / codec / native source HDR — independent of the
    /// RTX Video HDR display toggle) for the detail view's preselection dropdowns.
    /// Disk-cached (M19): the projection is user-state-free playback metadata, so a
    /// 1 h TTL makes re-opened details instant and spares the flaky LAN a PlaybackInfo
    /// round-trip. (Item-detail responses are deliberately NOT cached — they carry
    /// UserData whose staleness would regress watched/resume freshness.)</summary>
    public Task<MediaSourceStreams?> GetMediaStreamsAsync(Guid itemId)
        => Imaging.MetadataCache.GetOrFetchAsync($"streams:{ServerUrl}:{itemId:N}",
            TimeSpan.FromHours(1), () => FetchMediaStreamsAsync(itemId));

    private async Task<MediaSourceStreams?> FetchMediaStreamsAsync(Guid itemId)
    {
        var info = await WithRetry(() => _client.Items[itemId].PlaybackInfo.GetAsync(cfg =>
            cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
        var source = info?.MediaSources?.FirstOrDefault();
        if (source?.MediaStreams is not { Count: > 0 } streams)
            return null;

        var video = new List<MediaStreamChoice>();
        var audio = new List<MediaStreamChoice>();
        var subtitles = new List<MediaStreamChoice>();
        int videoOrdinal = 0, audioOrdinal = 0, subOrdinal = 0;
        foreach (var s in streams)
        {
            if (s.Index is not { } index)
                continue;
            var external = s.IsExternal == true;
            switch (s.Type)
            {
                case MediaStream_Type.Video:
                    video.Add(new MediaStreamChoice(index, external ? -1 : videoOrdinal++,
                        StreamDisplay(s), s.Language, s.Title, s.IsDefault == true, external));
                    break;
                case MediaStream_Type.Audio:
                    audio.Add(new MediaStreamChoice(index, external ? -1 : audioOrdinal++,
                        StreamDisplay(s), s.Language, s.Title, s.IsDefault == true, external));
                    break;
                case MediaStream_Type.Subtitle:
                    subtitles.Add(new MediaStreamChoice(index, external ? -1 : subOrdinal++,
                        StreamDisplay(s), s.Language, s.Title, s.IsDefault == true, external));
                    break;
            }
        }

        var summary = new List<MediaInfoField>();
        if (streams.FirstOrDefault(s => s.Type == MediaStream_Type.Video) is { } v)
        {
            if (v is { Width: > 0, Height: > 0 })
            {
                summary.Add(new MediaInfoField("Resolution", $"{v.Width}x{v.Height}"));
                summary.Add(new MediaInfoField("Aspect", v.AspectRatio is { Length: > 0 } ar
                    ? ar
                    : FormattableString.Invariant($"{(double)v.Width.Value / v.Height.Value:0.##}:1")));
            }
            if (v.Codec is { Length: > 0 } codec)
                summary.Add(new MediaInfoField("Codec", v.Profile is { Length: > 0 } profile
                    ? $"{codec.ToUpperInvariant()} {profile}"
                    : codec.ToUpperInvariant()));
            summary.Add(new MediaInfoField("Range", HdrLabel(v.VideoRangeType?.ToString())));
        }
        // Default audio stream → a compact codec + channels badge (detail gem pill).
        var defaultAudio = streams.Where(s => s.Type == MediaStream_Type.Audio)
            .OrderByDescending(s => s.IsDefault == true).FirstOrDefault();
        if (defaultAudio is not null && AudioPillLabel(defaultAudio) is { Length: > 0 } audioBadge)
            summary.Add(new MediaInfoField("Audio", audioBadge));

        return new MediaSourceStreams(source.Id, video, audio, subtitles, summary);
    }

    /// <summary>ComboBox row text: the server's DisplayTitle when present, else the
    /// same composition GetMediaInfoAsync uses; externals are always marked.</summary>
    private static string StreamDisplay(MediaStream s)
    {
        var external = s.IsExternal == true;
        if (s.DisplayTitle is { Length: > 0 } dt)
            return external && !dt.Contains("external", StringComparison.OrdinalIgnoreCase)
                ? dt + " (external)"
                : dt;

        var parts = new List<string>();
        if (s.Type == MediaStream_Type.Subtitle && s.Title is { Length: > 0 } title)
            parts.Add(title);
        if (s.Codec is { Length: > 0 } codec)
            parts.Add(s.Type == MediaStream_Type.Video && s.Profile is { Length: > 0 } profile
                ? $"{codec.ToUpperInvariant()} {profile}"
                : codec.ToUpperInvariant());
        if (s.Type == MediaStream_Type.Video && s is { Width: > 0, Height: > 0 })
            parts.Add($"{s.Width}x{s.Height}");
        if (s.Type == MediaStream_Type.Audio)
        {
            if (s.ChannelLayout is { Length: > 0 } layout)
                parts.Add(layout);
            else if (s.Channels is > 0)
                parts.Add($"{s.Channels} ch");
        }
        if (s.Language is { Length: > 0 } lang)
            parts.Add($"({lang})");
        var display = parts.Count > 0 ? string.Join(" · ", parts) : "Unknown";
        return external ? display + " (external)" : display;
    }

    /// <summary>Native source dynamic range label from the server's VideoRangeType
    /// (SDR / HDR10 / HDR10+ / HLG / DOVI*), independent of the RTX HDR toggle.</summary>
    private static string HdrLabel(string? rangeType)
    {
        if (rangeType is { Length: > 0 } && rangeType.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase))
            return "Dolby Vision";
        return rangeType?.ToUpperInvariant() switch
        {
            "HDR10" => "HDR10",
            "HDR10PLUS" => "HDR10+",
            "HLG" => "HLG",
            _ => "SDR",
        };
    }

    /// <summary>Compact audio badge for the detail gem pill: a friendly codec name plus
    /// the channel layout (e.g. "TRUEHD 7.1", "DD+ 5.1", "AAC 2.0"). Dolby Atmos / DTS:X
    /// surface when the server marks the profile.</summary>
    private static string? AudioPillLabel(MediaStream s)
    {
        var profile = s.Profile ?? "";
        if (profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase))
            return $"ATMOS {ChannelBadge(s)}".TrimEnd();
        if (profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("DTS-X", StringComparison.OrdinalIgnoreCase))
            return $"DTS:X {ChannelBadge(s)}".TrimEnd();

        var codec = (s.Codec ?? "").ToLowerInvariant() switch
        {
            "eac3" => "DD+",
            "ac3" => "DD",
            "truehd" => "TRUEHD",
            "dts" or "dca" => "DTS",
            "aac" => "AAC",
            "flac" => "FLAC",
            "opus" => "OPUS",
            "mp3" => "MP3",
            "pcm" or "pcm_s16le" or "pcm_s24le" => "PCM",
            { Length: > 0 } other => other.ToUpperInvariant(),
            _ => "",
        };
        if (codec.Length == 0)
            return null;
        var channels = ChannelBadge(s);
        return channels.Length > 0 ? $"{codec} {channels}" : codec;
    }

    /// <summary>"7.1" / "5.1" / "2.0" from the layout, else the raw channel count.</summary>
    private static string ChannelBadge(MediaStream s)
    {
        if (s.ChannelLayout is { Length: > 0 } layout)
        {
            var head = layout.Split('(')[0].Trim();
            if (head.Length > 0 && (head.Contains('.') || char.IsDigit(head[0])))
                return head;
        }
        return s.Channels switch
        {
            8 => "7.1",
            6 => "5.1",
            2 => "2.0",
            1 => "1.0",
            > 0 => $"{s.Channels}ch",
            _ => "",
        };
    }

    /// <summary>The item's trickplay bucket closest to 320 px wide, or null when the
    /// server has no tiles. Raw JSON fetch: the SDK leaves BaseItemDto.Trickplay as
    /// untyped AdditionalData, and the single-item builder has no Fields param.</summary>
    /// <param name="mediaSourceId">The source to read sheets for, or null for whichever the
    /// server lists first. An alternate version has its OWN trickplay bucket — its sheets are
    /// cut from a different file, so a 4K remux served the 1080p encode's sheets shows previews
    /// from the wrong edit (and, when the two runtimes differ, from the wrong minute).</param>
    public async Task<TrickplayInfo?> GetTrickplayAsync(Guid itemId, string? mediaSourceId = null)
    {
        var json = await GetRawAsync($"/Items/{itemId}?fields=Trickplay").ConfigureAwait(false);
        if (json is null)
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Trickplay", out var trickplay)
                || trickplay.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            foreach (var source in trickplay.EnumerateObject())
            {
                // A named source skips every other bucket rather than falling back to the first:
                // returning the default source's sheets under the requested id would silently
                // fetch tiles the server has no such thing for (404 -> no preview at all), which
                // is strictly worse than the hover preview being absent for one version.
                if (mediaSourceId is { Length: > 0 }
                    && !string.Equals(source.Name, mediaSourceId, StringComparison.Ordinal))
                    continue;
                if (source.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                TrickplayInfo? best = null;
                foreach (var bucket in source.Value.EnumerateObject())
                {
                    var v = bucket.Value;
                    if (v.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;
                    int Get(string name) => v.TryGetProperty(name, out var p)
                        && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt32() : 0;
                    var info = new TrickplayInfo(source.Name, Get("Width"), Get("Height"),
                        Get("TileWidth"), Get("TileHeight"), Get("ThumbnailCount"), Get("Interval"));
                    if (info is not { Width: > 0, Height: > 0, TileWidth: > 0, TileHeight: > 0,
                        ThumbnailCount: > 0, IntervalMs: > 0 })
                        continue;
                    if (best is null || Math.Abs(info.Width - 320) < Math.Abs(best.Width - 320))
                        best = info;
                }
                if (best is not null)
                    return best;  // some items carry an empty Trickplay {} — skip those sources
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    /// <summary>Tile-sheet JPEG for a trickplay bucket (fetched through the image cache,
    /// which sends the Authorization header).</summary>
    public string GetTrickplayTileUrl(Guid itemId, int width, int sheetIndex, string mediaSourceId)
        => $"{ServerUrl}/Videos/{itemId}/Trickplay/{width}/{sheetIndex}.jpg?mediaSourceId={mediaSourceId}";

    /// <summary>Items similar to the given one ("More like this" row).
    /// NOTE: the server's result set is <b>not stable</b> — two identical calls seconds apart
    /// returned 12 items sharing only 2 ids (measured, Jellyfin 10.11.11), so the row legitimately
    /// shows different titles each time a detail view is opened. Any test that cross-checks this
    /// row against the API will be flaky unless it compares per id and tolerates the churn.</summary>
    public async Task<List<MediaItem>> GetSimilarAsync(Guid itemId, int limit = 12)
    {
        var result = await WithRetry(() => _client.Items[itemId].Similar.GetAsync(cfg =>
        {
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Marks an item favorite/unfavorite. UserId explicit (see SetPlayedAsync).</summary>
    public async Task<bool> SetFavoriteAsync(Guid itemId, bool favorite)
    {
        try
        {
            if (favorite)
                await WithRetry(() => _client.UserFavoriteItems[itemId].PostAsync(cfg =>
                    cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
            else
                await WithRetry(() => _client.UserFavoriteItems[itemId].DeleteAsync(cfg =>
                    cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Favorite movies/series/episodes (Home row).</summary>
    public async Task<List<MediaItem>> GetFavoritesAsync(int limit = 20)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filters = [ItemFilter.IsFavorite];
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];
            cfg.QueryParameters.SortBy = [ItemSortBy.SortName];
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    /// <summary>Marks an item watched/unwatched. UserId is passed explicitly — some
    /// servers reject the inferred user on these endpoints.</summary>
    public async Task<bool> SetPlayedAsync(Guid itemId, bool played)
    {
        try
        {
            if (played)
                await WithRetry(() => _client.UserPlayedItems[itemId].PostAsync(cfg =>
                {
                    cfg.QueryParameters.UserId = UserId;
                    cfg.QueryParameters.DatePlayed = DateTimeOffset.UtcNow;
                })).ConfigureAwait(false);
            else
                await WithRetry(() => _client.UserPlayedItems[itemId].DeleteAsync(cfg =>
                    cfg.QueryParameters.UserId = UserId)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The episode following the given one in its series, or null at the end.</summary>
    public async Task<MediaItem?> GetNextEpisodeAsync(Guid seriesId, Guid currentEpisodeId)
        => (await GetAdjacentEpisodesAsync(seriesId, currentEpisodeId).ConfigureAwait(false)).Next;

    /// <summary>The episodes on either side of the given one in pure series order
    /// (AdjacentTo spans season boundaries); null at the respective end.</summary>
    public async Task<(MediaItem? Previous, MediaItem? Next)> GetAdjacentEpisodesAsync(
        Guid seriesId, Guid currentEpisodeId)
    {
        var result = await WithRetry(() => _client.Shows[seriesId].Episodes.GetAsync(cfg =>
        {
            cfg.QueryParameters.AdjacentTo = currentEpisodeId;
            cfg.QueryParameters.IsMissing = false; // never step onto a virtual placeholder
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        var items = result?.Items;
        if (items is null)
            return (null, null);
        var idx = items.FindIndex(i => i.Id == currentEpisodeId);
        if (idx < 0)
            return (null, null);
        var prev = idx > 0 && items[idx - 1].Id is not null ? MapOne(items[idx - 1]) : null;
        var next = idx + 1 < items.Count && items[idx + 1].Id is not null ? MapOne(items[idx + 1]) : null;
        return (prev, next);
    }

    /// <summary>Server-side search across movies, series and episodes.
    /// Recursive is required for SearchTerm to filter at all.</summary>
    public async Task<List<MediaItem>> SearchAsync(string term, int limit = 40)
    {
        var result = await WithRetry(() => _client.Items.GetAsync(cfg =>
        {
            cfg.QueryParameters.SearchTerm = term;
            cfg.QueryParameters.Recursive = true;
            cfg.QueryParameters.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];
            cfg.QueryParameters.Limit = limit;
            cfg.QueryParameters.Fields = [ItemFields.Overview];
        })).ConfigureAwait(false);
        return Map(result?.Items);
    }

    public async Task<MediaItem?> GetItemAsync(Guid itemId)
    {
        var dto = await WithRetry(() => _client.Items[itemId].GetAsync()).ConfigureAwait(false);
        return dto is null ? null : MapOne(dto);
    }

    private List<MediaItem> Map(List<BaseItemDto>? dtos)
        => dtos?.Where(d => d.Id is not null).Select(MapOne).ToList() ?? [];

    private MediaItem MapOne(BaseItemDto dto)
    {
        var id = dto.Id!.Value;
        var primaryTag = dto.ImageTags?.AdditionalData?.TryGetValue("Primary", out var tag) == true
            ? tag?.ToString()
            : null;
        var backdropTag = dto.BackdropImageTags?.FirstOrDefault();
        return new MediaItem
        {
            Id = id,
            Etag = dto.Etag,
            Name = dto.Name ?? "(unnamed)",
            SortName = dto.SortName,
            Type = dto.Type ?? BaseItemDto_Type.Folder,
            ParentId = dto.ParentId,
            SeriesId = dto.SeriesId,
            SeriesName = dto.SeriesName,
            SeasonId = dto.SeasonId,
            SeasonName = dto.SeasonName,
            IndexNumber = dto.IndexNumber,
            ParentIndexNumber = dto.ParentIndexNumber,
            ProductionYear = dto.ProductionYear,
            PremiereDate = dto.PremiereDate,
            DateCreated = dto.DateCreated,
            RuntimeTicks = dto.RunTimeTicks,
            Overview = dto.Overview,
            CommunityRating = dto.CommunityRating,
            OfficialRating = dto.OfficialRating,
            Genres = dto.Genres ?? [],
            ResumePositionTicks = dto.UserData?.PlaybackPositionTicks ?? 0,
            PlayedPercentage = dto.UserData?.PlayedPercentage,
            Played = dto.UserData?.Played ?? false,
            IsFavorite = dto.UserData?.IsFavorite ?? false,
            CollectionType = dto.CollectionType?.ToString().ToLowerInvariant(),
            ProviderIds = dto.ProviderIds?.AdditionalData?
                .Where(kv => kv.Value?.ToString() is { Length: > 0 })
                .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(),
            TrailerUrl = PickTrailerUrl(dto.RemoteTrailers),
            People = dto.People?.Where(p => p.Name is { Length: > 0 })
                .Select(p => new PersonEntry(p.Id ?? Guid.Empty, p.Name!,
                    p.Role is { Length: > 0 } role ? role : p.Type?.ToString(),
                    p.Id is { } pid && p.PrimaryImageTag is { Length: > 0 } tag
                        ? GetPrimaryImageUrl(pid, tag)
                        : null))
                .ToList() ?? [],
            Chapters = dto.Chapters?.Select(c => new MediaChapter(c.Name, c.StartPositionTicks ?? 0)).ToList() ?? [],
            PrimaryImageTag = primaryTag,
            BackdropImageTag = backdropTag,
            PosterUrl = primaryTag is null ? null : GetPrimaryImageUrl(id, primaryTag),
            BackdropUrl = backdropTag is null ? null : GetBackdropImageUrl(id, backdropTag),
            // Episodes' Primary image is the landscape still; fetch it wider for 16:9 cards.
            // Falls back to ParentThumb (season/series thumbnail) or series primary if the episode has no still.
            ThumbUrl = dto.Type != BaseItemDto_Type.Episode
                ? null
                : primaryTag is not null
                    ? GetPrimaryImageUrl(id, primaryTag, maxWidth: 480)
                    : dto.ParentThumbItemId is { } parentThumbId && dto.ParentThumbImageTag is { Length: > 0 } parentThumbTag
                        ? GetThumbImageUrl(parentThumbId, parentThumbTag, maxWidth: 480)
                        : dto.SeriesId is { } seriesId && dto.SeriesPrimaryImageTag is { Length: > 0 } seriesTag
                            ? GetPrimaryImageUrl(seriesId, seriesTag, maxWidth: 480)
                            : backdropTag is not null
                                ? GetBackdropImageUrl(id, backdropTag, maxWidth: 480)
                                : null,
        };
    }

    /// <summary>First usable remote-trailer URL (a YouTube link preferred), or null.</summary>
    private static string? PickTrailerUrl(List<MediaUrl>? trailers)
    {
        if (trailers is null)
            return null;
        var urls = trailers.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        return urls.FirstOrDefault(u => u!.Contains("youtu", StringComparison.OrdinalIgnoreCase))
            ?? urls.FirstOrDefault();
    }

    /// <summary>Authorized raw GET against the server (for plugin endpoints outside the SDK).
    /// Returns null on any failure or non-success status.</summary>
    public async Task<string?> GetRawAsync(string path)
    {
        if (ServerUrl is null || AccessToken is null)
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                return null;
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Direct-play stream URL; mpv authenticates via the Authorization header.</summary>
    public string GetStreamUrl(Guid itemId, string? mediaSourceId = null)
        => $"{ServerUrl}/Videos/{itemId}/stream?static=true"
            + (mediaSourceId is { Length: > 0 }
                ? $"&mediaSourceId={Uri.EscapeDataString(mediaSourceId)}"
                : "");

    /// <summary>Self-authenticating stream URL for use OUTSIDE the app (M10 "Copy stream
    /// URL") — appends the <c>ApiKey</c> query parameter since external players can't send
    /// our auth header. The casing is load-bearing: Jellyfin 12 disabled legacy authorization
    /// (server PR #15559), and the lower-case <c>api_key</c> spelling is one of the legacy
    /// forms — it now returns 401 on a 12.0 server (found 2026-09-08 against Tower 12.0.0).
    /// Handle with care: the URL embeds the access token.</summary>
    public string? GetShareableStreamUrl(Guid itemId) =>
        AccessToken is null ? null : $"{GetStreamUrl(itemId)}&ApiKey={AccessToken}";

    /// <summary>Download URL (Phase 7 M20; port of Velly's getDownloadUrl): the plain
    /// static stream for original quality, or the server transcoder's h264/aac output
    /// when a maxWidth tier was picked. Auth rides the Authorization header.</summary>
    public string GetDownloadUrl(Guid itemId, string? mediaSourceId = null, int? maxWidth = null,
        int? videoBitRate = null, string? container = null)
    {
        var sb = new System.Text.StringBuilder($"{ServerUrl}/Videos/{itemId}/stream");
        if (maxWidth is not null && container is { Length: > 0 })
            sb.Append('.').Append(container);
        sb.Append('?');
        if (maxWidth is null)
        {
            sb.Append("static=true");
        }
        else
        {
            sb.Append("static=false&videoCodec=h264&audioCodec=aac");
            sb.Append("&maxWidth=").Append(maxWidth.Value);
            if (videoBitRate is not null)
                sb.Append("&videoBitRate=").Append(videoBitRate.Value);
        }
        if (mediaSourceId is { Length: > 0 })
            sb.Append("&mediaSourceId=").Append(mediaSourceId);
        return sb.ToString();
    }

    /// <summary>Resolution picker rows for an item (Phase 7 M20): the original source
    /// with its exact size, plus lower transcode tiers (1080p/720p/480p) with sizes
    /// estimated from tier bitrate × runtime — Velly's buildDownloadOptions ported.
    /// Empty on failure or when the item has no video stream. This legacy one-argument
    /// entry point remains the batch-download path: unlike a detail preload it has no view
    /// lifetime to cancel, and its existing empty-on-failure behavior is preserved.</summary>
    public async Task<List<DownloadOption>> GetDownloadOptionsAsync(Guid itemId)
    {
        try
        {
            return await GetDownloadOptionsAsync(itemId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Cancellable detail-view download-option preload. An empty list means the
    /// server answered successfully but exposed no video source; request failures propagate
    /// so the UI can distinguish them, and deliberate cancellation is never retried.</summary>
    public async Task<List<DownloadOption>> GetDownloadOptionsAsync(Guid itemId,
        CancellationToken cancellationToken)
    {
#if DEBUG
        // Deterministic UIA hooks. Release builds do not contain these branches, and a Debug
        // process consumes the failure at most once even under concurrency.
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_DOWNLOAD_OPTIONS_FAIL_ONCE") == "1"
            && Interlocked.CompareExchange(ref _downloadOptionsFailOnceConsumed, 1, 0) == 0)
            throw new HttpRequestException("Simulated download-option preload failure.");

        // Deterministic UIA hook: model the slow PlaybackInfo response without blocking the
        // dispatcher. The same view-lifetime token must interrupt both this delay and the GET.
        if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_DOWNLOAD_OPTIONS_DELAY_MS"),
                out var delayMs) && delayMs > 0)
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
#endif

        var info = await WithRetry(token => _client.Items[itemId].PlaybackInfo.GetAsync(cfg =>
            cfg.QueryParameters.UserId = UserId, token), cancellationToken).ConfigureAwait(false);
        var options = new List<DownloadOption>();
        foreach (var source in info?.MediaSources ?? [])
        {
            var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            if (video?.Height is not { } height)
                continue;
            var resLabel = ResolutionLabel(video.Width, height);
            var sizeLabel = source.Size is { } size
                ? Downloads.DownloadManager.FormatBytes(size)
                : "Unknown size";
            options.Add(new DownloadOption($"Original · {resLabel} · {sizeLabel}",
                source.Id, IsOriginal: true, MaxWidth: null, VideoBitRate: null,
                EstimatedSizeBytes: source.Size, source.Container, "Original"));

            var durationSecs = (source.RunTimeTicks ?? 0) / 10_000_000;
            foreach (var (tierHeight, tierWidth, tierBitrate) in (ReadOnlySpan<(int, int, int)>)
                     [(1080, 1920, 8_000_000), (720, 1280, 4_000_000), (480, 854, 2_000_000)])
            {
                if (height <= tierHeight)
                    continue;
                long? estimated = durationSecs > 0 ? (long)tierBitrate * durationSecs / 8 : null;
                // A "smaller" tier whose estimate exceeds the original is pointless —
                // low-bitrate sources hit this (found in screenshot review: a 520 MB
                // 720p special offered a ~809 MB 480p "downscale").
                if (estimated is { } est && source.Size is { } srcSize && est >= srcSize)
                    continue;
                var estLabel = estimated is { } e
                    ? $"~{Downloads.DownloadManager.FormatBytes(e)}"
                    : "Transcoded";
                options.Add(new DownloadOption(
                    FormattableString.Invariant($"{tierHeight}p · {estLabel}"),
                    source.Id, IsOriginal: false, tierWidth, tierBitrate,
                    estimated, "mp4", FormattableString.Invariant($"{tierHeight}p")));
            }
            break;   // picker reflects the default (first) media source
        }
        return options;
    }

    /// <summary>Height-bucketed resolution label. Width decides 4K on its own because a
    /// scope-ratio 4K source is only ~1600 px tall, which the height buckets would call 1440p.</summary>
    private static string ResolutionLabel(int? width, int? height)
        => width is >= 3840
            ? "4K"
            : height switch
            {
                >= 2160 => "4K",
                >= 1440 => "1440p",
                >= 1080 => "1080p",
                >= 720 => "720p",
                >= 480 => "480p",
                > 0 => FormattableString.Invariant($"{height}p"),
                _ => "",
            };

    /// <summary>Every media source (alternate version) of an item, for the detail-view version
    /// picker. Empty on failure — the picker is an enhancement, so a bad response hides it
    /// rather than surfacing an error over an item that plays fine.
    /// <para>Disk-cached (M19) on the same terms as <see cref="GetMediaStreamsAsync"/> and for
    /// the same reason: the source list is user-state-free playback metadata, and uncached it
    /// puts a SECOND PlaybackInfo round-trip next to the streams one on every detail-view open,
    /// which is exactly the cost that cache exists to spare the flaky LAN. Only a non-empty
    /// answer is stored — an enumeration that failed must retry on the next open rather than
    /// sit on a hidden button for the whole TTL.</para></summary>
    public async Task<IReadOnlyList<MediaVersion>> GetMediaVersionsAsync(Guid itemId)
        => await Imaging.MetadataCache.GetOrFetchAsync($"versions:{ServerUrl}:{itemId:N}",
            TimeSpan.FromHours(1), () => FetchMediaVersionsAsync(itemId)).ConfigureAwait(false)
            ?? [];

    private async Task<List<MediaVersion>?> FetchMediaVersionsAsync(Guid itemId)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var body = new PlaybackInfoDto
            {
                UserId = UserId,
                MaxStreamingBitrate = UnlimitedBitrateBps,
                EnableDirectPlay = true,
                EnableDirectStream = true,
                EnableTranscoding = true,
                // The one field that differs from NegotiatePlaybackAsync, and the reason this
                // POST may go through WithRetry at all: enumerating versions must not open a
                // live stream or hand back an ffmpeg job for a source nobody picked. Nothing
                // here adopts the returned PlaySessionId either, so a retried request leaks no
                // session the way a retried negotiation would (B12).
                AutoOpenLiveStream = false,
                DeviceProfile = BuildDeviceProfile(UnlimitedBitrateBps),
            };
            var info = await WithRetry(() => _client.Items[itemId].PlaybackInfo.PostAsync(body))
                .ConfigureAwait(false);
            var versions = new List<MediaVersion>();
            foreach (var source in info?.MediaSources ?? [])
            {
                if (source.Id is not { Length: > 0 } id)
                    continue;
                var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
                versions.Add(new MediaVersion(id,
                    source.Name is { Length: > 0 } name
                        ? name
                        : DerivedVersionLabel(video, source.Container),
                    source.Container, source.Bitrate, video?.Width, video?.Height, source.Size,
                    // The SDK exposes no primary-version marker on MediaSourceInfo (checked
                    // against 2025.10.21), so "default" is the order the server returned.
                    IsDefault: versions.Count == 0));
            }
            Diagnostics.AppLog.Detail("jellyfin", FormattableString.Invariant(
                $"event=media_versions outcome=success elapsed_ms={started.ElapsedMilliseconds} item={itemId:N} count={versions.Count}"));
            // null, not an empty list: GetOrFetchAsync stores anything non-null, and a response
            // that named no source is a non-answer — caching it would hide the picker for an
            // hour on an item that has versions.
            return versions.Count > 0 ? versions : null;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("jellyfin", FormattableString.Invariant(
                $"event=media_versions outcome=failure elapsed_ms={started.ElapsedMilliseconds} item={itemId:N}"), ex);
            return null;
        }
    }

    /// <summary>Label for a media source the server did not name: resolution, video codec and
    /// container are what actually tell two unnamed versions of one item apart.</summary>
    private static string DerivedVersionLabel(MediaStream? video, string? container)
    {
        var sb = new StringBuilder(ResolutionLabel(video?.Width, video?.Height));
        if (video?.Codec is { Length: > 0 } codec)
            sb.Append(sb.Length > 0 ? " " : "").Append(codec.ToUpperInvariant());
        if (container is { Length: > 0 } c)
            sb.Append(sb.Length > 0 ? $" ({c})" : c);
        return sb.Length > 0 ? sb.ToString() : "Unknown version";
    }

    /// <summary>
    /// Negotiates playback with the server (POST PlaybackInfo): direct play when the
    /// server allows it under the bitrate cap, else the server's HLS transcode URL.
    /// Returns null when negotiation fails entirely (caller falls back to direct play
    /// with client-side reporting, the pre-negotiation behavior).
    /// </summary>
    /// <param name="maxBitrateBps">Bitrate cap in bits/s; 0 = unlimited.</param>
    /// <param name="forceTranscode">Disables direct play/stream — used to retry a
    /// failed direct play via the transcoder.</param>
    /// <param name="audioStreamIndex">Preselected audio stream (Jellyfin global index);
    /// under transcode the server bakes it into the HLS stream.</param>
    /// <param name="subtitleStreamIndex">Preselected subtitle stream (Jellyfin global
    /// index); -1 = explicitly no subtitles (jellyfin-web convention).</param>
    /// <param name="mediaSourceId">Required alongside the stream indices: the server
    /// ignores them unless the POST names the media source they belong to.</param>
    public async Task<PlaybackDecision?> NegotiatePlaybackAsync(Guid itemId, int maxBitrateBps,
        bool forceTranscode = false, int? audioStreamIndex = null, int? subtitleStreamIndex = null,
        string? mediaSourceId = null)
    {
        // Instrumented here rather than through WithRetry, because this call deliberately does not
        // go through it (B12): the one server call with no retry is also the one whose failure
        // silently downgrades playback, so it needs its own outcome record.
        var started = System.Diagnostics.Stopwatch.StartNew();
        // "Unlimited" must be an explicit huge value (see UnlimitedBitrateBps); jellyfin-web
        // always sends a value too. Resolved outside the try so the failure log can still
        // name what was asked for.
        var effectiveBitrate = maxBitrateBps > 0 ? maxBitrateBps : UnlimitedBitrateBps;
        try
        {
#if DEBUG
            await ApplyQualityNegotiationTestHooksAsync(effectiveBitrate).ConfigureAwait(false);
#endif
            var body = new PlaybackInfoDto
            {
                UserId = UserId,
                MaxStreamingBitrate = effectiveBitrate,
                EnableDirectPlay = !forceTranscode,
                EnableDirectStream = !forceTranscode,
                EnableTranscoding = true,
                AutoOpenLiveStream = true,
                AlwaysBurnInSubtitleWhenTranscoding = false,
                AudioStreamIndex = audioStreamIndex,
                SubtitleStreamIndex = subtitleStreamIndex,
                MediaSourceId = mediaSourceId,
                DeviceProfile = BuildDeviceProfile(effectiveBitrate),
            };
            // NOT WithRetry (B12). This POST allocates server-side state per call — a
            // PlaySessionId, an opened live stream (AutoOpenLiveStream above) and, for a
            // transcoded source, an ffmpeg job. WithRetry fires on TaskCanceledException, which
            // is also how the 30 s request timeout surfaces, so a slow-but-successful negotiation
            // was being sent twice: two sessions, of which the client learns only the second, so
            // nothing ever sends a stop report for the first and it leaks until the server reaps
            // it. A failed negotiation already has a graceful fallback (null → direct play with
            // client-side reporting), which is a far better outcome than a duplicate session.
            var info = await _client.Items[itemId].PlaybackInfo.PostAsync(body).ConfigureAwait(false);
            var source = info?.MediaSources?.FirstOrDefault();
            if (source is null || info?.PlaySessionId is not { Length: > 0 } playSessionId)
            {
                NegotiateDetail(itemId, "incomplete", started, forceTranscode, effectiveBitrate);
                return null;
            }

            if (!forceTranscode && source.SupportsDirectPlay == true)
            {
                NegotiateDetail(itemId, "direct_play", started, forceTranscode, effectiveBitrate);
                return new PlaybackDecision(GetStreamUrl(itemId, source.Id), false, playSessionId,
                    source.Id, audioStreamIndex ?? source.DefaultAudioStreamIndex,
                    subtitleStreamIndex ?? source.DefaultSubtitleStreamIndex);
            }
            if (source.TranscodingUrl is { Length: > 0 } transcodingUrl)
            {
                NegotiateDetail(itemId, "transcode", started, forceTranscode, effectiveBitrate);
                return new PlaybackDecision(ServerUrl + transcodingUrl, true, playSessionId,
                    source.Id, audioStreamIndex ?? source.DefaultAudioStreamIndex,
                    subtitleStreamIndex ?? source.DefaultSubtitleStreamIndex,
                    source.MediaStreams?.Any(s =>
                        s.Index == (subtitleStreamIndex ?? source.DefaultSubtitleStreamIndex)
                        && s.DeliveryMethod == MediaStream_DeliveryMethod.Encode) == true);
            }
            // No transcode URL offered — direct is the only option left.
            NegotiateDetail(itemId, forceTranscode ? "no_transcode_offered" : "direct_play",
                started, forceTranscode, effectiveBitrate);
            return forceTranscode
                ? null
                : new PlaybackDecision(GetStreamUrl(itemId, source.Id), false, playSessionId,
                    source.Id, audioStreamIndex ?? source.DefaultAudioStreamIndex,
                    subtitleStreamIndex ?? source.DefaultSubtitleStreamIndex);
        }
        catch (Exception ex)
        {
            NegotiateDetail(itemId, "failure", started, forceTranscode, effectiveBitrate, ex);
            return null;
        }
    }

#if DEBUG
    /// <summary>Deterministic guest-suite seams. A hook applies only to the exact requested
    /// bitrate, so initial playback remains untouched while one quality request is delayed
    /// or failed. Release builds do not contain this code.</summary>
    private static async Task ApplyQualityNegotiationTestHooksAsync(int effectiveBitrateBps)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(
                "LIGHTWEAVER_TEST_NEGOTIATE_DELAY_BITRATE_BPS"), out var delayedBitrate)
            && delayedBitrate == effectiveBitrateBps
            && int.TryParse(Environment.GetEnvironmentVariable(
                "LIGHTWEAVER_TEST_NEGOTIATE_DELAY_MS"), out var delayMs)
            && delayMs > 0)
        {
            Diagnostics.AppLog.Detail("jellyfin", FormattableString.Invariant(
                $"event=quality_test outcome=delay_started max_bitrate_bps={effectiveBitrateBps}"));
            await Task.Delay(Math.Clamp(delayMs, 1, 10_000)).ConfigureAwait(false);
        }
        if (int.TryParse(Environment.GetEnvironmentVariable(
                "LIGHTWEAVER_TEST_NEGOTIATE_FAIL_BITRATE_BPS"), out var failedBitrate)
            && failedBitrate == effectiveBitrateBps)
        {
            throw new InvalidOperationException("Quality negotiation failure requested by test hook.");
        }
    }
#endif

    /// <summary>Tears down whatever the server started for a decision that was negotiated and
    /// then never handed to mpv — a superseded quality change, or a playback the user left
    /// during the POST. Idempotent: for a VOD item the PlaybackInfo POST only mints a
    /// PlaySessionId and computes a TranscodingUrl, and ffmpeg does not start until the
    /// playlist is first fetched, so most discards find nothing to kill.
    ///
    /// <para>Deliberately NOT <c>POST /Sessions/Playing/Stopped</c>: that also clears the
    /// device's NowPlaying and saves the item's resume position from the reported ticks, so
    /// using it to drop an orphan would overwrite the resume point of the stream that is
    /// still playing. And deliberately not retried — a lost teardown costs an idle ffmpeg job
    /// the server reaps on its own, while a retry storm on the live session costs more.</para></summary>
    public async Task DiscardUnplayedDecisionAsync(PlaybackDecision decision)
    {
        try
        {
            await _client.Videos.ActiveEncodings.DeleteAsync(cfg =>
            {
                cfg.QueryParameters.DeviceId = _deviceId;
                cfg.QueryParameters.PlaySessionId = decision.PlaySessionId;
            }).ConfigureAwait(false);
            DiscardDetail(decision, "sent");
        }
        catch (Exception ex)
        {
            DiscardDetail(decision, "failure", ex);
        }
    }

    private static void DiscardDetail(PlaybackDecision decision, string outcome, Exception? ex = null)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        // Presence, not the value. PlaybackReporter.Start logs `server_session=true|false` for the
        // same field deliberately, and AppLog.Redact matches URLs, Token=, api_key= and
        // AccessToken - not a bare session GUID, so a value written here would survive into the
        // log file verbatim.
        var line = $"event=discard_decision outcome={outcome} server_session={(decision.PlaySessionId is null ? "false" : "true")} was_transcode={(decision.IsTranscode ? "true" : "false")}";
        if (ex is not null)
            line += $" error={ex.GetType().Name}";
        Diagnostics.AppLog.Detail("jellyfin", line);
    }

    /// <summary>The negotiation verdict: which play method the server granted, or how it failed.
    /// The item GUID is allowed (correlation); the negotiated URL is not.
    ///
    /// <para>The posted bitrate and the forced-transcode flag are logged because they are the only
    /// evidence of what was actually asked for: the request body itself is never captured, and
    /// with a per-item quality override the value no longer follows from the Settings file a
    /// test can read. There is deliberately no <c>direct_play=</c> field: it was only ever
    /// <c>!forced_transcode</c> restated on the same line, and it read as though the SERVER had
    /// granted direct play, which is what <c>outcome=</c> carries.</para></summary>
    private static void NegotiateDetail(Guid itemId, string outcome,
        System.Diagnostics.Stopwatch started, bool forceTranscode, int effectiveBitrateBps,
        Exception? ex = null)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant(
            $"event=negotiate outcome={outcome} elapsed_ms={started.ElapsedMilliseconds} item={itemId:N} forced_transcode={(forceTranscode ? "true" : "false")} max_bitrate_bps={effectiveBitrateBps}");
        if (ex is not null)
            line += $" error={ex.GetType().Name}";
        Diagnostics.AppLog.Detail("jellyfin", line);
    }

    /// <summary>
    /// mpv direct-plays essentially every container/codec, so the profile's real job
    /// is carrying the bitrate cap and declaring HLS/ts as the transcode target.
    /// Empty container/codec strings in a DirectPlayProfile mean "match everything".
    /// </summary>
    private static DeviceProfile BuildDeviceProfile(int maxBitrateBps) => new()
    {
        Name = "LightWeaver",
        MaxStreamingBitrate = maxBitrateBps,
        DirectPlayProfiles =
        [
            new DirectPlayProfile { Type = DirectPlayProfile_Type.Video },
            new DirectPlayProfile { Type = DirectPlayProfile_Type.Audio },
        ],
        TranscodingProfiles =
        [
            new TranscodingProfile
            {
                Type = TranscodingProfile_Type.Video,
                Container = "ts",
                VideoCodec = "h264",
                AudioCodec = "aac,ac3,mp3",
                Protocol = TranscodingProfile_Protocol.Hls,
                Context = TranscodingProfile_Context.Streaming,
            },
        ],
        // "subrip" is the codec name external SRTs actually report — without it the
        // server transcodes with SubtitleCodecNotSupported (found live).
        SubtitleProfiles =
        [
            .. new[] { "srt", "subrip", "ass", "ssa", "vtt" }
                .Select(f => new SubtitleProfile { Format = f, Method = SubtitleProfile_Method.External }),
            .. new[] { "srt", "subrip", "ass", "ssa", "vtt", "pgssub", "pgs", "dvdsub", "mov_text" }
                .Select(f => new SubtitleProfile { Format = f, Method = SubtitleProfile_Method.Embed }),
        ],
    };

    public string GetPrimaryImageUrl(Guid itemId, string? tag, int maxWidth = 300)
        => $"{ServerUrl}/Items/{itemId}/Images/Primary?maxWidth={maxWidth}&quality=90{TagParam(tag)}";

    public string GetThumbImageUrl(Guid itemId, string? tag, int maxWidth = 480)
        => $"{ServerUrl}/Items/{itemId}/Images/Thumb?maxWidth={maxWidth}&quality=90{TagParam(tag)}";

    public string GetBackdropImageUrl(Guid itemId, string? tag, int maxWidth = 1280)
        => $"{ServerUrl}/Items/{itemId}/Images/Backdrop?maxWidth={maxWidth}&quality=80{TagParam(tag)}";

    private static string TagParam(string? tag) => tag is null ? "" : $"&tag={tag}";

    private void ApplySession(string url, string token, Guid userId)
    {
        ServerUrl = url;
        AccessToken = token;
        UserId = userId;
        _settings.SetAccessToken(token);
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = "http://" + trimmed;
        return trimmed;
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        ApiException { ResponseStatusCode: 401 } => "Wrong username or password.",
        ApiException { ResponseStatusCode: var s and >= 500 } => $"Server error ({s}). Is this a Jellyfin server?",
        ApiException { ResponseStatusCode: var s } => $"Login failed (HTTP {s}).",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "Server not found. Check the URL.",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "Could not connect to the server.",
        HttpRequestException hre => $"Network error: {hre.Message}",
        TaskCanceledException => "Connection timed out.",
        _ => $"Login failed: {ex.Message}",
    };
}
