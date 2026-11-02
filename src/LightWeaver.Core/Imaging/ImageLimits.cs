namespace LightWeaver.Imaging;

/// <summary>Bounds on what one artwork response may cost, shared by the WPF image cache and the
/// WinUI image loader, so a server cannot exhaust memory with a huge body or bitmap.</summary>
public static class ImageLimits
{
    /// <summary>Ceiling on one image response after decompression. A 4K backdrop JPEG is a few
    /// megabytes; anything near this is a server feeding the cache, not a picture.</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>Ceiling on the pixels one decode may produce, about an 8K frame. A decoded bitmap
    /// takes four bytes a pixel, so this bounds one image to ~160 MB.</summary>
    public const long MaxDecodedPixels = 40_000_000;

    /// <summary>The decode width to ask the decoder for: the requested width, but never wider than
    /// the source, because WPF and WinUI scale UP to it and a 1 px wide, very tall image would grow
    /// by the same factor in both directions. 0 = full size. The image control stretches the
    /// result anyway.</summary>
    public static int DecodeWidth(int sourceWidth, int decodePixelWidth)
        => decodePixelWidth > 0 && sourceWidth > 0 ? Math.Min(decodePixelWidth, sourceWidth) : 0;

    /// <summary>The pixel count a decode at <see cref="DecodeWidth"/> would produce; the decoder
    /// keeps the aspect ratio.</summary>
    public static long DecodedPixels(int width, int height, int decodePixelWidth)
    {
        if (width <= 0 || height <= 0)
            return 0;
        var decodeWidth = DecodeWidth(width, decodePixelWidth);
        if (decodeWidth == 0 || decodeWidth == width)
            return (long)width * height;
        return (long)decodeWidth * Math.Max(1, (long)height * decodeWidth / width);
    }
}
