using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Controls;

/// <summary>A two-thumb slider for the Year range (design 05). WinUI has no range slider; this one
/// copies the stock Slider's Windows 11 look: a 4 px track, accent fill between the thumbs, and
/// <see cref="RangeThumb"/>s. Thumbs drag, take focus and move with the arrow keys (Page Up/Down
/// by ten, Home/End to the ends).</summary>
public sealed partial class RangeSlider : UserControl
{
    private const double ThumbSize = 20;
    private readonly Canvas _canvas = new() { Height = ThumbSize };
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2) };
    private readonly Border _fill = new() { Height = 4 };
    private double _lower, _upper, _dragValue;

    public RangeSlider()
    {
        LowerThumb = new RangeThumb();
        UpperThumb = new RangeThumb();
        LowerThumb.Read = () => _lower;
        UpperThumb.Read = () => _upper;
        LowerThumb.Write = v => Move(lower: true, Math.Round(v));
        UpperThumb.Write = v => Move(lower: false, Math.Round(v));
        _track.Background = (Brush)Application.Current.Resources["LwTrackBrush"];
        _fill.Background = (Brush)Application.Current.Resources["LwAccentFgBrush"];
        Canvas.SetTop(_track, 8);
        Canvas.SetTop(_fill, 8);
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(LowerThumb);
        _canvas.Children.Add(UpperThumb);
        Content = _canvas;
        Height = ThumbSize;
        Padding = new Thickness(6, 0, 6, 0);
        SizeChanged += (_, _) => Layout();
        Hook(LowerThumb, lower: true);
        Hook(UpperThumb, lower: false);
    }

    public RangeThumb LowerThumb { get; }
    public RangeThumb UpperThumb { get; }

    public double Minimum
    {
        get => _minimum;
        set { _minimum = value; LowerThumb.Minimum = UpperThumb.Minimum = value; }
    }

    public double Maximum
    {
        get => _maximum;
        set { _maximum = value; LowerThumb.Maximum = UpperThumb.Maximum = value; }
    }

    private double _minimum;
    private double _maximum = 100;

    public double LowerValue { get => _lower; set => Set(value, _upper, raise: false); }
    public double UpperValue { get => _upper; set => Set(_lower, value, raise: false); }

    /// <summary>Raised while a thumb moves (drag or keys), with the new lower and upper values.</summary>
    public event EventHandler? RangeChanged;

    public void SetRange(double lower, double upper) => Set(lower, upper, raise: false);

    private void Hook(RangeThumb thumb, bool lower)
    {
        thumb.DragStarted += (_, _) => _dragValue = lower ? _lower : _upper;
        thumb.Dragged += (_, dx) =>
        {
            var width = Math.Max(1, ActualWidth - Padding.Left - Padding.Right);
            _dragValue += dx / width * (Maximum - Minimum);
            Move(lower, Math.Round(_dragValue));
        };
        thumb.KeyDown += (_, e) =>
        {
            var current = lower ? _lower : _upper;
            double? target = e.Key switch
            {
                VirtualKey.Left or VirtualKey.Down => current - 1,
                VirtualKey.Right or VirtualKey.Up => current + 1,
                VirtualKey.PageDown => current - 10,
                VirtualKey.PageUp => current + 10,
                VirtualKey.Home => Minimum,
                VirtualKey.End => Maximum,
                _ => null,
            };
            if (target is null) return;
            Move(lower, target.Value);
            e.Handled = true;
        };
    }

    private void Move(bool lower, double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (lower) Set(Math.Min(value, _upper), _upper, raise: true);
        else Set(_lower, Math.Max(value, _lower), raise: true);
    }

    private void Set(double lower, double upper, bool raise)
    {
        lower = Math.Clamp(lower, Minimum, Maximum);
        upper = Math.Clamp(upper, Minimum, Maximum);
        if (lower > upper) (lower, upper) = (upper, lower);
        var changed = lower != _lower || upper != _upper;
        var (oldLower, oldUpper) = (_lower, _upper);
        _lower = lower;
        _upper = upper;
        if (oldLower != lower) LowerThumb.RaiseValueChanged(oldLower, lower);
        if (oldUpper != upper) UpperThumb.RaiseValueChanged(oldUpper, upper);
        Layout();
        if (raise && changed) RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Layout()
    {
        var width = ActualWidth - Padding.Left - Padding.Right;
        if (width <= 0) return;
        double X(double v) => Padding.Left + (Maximum > Minimum ? (v - Minimum) / (Maximum - Minimum) : 0) * width;
        _track.Width = width;
        Canvas.SetLeft(_track, Padding.Left);
        var a = X(_lower);
        var b = X(_upper);
        Canvas.SetLeft(_fill, a);
        _fill.Width = Math.Max(0, b - a);
        Canvas.SetLeft(LowerThumb, a - ThumbSize / 2);
        Canvas.SetLeft(UpperThumb, b - ThumbSize / 2);
    }
}
