using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace LightWeaver.Localization;

/// <summary>
/// Mirrors the interface for right-to-left UI languages. <see cref="Follow"/> goes on content
/// roots, never on a Window: a right-to-left Window sets WS_EX_LAYOUTRTL on its HWND, which mpv's
/// child window would inherit and which mirrors the client coordinates the overlay is aligned by.
/// Media controls, the seek bar, played-progress fills and time codes pin LeftToRight where they
/// are declared. Font glyphs are text and never mirror on their own, so a directional one opts in
/// with <see cref="MirrorGlyphProperty"/>.
/// </summary>
public static class UiDirection
{
    public static FlowDirection Current => L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Keeps <paramref name="root"/> on the UI language's direction, including a live switch.</summary>
    public static void Follow(FrameworkElement root)
        => LiveText.Bind(root, FrameworkElement.FlowDirectionProperty, () => Current);

    private static bool _registered;

    /// <summary>Class handlers for every ToolTip and ContextMenu, whose popups open outside the
    /// tree and inherit no direction. Idempotent, like <see cref="Theme.ZoomPopups.Register"/>.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnOwnedPopupOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOwnedPopupOpened));
    }

    /// <summary>The owner's direction rather than the language's, so a tooltip on a pinned
    /// LeftToRight transport button reads the same way as its button.</summary>
    private static void OnOwnedPopupOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        var (element, target) = sender switch
        {
            ToolTip tip => ((FrameworkElement)tip, tip.PlacementTarget),
            ContextMenu menu => (menu, menu.PlacementTarget),
            _ => (null, null),
        };
        if (element is not null)
            element.FlowDirection = target is FrameworkElement owner ? owner.FlowDirection : Current;
    }

    /// <summary>Flips a directional glyph (back, forward, see-all chevron) horizontally while its
    /// element is right-to-left. Not for media transport glyphs, which keep tape direction.</summary>
    public static readonly DependencyProperty MirrorGlyphProperty = DependencyProperty.RegisterAttached(
        "MirrorGlyph", typeof(bool), typeof(UiDirection), new PropertyMetadata(false, OnMirrorGlyphChanged));

    public static bool GetMirrorGlyph(DependencyObject element) => (bool)element.GetValue(MirrorGlyphProperty);

    public static void SetMirrorGlyph(DependencyObject element, bool value) => element.SetValue(MirrorGlyphProperty, value);

    private static void OnMirrorGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        if (e.NewValue is not true)
        {
            BindingOperations.ClearBinding(element, UIElement.RenderTransformProperty);
            return;
        }
        // About the centre, so the flipped glyph keeps its slot and its automation bounds.
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        BindingOperations.SetBinding(element, UIElement.RenderTransformProperty,
            new Binding(nameof(FrameworkElement.FlowDirection))
            {
                RelativeSource = RelativeSource.Self,
                Converter = MirrorTransform.Instance,
            });
    }

    private sealed class MirrorTransform : IValueConverter
    {
        public static readonly MirrorTransform Instance = new();
        private static readonly Transform Flip = CreateFlip();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FlowDirection.RightToLeft ? Flip : Transform.Identity;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Transform CreateFlip()
        {
            var flip = new ScaleTransform(-1, 1);
            flip.Freeze();
            return flip;
        }
    }
}
