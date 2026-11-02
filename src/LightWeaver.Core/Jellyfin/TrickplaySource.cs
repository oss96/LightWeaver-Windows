namespace LightWeaver.Jellyfin;

/// <summary>Where a playback position's trickplay thumbnail is: which tile sheet and which cell of
/// its grid. Framework-neutral; each app loads and crops the sheet with its own imaging.</summary>
public class TrickplaySource(JellyfinService jellyfin, Guid itemId, TrickplayInfo info)
{
    public int ThumbWidth => info.Width;
    public int ThumbHeight => info.Height;

    /// <summary>Whether the bucket's sizes are safe for the sheet and crop arithmetic; see
    /// <see cref="TrickplayInfo.IsUsable"/>.</summary>
    public bool IsUsable => info.IsUsable;

    /// <summary>A full sheet's width in pixels: the thumbnails of one row side by side.</summary>
    public int SheetWidth => info.TileWidth * info.Width;

    /// <summary>Thumbnail index for a position; clamped so hover past the end shows the last thumb.</summary>
    public int ThumbIndex(double seconds)
        => Math.Clamp((int)(seconds * 1000.0 / info.IntervalMs), 0, info.ThumbnailCount - 1);

    /// <summary>The sheet URL and the thumbnail's cell within it, in sheet pixels.</summary>
    public (string SheetUrl, int X, int Y, int Width, int Height) Locate(double seconds)
    {
        var thumbIndex = ThumbIndex(seconds);
        var perSheet = info.TileWidth * info.TileHeight;
        var cell = thumbIndex % perSheet;
        var url = jellyfin.GetTrickplayTileUrl(itemId, info.Width, thumbIndex / perSheet, info.MediaSourceId);
        return (url, cell % info.TileWidth * info.Width, cell / info.TileWidth * info.Height, info.Width, info.Height);
    }
}
