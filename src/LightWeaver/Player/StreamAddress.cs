namespace LightWeaver.Player;

/// <summary>Validation and display naming for user-entered network media.</summary>
public static class StreamAddress
{
    public static bool TryParse(string text, out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length != 0)
            return false;
        address = parsed;
        return true;
    }

    public static string DisplayName(Uri address)
    {
        var segment = address.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(segment) ? address.Host : Uri.UnescapeDataString(segment);
    }
}
