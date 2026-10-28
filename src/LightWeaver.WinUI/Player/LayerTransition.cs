using System.Diagnostics;
using System.Numerics;
using LightWeaver.Player;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace LightWeaver.WinUI.Player;

/// <summary>The player's grow and shrink on the compositor: the layer is laid out once at the end
/// rect and its visual is scaled, moved and clipped so that it shows each frame's rect of
/// <see cref="LayerMotion"/>, so a busy UI thread cannot stall it. The picture keeps its shape
/// (<see cref="LayerContent"/>) and the corners stay the given radius on screen; the clip is a
/// rounded-rectangle geometry, since a <see cref="RectangleClip"/>'s corner radii cannot be animated.
/// The scaled layer can fall short of a frame's rect (a 16:9 picture leaving a 16:10 window), so an
/// opaque layer gets a black backdrop under it, drawn unscaled at each frame's rect with the same
/// corners. A new end rect mid-way (<see cref="Retarget"/>) continues from the time already spent,
/// or lands at once when too little time is left (<see cref="LayerMotion.Plan"/>).</summary>
internal sealed class LayerTransition
{
    private readonly UIElement _layer;
    private readonly Visual _visual;
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _clipShape;
    private readonly CompositionGeometricClip _clip;
    private readonly LayerContent _content;
    private readonly double _radius;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _deadline;
    private readonly UIElement? _backdropHost;
    private readonly ShapeVisual? _backdrop;
    private readonly CompositionRoundedRectangleGeometry? _backdropRect;
    private LayerRect _from;
    private LayerRect _layout;
    private long _started;
    private CompositionScopedBatch? _batch;
    private bool _done;

    /// <summary>A transition of <paramref name="layer"/>; <paramref name="backdropHost"/>, an element
    /// under it at the window's origin, carries the black backdrop of an opaque layer.</summary>
    public LayerTransition(UIElement layer, LayerRect from, LayerRect layout, LayerContent content, double radius, UIElement? backdropHost)
    {
        _layer = layer;
        _visual = ElementCompositionPreview.GetElementVisual(layer);
        _compositor = _visual.Compositor;
        _clipShape = _compositor.CreateRoundedRectangleGeometry();
        _clip = _compositor.CreateGeometricClip(_clipShape);
        (_from, _layout, _content, _radius) = (from, layout, content, radius);
        _deadline = layer.DispatcherQueue.CreateTimer();
        _deadline.IsRepeating = false;
        _deadline.Tick += (_, _) => Finish();
        if (backdropHost is null) return;
        _backdropHost = backdropHost;
        _backdropRect = _compositor.CreateRoundedRectangleGeometry();
        _backdropRect.CornerRadius = new Vector2((float)radius);
        var fill = _compositor.CreateSpriteShape(_backdropRect);
        fill.FillBrush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
        _backdrop = _compositor.CreateShapeVisual();
        _backdrop.Shapes.Add(fill);
    }

    /// <summary>The last frame is on screen.</summary>
    public event EventHandler? Completed;

    /// <summary>The share of the time spent, 0 to 1.</summary>
    public double Progress => _started == 0 ? 0 : Math.Min(1, Stopwatch.GetElapsedTime(_started) / LayerMotion.Duration);

    public void Start()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_layer, true);
        _visual.Clip = _clip;
        _started = Stopwatch.GetTimestamp();
        Run(0);
    }

    /// <summary>Draws the layer at the start rect and keeps it there, without animating or
    /// completing, until <see cref="Stop"/>: the composition the window showed before a change of
    /// its own size, while the content is already laid out for the new one.</summary>
    public void Hold()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_layer, true);
        _visual.Clip = _clip;
        if (!_from.IsUsable || !_layout.IsUsable) return;
        Apply(LayerMotion.Frame(_from, _layout, _content, _radius));
        PlaceBackdrop(_from);
        if (_backdropHost is not null) ElementCompositionPreview.SetElementChildVisual(_backdropHost, _backdrop);
    }

    /// <summary>Moves the ends: the layer must be laid out at <paramref name="layout"/> in the same
    /// UI tick. False when neither end moved by half a pixel or more, or when <paramref name="layout"/>
    /// is not a usable rect (a window between sizes); an unusable <paramref name="from"/> keeps the old start.</summary>
    public bool Retarget(LayerRect from, LayerRect layout)
    {
        if (_done || !layout.IsUsable) return false;
        if (!from.IsUsable) from = _from;
        if (Same(from, _from) && Same(layout, _layout)) return false;
        (_from, _layout) = (from, layout);
        Run(Progress);
        return true;
    }

    /// <summary>Ends it where it is and puts the visual back as laid out.</summary>
    public void Stop()
    {
        _done = true;
        _batch = null;
        _deadline.Stop();
        StopAnimations();
        _visual.Scale = Vector3.One;
        _visual.CenterPoint = Vector3.Zero;
        _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _visual.Clip = null;
        if (_backdropHost is not null) ElementCompositionPreview.SetElementChildVisual(_backdropHost, null);
    }

    private static readonly string[] ClipProperties = ["Offset", "Size", "CornerRadius"];

    private static bool Same(LayerRect a, LayerRect b)
        => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private void Run(double startT)
    {
        var plan = _from.IsUsable && _layout.IsUsable ? LayerMotion.Plan(startT, LayerMotion.Duration) : null;
        if (plan is null)
        {
            Snap();
            return;
        }
        var rects = plan.Times.Select(t => LayerMotion.At(_from, _layout, t)).ToArray();
        var frames = rects.Select(r => LayerMotion.Frame(r, _layout, _content, _radius)).ToArray();
        if (!frames.All(f => f.IsValid))
        {
            Diagnostics.AppLog.Info("player", "event=layer_transition outcome=snapped reason=invalid_frame");
            Snap();
            return;
        }

        Apply(frames[0]);
        var linear = _compositor.CreateLinearEasingFunction();
        var scale = _compositor.CreateVector3KeyFrameAnimation();
        var translation = _compositor.CreateVector3KeyFrameAnimation();
        var clipOffset = _compositor.CreateVector2KeyFrameAnimation();
        var clipSize = _compositor.CreateVector2KeyFrameAnimation();
        var corners = _compositor.CreateVector2KeyFrameAnimation();
        for (var i = 0; i < frames.Length; i++)
        {
            var key = plan.Keys[i];
            var f = frames[i];
            scale.InsertKeyFrame(key, new Vector3((float)f.ScaleX, (float)f.ScaleY, 1), linear);
            translation.InsertKeyFrame(key, new Vector3((float)f.TranslateX, (float)f.TranslateY, 0), linear);
            clipOffset.InsertKeyFrame(key, new Vector2((float)f.ClipLeft, (float)f.ClipTop), linear);
            clipSize.InsertKeyFrame(key, new Vector2((float)f.ClipWidth, (float)f.ClipHeight), linear);
            corners.InsertKeyFrame(key, new Vector2((float)f.RadiusX, (float)f.RadiusY), linear);
        }
        var animations = new KeyFrameAnimation[] { scale, translation, clipOffset, clipSize, corners };
        foreach (var animation in animations) animation.Duration = plan.Remaining;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        try
        {
            RunBackdrop(rects, plan);
            _visual.StartAnimation("Scale", scale);
            _visual.StartAnimation("Translation", translation);
            _clipShape.StartAnimation("Offset", clipOffset);
            _clipShape.StartAnimation("Size", clipSize);
            _clipShape.StartAnimation("CornerRadius", corners);
        }
        catch (ArgumentException e)
        {
            batch.End();
            Diagnostics.AppLog.Info("player", $"event=layer_transition outcome=snapped reason=start_animation hresult=0x{e.HResult:X8}");
            Snap();
            return;
        }
        batch.End();
        _batch = batch;
        batch.Completed += (_, _) => { if (ReferenceEquals(_batch, batch)) Finish(); };
        _deadline.Stop();
        _deadline.Interval = plan.Remaining + TimeSpan.FromMilliseconds(100);
        _deadline.Start();
    }

    /// <summary>The black under the layer at each frame's rect, unscaled, with the layer's corners.</summary>
    private void RunBackdrop(LayerRect[] rects, MotionPlan plan)
    {
        if (_backdrop is null || _backdropRect is null) return;
        _backdrop.Size = new Vector2((float)rects.Max(r => r.X + r.Width), (float)rects.Max(r => r.Y + r.Height));
        _backdropRect.Offset = new Vector2((float)rects[0].X, (float)rects[0].Y);
        _backdropRect.Size = new Vector2((float)rects[0].Width, (float)rects[0].Height);
        var linear = _compositor.CreateLinearEasingFunction();
        var offset = _compositor.CreateVector2KeyFrameAnimation();
        var size = _compositor.CreateVector2KeyFrameAnimation();
        for (var i = 0; i < rects.Length; i++)
        {
            offset.InsertKeyFrame(plan.Keys[i], new Vector2((float)rects[i].X, (float)rects[i].Y), linear);
            size.InsertKeyFrame(plan.Keys[i], new Vector2((float)rects[i].Width, (float)rects[i].Height), linear);
        }
        offset.Duration = size.Duration = plan.Remaining;
        _backdropRect.StartAnimation("Offset", offset);
        _backdropRect.StartAnimation("Size", size);
        ElementCompositionPreview.SetElementChildVisual(_backdropHost, _backdrop);
    }

    /// <summary>Sets a frame at once, so the first frame drawn is already the start rect.</summary>
    private void Apply(LayerFrame f)
    {
        _visual.CenterPoint = new Vector3((float)(_layout.Width / 2), (float)(_layout.Height / 2), 0);
        _visual.Scale = new Vector3((float)f.ScaleX, (float)f.ScaleY, 1);
        _visual.Properties.InsertVector3("Translation", new Vector3((float)f.TranslateX, (float)f.TranslateY, 0));
        _clipShape.Offset = new Vector2((float)f.ClipLeft, (float)f.ClipTop);
        _clipShape.Size = new Vector2((float)f.ClipWidth, (float)f.ClipHeight);
        _clipShape.CornerRadius = new Vector2((float)f.RadiusX, (float)f.RadiusY);
    }

    /// <summary>Too little time left to animate, or nothing valid to animate: shows the end rect at
    /// once and lands on the next UI turn, after the caller has laid the layer out there.</summary>
    private void Snap()
    {
        _batch = null;
        _deadline.Stop();
        StopAnimations();
        if (_layout.IsUsable)
        {
            Apply(LayerMotion.Frame(_layout, _layout, _content, _radius));
            PlaceBackdrop(_layout);
        }
        _layer.DispatcherQueue.TryEnqueue(Finish);
    }

    private void PlaceBackdrop(LayerRect rect)
    {
        if (_backdrop is null || _backdropRect is null) return;
        _backdrop.Size = new Vector2((float)(rect.X + rect.Width), (float)(rect.Y + rect.Height));
        _backdropRect.Offset = new Vector2((float)rect.X, (float)rect.Y);
        _backdropRect.Size = new Vector2((float)rect.Width, (float)rect.Height);
    }

    private void StopAnimations()
    {
        _visual.StopAnimation("Scale");
        _visual.StopAnimation("Translation");
        foreach (var property in ClipProperties) _clipShape.StopAnimation(property);
        _backdropRect?.StopAnimation("Offset");
        _backdropRect?.StopAnimation("Size");
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        _deadline.Stop();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
