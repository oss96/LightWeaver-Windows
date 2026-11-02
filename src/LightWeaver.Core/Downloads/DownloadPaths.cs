using System.IO;

namespace LightWeaver.Downloads;

/// <summary>Builds and checks download file names so no server-supplied value can pick the folder
/// a download is written to or deleted from.</summary>
public static class DownloadPaths
{
    private const int MaxExtensionLength = 12;

    /// <summary>The first token of a Jellyfin container list ("mov,mp4,m4a"), reduced to ASCII
    /// letters and digits; "mkv" when nothing usable is left.</summary>
    public static string Extension(string? container)
    {
        var token = (container ?? "").Split(',')[0];
        var extension = new string(token.Where(char.IsAsciiLetterOrDigit).Take(MaxExtensionLength).ToArray());
        return extension.Length > 0 ? extension : "mkv";
    }

    /// <summary>True when the video path is fully qualified, has no relative segments, and names a
    /// file that starts with the item id and holds no path or stream separators.</summary>
    public static bool IsContained(DownloadItem item)
    {
        var path = item.FilePath;
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            return false;
        if (path.Split('\\', '/').Skip(1).Any(segment => segment is "." or ".."))
            return false;
        var name = Path.GetFileName(path);
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name.StartsWith(FormattableString.Invariant($"{item.ItemId:N}_"), StringComparison.OrdinalIgnoreCase);
    }
}
