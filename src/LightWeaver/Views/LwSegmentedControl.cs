using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LightWeaver.Views;

/// <summary>
/// Segmented control (Phase 7 M22): a ListBox whose template overlays a sliding thumb
/// behind the segments. Selection semantics, Items, SelectedIndex and the UIA tree are
/// plain ListBox — only the visuals differ; style it with the keyed
/// <c>LwSegmented</c> style (Theme/Controls.xaml), which names the thumb parts.
/// </summary>
public class LwSegmentedControl : ListBox
{
    private Border? _thumb;
    private TranslateTransform? _thumbTx;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("Thumb") as Border;
        _thumbTx = GetTemplateChild("ThumbTx") as TranslateTransform;
        Loaded += (_, _) => UpdateThumb(animated: false);
        SizeChanged += (_, _) => UpdateThumb(animated: false);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateThumb(animated: true);
    }

    /// <summary>Slides the thumb under the selected segment — 240 ms (LwDurGlowIn),
    /// cubic ease; instant on layout passes.</summary>
    private void UpdateThumb(bool animated)
    {
        if (_thumb is null || _thumbTx is null || SelectedIndex < 0)
            return;
        if (ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not ListBoxItem container
            || container.ActualWidth <= 0)
        {
            // containers not generated yet — retry after layout
            Dispatcher.BeginInvoke(() => UpdateThumb(animated: false),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        var host = VisualTreeHelper.GetParent(container) as UIElement ?? this;
        var x = container.TranslatePoint(new Point(0, 0), host).X;
        _thumb.Height = container.ActualHeight;
        if (animated)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _thumbTx.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            _thumb.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(container.ActualWidth, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }
        else
        {
            _thumbTx.BeginAnimation(TranslateTransform.XProperty, null);
            _thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
            _thumbTx.X = x;
            _thumb.Width = container.ActualWidth;
        }
    }
}
