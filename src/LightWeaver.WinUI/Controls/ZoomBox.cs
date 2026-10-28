using LightWeaver.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LightWeaver.WinUI.Controls;

/// <summary>Lays out and renders its child at the app zoom.</summary>
[Microsoft.UI.Xaml.Markup.ContentProperty(Name = nameof(Child))]
public sealed partial class ZoomBox : Panel
{
    private readonly Border _host = new();
    private readonly ScaleTransform _scale = new();
    private bool _follows = true;
    private bool _subscribed;

    public ZoomBox()
    {
        _host.RenderTransform = _scale;
        Children.Add(_host);
        Loaded += (_, _) => Subscribe(true);
        Unloaded += (_, _) => { if (!IsLoaded) Subscribe(false); };
    }

    /// <summary>The zoomed content.</summary>
    public UIElement? Child
    {
        get => _host.Child;
        set => _host.Child = value;
    }

    /// <summary>Off, the content keeps 100% (the mini-player's fixed layout).</summary>
    public bool FollowsZoom
    {
        get => _follows;
        set
        {
            if (_follows == value) return;
            _follows = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The scale the content is drawn at.</summary>
    public double Factor => _follows ? UiZoom.Scale : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var z = Factor;
        _host.Measure(new Size(availableSize.Width / z, availableSize.Height / z));
        return new Size(_host.DesiredSize.Width * z, _host.DesiredSize.Height * z);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var z = Factor;
        _scale.ScaleX = _scale.ScaleY = z;
        _host.Arrange(new Rect(0, 0, finalSize.Width / z, finalSize.Height / z));
        return finalSize;
    }

    private void Subscribe(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        if (on)
        {
            UiZoom.Changed += OnZoomChanged;
            InvalidateMeasure();
        }
        else
        {
            UiZoom.Changed -= OnZoomChanged;
        }
    }

    private void OnZoomChanged(double scale) => InvalidateMeasure();
}
