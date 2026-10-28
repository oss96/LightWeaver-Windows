using LightWeaver.Views;
using Outcome = LightWeaver.Views.SeekGesture.Outcome;

/// <summary>
/// Proves the seek bar commits a gesture exactly once, or not at all when it was interrupted. Each
/// leg replays the event ORDER WPF delivers for one gesture into the production
/// <see cref="SeekGesture"/> and counts what came out; the orders themselves are the documented
/// ones from <c>OverlayWindow</c> and <c>SliderDrag</c>, not measured here.
/// <para>Offscreen by construction: no Application, no window, no mpv. What it cannot prove is that
/// WPF still delivers those orders — that a real Alt+Tab keeps the thumb's capture, or that the
/// preview-up still precedes the thumb's own mouse-up. That half needs the UIA suites on the
/// Hyper-V guest.</para>
/// </summary>
internal static class SeekGestureFixture
{
    /// <param name="test">The harness's own test recorder.</param>
    public static void Run(Action<string, Action> test)
    {
        // The everyday release, and the reason a capture loss cannot simply mean "abandon":
        // Thumb.OnMouseLeftButtonUp releases capture BEFORE it raises DragCompleted, so the
        // slider sees a LostMouseCapture in the middle of every good drag.
        test("a track press dragged and released commits once and logs no abandon", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 0,
                // A track press: SliderDrag's handler starts the drag before the overlay's press.
                DragStarted, Press, Release, LostCapture, Completed);
        });

        test("a press on the knob dragged and released commits once", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 0,
                // On the knob the order flips: the slider's preview-down first, then the thumb.
                Press, DragStarted, Release, LostCapture, Completed);
        });

        // The overlay asks WantsSliderCapture once the press has finished routing. On the knob that
        // is after the thumb's own mouse-down has started the drag, so the slider takes nothing.
        test("a press on the knob leaves the capture to the thumb", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 0,
                Press, DragStarted, NoSliderCapture, Release, LostCapture, Completed);
        });

        // Why the question waits for the routing to finish. Asked in the preview-down, a knob press
        // still reads Pressed; the slider captured, the thumb's own capture took it away, and that
        // LostMouseCapture arrived before DragStarted, so every knob drag logged an abandon.
        test("a slider capture the thumb steals before its drag starts is an abandon", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 1,
                Press, SliderCapture, LostCapture, DragStarted, Release, LostCapture, Completed);
        });

        test("SliderDrag's unwind of a failed capture leaves the press to the slider's capture", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 1, DragStarted, Canceled, Press, SliderCapture, Release);
        });

        test("a plain track click with no drag commits once", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 0, Press, Release);
        });

        // The reported bug. Before the state machine the release committed from the preview-up
        // (no drag active any more) AND from the still-captured thumb's DragCompleted: two seeks.
        test("Alt+Tab mid-drag with capture kept: the release commits nothing", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 1,
                DragStarted, Press, Deactivated, Release, LostCapture, Completed);
        });

        test("Alt+Tab mid-drag with the overlay's CancelDrag: one abandon, nothing commits", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 1,
                // AbandonSeekGesture tears down first, then CancelDrag() raises the cancelled
                // completion and releases capture; the button comes up over another app later.
                DragStarted, Press, Deactivated, Canceled, LostCapture);
        });

        test("capture lost mid-drag: the thumb's own cancel abandons once, nothing commits", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 1,
                DragStarted, Press, Canceled, LostCapture, Deactivated, Release);
        });

        test("Alt+Tab during a plain click: the release commits nothing", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 1, Press, Deactivated, Release);
        });

        test("SliderDrag's unwind of a failed capture still commits the click as a track click", () =>
        {
            // The unwind happens inside the same press, before the overlay's own press handler,
            // so the press must start a fresh gesture rather than inherit the teardown.
            var g = new SeekGesture();
            Expect(g, commits: 1, abandons: 1, DragStarted, Canceled, Press, Release);
        });

        test("a release that began elsewhere commits nothing", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 0, Release);
        });

        test("the press after a teardown starts a gesture that commits again", () =>
        {
            var g = new SeekGesture();
            Expect(g, commits: 0, abandons: 1, DragStarted, Press, Deactivated, Release, LostCapture, Completed);
            Expect(g, commits: 1, abandons: 0, DragStarted, Press, Release, LostCapture, Completed);
        });
    }

    private static Outcome Press(SeekGesture g) { g.Press(); return Outcome.None; }
    private static Outcome DragStarted(SeekGesture g) { g.DragStarted(); return Outcome.None; }
    private static Outcome Release(SeekGesture g) => g.Release();
    private static Outcome LostCapture(SeekGesture g) => g.LostCapture();
    private static Outcome Deactivated(SeekGesture g) => g.TearDown();
    private static Outcome Completed(SeekGesture g) => g.DragCompleted(canceled: false);
    private static Outcome Canceled(SeekGesture g) => g.DragCompleted(canceled: true);

    /// <summary>The overlay's posted capture check, where it must take the capture.</summary>
    private static Outcome SliderCapture(SeekGesture g) => g.WantsSliderCapture
        ? Outcome.None
        : throw new InvalidOperationException($"Expected the slider to take the capture in {g.Current}.");

    /// <summary>The overlay's posted capture check, where the thumb already owns the press.</summary>
    private static Outcome NoSliderCapture(SeekGesture g) => !g.WantsSliderCapture
        ? Outcome.None
        : throw new InvalidOperationException($"Expected no slider capture in {g.Current}.");

    /// <summary>Replays one gesture and checks the counts, and that it leaves IsSeeking released —
    /// every gesture here has ended, so a gesture still active afterwards is B8 again.</summary>
    private static void Expect(SeekGesture g, int commits, int abandons, params Func<SeekGesture, Outcome>[] steps)
    {
        var outcomes = steps.Select(step => step(g)).ToList();
        var gotCommits = outcomes.Count(o => o == Outcome.Commit);
        var gotAbandons = outcomes.Count(o => o == Outcome.Abandon);
        if (gotCommits != commits || gotAbandons != abandons)
            throw new InvalidOperationException(
                $"Expected {commits} commit(s) and {abandons} abandon(s), got {gotCommits} and {gotAbandons} "
                + $"({string.Join(", ", outcomes)}).");
        if (g.IsActive)
            throw new InvalidOperationException(
                $"The gesture is still active ({g.Current}) after it ended, which would hold IsSeeking.");
    }
}
