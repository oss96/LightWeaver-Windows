namespace LightWeaver.Player;

/// <summary>How a link to a video or stream is named while it plays and while it is dragged over the
/// window: from its host and path only, never from its query or fragment.</summary>
public static class StreamLink
{
    private static readonly string[] GenericNames = ["index", "master", "playlist", "manifest", "stream", "live"];

    /// <summary>The title and subtitle a link plays under before mpv names it: the file name without
    /// its extension, or the nearest folder above a generic name, with the host under it; the host
    /// alone when the path names nothing.</summary>
    public static (string Title, string? Subtitle) Title(Uri link)
    {
        var path = link.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var name = Uri.UnescapeDataString(segments[i]);
            if (i == segments.Length - 1 && !path.EndsWith('/')) name = WithoutExtension(name);
            if (name.Length > 0 && !GenericNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return (name, link.Authority);
        }
        return (link.Authority, null);
    }

    /// <summary>mpv's media-title when it is a real name (a container tag, an HLS or ICY name), not
    /// mpv's fallback to the address or its file name; null otherwise.</summary>
    public static string? MediaTitle(Uri link, string? mediaTitle)
    {
        if (string.IsNullOrWhiteSpace(mediaTitle)) return null;
        var title = mediaTitle.Trim();
        if (title.Contains("://", StringComparison.Ordinal)) return null;
        var query = link.Query.TrimStart('?');
        if (query.Length > 0 && (title.Contains(query, StringComparison.OrdinalIgnoreCase) || title.Contains(Uri.UnescapeDataString(query), StringComparison.OrdinalIgnoreCase))) return null;
        var raw = link.Segments[^1].TrimEnd('/');
        foreach (var file in new[] { raw, Uri.UnescapeDataString(raw) })
        {
            if (file.Length == 0) continue;
            if (string.Equals(title, file, StringComparison.OrdinalIgnoreCase)) return null;
            if (title.Length > file.Length && title.StartsWith(file, StringComparison.OrdinalIgnoreCase) && title[file.Length] is '?' or '#') return null;
        }
        return title;
    }

    /// <summary>The link as the drop overlay names it: host and path, without the scheme, query or
    /// fragment.</summary>
    public static string Display(Uri link)
        => link.AbsolutePath == "/" ? link.Authority : link.Authority + Uri.UnescapeDataString(link.AbsolutePath);

    /// <summary>Whether <paramref name="text"/> is an address that names a user or password, which
    /// links may not carry.</summary>
    public static bool HasCredentials(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var address) && address.UserInfo.Length > 0;

    private static string WithoutExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}

/// <summary>What a drop on the window would play: a local file's path or a link's address, and the
/// name the drop overlay shows for it.</summary>
public sealed record DropPlay(string Address, string Name, bool IsLink)
{
    /// <summary>A dropped file; null when it has no path on disk.</summary>
    public static DropPlay? File(string? path)
        => string.IsNullOrEmpty(path) ? null : new DropPlay(path, Path.GetFileName(path), false);

    /// <summary>A dropped web link or text, when it is a link that plays (<see cref="StreamAddress"/>).</summary>
    public static DropPlay? Link(string? text)
        => text is not null && StreamAddress.TryParse(text, out var link) ? new DropPlay(link!.AbsoluteUri, StreamLink.Display(link), true) : null;
}
