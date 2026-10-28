using System.Text.RegularExpressions;

namespace LightWeaver.Login;

/// <summary>What the login form does with a typed server address (design 12a): a host, an IP or a
/// full URL. Without a scheme https is tried first, then http, then Jellyfin's port 8096.</summary>
public static partial class ServerAddress
{
    public const int DefaultPort = 8096;

    /// <summary>The URLs to try, in order. Empty when the text is not an address.</summary>
    public static IReadOnlyList<string> Candidates(string? input)
    {
        var text = input?.Trim().TrimEnd('/') ?? "";
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return [];
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(text, UriKind.Absolute, out var full) && full.Host.Length > 0 ? [Normalize(full)] : [];

        var slash = text.IndexOf('/');
        var authority = slash < 0 ? text : text[..slash];
        var path = slash < 0 ? "" : text[slash..];
        string[] urls = HostWithPort().IsMatch(authority)
            ? [$"https://{text}", $"http://{text}"]
            : [$"https://{text}", $"http://{text}", $"http://{authority}:{DefaultPort}{path}"];
        return urls.All(u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0) ? urls : [];
    }

    /// <summary>The address as people write it: host, port and path, without the scheme.</summary>
    public static string Display(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Authority + uri.AbsolutePath.TrimEnd('/')
            : url;

    private static string Normalize(Uri uri) => $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";

    [GeneratedRegex(@"(^[^:\[\]]+|\])\:\d+$")]
    private static partial Regex HostWithPort();
}
