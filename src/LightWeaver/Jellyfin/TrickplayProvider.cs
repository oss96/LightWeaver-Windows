using System.Windows;
using System.Windows.Media.Imaging;
using LightWeaver.Imaging;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Resolves a playback position to a trickplay thumbnail: sheet index + crop rect
/// within the tile grid. Sheets are fetched through the image disk cache.
/// </summary>
public sealed class TrickplayProvider(JellyfinService jellyfin, Guid itemId, TrickplayInfo info)
{
    public int ThumbWidth => info.Width;
    public int ThumbHeight => info.Height;

    /// <summary>Thumbnail index for a position; clamped so hover past the end shows the last thumb.</summary>
    public int ThumbIndex(double seconds)
        => Math.Clamp((int)(seconds * 1000.0 / info.IntervalMs), 0, info.ThumbnailCount - 1);

    /// <summary>The tile sheet containing the position's thumbnail and its crop rect,
    /// or null while offline / on a decode failure.</summary>
    public async Task<(BitmapSource Sheet, Int32Rect Crop)?> GetPreviewAsync(double seconds)
    {
        var thumbIndex = ThumbIndex(seconds);
        var perSheet = info.TileWidth * info.TileHeight;
        var sheetIndex = thumbIndex / perSheet;
        var cell = thumbIndex % perSheet;

        var url = jellyfin.GetTrickplayTileUrl(itemId, info.Width, sheetIndex, info.MediaSourceId);
        var sheet = await ImageCache.GetImageAsync(url);
        if (sheet is null)
            return null;

        var crop = new Int32Rect(
            cell % info.TileWidth * info.Width,
            cell / info.TileWidth * info.Height,
            info.Width, info.Height);
        // The last sheet is cropped to its used rows — never cut outside it.
        if (crop.X + crop.Width > sheet.PixelWidth || crop.Y + crop.Height > sheet.PixelHeight)
            return null;
        return (sheet, crop);
    }
}
