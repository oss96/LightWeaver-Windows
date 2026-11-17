using LightWeaver.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>A bar row of two or three groups: the first child at the start, the last at the end, and
/// a middle one, when there are three, centred on the row as far as the others allow. When they do
/// not fit on one line, the end group moves to its own row on top, aligned to the end, and wraps
/// there if it still does not fit (give it a WrapPanel); the middle group takes a row of its own
/// when it no longer fits beside the start group, so no control is ever pushed out of reach.</summary>
public sealed partial class BarRow : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(BarRow), new PropertyMetadata(8d, (d, _) => ((BarRow)d).InvalidateMeasure()));

    /// <summary>The gap between the groups, across and between rows.</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private bool _stacked;
    private bool _middleAlone;

    /// <summary>The width all groups take on one row, from the last measure.</summary>
    public double OneRowWidth { get; private set; }

    /// <summary>The width the row was given at the last measure.</summary>
    public double AvailableWidth { get; private set; } = double.PositiveInfinity;

    /// <summary>Raised after a measure that changed <see cref="OneRowWidth"/> or
    /// <see cref="AvailableWidth"/>, once the layout pass is over, so a handler may change the
    /// groups (fold buttons together) without measuring inside a measure.</summary>
    public event EventHandler? Measured;

    private UIElement Start => Children[0];
    private UIElement? Middle => Children.Count > 2 ? Children[1] : null;
    private UIElement End => Children[^1];
    private double MiddleWidth => Middle is { } middle ? middle.DesiredSize.Width + Spacing : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return MeasureSingle(availableSize);
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children) child.Measure(infinite);
        var width = availableSize.Width;
        var oneRow = Start.DesiredSize.Width + MiddleWidth + Spacing + End.DesiredSize.Width;
        if (oneRow != OneRowWidth || width != AvailableWidth)
        {
            (OneRowWidth, AvailableWidth) = (oneRow, width);
            DispatcherQueue.TryEnqueue(() => Measured?.Invoke(this, EventArgs.Empty));
        }
        _stacked = !double.IsInfinity(width) && oneRow > width;
        _middleAlone = _stacked && Start.DesiredSize.Width + MiddleWidth > width;
        if (!_stacked)
            return new Size(oneRow, Children.Max(child => child.DesiredSize.Height));
        End.Measure(new Size(width, double.PositiveInfinity));
        Start.Measure(new Size(width, double.PositiveInfinity));
        var height = End.DesiredSize.Height + Spacing + Start.DesiredSize.Height;
        if (Middle is { } middle)
            height = _middleAlone ? height + Spacing + middle.DesiredSize.Height : Math.Max(height, End.DesiredSize.Height + Spacing + middle.DesiredSize.Height);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
        var width = finalSize.Width;
        if (!_stacked)
        {
            ArrangeLine(0, finalSize.Height, width, End);
            return finalSize;
        }
        var top = Math.Min(End.DesiredSize.Width, width);
        End.Arrange(new Rect(width - top, 0, top, End.DesiredSize.Height));
        var y = End.DesiredSize.Height + Spacing;
        if (Middle is { } middle && _middleAlone)
        {
            middle.Arrange(new Rect(Math.Max(0, (width - middle.DesiredSize.Width) / 2), y, middle.DesiredSize.Width, middle.DesiredSize.Height));
            y += middle.DesiredSize.Height + Spacing;
            Start.Arrange(new Rect(0, y, Math.Min(Start.DesiredSize.Width, width), Start.DesiredSize.Height));
            return finalSize;
        }
        ArrangeLine(y, Math.Max(Start.DesiredSize.Height, Middle?.DesiredSize.Height ?? 0), width, null);
        return finalSize;
    }

    private void ArrangeLine(double y, double height, double width, UIElement? end)
    {
        Start.Arrange(Centred(0, y, height, Start, width));
        var endWidth = end is null ? 0 : Math.Min(end.DesiredSize.Width, width);
        if (end is not null) end.Arrange(Centred(width - endWidth, y, height, end, endWidth));
        if (Middle is { } middle)
            middle.Arrange(Centred(PlayerBar.MiddleStart(width, Start.DesiredSize.Width, middle.DesiredSize.Width, endWidth, Spacing), y, height, middle, width));
    }

    private static Rect Centred(double x, double y, double height, UIElement child, double maxWidth)
        => new(x, y + (height - child.DesiredSize.Height) / 2, Math.Min(child.DesiredSize.Width, maxWidth), child.DesiredSize.Height);

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
