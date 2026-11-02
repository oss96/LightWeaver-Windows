using System.Net.Http;
using System.Text.Json;

namespace LightWeaver.Metadata;

/// <summary>
/// Fetches IMDb / Rotten Tomatoes / Metacritic scores from OMDB by IMDb id
/// (Phase 7 M1). Modeled on <see cref="Updates.GitHubUpdateClient"/>: a shared HttpClient and
/// System.Text.Json, with <b>any</b> failure returning null silently — a blank key,
/// no network, an error payload, or a missing score never surfaces to the user.
/// Answers are cached on disk for a week via
/// <see cref="Imaging.ExternalMetadataCache"/>, so opening a detail view twice costs
/// one request.
/// </summary>
public static class OmdbClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Null when the key/id is blank, the request fails, or OMDB has no match.</summary>
    public static async Task<ExternalRatings?> GetByImdbIdAsync(string? imdbId, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(apiKey))
            return null;

        // The key is deliberately absent from the cache key: it hashes into a filename and
        // prints into the LIGHTWEAVER_CACHE_LOG lines, and a secret belongs in neither.
        // No server or profile either — OMDB scores are global, identical for every account.
        var cached = await Imaging.ExternalMetadataCache.GetOrFetchAsync<ExternalRatings>(
            $"omdb:ratings:{imdbId}", TimeSpan.FromDays(7), () => FetchAsync(imdbId, apiKey))
            .ConfigureAwait(false);
        return cached is { HasAny: true } ? cached : null;
    }

    /// <summary>One live OMDB request. Returns an <b>empty</b> ExternalRatings for a title OMDB
    /// knows but carries no usable score for, and null only on genuine failure.</summary>
    private static async Task<ExternalRatings?> FetchAsync(string imdbId, string apiKey)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}"
                + $"&plot=short&apikey={Uri.EscapeDataString(apiKey)}";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("Response", out var ok) && ok.GetString() == "True"))
            {
                Detail("no_match", started);
                return null;   // no answer from OMDB: a failure, so it stays uncached and retries
            }

            string? imdb = root.TryGetProperty("imdbRating", out var ir)
                && ir.GetString() is { Length: > 0 } r && r != "N/A"
                    ? $"{r}/10" : null;

            string? rt = null, mc = null;
            if (root.TryGetProperty("Ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
            {
                foreach (var rating in ratings.EnumerateArray())
                {
                    var source = rating.TryGetProperty("Source", out var s) ? s.GetString() : null;
                    var value = rating.TryGetProperty("Value", out var v) ? v.GetString() : null;
                    if (value is not { Length: > 0 })
                        continue;
                    if (source == "Rotten Tomatoes")
                        rt = value;                       // e.g. "94%"
                    else if (source == "Metacritic")
                        mc = value.Split('/')[0];         // "78/100" → "78"
                }
            }

            // A scoreless answer is a known negative, not a failure: return the empty record so it
            // gets cached and the title stops re-requesting on every open. GetOrFetchAsync never
            // persists null, so only the returns above (and the catch) leave the cache empty.
            var scored = new ExternalRatings(imdb, rt, mc);
            Detail(scored.HasAny ? "success" : "no_scores", started,
                scores: (imdb is null ? 0 : 1) + (rt is null ? 0 : 1) + (mc is null ? 0 : 1));
            return scored;
        }
        catch (Exception ex)
        {
            Detail("failure", started, ex: ex);
            return null;   // still silent to the USER by design — and uncached, so the next open retries
        }
    }

    /// <summary>
    /// The outcome of one third-party lookup. Every failure mode here used to be a bare
    /// <c>catch { return null; }</c>, so a wrong API key, a rate-limited account and a dead network
    /// were indistinguishable from "this film has no critic scores" — the ratings row simply never
    /// appeared, forever, with nothing written down.
    ///
    /// <para>What is deliberately NOT in the record: the request URL (it carries the API key as a
    /// query parameter), the key itself, and the IMDb id. The wrapping
    /// <c>[cache] event=fetch cache=externalcache … key_hash=…</c> pair brackets this record in the
    /// same file, which is how a line here is tied back to an entry without printing an id.</para></summary>
    private static void Detail(string outcome, System.Diagnostics.Stopwatch started,
        int scores = 0, Exception? ex = null)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant(
            $"event=fetch source=omdb outcome={outcome} elapsed_ms={started.ElapsedMilliseconds} scores={scores}");
        if (ex is HttpRequestException { StatusCode: { } status })
            line += FormattableString.Invariant($" status={(int)status}");
        if (ex is not null)
            line += $" error={ex.GetType().Name}";
        Diagnostics.AppLog.Detail("omdb", line);
    }
}
