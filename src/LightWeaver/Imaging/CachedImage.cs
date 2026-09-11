using System.Windows;
using System.Windows.Controls;

namespace LightWeaver.Imaging;

/// <summary>
/// Attached properties routing an Image's source through <see cref="ImageCache"/>:
/// <c>img:CachedImage.SourceUrl="{Binding PosterUrl}" img:CachedImage.DecodeWidth="300"</c>.
/// Set DecodeWidth before SourceUrl (attribute order suffices for constants).
/// </summary>
public static class CachedImage
{
    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.RegisterAttached("SourceUrl", typeof(string), typeof(CachedImage),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(CachedImage),
            new PropertyMetadata(0));

    public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
    public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

    public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

    private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;
        image.Source = null;
        if (e.NewValue is not string url || url.Length == 0)
            return;
        var bitmap = await ImageCache.GetImageAsync(url, GetDecodeWidth(image));
        // Virtualized containers recycle — the Image may be bound to another URL by now.
        if (bitmap is not null && GetSourceUrl(image) == url)
            image.Source = bitmap;
    }
}
