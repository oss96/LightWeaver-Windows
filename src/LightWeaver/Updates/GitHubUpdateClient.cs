using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace LightWeaver.Updates;

/// <summary>Strict public GitHub release client. Production endpoint selection is not configurable.</summary>
public sealed class GitHubUpdateClient
{
    private const string ProductionApiHost = "api.github.com";
    private static readonly Uri ProductionApi = new("https://api.github.com/repos/oss96/LightWeaver-Windows/");
    // Exact hosts, never suffix matches: the release page and every asset URL are on github.com,
    // and an asset download redirects to one of the two release storage hosts.
    private static readonly string[] ProductionHosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = GetRequestTimeout() };

    public static Version CurrentVersion =>
        typeof(GitHubUpdateClient).Assembly.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var api = GetApiBase();
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, "releases?per_page=10"));
        // GitHub rejects an anonymous request without a User-Agent; the other two headers pin the
        // response shape. Set per request so an injected HttpClient is never mutated.
        request.Headers.TryAddWithoutValidation("User-Agent", $"LightWeaver/{CurrentVersion.ToString(3)}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Detail("event=feed outcome=failure reason=root_shape");
            throw new JsonException("The release feed root is not an array.");
        }
        // The feed's own content is never recorded — only how many releases it carried and which
        // class of endpoint served it.
        Detail(FormattableString.Invariant(
            $"event=feed outcome=success endpoint={(IsDebugFixture ? "fixture" : "production")} releases={doc.RootElement.GetArrayLength()} elapsed_ms={started.ElapsedMilliseconds}"));

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object)
            {
                Detail("event=release outcome=failure reason=release_shape");
                throw new JsonException("The release feed contains a non-object release.");
            }
            var draft = GetBoolean(release, "draft");
            if (draft || GetBoolean(release, "prerelease"))
            {
                Detail($"event=release outcome=noop reason={(draft ? "draft" : "prerelease")}");
                continue;
            }

            var tag = RequiredString(release, "tag_name");
            if (!TryParseThreePartVersion(tag, out var version))
            {
                // The tag itself is feed-supplied text and is not recorded; only that it was rejected.
                Detail("event=release outcome=failure reason=version_shape");
                throw new JsonException("The latest stable release tag is not a three-part version.");
            }
            var page = RequiredUri(release, "html_url");
            if (!IsAllowedEndpoint(page, allowFixture: IsDebugFixture))
            {
                Detail("event=release outcome=failure reason=endpoint_not_allowed");
                throw new JsonException("The latest stable release page is not an approved endpoint.");
            }
            if (version <= CurrentVersion)
            {
                Detail(FormattableString.Invariant(
                    $"event=release outcome=current from_version={CurrentVersion.ToString(3)} to_version={version.ToString(3)}"));
                return null;
            }

            var expectedName = $"LightWeaver-{version.ToString(3)}-setup.exe";
            Uri? download = null;
            long? expectedSize = null;
            if (release.TryGetProperty("assets", out var assets))
            {
                if (assets.ValueKind != JsonValueKind.Array)
                {
                    Detail("event=asset outcome=failure reason=assets_shape");
                    throw new JsonException("Release assets are not an array.");
                }
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object)
                    {
                        Detail("event=asset outcome=failure reason=asset_shape");
                        throw new JsonException("Release assets contain a non-object asset.");
                    }
                    if (GetOptionalString(asset, "name") == expectedName
                        && TryGetAbsoluteUri(GetOptionalString(asset, "browser_download_url"), out var candidate)
                        && IsApprovedAssetEndpoint(candidate))
                    {
                        download = candidate;
                        if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                            || !size.TryGetInt64(out var bytes) || bytes <= 0)
                        {
                            Detail("event=asset outcome=failure reason=size_invalid");
                            throw new JsonException("The installer asset size is missing or invalid.");
                        }
                        expectedSize = bytes;
                        break;
                    }
                }
            }
            var fixtureHash = GetDebugFixtureHash();
            if (IsDebugFixture && download is not null && fixtureHash is null)
            {
                Detail("event=asset outcome=failure reason=fixture_hash_missing");
                throw new JsonException("The debug update fixture is missing its exact SHA-256.");
            }
            // The asset NAME is the app's own expectedName and the URI is never recorded: what the
            // selection is diagnosed from is whether one matched, for which version, and how big.
            Detail(FormattableString.Invariant(
                $"event=asset outcome={(download is null ? "miss" : "success")} to_version={version.ToString(3)} bytes_total={(expectedSize is { } assetBytes ? FormattableString.Invariant($"{assetBytes}") : "unknown")}"));
            return new UpdateInfo(version, download, page, download is null ? null : expectedName,
                expectedSize, DebugSha256: fixtureHash);
        }
        return null;
    }

    private static void Detail(string record) => Diagnostics.AppLog.Detail("updates", record);

    /// <summary>The allowlist check for a name-matched asset, with the rejection recorded. The
    /// predicate is unchanged: a rejected candidate still simply fails to match and the search
    /// continues to the next asset.</summary>
    private static bool IsApprovedAssetEndpoint(Uri uri)
    {
        if (IsAllowedEndpoint(uri, allowFixture: IsDebugFixture))
            return true;
        Detail("event=asset outcome=noop reason=endpoint_not_allowed");
        return false;
    }

    /// <summary>Validates every redirect hop as well as URLs supplied by GitHub.</summary>
    public static bool IsApprovedDownloadUri(Uri uri) => IsAllowedEndpoint(uri, allowFixture: IsDebugFixture);

    private static bool GetBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"Release property {property} is not a boolean."),
        };
    }

    private static string RequiredString(JsonElement element, string property) =>
        GetOptionalString(element, property) is { Length: > 0 } value
            ? value : throw new JsonException($"Release property {property} is missing.");

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri RequiredUri(JsonElement element, string property) =>
        TryGetAbsoluteUri(RequiredString(element, property), out var uri)
            ? uri : throw new JsonException($"Release property {property} is not an absolute URI.");

    private static bool TryParseThreePartVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag;
        if (text.StartsWith('v')) text = text[1..];
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0
                || part.Any(character => character is < '0' or > '9'))
            || !Version.TryParse(text, out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    private static bool TryGetAbsoluteUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && !uri.UserInfo.Any() && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsAllowedEndpoint(Uri uri, bool allowFixture)
    {
#if DEBUG
        if (allowFixture && uri.IsLoopback && string.Equals(uri.Host, GetApiBase().Host, StringComparison.OrdinalIgnoreCase))
            return true;
#endif
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && ProductionHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    private static Uri GetApiBase()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_API") is { Length: > 0 } fixture
            && Uri.TryCreate(fixture.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
            return uri;
#endif
        return ProductionApi;
    }

    private static bool IsDebugFixture
    {
        get
        {
#if DEBUG
            return !GetApiBase().Host.Equals(ProductionApiHost, StringComparison.OrdinalIgnoreCase);
#else
            return false;
#endif
        }
    }

    private static string? GetDebugFixtureHash()
    {
#if DEBUG
        var hash = Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_SHA256")?.Trim();
        return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash : null;
#else
        return null;
#endif
    }

    private static TimeSpan GetRequestTimeout()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_TIMEOUT_MS"), out var milliseconds)
            && milliseconds is >= 100 and <= 30_000)
            return TimeSpan.FromMilliseconds(milliseconds);
#endif
        return TimeSpan.FromSeconds(30);
    }
}
