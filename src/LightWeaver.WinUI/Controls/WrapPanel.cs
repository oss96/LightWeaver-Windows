using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>Lays children out left to right and wraps to the next line (CSS flex-wrap). WinUI
/// ships no wrap panel; the token rows and token fields need one.</summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, Invalidate));
    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, Invalidate));

    public double HorizontalSpacing { get => (double)GetValue(HorizontalSpacingProperty); set => SetValue(HorizontalSpacingProperty, value); }
    public double VerticalSpacing { get => (double)GetValue(VerticalSpacingProperty); set => SetValue(VerticalSpacingProperty, value); }

    /// <summary>Layout rounding can make the arranged width a fraction short of the measured one;
    /// that must not push the last child onto a line nobody measured.</summary>
    private const double Slack = 0.5;

    private static void Invalidate(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (s.Width == 0 && s.Height == 0) continue;
            if (x > 0 && x + s.Width > availableSize.Width + Slack)
            {
                y += line + VerticalSpacing;
                x = 0;
                line = 0;
            }
            x += s.Width + HorizontalSpacing;
            width = Math.Max(width, x - HorizontalSpacing);
            line = Math.Max(line, s.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        var row = new List<(UIElement Child, double X)>();
        // Each line's children are centred vertically on it, like align-items: center.
        void Flush()
        {
            foreach (var (c, cx) in row)
            {
                var s = c.DesiredSize;
                c.Arrange(new Rect(cx, y + (line - s.Height) / 2, Math.Min(s.Width, finalSize.Width), s.Height));
            }
            row.Clear();
        }
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (s.Width == 0 && s.Height == 0) { child.Arrange(new Rect(0, 0, 0, 0)); continue; }
            if (x > 0 && x + s.Width > finalSize.Width + Slack)
            {
                Flush();
                y += line + VerticalSpacing;
                x = 0;
                line = 0;
            }
            line = Math.Max(line, s.Height);
            row.Add((child, x));
            x += s.Width + HorizontalSpacing;
        }
        Flush();
        return finalSize;
    }
}
