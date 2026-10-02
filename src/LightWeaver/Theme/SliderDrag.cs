using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LightWeaver.Theme;

/// <summary>
/// Makes a <see cref="Slider"/> begin a real thumb drag from wherever its track was pressed, so
/// one gesture both jumps the value to the click point and keeps dragging from there. Attached
/// once in the <c>LwSlider</c> style, so every current and future slider gets it:
/// <c>&lt;Setter Property="theme:SliderDrag.ClickToDrag" Value="True" /&gt;</c>.
/// <para>
/// <b>Why this exists.</b> <see cref="Slider.IsMoveToPointEnabled"/> only does half the job. WPF
/// registers a <i>class</i> handler for <see cref="UIElement.PreviewMouseLeftButtonDownEvent"/> on
/// <c>Slider</c>; when the flag is set and the press is not over the thumb, it computes
/// <c>Track.ValueFromPoint</c>, assigns the value, and marks the event handled. It never hands the
/// press to the <see cref="Thumb"/>, so the thumb never captures the mouse and never raises
/// <see cref="Thumb.DragStartedEvent"/> — the value jumps once and the gesture is over before the
/// user has moved. Dragging therefore only ever worked if the press landed on the 12 px knob.
/// </para>
/// <para>
/// Without <see cref="Slider.IsMoveToPointEnabled"/> it is worse still: a track press routes to the
/// <c>Slider.DecreaseLarge</c> / <c>IncreaseLarge</c> RepeatButtons that the <c>LwSlider</c>
/// template puts in the Track, and <see cref="System.Windows.Controls.Primitives.RangeBase.LargeChange"/>
/// defaults to <b>1</b> — measured on the volume slider before it carried the flag, a click at the
/// 15% mark moved the value from 50 to 49 instead of to 11.1 (2026-08-05). That is why the style
/// sets both properties together, and why this behaviour is inert unless the flag is on: it relies
/// on the class handler having already moved the value to the press point.
/// </para>
/// <para>
/// <b>The forced layout pass is load-bearing.</b> <c>Thumb.OnMouseLeftButtonDown</c> records its
/// drag origin as <c>e.MouseDevice.GetPosition(this)</c>. The class handler has changed
/// <see cref="System.Windows.Controls.Primitives.RangeBase.Value"/> by the time this handler runs,
/// but the <see cref="Track"/> has not been re-arranged yet, so that position would be measured
/// against the thumb's <i>old</i> location and the whole offset would be baked into the drag — the
/// knob leaps away from the cursor on the first mouse-move. <see cref="UIElement.UpdateLayout"/>
/// arranges the track synchronously first, so the thumb is already under the cursor when it
/// takes the press.
/// </para>
/// </summary>
public static class SliderDrag
{
    /// <summary>Set to true to continue a track press as a thumb drag. Requires
    /// <see cref="Slider.IsMoveToPointEnabled"/>; inert without it.</summary>
    public static readonly DependencyProperty ClickToDragProperty =
        DependencyProperty.RegisterAttached(
            "ClickToDrag", typeof(bool), typeof(SliderDrag),
            new FrameworkPropertyMetadata(false, OnClickToDragChanged));

    public static void SetClickToDrag(DependencyObject element, bool value)
        => element.SetValue(ClickToDragProperty, value);

    public static bool GetClickToDrag(DependencyObject element)
        => (bool)element.GetValue(ClickToDragProperty);

    private static void OnClickToDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        // handledEventsToo: the class handler above marks the preview-down handled, so a plain
        // handler would never run at all. Removing first keeps this idempotent — a style setter
        // can be re-applied when a templated parent recycles its containers.
        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown));

        if (e.NewValue is true)
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), handledEventsToo: true);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // GetClickToDrag is re-read rather than trusted to the handler having been removed. The
        // removal does work (delegate equality over a static method matches), but a feature that
        // fails safe when it is turned off beats one that depends on a detach.
        if (sender is not Slider slider || !slider.IsMoveToPointEnabled || !GetClickToDrag(slider))
            return;

        var thumb = FindThumb(slider);

        // A press on the knob is already a native drag: WPF's class handler skips move-to-point
        // precisely when the thumb is under the mouse, and re-raising here would start the
        // gesture twice.
        if (thumb is null || !thumb.IsEnabled || thumb.IsMouseOver || thumb.IsDragging)
            return;

        slider.UpdateLayout();

        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb,
        });

        // Thumb.OnMouseLeftButtonDown ignores whether CaptureMouse() succeeded: it sets
        // IsDragging and raises DragStarted either way. But Thumb.OnMouseLeftButtonUp is guarded
        // on IsMouseCaptured, so a capture that did not take means DragCompleted never fires and
        // IsDragging LATCHES - after which the guard above makes every later track press inert,
        // and on the seek bar the overlay's own IsSeeking would stay set too, which is exactly
        // B8's symptom made permanent. Unwinding a drag that never really started turns that
        // latch into a harmless no-op. No trigger for a failing capture is known here; this is
        // cheap insurance, not a fix for an observed defect.
        if (thumb.IsDragging && !thumb.IsMouseCaptured)
            thumb.CancelDrag();
    }

    /// <summary>The template's track, by the name <c>Slider</c> itself requires. Resolved per press
    /// rather than cached: the style is shared by five sliders and a re-template would strand a
    /// cached reference.</summary>
    private static Thumb? FindThumb(Slider slider)
    {
        slider.ApplyTemplate();
        return (slider.Template?.FindName("PART_Track", slider) as Track)?.Thumb;
    }
}
