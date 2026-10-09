using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;

/// <summary>
/// Jellyfin 12 behaviour probe. Measures, against the signed-in test server, the server
/// behaviours that changed in 12.0 under code written against 10.11: the two 10.11 quirks the
/// device profile encodes (explicit "unlimited" bitrate, the <c>subrip</c> codec name), graphical
/// subtitles on a forced transcode, duplicate playlist entries, local-vs-server name ordering,
/// empty Similar answers and multi-version sources.
/// <para>Windowless, like <see cref="ResumeItemTypesFixture"/>: no window, no input, no mpv, so it
/// runs on the host. Every leg prints what it measured. A leg FAILS only on a client defect the
/// data proves; a leg whose server data cannot answer says so as UNANSWERED, never as a pass.</para>
/// <para>The one write is the duplicate-playlist leg: it creates a private playlist on the TEST
/// account and deletes it in a finally, the same way the queue suites stage theirs.</para>
/// </summary>
internal static class Jellyfin12ProbeFixture
{
    public const string Mode = "--jellyfin12-probe";

    private static string CredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightWeaver-Debug", "credentials.dat");

    /// <summary>The app's subtitle formats, as a literal rather than reflected out of
    /// JellyfinService: the oracle must not be the code under test.</summary>
    private static readonly string[] ExternalFormats = ["srt", "subrip", "ass", "ssa", "vtt"];
    private static readonly string[] EmbedFormats = ["srt", "subrip", "ass", "ssa", "vtt", "pgssub", "pgs", "dvdsub", "mov_text"];
    private static readonly string[] GraphicalCodecs = ["pgssub", "pgs", "hdmv_pgs_subtitle", "dvdsub", "dvd_subtitle", "vobsub"];

    private const int UnlimitedBps = 1_000_000_000;
    private const int ScanLimit = 2000;

    private static readonly List<string> Failures = [];
    private static readonly List<string> Unanswered = [];

    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var credentials = ReadTestProfile();
        if (credentials is null)
        {
            Console.WriteLine("SKIP: the Jellyfin 12 probe needs a signed-in debug profile named 'test'.");
            Console.WriteLine("RESULT: SKIP");
            return 2;
        }

        using var service = new JellyfinService();
        if (!await service.ReconnectAsync(credentials).ConfigureAwait(false))
        {
            Console.WriteLine("FAIL: the saved Jellyfin token was rejected. STOP: do not mint or "
                + "re-authorise one; sign the debug profile in again, then re-run.");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // Runtime-only use of the token, and only as a header. Never logged, never in a URL.
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"MediaBrowser Token=\"{credentials.AccessToken}\"");
        var api = new Api(http, credentials.ServerUrl.TrimEnd('/'), credentials.UserId.ToString("N"));

        var info = await api.GetAsync("/System/Info").ConfigureAwait(false);
        Console.WriteLine($"SERVER: version={info?["Version"]}");

        var videos = await ScanVideosAsync(api).ConfigureAwait(false);
        Console.WriteLine($"SCAN: {videos.Count} movies/episodes with media sources");

        await Leg("quirk 1: unlimited bitrate must be explicit", () => UnlimitedBitrateAsync(api, videos));
        await Leg("quirk 2: external SRT codec name", () => ExternalSubtitleCodecAsync(api, videos));
        await Leg("graphical subtitles on a forced transcode", () => GraphicalSubtitleAsync(api, videos));
        await Leg("multi-version sources", () => VersionsAsync(api, service, videos));
        await Leg("playlist duplicates and order", () => PlaylistAsync(api, service, videos));
        await Leg("local name sort vs server SortName", () => SortAsync(api, service));
        await Leg("Similar answers", () => SimilarAsync(api, videos));

        foreach (var u in Unanswered)
            Console.WriteLine("UNANSWERED: " + u);
        foreach (var f in Failures)
            Console.WriteLine("FAIL: " + f);
        var result = Failures.Count > 0 ? "FAIL" : Unanswered.Count > 0 ? "INCOMPLETE" : "PASS";
        Console.WriteLine($"RESULT: {result} ({Failures.Count} failures, {Unanswered.Count} unanswered)");
        return Failures.Count > 0 ? 1 : Unanswered.Count > 0 ? 2 : 0;
    }

    private static async Task Leg(string name, Func<Task> body)
    {
        Console.WriteLine($"--- {name}");
        try { await body().ConfigureAwait(false); }
        catch (Exception e)
        {
            // Exception text from the HTTP stack can name the host; the type is enough to act on.
            Failures.Add($"{name}: leg threw {e.GetType().Name}: {(e is ProbeException ? e.Message : "(message withheld)")}");
        }
    }

    // ---- scan --------------------------------------------------------------------------------

    private sealed record Video(string Id, string Name, string Type, JsonArray Sources);

    private static async Task<List<Video>> ScanVideosAsync(Api api)
    {
        // One pass per type: a combined name-sorted scan fills its cap with episodes before it
        // reaches a single movie.
        var videos = new List<Video>();
        foreach (var type in new[] { "Movie", "Episode" })
        {
            for (var start = 0; start < ScanLimit; start += 200)
            {
                var page = await api.GetAsync($"/Items?userId={api.User}&Recursive=true"
                    + $"&IncludeItemTypes={type}&Fields=MediaSources,MediaStreams&StartIndex={start}&Limit=200"
                    + "&SortBy=SortName&EnableImages=false").ConfigureAwait(false);
                var items = page?["Items"]?.AsArray() ?? [];
                foreach (var item in items)
                {
                    if (item?["MediaSources"] is JsonArray sources && sources.Count > 0)
                        videos.Add(new Video(item["Id"]!.ToString(), item["Name"]?.ToString() ?? "?",
                            item["Type"]?.ToString() ?? "?", sources));
                }
                if (items.Count < 200)
                    break;
            }
        }
        return videos;
    }

    private static IEnumerable<JsonObject> Streams(JsonNode source) =>
        (source["MediaStreams"]?.AsArray() ?? []).OfType<JsonObject>();

    // ---- legs --------------------------------------------------------------------------------

    /// <summary>10.11: omitting MaxStreamingBitrate made the server cap at ~8 Mbps and transcode.
    /// Re-measured on a source above that cap, with and without the explicit 1 Gbps.</summary>
    private static async Task UnlimitedBitrateAsync(Api api, List<Video> videos)
    {
        var candidate = videos
            .Select(v => (v, bitrate: v.Sources[0]?["Bitrate"]?.GetValue<long>() ?? 0))
            .Where(x => x.bitrate > 12_000_000)
            .OrderBy(x => x.bitrate)
            .FirstOrDefault();
        if (candidate.v is null)
        {
            Unanswered.Add("quirk 1: no source above 12 Mbps to discriminate an ~8 Mbps cap.");
            return;
        }
        var explicitInfo = await api.PlaybackInfoAsync(candidate.v.Id, UnlimitedBps, profileBitrate: UnlimitedBps).ConfigureAwait(false);
        var omittedInfo = await api.PlaybackInfoAsync(candidate.v.Id, null, profileBitrate: null).ConfigureAwait(false);
        var explicitDirect = explicitInfo?["MediaSources"]?[0]?["SupportsDirectPlay"]?.GetValue<bool>();
        var omittedDirect = omittedInfo?["MediaSources"]?[0]?["SupportsDirectPlay"]?.GetValue<bool>();
        Console.WriteLine($"  source {candidate.bitrate / 1_000_000.0:F1} Mbps: explicit 1 Gbps -> direct={explicitDirect}; "
            + $"omitted -> direct={omittedDirect} transcodeReason={Reasons(omittedInfo)}");
        if (explicitDirect != true)
            Failures.Add($"quirk 1: the app's explicit 1 Gbps no longer direct-plays a {candidate.bitrate / 1_000_000.0:F1} Mbps source "
                + $"(reasons {Reasons(explicitInfo)}).");
        Console.WriteLine(omittedDirect == true
            ? "  MEASURED: 12.x no longer caps an omitted bitrate; the explicit value is now belt-and-braces."
            : "  MEASURED: 12.x still caps an omitted bitrate; the explicit value remains required.");
    }

    /// <summary>10.11: external SRTs report codec <c>subrip</c>. Every external text codec the
    /// server reports must be in the profile, or the item transcodes.</summary>
    private static Task ExternalSubtitleCodecAsync(Api api, List<Video> videos)
    {
        var codecs = videos
            .SelectMany(v => Streams(v.Sources[0]!))
            .Where(s => s["Type"]?.ToString() == "Subtitle" && s["IsExternal"]?.GetValue<bool>() == true)
            .GroupBy(s => s["Codec"]?.ToString()?.ToLowerInvariant() ?? "(null)")
            .ToDictionary(g => g.Key, g => g.Count());
        if (codecs.Count == 0)
        {
            Unanswered.Add("quirk 2: no external subtitle in the scanned library.");
            return Task.CompletedTask;
        }
        Console.WriteLine("  external subtitle codecs: " + string.Join(", ", codecs.Select(kv => $"{kv.Key}={kv.Value}")));
        var missing = codecs.Keys.Where(c => !ExternalFormats.Contains(c) && !GraphicalCodecs.Contains(c)).ToList();
        if (missing.Count > 0)
            Failures.Add("quirk 2: external text subtitle codec(s) the profile does not declare: " + string.Join(", ", missing));
        return Task.CompletedTask;
    }

    /// <summary>12.0 PR #17512: graphical subtitles can stay client-rendered during a remux.
    /// The app reads burn-in from DeliveryMethod == Encode alone, so the leg records what the
    /// server answers for a PGS/VobSub stream on the app's forced-transcode request.</summary>
    private static async Task GraphicalSubtitleAsync(Api api, List<Video> videos)
    {
        // A source whose video the transcoding profile can copy (h264) is the only kind on which
        // the server can choose a remux, which is where 12.0 changed graphical subtitles.
        static string VideoCodec(Video v) =>
            Streams(v.Sources[0]!).FirstOrDefault(s => s["Type"]?.ToString() == "Video")?["Codec"]?.ToString()?.ToLowerInvariant() ?? "?";
        var hits = videos
            .SelectMany(v => Streams(v.Sources[0]!).Select(s => (v, s)))
            .Where(x => x.s["Type"]?.ToString() == "Subtitle"
                && GraphicalCodecs.Contains(x.s["Codec"]?.ToString()?.ToLowerInvariant() ?? ""))
            .ToList();
        Console.WriteLine($"  graphical subtitle streams: {hits.Count} on {hits.Select(x => x.v.Id).Distinct().Count()} item(s); "
            + "video codecs: " + string.Join(", ", hits.GroupBy(x => $"{x.s["Codec"]}/{VideoCodec(x.v)}").Select(g => $"{g.Key}={g.Count()}")));
        var hit = hits.FirstOrDefault(x => VideoCodec(x.v) == "h264");
        if (hit.v is null)
        {
            Unanswered.Add("graphical subtitles: no PGS/VobSub stream on an h264 source, so the remux case cannot be "
                + "measured (operator: provision an h264 MKV with a PGS track).");
            hit = hits.FirstOrDefault();
        }
        if (hit.v is null)
        {
            Unanswered.Add("graphical subtitles: no PGS/VobSub stream in the scanned library (operator: provision a PGS fixture).");
            return;
        }
        var index = hit.s["Index"]!.GetValue<int>();
        var sourceId = hit.v.Sources[0]!["Id"]!.ToString();
        Console.WriteLine($"  item '{hit.v.Name}' stream {index} codec={hit.s["Codec"]} video={VideoCodec(hit.v)} external={hit.s["IsExternal"]}");
        foreach (var (label, directStream) in new[] { ("forced transcode (app)", false), ("remux allowed", true) })
        {
            var answer = await api.PlaybackInfoAsync(hit.v.Id, UnlimitedBps, UnlimitedBps,
                directPlay: false, directStream: directStream, subtitleIndex: index, mediaSourceId: sourceId).ConfigureAwait(false);
            var source = (answer?["MediaSources"]?.AsArray() ?? [])
                .FirstOrDefault(s => s?["Id"]?.ToString() == sourceId);
            var stream = source is null ? null : Streams(source).FirstOrDefault(s => s["Index"]?.GetValue<int>() == index);
            var url = source?["TranscodingUrl"]?.ToString() ?? "";
            var method = QueryValue(url, "SubtitleMethod");
            if (source is null || stream is null || url.Length == 0
                || string.IsNullOrEmpty(stream["DeliveryMethod"]?.ToString()) || string.IsNullOrEmpty(method))
            {
                Unanswered.Add($"graphical subtitles ({label}): PlaybackInfo omitted the selected source, subtitle delivery, or transcode URL/method.");
                continue;
            }
            Console.WriteLine($"  {label}: delivery={stream?["DeliveryMethod"]} url.SubtitleMethod={method ?? "(none)"} "
                + $"directStream={source?["SupportsDirectStream"]} reasons={Reasons(answer)}");
            // The app's contract: burned in exactly when the stream says Encode. A URL that encodes
            // while the stream says otherwise would draw the subtitle twice (mpv + burn-in).
            if (string.Equals(method, "Encode", StringComparison.OrdinalIgnoreCase)
                && stream?["DeliveryMethod"]?.ToString() != "Encode")
                Failures.Add($"graphical subtitles ({label}): TranscodingUrl burns in but the stream reports "
                    + $"{stream?["DeliveryMethod"]}; the app would not suppress mpv's copy.");
        }
    }

    /// <summary>Measure source presence in the raw server response, then verify the
    /// production decision. A selected source need not be first in a multi-source answer.</summary>
    private static async Task VersionsAsync(Api api, JellyfinService service, List<Video> videos)
    {
        var multi = videos.Where(v => v.Sources.Count > 1).ToList();
        Console.WriteLine($"  multi-version items: {multi.Count} " +
            $"(episodes {multi.Count(v => v.Type == "Episode")}, movies {multi.Count(v => v.Type == "Movie")})");
        var target = multi.FirstOrDefault(v => v.Type == "Episode") ?? multi.FirstOrDefault() ?? videos.FirstOrDefault();
        if (target is null)
        {
            Unanswered.Add("source selection: no playable source in the scanned library.");
            return;
        }
        if (multi.Count == 0)
            Console.WriteLine("  NOTE: no live multi-source fixture; reordered multi-source answers are covered by the quality contract mode.");
        var selectedIndex = target.Sources.Count > 1 ? 1 : 0;
        var second = target.Sources[selectedIndex]!["Id"]!.ToString();
        var answer = await api.PlaybackInfoAsync(target.Id, UnlimitedBps, UnlimitedBps, mediaSourceId: second).ConfigureAwait(false);
        var ids = (answer?["MediaSources"]?.AsArray() ?? []).Select(s => s?["Id"]?.ToString()).ToList();
        Console.WriteLine($"  {target.Type} '{target.Name}': posted source index {selectedIndex}; answer lists {ids.Count} source(s), first is "
            + (ids.FirstOrDefault() == second ? "the posted one" : "a different one"));
        if (!ids.Contains(second))
        {
            Unanswered.Add("source selection: PlaybackInfo omitted the requested source; production selection cannot be verified.");
            return;
        }
        var selectedDecision = await service.NegotiatePlaybackAsync(Guid.Parse(target.Id), 0, mediaSourceId: second).ConfigureAwait(false);
        try
        {
            if (selectedDecision?.MediaSourceId != second)
                Failures.Add("source selection: production negotiation did not select the explicitly requested source.");
            else
                Console.WriteLine("  production decision selects the requested source regardless of its response position.");
        }
        finally
        {
            if (selectedDecision is not null)
                await service.DiscardUnplayedDecisionAsync(selectedDecision).ConfigureAwait(false);
        }
        var plain = await api.PlaybackInfoAsync(target.Id, UnlimitedBps, UnlimitedBps).ConfigureAwait(false);
        var plainIds = (plain?["MediaSources"]?.AsArray() ?? []).Select(s => s?["Id"]?.ToString()).ToList();
        Console.WriteLine($"  without MediaSourceId: {plainIds.Count} source(s), order matches /Items: "
            + plainIds.SequenceEqual(target.Sources.Select(s => s?["Id"]?.ToString())));
    }

    /// <summary>12.0 PR #17416: a playlist may hold the same item twice. Stages [B, A, B] with
    /// A sorting before B by name, so both a collapsed duplicate and a name re-sort show.</summary>
    private static async Task PlaylistAsync(Api api, JellyfinService service, List<Video> videos)
    {
        var movies = videos.Where(v => v.Type == "Movie").ToList();
        if (movies.Count < 2)
        {
            Unanswered.Add("playlist: fewer than two movies to stage a playlist with.");
            return;
        }
        // The scan is SortName-ordered, so the first movie sorts before the last.
        var a = movies[0];
        var b = movies[^1];
        var created = await api.PostAsync("/Playlists", new JsonObject
        {
            ["Name"] = "LW probe duplicates " + Guid.NewGuid().ToString("N")[..8],
            ["Ids"] = new JsonArray(b.Id, a.Id, b.Id),
            ["UserId"] = api.User,
            ["MediaType"] = "Video",
            ["IsPublic"] = false,
        }).ConfigureAwait(false);
        var playlistId = created?["Id"]?.ToString() ?? throw new ProbeException("playlist create returned no Id");
        try
        {
            var expected = new[] { b.Id, a.Id, b.Id };
            var viaPlaylists = await api.GetAsync($"/Playlists/{playlistId}/Items?userId={api.User}").ConfigureAwait(false);
            var playlistIds = Ids(viaPlaylists);
            var entryIds = (viaPlaylists?["Items"]?.AsArray() ?? []).Select(i => i?["PlaylistItemId"]?.ToString()).ToList();
            Console.WriteLine($"  /Playlists/{{id}}/Items: {Shape(playlistIds, a.Id, b.Id)}; distinct PlaylistItemIds={entryIds.Where(x => x != null).Distinct().Count()}");
            if (playlistIds.Count != 3)
                Unanswered.Add($"playlist: the server stored {playlistIds.Count} entries for [B, A, B]; duplicates were not kept, so the client paths cannot be exercised.");

            var viaParent = await api.GetAsync($"/Items?userId={api.User}&ParentId={playlistId}&SortBy=SortName&SortOrder=Ascending").ConfigureAwait(false);
            Console.WriteLine($"  /Items?ParentId (SortName, the grid's query): {Shape(Ids(viaParent), a.Id, b.Id)}");

            // The app's own calls.
            var queue = await service.GetPlaylistItemsAsync(Guid.Parse(playlistId)).ConfigureAwait(false);
            var queueIds = queue.Select(i => i.Id.ToString("N")).ToList();
            Console.WriteLine($"  app GetPlaylistItemsAsync (Play all / Shuffle): {Shape(queueIds, a.Id, b.Id)}");
            if (playlistIds.Count == 3 && !queueIds.SequenceEqual(expected))
                Failures.Add("playlist: GetPlaylistItemsAsync does not return the playlist in order with its duplicate.");

            Console.WriteLine("  NOTE: production playlist view uses this ordered endpoint; cold/warm grid occurrence checks run in the guest UI mode.");
        }
        finally
        {
            try { await api.DeleteAsync($"/Items/{playlistId}").ConfigureAwait(false); }
            catch (Exception e) { Failures.Add($"playlist: could not delete the staged playlist ({e.GetType().Name}); remove 'LW probe duplicates' by hand."); }
        }
    }

    /// <summary>12.0 PR #17503: the server sorts names by CleanName. The folder cache re-sorts
    /// locally on SortName, so for each library the two orders are compared item for item.</summary>
    private static async Task SortAsync(Api api, JellyfinService service)
    {
        var views = await api.GetAsync($"/UserViews?userId={api.User}").ConfigureAwait(false);
        var compared = 0;
        foreach (var view in views?["Items"]?.AsArray() ?? [])
        {
            var type = view?["CollectionType"]?.ToString();
            if (type is not ("movies" or "tvshows" or "music" or "boxsets" or "homevideos" or null))
                continue;
            var id = Guid.Parse(view!["Id"]!.ToString());
            var server = new List<MediaItem>();
            for (var start = 0; start < ScanLimit; start += 200)
            {
                var (page, total) = await service.GetFolderPageAsync(id, start, 200, CancellationToken.None).ConfigureAwait(false);
                server.AddRange(page);
                if (page.Count < 200 || server.Count >= total)
                    break;
            }
            if (server.Count < 2)
                continue;
            compared++;
            var local = BrowseFolderCache.ApplyQuery(server, ItemSortBy.SortName, SortOrder.Ascending, null, null, null, 0);
            var moved = Enumerable.Range(0, server.Count).Count(i => server[i].Id != local[i].Id);
            if (moved != 0)
                Failures.Add($"sort: {moved} item positions differ between server SortName and the local cache.");
            Console.WriteLine($"  library '{view["Name"]}' ({type ?? "mixed"}): {server.Count} items, {moved} at a different position locally");
            foreach (var i in Enumerable.Range(0, server.Count).Where(i => server[i].Id != local[i].Id).Take(4))
                Console.WriteLine($"    #{i}: server '{server[i].Name}' (sort '{server[i].SortName}') / local '{local[i].Name}' (sort '{local[i].SortName}')");
        }
        if (compared == 0)
            Unanswered.Add("sort: no library with two or more items.");
    }

    /// <summary>12.0: Similar depends on the per-library recommendation source and may be empty.
    /// The detail view collapses the row on empty, so this only records how often that happens.</summary>
    private static async Task SimilarAsync(Api api, List<Video> videos)
    {
        var sample = videos.Where(v => v.Type == "Movie").Take(8)
            .Concat(videos.Where(v => v.Type == "Episode").Take(4)).ToList();
        var counts = new List<int>();
        foreach (var v in sample)
        {
            var answer = await api.GetAsync($"/Items/{v.Id}/Similar?userId={api.User}&limit=12").ConfigureAwait(false);
            counts.Add(answer?["Items"]?.AsArray().Count ?? -1);
        }
        Console.WriteLine($"  Similar counts over {sample.Count} items: [{string.Join(", ", counts)}]; empty={counts.Count(c => c == 0)}");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static List<string> Ids(JsonNode? result) =>
        (result?["Items"]?.AsArray() ?? []).Select(i => Guid.Parse(i!["Id"]!.ToString()).ToString("N")).ToList();

    private static string Shape(IEnumerable<string> ids, string a, string b)
    {
        var na = Guid.Parse(a).ToString("N");
        var nb = Guid.Parse(b).ToString("N");
        return "[" + string.Join(", ", ids.Select(id => id == na ? "A" : id == nb ? "B" : "?")) + "]";
    }

    private static string Reasons(JsonNode? info) =>
        info?["MediaSources"]?[0]?["TranscodingUrl"]?.ToString() is { } url
            ? QueryValue(url, "TranscodeReasons") ?? "(none)"
            : "(no transcode)";

    private static string? QueryValue(string url, string key)
    {
        var q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in url[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private sealed class ProbeException(string message) : Exception(message);

    /// <summary>Raw HTTP, not JellyfinService: the oracle side of each comparison must not be
    /// the code under test.</summary>
    private sealed class Api(HttpClient http, string server, string user)
    {
        public string User => user;

        public async Task<JsonNode?> GetAsync(string path) =>
            await SendAsync(new HttpRequestMessage(HttpMethod.Get, server + path)).ConfigureAwait(false);

        public async Task<JsonNode?> PostAsync(string path, JsonNode body) =>
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, server + path)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            }).ConfigureAwait(false);

        public async Task DeleteAsync(string path) =>
            await SendAsync(new HttpRequestMessage(HttpMethod.Delete, server + path)).ConfigureAwait(false);

        /// <summary>The app's PlaybackInfo POST, with AutoOpenLiveStream off so no live stream is
        /// opened. A null bitrate omits the field, which is what quirk 1 measures.</summary>
        public Task<JsonNode?> PlaybackInfoAsync(string itemId, int? maxBitrate, int? profileBitrate,
            bool directPlay = true, bool directStream = true, int? subtitleIndex = null, string? mediaSourceId = null)
        {
            var profile = new JsonObject
            {
                ["Name"] = "LightWeaver probe",
                ["DirectPlayProfiles"] = new JsonArray(new JsonObject { ["Type"] = "Video" }, new JsonObject { ["Type"] = "Audio" }),
                ["TranscodingProfiles"] = new JsonArray(new JsonObject
                {
                    ["Type"] = "Video", ["Container"] = "ts", ["VideoCodec"] = "h264",
                    ["AudioCodec"] = "aac,ac3,mp3", ["Protocol"] = "hls", ["Context"] = "Streaming",
                }),
                ["SubtitleProfiles"] = new JsonArray(
                    [.. ExternalFormats.Select(f => (JsonNode)new JsonObject { ["Format"] = f, ["Method"] = "External" }),
                     .. EmbedFormats.Select(f => (JsonNode)new JsonObject { ["Format"] = f, ["Method"] = "Embed" })]),
            };
            if (profileBitrate is { } pb)
                profile["MaxStreamingBitrate"] = pb;
            var body = new JsonObject
            {
                ["UserId"] = user,
                ["EnableDirectPlay"] = directPlay,
                ["EnableDirectStream"] = directStream,
                ["EnableTranscoding"] = true,
                ["AutoOpenLiveStream"] = false,
                ["AlwaysBurnInSubtitleWhenTranscoding"] = false,
                ["DeviceProfile"] = profile,
            };
            if (maxBitrate is { } mb) body["MaxStreamingBitrate"] = mb;
            if (subtitleIndex is { } si) body["SubtitleStreamIndex"] = si;
            if (mediaSourceId is { } ms) body["MediaSourceId"] = ms;
            return PostAsync($"/Items/{itemId}/PlaybackInfo", body);
        }

        private async Task<JsonNode?> SendAsync(HttpRequestMessage request)
        {
            using var _ = request;
            HttpResponseMessage response;
            try { response = await http.SendAsync(request).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Connect errors name host and port; keep the server out of the output.
                throw new ProbeException($"{request.Method} could not reach the server ({e.GetType().Name})");
            }
            using var __ = response;
            if (!response.IsSuccessStatusCode)
                throw new ProbeException($"{request.Method} {Redact(request.RequestUri)} -> HTTP {(int)response.StatusCode}");
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }

        private static string Redact(Uri? uri) => uri?.AbsolutePath ?? "?";
    }

    /// <summary>Runtime-only DPAPI access, as in ResumeItemTypesFixture. Never print the payload
    /// or the headers built from it.</summary>
    internal static SavedCredentials? ReadTestProfile()
    {
        if (!File.Exists(CredentialPath))
            return null;
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(CredentialPath), null, DataProtectionScope.CurrentUser);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(plain);
            var profiles = doc.RootElement.TryGetProperty("Profiles", out var list)
                ? list.EnumerateArray().Select(x => x.Deserialize<SavedCredentials>()!).ToArray()
                : [doc.RootElement.Deserialize<SavedCredentials>()!];
            return profiles.FirstOrDefault(x => x.Username.Equals("test", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
