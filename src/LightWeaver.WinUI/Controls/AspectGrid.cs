using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>A grid whose height follows its width at a fixed ratio, decided while measuring, so a
/// width change never needs a second layout pass (a SizeChanged handler setting Height can loop
/// into a layout cycle when a virtualized grid realizes many cards at once).</summary>
internal sealed partial class AspectGrid : Grid
{
    private double _ratio = 2.0 / 3;

    /// <summary>Width divided by height (2:3 posters, 16:9 stills).</summary>
    public double Ratio
    {
        get => _ratio;
        set
        {
            if (value <= 0 || Math.Abs(value - _ratio) < 0.0001) return;
            _ratio = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = !double.IsNaN(Width) ? Width : double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var size = new Size(width, width / _ratio);
        base.MeasureOverride(size);
        return size;
    }
}
