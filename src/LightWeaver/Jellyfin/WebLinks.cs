using System.Diagnostics.CodeAnalysis;

namespace LightWeaver.Jellyfin;

/// <summary>Accepts only absolute http and https links with a host and no user info: the only
/// server-supplied links the app hands to the Windows shell.</summary>
public static class WebLinks
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps
            || parsed.IsUnc || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length > 0)
            return false;
        uri = parsed;
        return true;
    }
}
