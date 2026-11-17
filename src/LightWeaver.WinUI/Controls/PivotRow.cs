using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>A row of three children that keeps the second in the middle: the first ends just before
/// it, the third starts just after it, and both sides take the wider one's width, so the middle child
/// stays put whichever side children are hidden.</summary>
public sealed partial class PivotRow : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(PivotRow), new PropertyMetadata(0d, (d, _) => ((PivotRow)d).InvalidateMeasure()));

    /// <summary>The gap on either side of the middle child.</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private double Side => Math.Max(Children[0].DesiredSize.Width, Children[2].DesiredSize.Width);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3) throw new InvalidOperationException("A PivotRow holds exactly three children.");
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children) child.Measure(infinite);
        var height = Children.Max(child => child.DesiredSize.Height);
        return new Size(2 * (Side + Spacing) + Children[1].DesiredSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (before, pivot, after) = (Children[0], Children[1], Children[2]);
        var pivotLeft = Side + Spacing;
        before.Arrange(Centred(pivotLeft - Spacing - before.DesiredSize.Width, before, finalSize.Height));
        pivot.Arrange(Centred(pivotLeft, pivot, finalSize.Height));
        after.Arrange(Centred(pivotLeft + pivot.DesiredSize.Width + Spacing, after, finalSize.Height));
        return finalSize;
    }

    private static Rect Centred(double x, UIElement child, double height)
        => new(x, (height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height);
}
