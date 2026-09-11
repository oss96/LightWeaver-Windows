using System.Windows;
using System.Windows.Controls;
using LightWeaver.Jellyfin;
using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Views;

/// <summary>
/// Picks the card template per item kind: libraries get a collection-type icon tile,
/// episodes a 16:9 landscape card (their Primary image is the episode still), and
/// everything else the 2:3 poster.
/// </summary>
public sealed class MediaCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PosterTemplate { get; set; }
    public DataTemplate? EpisodeTemplate { get; set; }
    public DataTemplate? LibraryTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item switch
        {
            MediaItem { IsLibrary: true } => LibraryTemplate,
            MediaItem { Type: BaseItemDto_Type.Episode } => EpisodeTemplate,
            _ => PosterTemplate,
        };
}
