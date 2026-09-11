using System.Windows;
using System.Windows.Media;

namespace LightWeaver.Theme;

/// <summary>
/// Round-clips an element's content to a rounded rectangle of the given corner radius.
/// Attach it to the child of a rounded <c>Border</c> that hosts artwork:
/// <c>&lt;Grid theme:RoundedClip.Radius="11"&gt;</c>.
/// <para>
/// <b>Why this exists (Phase 10 M6, found by measurement in M5).</b>
/// <c>ClipToBounds="True"</c> on a <c>Border</c> does <b>NOT</b> round-clip. It clips to the
/// element's <i>rectangular</i> layout bounds, ignoring <c>CornerRadius</c> entirely — that
/// property only affects the shapes the <c>Border</c> itself paints (its background and its
/// border stroke). Any child that fills the Border — an <c>Image</c> with
/// <c>Stretch="UniformToFill"</c>, a progress bar pinned to the bottom edge — therefore keeps
/// square corners and paints straight over the rounded ones. Measured on a bright poster card:
/// a perfect 90-degree corner, zero arc pixels. The radius had only ever been visible on the
/// hairline and on image-less cards, from Phase 6 through Phase 9.
/// </para>
/// <para>
/// A <c>RectangleGeometry</c> with equal <c>RadiusX</c>/<c>RadiusY</c> is the fix: it is a real
/// rounded rect, so the clip follows the corner. It has to be rebuilt whenever the element
/// resizes, which is why this is an attached behaviour rather than a converter on
/// <c>UIElement.Clip</c> — cards, thumbs and hero posters are all data-templated and sized by
/// their parents.
/// </para>
/// <para>
/// The radius passed here is the <i>inner</i> radius, per the shape ladder's concentric rule:
/// outer token minus the Border's own thickness (a 12 px card with a 1 px hairline clips its
/// content at 11), so the arc of the clip sits concentric inside the arc of the stroke instead
/// of cutting across it.
/// </para>
/// <para>
/// It also subsumes the rectangular job <c>ClipToBounds</c> was doing (containing
/// <c>UniformToFill</c> overflow), so sites that gain this attribute drop that one.
/// </para>
/// <para>
/// <b>Attach it to a CONTAINER (a <c>Grid</c> or the <c>Border</c>), never to the
/// <c>Image</c> itself</b> — measured in M6, after doing exactly that and watching the
/// Downloads row thumb come out square with the poster bleeding outside its Border.
/// <c>Image.ArrangeOverride</c> returns the stretch-computed content size, not the arrange
/// slot, so an <c>Image</c> with <c>Stretch="UniformToFill"</c> whose source aspect differs
/// from its slot has an <c>ActualWidth</c>/<c>ActualHeight</c> LARGER than the slot. A clip
/// built from those numbers is the overflowing box itself, so it trims nothing at all — and
/// with <c>ClipToBounds</c> now gone, the overflow escapes. A <c>Grid</c> is safe because
/// <c>Grid.ArrangeOverride</c> always returns <c>finalSize</c>, so its size IS the slot.
/// (The trap hides well: sites whose artwork happens to match the slot's aspect — a 2:3
/// poster in a 2:3 frame, a square headshot in a square avatar — clip correctly by luck.)
/// </para>
/// </summary>
public static class RoundedClip
{
    /// <summary>Inner corner radius, in device-independent pixels. 0 (default) = no clip.</summary>
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip),
            new FrameworkPropertyMetadata(0d, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value)
        => element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element)
        => (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        // Idempotent: the same handler is only ever attached once per element, even though a
        // templated element can have the property re-set as containers are recycled.
        fe.SizeChanged -= OnSizeChanged;
        fe.SizeChanged += OnSizeChanged;
        Apply(fe);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe) Apply(fe);
    }

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        double w = fe.ActualWidth, h = fe.ActualHeight;

        if (r <= 0d || w <= 0d || h <= 0d ||
            double.IsNaN(w) || double.IsNaN(h) || double.IsInfinity(w) || double.IsInfinity(h))
        {
            fe.Clip = null;
            return;
        }

        // Never exceed half the shorter side: past that the arcs overlap and the geometry
        // degenerates (the same clamp Border applies per edge — see HalfHeightRadiusConverter).
        r = Math.Min(r, Math.Min(w, h) / 2d);
        fe.Clip = new RectangleGeometry(new Rect(0d, 0d, w, h), r, r);
    }
}
