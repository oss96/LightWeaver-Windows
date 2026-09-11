using System.Windows;

namespace LightWeaver.Theme;

/// <summary>
/// Attached, inheritable flag for the left navigation rail. Setting
/// <see cref="IsCollapsedProperty"/> on the rail container cascades to every nav
/// item (property-value inheritance), so a single toggle switches the whole rail
/// between the expanded (232) and collapsed (64, icons-only) layouts without the
/// code-behind having to reach into control templates.
/// </summary>
public static class NavRail
{
    public static readonly DependencyProperty IsCollapsedProperty =
        DependencyProperty.RegisterAttached(
            "IsCollapsed",
            typeof(bool),
            typeof(NavRail),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetIsCollapsed(DependencyObject element, bool value)
        => element.SetValue(IsCollapsedProperty, value);

    public static bool GetIsCollapsed(DependencyObject element)
        => (bool)element.GetValue(IsCollapsedProperty);
}
