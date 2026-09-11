using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LightWeaver;

/// <summary>
/// Maps a Jellyfin library collection type to its Material icon (the same glyph the
/// nav rail shows for that library). Single source of truth for both the nav rail
/// (<see cref="MainWindow.BuildLibraryNavItem"/>) and the Home "Libraries" tiles.
/// The glyph codepoints themselves live only in <c>Theme/Icons.xaml</c>.
/// </summary>
public static class LibraryIcons
{
    /// <summary>Icons.xaml resource key for a library's collection type.</summary>
    public static string ResourceKey(string? collectionType) => collectionType switch
    {
        "movies" => "IconMovies",
        "tvshows" => "IconSeries",
        "boxsets" => "IconCollections",
        "playlists" => "IconPlaylists",
        "music" => "IconMusic",
        "homevideos" => "IconFolder",
        "folders" => "IconFolder",
        _ => "IconCollections",
    };
}

/// <summary>Resolves a library <c>CollectionType</c> to its Material glyph string so a
/// library card can render the icon in place of a preview image.</summary>
public sealed class CollectionTypeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current?.TryFindResource(LibraryIcons.ResourceKey(value as string)) as string ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
