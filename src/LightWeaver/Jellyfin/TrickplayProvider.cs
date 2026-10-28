using System.Windows;
using System.Windows.Media.Imaging;
using LightWeaver.Imaging;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Resolves a playback position to a trickplay thumbnail: sheet index + crop rect
/// within the tile grid. Sheets are fetched through the image disk cache.
/// </summary>
public sealed class TrickplayProvider(JellyfinService jellyfin, Guid itemId, TrickplayInfo info)
    : TrickplaySource(jellyfin, itemId, info)
{
    /// <summary>The tile sheet containing the position's thumbnail and its crop rect,
    /// or null while offline / on a decode failure.</summary>
    public async Task<(BitmapSource Sheet, Int32Rect Crop)?> GetPreviewAsync(double seconds)
    {
        if (!IsUsable)
            return null;
        var (url, x, y, width, height) = Locate(seconds);
        var sheet = await ImageCache.GetImageAsync(url);
        if (sheet is null)
            return null;

        var crop = new Int32Rect(x, y, width, height);
        // The last sheet is cropped to its used rows — never cut outside it.
        if (crop.X + crop.Width > sheet.PixelWidth || crop.Y + crop.Height > sheet.PixelHeight)
            return null;
        return (sheet, crop);
    }
}
