using System.Numerics;
using LightWeaver.Navigation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LightWeaver.WinUI.Controls;

/// <summary>A rounded <see cref="Border"/>'s fill and 1 px stroke drawn on the compositor under
/// <c>host</c>, so its horizontal span can animate without layout. Matches a Border with
/// solid-colour brushes: the stroke inside the edge, the fill inside the stroke.</summary>
internal sealed class BorderShape
{
    private readonly UIElement _host;
    private readonly Compositor _compositor;
    private readonly ShapeVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _fill;
    private readonly CompositionRoundedRectangleGeometry _stroke;
    private readonly CompositionColorBrush _fillBrush;
    private readonly double _height;
    private double _origin;

    /// <summary>Draws <paramref name="look"/>'s brushes, radius and height under <paramref name="host"/>,
    /// whose top-left is the border's.</summary>
    public BorderShape(UIElement host, Border look)
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _height = look.ActualHeight > 0 ? look.ActualHeight : look.Height;
        var radius = (float)look.CornerRadius.TopLeft;
        _fillBrush = _compositor.CreateColorBrush(ColorOf(look.Background));
        _fill = _compositor.CreateRoundedRectangleGeometry();
        _fill.CornerRadius = new Vector2(Math.Max(0, radius - 1));
        _stroke = _compositor.CreateRoundedRectangleGeometry();
        _stroke.CornerRadius = new Vector2(Math.Max(0, radius - 0.5f));
        var fill = _compositor.CreateSpriteShape(_fill);
        fill.FillBrush = _fillBrush;
        var stroke = _compositor.CreateSpriteShape(_stroke);
        stroke.StrokeBrush = _compositor.CreateColorBrush(ColorOf(look.BorderBrush));
        stroke.StrokeThickness = 1;
        _visual = _compositor.CreateShapeVisual();
        _visual.Shapes.Add(fill);
        _visual.Shapes.Add(stroke);
    }

    /// <summary>The fill's colour, for a border whose background changes meanwhile (focus).</summary>
    public void SetFill(Brush? brush) => _fillBrush.Color = ColorOf(brush);

    /// <summary>Shows the shape at <paramref name="spans"/>[i] at <paramref name="keys"/>[i] of
    /// <paramref name="duration"/>, starting at once on the first; spans are in the host's coordinates.</summary>
    public void Animate(IReadOnlyList<BarSpan> spans, IReadOnlyList<float> keys, TimeSpan duration)
    {
        _origin = spans.Min(s => s.Left) - 1;
        var right = spans.Max(s => s.Right) + 1;
        _visual.Offset = new Vector3((float)_origin, 0, 0);
        _visual.Size = new Vector2((float)(right - _origin), (float)_height);
        Place(spans[0]);
        var linear = _compositor.CreateLinearEasingFunction();
        var fillOffset = _compositor.CreateVector2KeyFrameAnimation();
        var fillSize = _compositor.CreateVector2KeyFrameAnimation();
        var strokeOffset = _compositor.CreateVector2KeyFrameAnimation();
        var strokeSize = _compositor.CreateVector2KeyFrameAnimation();
        for (var i = 0; i < spans.Count; i++)
        {
            var (fo, fs, so, ss) = Geometry(spans[i]);
            fillOffset.InsertKeyFrame(keys[i], fo, linear);
            fillSize.InsertKeyFrame(keys[i], fs, linear);
            strokeOffset.InsertKeyFrame(keys[i], so, linear);
            strokeSize.InsertKeyFrame(keys[i], ss, linear);
        }
        foreach (var animation in new[] { fillOffset, fillSize, strokeOffset, strokeSize }) animation.Duration = duration;
        _fill.StartAnimation("Offset", fillOffset);
        _fill.StartAnimation("Size", fillSize);
        _stroke.StartAnimation("Offset", strokeOffset);
        _stroke.StartAnimation("Size", strokeSize);
        ElementCompositionPreview.SetElementChildVisual(_host, _visual);
    }

    /// <summary>Takes the shape away.</summary>
    public void Remove()
    {
        foreach (var geometry in new[] { _fill, _stroke })
        {
            geometry.StopAnimation("Offset");
            geometry.StopAnimation("Size");
        }
        ElementCompositionPreview.SetElementChildVisual(_host, null);
    }

    private void Place(BarSpan span)
    {
        var (fo, fs, so, ss) = Geometry(span);
        (_fill.Offset, _fill.Size, _stroke.Offset, _stroke.Size) = (fo, fs, so, ss);
    }

    private (Vector2 FillOffset, Vector2 FillSize, Vector2 StrokeOffset, Vector2 StrokeSize) Geometry(BarSpan span)
    {
        var x = (float)(span.Left - _origin);
        var w = (float)Math.Max(2, span.Width);
        var h = (float)Math.Max(2, _height);
        return (new Vector2(x + 1, 1), new Vector2(w - 2, h - 2), new Vector2(x + 0.5f, 0.5f), new Vector2(w - 1, h - 1));
    }

    private static Color ColorOf(Brush? brush) => brush is SolidColorBrush solid
        ? Color.FromArgb((byte)Math.Round(solid.Color.A * Math.Clamp(solid.Opacity, 0, 1)), solid.Color.R, solid.Color.G, solid.Color.B)
        : default;
}
