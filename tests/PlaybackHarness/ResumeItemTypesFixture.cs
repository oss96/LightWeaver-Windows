using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using LightWeaver.Jellyfin;

/// <summary>
/// B45. Proves the Continue Watching queries ask the server for playable leaf items only.
/// <para>Offscreen by construction: it opens no window, drives no input and needs no GPU or
/// mpv — it stands up <see cref="JellyfinService"/> directly and talks HTTP. That is why this
/// lives here rather than in a UIA suite, per the project's preference for a windowless check
/// over a VM run. The UIA suite still exists for the rendered rail; this covers the query.</para>
/// <para>The discriminating leg is leg 2, and it is deliberately NOT "are there containers in
/// the response". Whether a container happens to be resumable right now is server data that can
/// change under us; what cannot change is that an unconstrained request and a constrained one
/// return different totals whenever the library has any container in progress. Leg 2 compares
/// the app's own TotalCount against the constrained oracle, so it fails on an unfixed build and
/// says so loudly when the server data cannot discriminate today.</para>
/// </summary>
internal static class ResumeItemTypesFixture
{
    /// <summary>Exactly the set the fix sends. Kept as a literal rather than reflected out of
    /// JellyfinService on purpose: a test that derives its expectation from the code under test
    /// cannot detect that code changing.</summary>
    private const string ExpectedKinds = "Movie,Episode,Video,MusicVideo,Audio";

    private static string CredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightWeaver-Debug", "credentials.dat");

    /// <summary>Reports the skip and returns true when there is no signed-in debug profile to
    /// reach a server with. An absent profile is not a failure: the fixture has nothing to ask.</summary>
    public static bool TrySkip()
    {
        if (!File.Exists(CredentialPath))
        {
            Console.WriteLine("SKIP: Continue Watching item types needs a signed-in debug profile "
                + "(no credentials.dat).");
            return true;
        }
        if (ReadTestProfile() is null)
        {
            Console.WriteLine("SKIP: Continue Watching item types needs a debug profile named "
                + "'test'; the store has none.");
            return true;
        }
        return false;
    }

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        // The caller gates this on TrySkip(), so reaching Run() without a profile is a wiring bug.
        // Throwing rather than returning keeps a test that did nothing from being reported as passed.
        var credentials = ReadTestProfile()
            ?? throw new InvalidOperationException(
                "ResumeItemTypesFixture.Run was called without a 'test' debug profile; gate it on TrySkip().");

        using var service = new JellyfinService();
        if (!await service.ReconnectAsync(credentials).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The saved Jellyfin token was rejected. STOP: do not mint or re-authorise one — "
                + "sign the debug profile in again, then re-run.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Runtime-only use of the token, and only as a header. Never logged, never in a URL.
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"MediaBrowser Token=\"{credentials.AccessToken}\"");
        var server = credentials.ServerUrl.TrimEnd('/');
        var user = credentials.UserId.ToString("N");

        // The oracle is raw HTTP, not another call into JellyfinService: an oracle built from the
        // code under test is a mirror, which is precisely how the existing see-all suite stayed
        // green through this bug.
        var unconstrainedTotal = await TotalAsync(http,
            $"{server}/UserItems/Resume?userId={user}&Limit=1&EnableTotalRecordCount=true")
            .ConfigureAwait(false);
        var constrainedTotal = await TotalAsync(http,
            $"{server}/UserItems/Resume?userId={user}&Limit=1&EnableTotalRecordCount=true&IncludeItemTypes={ExpectedKinds}")
            .ConfigureAwait(false);
        var discriminating = unconstrainedTotal != constrainedTotal;
        Console.WriteLine($"RESUME ORACLE: unconstrained={unconstrainedTotal} constrained={constrainedTotal} "
            + $"discriminating={discriminating}");
        if (!discriminating)
            Console.WriteLine("WARNING: the server has no resumable containers right now, so leg 2 cannot "
                + "tell a fixed build from a broken one on this data. Leg 1 and leg 3 still hold.");

        var failures = new List<string>();

        // Leg 1 — the rail. Every card must be something a click can actually play. A Season here
        // is the reported bug; a Book here would be a card whose click is a silent no-op.
        var rail = await service.GetResumeItemsAsync(50).ConfigureAwait(false);
        var railJunk = rail.Where(i => !i.IsPlayable).ToList();
        Console.WriteLine($"RAIL: {rail.Count} items, kinds=[{Kinds(rail)}]");
        if (railJunk.Count > 0)
            failures.Add($"leg 1: GetResumeItemsAsync returned {railJunk.Count} non-playable item(s): "
                + string.Join(", ", railJunk.Take(5).Select(i => $"{i.Type} '{i.Name}'")));

        // Leg 2 — the discriminating one. The paged query's TotalCount comes straight from the
        // server, so it equals the constrained oracle only if the request carried the constraint.
        // On an unfixed build this is the unconstrained total instead.
        var (paged, total) = await service.GetResumePagedAsync(0, 50).ConfigureAwait(false);
        Console.WriteLine($"PAGED: total={total} returned={paged.Count} kinds=[{Kinds(paged)}]");
        if (total != constrainedTotal)
            failures.Add($"leg 2: GetResumePagedAsync reported TotalCount {total}, expected the "
                + $"constrained total {constrainedTotal} (unconstrained is {unconstrainedTotal}). "
                + "Either the request is not carrying IncludeItemTypes, or JellyfinService's "
                + $"ResumeItemTypes no longer matches this fixture's expectation ({ExpectedKinds}).");
        var pagedJunk = paged.Where(i => !i.IsPlayable).ToList();
        if (pagedJunk.Count > 0)
            failures.Add($"leg 2: GetResumePagedAsync returned {pagedJunk.Count} non-playable item(s): "
                + string.Join(", ", pagedJunk.Take(5).Select(i => $"{i.Type} '{i.Name}'")));

        // Leg 3 — paging continuity. A client-side post-filter would pass legs 1 and 2's type
        // checks and break here: dropping k items per page makes the next startIndex short, so the
        // server re-serves rows it already sent. Small page size so the walk crosses boundaries.
        if (total >= 2)
        {
            var seen = new List<string>();
            var stable = true;
            for (var start = 0; start < total; start += 2)
            {
                var (page, pageTotal) = await service.GetResumePagedAsync(start, 2).ConfigureAwait(false);
                // Someone watching something mid-walk moves the server's total. That is not a
                // paging defect, so record it rather than failing the leg on it.
                if (pageTotal != total) stable = false;
                seen.AddRange(page.Select(i => i.Id.ToString("N")));
            }
            var duplicates = seen.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Console.WriteLine($"PAGING: walked {total} in pages of 2, saw {seen.Count} ids, "
                + $"{seen.Distinct().Count()} distinct");
            if (duplicates.Count > 0)
                failures.Add($"leg 3: paging re-served {duplicates.Count} id(s) across pages — the "
                    + "offsets and the filtered set disagree.");
            if (!stable)
                Console.WriteLine("PAGING: the server's total moved during the walk (something was "
                    + "watched); skipping the count assertion, the duplicate check still held.");
            else if (seen.Distinct().Count() != total)
                failures.Add($"leg 3: walking every page yielded {seen.Distinct().Count()} distinct "
                    + $"items but TotalCount says {total}.");
        }
        else
        {
            Console.WriteLine($"PAGING: skipped, only {total} resumable item(s) — too few to cross a "
                + "page boundary. Not an assertion.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static string Kinds(IEnumerable<MediaItem> items) =>
        string.Join(" ", items.GroupBy(i => i.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}"));

    private static async Task<int> TotalAsync(HttpClient http, string url)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // .NET's connect/DNS messages name the host and port. Never a token, but the harness
            // prints exception messages verbatim, so keep the server out of the output entirely.
            throw new InvalidOperationException($"Resume oracle could not reach the server ({e.GetType().Name}).");
        }
        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Resume oracle HTTP status {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return doc.RootElement.GetProperty("TotalRecordCount").GetInt32();
    }

    /// <summary>Runtime-only DPAPI access. Never print the credential payload or the headers built
    /// from it. Returns null when the store has no profile named 'test'.</summary>
    private static SavedCredentials? ReadTestProfile()
    {
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(
                File.ReadAllBytes(CredentialPath), null, DataProtectionScope.CurrentUser);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // TrySkip runs OUTSIDE Test()'s try/catch, so an unreadable store must degrade to
            // "no usable profile" rather than kill the harness before it prints RESULT:.
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
