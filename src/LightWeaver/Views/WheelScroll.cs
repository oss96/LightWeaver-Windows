using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightWeaver.Views;

/// <summary>Wheel forwarding for regions that sit outside (or overlay) the screen's
/// scroller — fixed headers, the A-Z rail, filter panels. WPF only routes the wheel
/// to ancestors, so content beside the scroller is a dead zone without this.</summary>
internal static class WheelScroll
{
    /// <summary>Scroll <paramref name="target"/> vertically by the wheel delta and
    /// swallow the event. No-op (event left unhandled) when the target is null.</summary>
    public static void ForwardTo(ScrollViewer? target, MouseWheelEventArgs e)
    {
        if (target is null)
            return;
        target.ScrollToVerticalOffset(target.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>First ScrollViewer inside a control's template (e.g. a ListBox's
    /// internal scroller).</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }
}
