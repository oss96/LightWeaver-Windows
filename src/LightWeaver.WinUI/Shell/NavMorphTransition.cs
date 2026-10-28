using System.Diagnostics;
using System.Numerics;
using LightWeaver.Navigation;
using LightWeaver.Player;
using LightWeaver.WinUI.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>The top bar's parts the nav morph moves.</summary>
internal sealed record NavMorphParts(
    Border Group, UIElement GroupShapeHost, UIElement GroupContent,
    FrameworkElement SearchField, Border SearchChrome, UIElement SearchShapeHost, FrameworkElement SearchBottom,
    IReadOnlyList<UIElement> SearchLeading, IReadOnlyList<UIElement> SearchTrailing);

/// <summary>The nav morph (design 01) on the compositor: the group is laid out once at the wider of
/// its two widths and the search field at its matching place; the group's pill is drawn as a shape
/// whose width animates, its items are clipped to it, and the search field's chrome, text and
/// buttons are drawn where they would be at each frame's group width. 240 ms, cubic ease-out, the
/// same look as the width storyboard it replaces, without a layout pass per frame. A resize that
/// moves the search field mid-way retargets it (<see cref="Retarget"/>).</summary>
internal sealed class NavMorphTransition
{
    private readonly NavMorphParts _parts;
    private readonly double _from;
    private readonly double _to;
    private readonly Compositor _compositor;
    private readonly BorderShape _pill;
    private readonly BorderShape _chrome;
    private readonly InsetClip _clip;
    private readonly Brush? _groupFill;
    private readonly Brush? _groupStroke;
    private readonly Brush? _chromeStroke;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _deadline;
    private BarSpan _slot;
    private double _navAtSlot;
    private Brush? _chromeFill;
    private CompositionScopedBatch? _batch;
    private long _started;
    private bool _done;

    /// <summary>A morph from <paramref name="from"/> to <paramref name="to"/>; <paramref name="slot"/>
    /// is the search field's column slot while the group is laid out <paramref name="navAtSlot"/> wide.</summary>
    public NavMorphTransition(NavMorphParts parts, double from, double to, BarSpan slot, double navAtSlot)
    {
        _parts = parts;
        (_from, _to, _slot, _navAtSlot) = (from, to, slot, navAtSlot);
        _compositor = ElementCompositionPreview.GetElementVisual(parts.Group).Compositor;
        _pill = new BorderShape(parts.GroupShapeHost, parts.Group);
        _chrome = new BorderShape(parts.SearchShapeHost, parts.SearchChrome);
        _clip = _compositor.CreateInsetClip();
        (_groupFill, _groupStroke) = (parts.Group.Background, parts.Group.BorderBrush);
        (_chromeFill, _chromeStroke) = (parts.SearchChrome.Background, parts.SearchChrome.BorderBrush);
        _deadline = parts.Group.DispatcherQueue.CreateTimer();
        _deadline.IsRepeating = false;
        _deadline.Tick += (_, _) => Finish();
    }

    /// <summary>The group's width laid out for the whole morph.</summary>
    public double LayoutWidth => Math.Max(_from, _to);

    /// <summary>The group's width on screen now.</summary>
    public double Width => NavMorphMotion.WidthAt(_from, _to, Progress);

    private double Progress => _started == 0 ? 0 : Math.Min(1, Stopwatch.GetElapsedTime(_started) / NavMorphMotion.Duration);

    public event EventHandler? Completed;

    /// <summary>Starts it; the group must be laid out <see cref="LayoutWidth"/> wide in the same UI tick.</summary>
    public void Start()
    {
        _started = Stopwatch.GetTimestamp();
        _parts.Group.Background = _parts.Group.BorderBrush = null;
        _parts.SearchChrome.Background = _parts.SearchChrome.BorderBrush = null;
        Run(0);
    }

    /// <summary>The search field's column slot as laid out now, with the group <paramref name="nav"/>
    /// wide. When the bar was resized or moved since the morph started, the rest of the morph is
    /// drawn from this slot, from the time already spent, or lands at once when too little time is
    /// left; false when nothing moved.</summary>
    public bool Retarget(BarSpan slot, double nav)
    {
        if (_done || !NavMorphMotion.SlotMoved(_slot, _navAtSlot, slot, nav)) return false;
        (_slot, _navAtSlot) = (slot, nav);
        Run(Progress);
        return true;
    }

    private void Run(double startT)
    {
        if (LayerMotion.Plan(startT, NavMorphMotion.Duration, NavMorphMotion.Samples) is not { } plan)
        {
            Snap();
            return;
        }
        var (times, keys, duration) = (plan.Times, plan.Keys, plan.Remaining);
        var count = times.Count - 1;
        var widths = times.Select(t => NavMorphMotion.WidthAt(_from, _to, t)).ToArray();
        var layout = LayoutWidth;
        var laidOut = Search(layout);
        var search = widths.Select(Search).ToArray();

        _pill.Animate([.. widths.Select(w => new BarSpan(0, w))], keys, duration);
        _chrome.Animate([.. search.Select(s => new BarSpan(s.Left - laidOut.Left, s.Width))], keys, duration);

        var linear = _compositor.CreateLinearEasingFunction();
        var inset = _compositor.CreateScalarKeyFrameAnimation();
        var leading = _compositor.CreateVector3KeyFrameAnimation();
        var trailing = _compositor.CreateVector3KeyFrameAnimation();
        var bottomShift = _compositor.CreateVector3KeyFrameAnimation();
        var bottomScale = _compositor.CreateVector3KeyFrameAnimation();
        var bottomWidth = Math.Max(1, laidOut.Width - 2);
        for (var i = 0; i <= count; i++)
        {
            var left = (float)(search[i].Left - laidOut.Left);
            inset.InsertKeyFrame(keys[i], (float)(layout - widths[i]), linear);
            leading.InsertKeyFrame(keys[i], new Vector3(left, 0, 0), linear);
            trailing.InsertKeyFrame(keys[i], new Vector3((float)(search[i].Right - laidOut.Right), 0, 0), linear);
            bottomShift.InsertKeyFrame(keys[i], new Vector3(left, 0, 0), linear);
            bottomScale.InsertKeyFrame(keys[i], new Vector3((float)(Math.Max(0, search[i].Width - 2) / bottomWidth), 1, 1), linear);
        }
        foreach (var animation in new KeyFrameAnimation[] { inset, leading, trailing, bottomShift, bottomScale }) animation.Duration = duration;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _clip.RightInset = (float)(layout - widths[0]);
        Visual(_parts.GroupContent).Clip = _clip;
        _clip.StartAnimation("RightInset", inset);
        foreach (var element in _parts.SearchLeading) Translate(element, leading, search[0].Left - laidOut.Left);
        foreach (var element in _parts.SearchTrailing) Translate(element, trailing, search[0].Right - laidOut.Right);
        Translate(_parts.SearchBottom, bottomShift, search[0].Left - laidOut.Left);
        var bottom = Visual(_parts.SearchBottom);
        bottom.CenterPoint = Vector3.Zero;
        bottom.Scale = new Vector3((float)(Math.Max(0, search[0].Width - 2) / bottomWidth), 1, 1);
        bottom.StartAnimation("Scale", bottomScale);
        batch.End();
        _batch = batch;
        batch.Completed += (_, _) => { if (ReferenceEquals(_batch, batch)) Finish(); };
        _deadline.Stop();
        _deadline.Interval = duration + TimeSpan.FromMilliseconds(100);
        _deadline.Start();
    }

    /// <summary>Too little time left to animate: lands on the next UI turn, where
    /// <see cref="Completed"/> lays the group out at its end width.</summary>
    private void Snap()
    {
        _batch = null;
        _deadline.Stop();
        _parts.Group.DispatcherQueue.TryEnqueue(Finish);
    }

    /// <summary>The search chrome's fill changed (focus) while the morph runs.</summary>
    public void SetSearchFill(Brush? brush)
    {
        _chromeFill = brush;
        if (!_done) _chrome.SetFill(brush);
    }

    /// <summary>Ends it and puts every part back as laid out, with its own brushes.</summary>
    public void Stop()
    {
        _done = true;
        _batch = null;
        _deadline.Stop();
        _pill.Remove();
        _chrome.Remove();
        _clip.StopAnimation("RightInset");
        Visual(_parts.GroupContent).Clip = null;
        foreach (var element in _parts.SearchLeading.Concat(_parts.SearchTrailing).Append(_parts.SearchBottom))
        {
            var visual = Visual(element);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        var bottom = Visual(_parts.SearchBottom);
        bottom.StopAnimation("Scale");
        bottom.Scale = Vector3.One;
        (_parts.Group.Background, _parts.Group.BorderBrush) = (_groupFill, _groupStroke);
        (_parts.SearchChrome.Background, _parts.SearchChrome.BorderBrush) = (_chromeFill, _chromeStroke);
    }

    private BarSpan Search(double nav) => NavMorphMotion.SearchSpan(_slot, _navAtSlot, nav, _parts.SearchField.MinWidth, _parts.SearchField.MaxWidth);

    private static Visual Visual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    private static void Translate(UIElement element, Vector3KeyFrameAnimation animation, double first)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Visual(element);
        visual.Properties.InsertVector3("Translation", new Vector3((float)first, 0, 0));
        visual.StartAnimation("Translation", animation);
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        _deadline.Stop();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
