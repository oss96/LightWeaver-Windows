using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LightWeaver;

/// <summary>Visible when the bound item id has a completed download (Phase 7 M20 card
/// badge). Cards can't observe the DownloadManager directly — MediaItem is immutable —
/// so completion/removal rebroadcasts the item through NotifyItemUserDataChanged, which
/// swaps the card's DataContext and re-runs this converter.</summary>
public sealed class DownloadedToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Guid id && Downloads.DownloadManager.Instance?.IsDownloaded(id) == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
