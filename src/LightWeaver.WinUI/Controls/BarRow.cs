using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>A bar row of two groups: the first child at the start, the second at the end. When both
/// do not fit on one line, the second moves to its own row on top, aligned to the end, and wraps
/// there if it still does not fit (give it a WrapPanel), so no control is ever pushed out of reach.</summary>
public sealed partial class BarRow : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(BarRow), new PropertyMetadata(8d, (d, _) => ((BarRow)d).InvalidateMeasure()));

    /// <summary>The gap between the groups, across and between rows.</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private bool _stacked;

    /// <summary>The width both groups take on one row, from the last measure.</summary>
    public double OneRowWidth { get; private set; }

    /// <summary>The width the row was given at the last measure.</summary>
    public double AvailableWidth { get; private set; } = double.PositiveInfinity;

    /// <summary>Raised after a measure that changed <see cref="OneRowWidth"/> or
    /// <see cref="AvailableWidth"/>, once the layout pass is over, so a handler may change the
    /// groups (fold buttons together) without measuring inside a measure.</summary>
    public event EventHandler? Measured;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return MeasureSingle(availableSize);
        var start = Children[0];
        var end = Children[1];
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        start.Measure(infinite);
        end.Measure(infinite);
        var width = availableSize.Width;
        var oneRow = start.DesiredSize.Width + Spacing + end.DesiredSize.Width;
        if (oneRow != OneRowWidth || width != AvailableWidth)
        {
            (OneRowWidth, AvailableWidth) = (oneRow, width);
            DispatcherQueue.TryEnqueue(() => Measured?.Invoke(this, EventArgs.Empty));
        }
        _stacked = !double.IsInfinity(width) && oneRow > width;
        if (!_stacked)
            return new Size(start.DesiredSize.Width + Spacing + end.DesiredSize.Width, Math.Max(start.DesiredSize.Height, end.DesiredSize.Height));
        end.Measure(new Size(width, double.PositiveInfinity));
        start.Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, end.DesiredSize.Height + Spacing + start.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
        var start = Children[0];
        var end = Children[1];
        if (!_stacked)
        {
            var height = finalSize.Height;
            start.Arrange(new Rect(0, (height - start.DesiredSize.Height) / 2, start.DesiredSize.Width, start.DesiredSize.Height));
            var endWidth = Math.Min(end.DesiredSize.Width, finalSize.Width);
            end.Arrange(new Rect(finalSize.Width - endWidth, (height - end.DesiredSize.Height) / 2, endWidth, end.DesiredSize.Height));
            return finalSize;
        }
        var top = Math.Min(end.DesiredSize.Width, finalSize.Width);
        end.Arrange(new Rect(finalSize.Width - top, 0, top, end.DesiredSize.Height));
        start.Arrange(new Rect(0, end.DesiredSize.Height + Spacing, Math.Min(start.DesiredSize.Width, finalSize.Width), start.DesiredSize.Height));
        return finalSize;
    }

    private Size MeasureSingle(Size availableSize)
    {
        var size = new Size();
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            size = new Size(Math.Max(size.Width, child.DesiredSize.Width), Math.Max(size.Height, child.DesiredSize.Height));
        }
        return size;
    }
}
