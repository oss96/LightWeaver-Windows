using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LightWeaver.Theme;

/// <summary>
/// Offscreen proof that the <c>LwSlider</c> style turns a press on the track into a real thumb
/// drag. No window, no HWND, no guest: the slider is measured and arranged by hand and the press
/// is a synthesised routed event, so this is deterministic and runs anywhere.
/// <para>
/// It cannot cover everything. Hit-testing a real cursor against a real track, and the value a
/// press actually lands on, need the UIA suite on the Hyper-V guest. What it does cover is the
/// half that would otherwise break silently: the style losing one of its two setters, the template
/// losing the <c>PART_Track</c> name <see cref="SliderDrag"/> looks the thumb up by, and the press
/// no longer reaching the thumb. Each of those disables click-to-drag with no error anywhere.
/// </para>
/// </summary>
internal static class SliderDragFixture
{
    /// <param name="test">The harness's own test recorder.</param>
    /// <param name="theme">The application's merged resources — <c>Application.Current.Resources</c>.
    /// It has to be the real thing. Merging the six theme dictionaries by hand does NOT work, and
    /// the reason is worth keeping: a StaticResource inside a ControlTemplate resolves against the
    /// dictionary chain captured when the template was PARSED, not against the runtime element tree
    /// (that is what DynamicResource does). Controls.xaml parsed on its own has no Colors.xaml above
    /// it, so the template's brushes stay unresolvable however the dictionaries are stitched together
    /// afterwards — measured as "Cannot find resource named 'LwStorm4Brush'" while applying the
    /// template to a slider whose own parent's Resources held exactly that brush. App.xaml is where
    /// the six get merged in the order that resolves.</param>
    public static void Run(Action<string, Action> test, ResourceDictionary theme)
    {
        // Resolved inside the first leg, not before it: a renamed or missing resource would
        // otherwise throw out of Run with a raw stack trace instead of the harness's own FAIL line.
        Style? style = null;
        // Named for what it actually checks: the STYLE, not the five instances. It cannot see a
        // future slider that copies an inline style instead of using LwSlider.
        test("the LwSlider style wires click-to-drag", () =>
        {
            style = (Style)theme["LwSlider"];
            // Both setters or neither. IsMoveToPointEnabled moves the value to the press point;
            // ClickToDrag hands the press on to the thumb. SliderDrag is deliberately inert
            // without the first, so losing either setter silently reverts the whole feature.
            AssertSetter(style, Slider.IsMoveToPointEnabledProperty);
            AssertSetter(style, SliderDrag.ClickToDragProperty);
        });
        if (style is null)
        {
            Console.WriteLine("SKIP: the remaining slider legs need the LwSlider style.");
            return;
        }

        test("LwSlider's template still exposes the thumb SliderDrag reaches for", () =>
        {
            if (FindThumb(Arrange(style)) is null)
                throw new InvalidOperationException(
                    "No Thumb under a Track named PART_Track. SliderDrag cannot find its target, "
                    + "so every slider stops dragging from a track press and nothing reports it.");
        });

        test("a press on the track starts a thumb drag", () =>
        {
            var slider = Arrange(style);
            var started = CountDragStarts(slider, () => PressTrack(slider));
            if (started != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one DragStarted from a track press, got {started}.");
        });

        test("control: the same press without ClickToDrag starts nothing", () =>
        {
            // The discriminating leg. Without it the assertion above could be passing on some
            // stray DragStarted rather than on the behaviour under test: this is the identical
            // press on an identically arranged slider with only ClickToDrag turned off.
            var slider = Arrange(style);
            SliderDrag.SetClickToDrag(slider, false);
            var started = CountDragStarts(slider, () => PressTrack(slider));
            if (started != 0)
                throw new InvalidOperationException(
                    $"A slider without ClickToDrag started {started} drags; the leg above proves nothing.");
        });

        test("a press whose capture fails does not latch the thumb", () =>
        {
            // Offscreen is the ONLY place this is observable, and here it is free: with no
            // PresentationSource, CaptureMouse() always fails, which is exactly the condition
            // SliderDrag's unwind exists for. Thumb.OnMouseLeftButtonDown sets IsDragging and
            // raises DragStarted whether or not the capture took, while OnMouseLeftButtonUp is
            // guarded on IsMouseCaptured - so with no unwind, IsDragging sticks, SliderDrag's own
            // guard then swallows every later track press, and on the seek bar IsSeeking would
            // stay set for good. That is B8's symptom made permanent.
            var slider = Arrange(style);
            var thumb = FindThumb(slider)!;
            PressTrack(slider);
            if (thumb.IsDragging)
                throw new InvalidOperationException(
                    "The thumb is still dragging after a press whose capture could not be taken. "
                    + "SliderDrag's unwind is gone and the latch is back.");

            // And because it did not latch, the next press still works.
            var started = CountDragStarts(slider, () => PressTrack(slider));
            if (started != 1)
                throw new InvalidOperationException(
                    $"A later track press started {started} drags; expected 1.");
        });
    }

    private static void AssertSetter(Style style, DependencyProperty property)
    {
        foreach (var setter in style.Setters)
            if (setter is Setter { Value: true } s && s.Property == property)
                return;
        throw new InvalidOperationException(
            $"LwSlider does not set {property.OwnerType.Name}.{property.Name} to true.");
    }

    /// <summary>A slider laid out at a real size but shown in no window. Measure/Arrange applies the
    /// template and positions the Track, which is all <see cref="SliderDrag"/> needs.</summary>
    private static Slider Arrange(Style style)
    {
        var slider = new Slider { Style = style, Minimum = 0, Maximum = 100, Value = 50, Width = 200 };
        try
        {
            slider.Measure(new Size(200, 24));
            slider.Arrange(new Rect(0, 0, 200, 24));
            slider.UpdateLayout();
        }
        catch (Exception ex)
        {
            // A template failure surfaces as a bare "Provide value on StaticResourceHolder threw an
            // exception", which names neither the resource nor the dictionary. Carry the innermost
            // message up so the failure line is actionable.
            var inner = ex;
            while (inner.InnerException is { } next) inner = next;
            throw new InvalidOperationException(
                $"Applying the LwSlider template failed: {inner.GetType().Name}: {inner.Message}", ex);
        }
        return slider;
    }

    private static Thumb? FindThumb(Slider slider)
    {
        slider.ApplyTemplate();
        return (slider.Template?.FindName("PART_Track", slider) as Track)?.Thumb;
    }

    /// <summary>The press the behaviour listens for. Tunnelling event, raised on the slider, so it
    /// takes the same path a real press does: WPF's move-to-point class handler first (which marks
    /// it handled), then SliderDrag's handledEventsToo handler.</summary>
    private static void PressTrack(Slider slider)
        => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = slider,
        });

    private static int CountDragStarts(Slider slider, Action press)
    {
        var started = 0;
        var handler = new DragStartedEventHandler((_, _) => started++);
        slider.AddHandler(Thumb.DragStartedEvent, handler);
        try { press(); }
        finally { slider.RemoveHandler(Thumb.DragStartedEvent, handler); }
        return started;
    }
}
