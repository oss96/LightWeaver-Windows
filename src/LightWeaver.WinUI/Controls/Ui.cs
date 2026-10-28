using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>Small factories for elements built in code, so they use the same resources as XAML.</summary>
internal static class Ui
{
    public static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public static FontIcon Icon(string glyph, double size) => new() { Style = Resource<Style>("LwIcon"), FontSize = size, Glyph = glyph };

    /// <summary>A glyph that points along the reading direction, mirrored right to left.</summary>
    public static FontIcon DirectionalIcon(string glyph, double size) => new() { Style = Resource<Style>("LwDirectionalIcon"), FontSize = size, Glyph = glyph };

    /// <summary>The small × button on a filter token.</summary>
    public static Button TokenRemoveButton(double cornerRadius) => new()
    {
        Style = Resource<Style>("LwSubtleButton"),
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(cornerRadius),
        Foreground = Resource<Brush>("LwText2Brush"),
        Content = Icon(Theme.Icons.Close, 13),
    };
}
