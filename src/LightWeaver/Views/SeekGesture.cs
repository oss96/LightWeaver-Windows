namespace LightWeaver.Views;

/// <summary>
/// The seek bar's press-to-release gesture as one state machine, so that "commit exactly once, or
/// not at all" is decided in one place instead of by flags spread over five event handlers. Pure:
/// the overlay feeds it the slider's events and acts on the <see cref="Outcome"/> each returns, so
/// the ordering rules below are testable without a window (<c>SeekGestureFixture</c>).
/// <para>
/// <b>Why two booleans were not enough.</b> The gesture can end in two different ways that WPF
/// reports through the SAME event. A normal release goes preview-up (<c>OnSeekClick</c>), then
/// <c>Thumb.OnMouseLeftButtonUp</c>, which calls <c>ReleaseMouseCapture()</c> BEFORE it raises
/// <c>DragCompleted</c> — so a <c>LostMouseCapture</c> reaches the slider in the middle of every
/// successful drag. A torn-down gesture (Alt+Tab mid-press, a genuine capture loss) also arrives
/// as <c>LostMouseCapture</c> or <c>Deactivated</c>. Treating every capture loss as a teardown made
/// each good drag log an abandon before its commit; latching one made good drags stop committing
/// (the first B46 fix attempt); not latching left Alt+Tab mid-drag committing TWICE on release —
/// once from the preview-up, which saw no drag any more, and once from the uncancelled
/// <c>DragCompleted</c> the still-captured thumb raised. <see cref="Phase.Released"/> is what tells
/// the two apart: once the preview-up has been seen during a drag, the capture loss that follows is
/// the release's own.
/// </para>
/// <para>
/// <b>Ordering it tolerates.</b> A press on the track reaches <see cref="DragStarted"/> BEFORE
/// <see cref="Press"/> (<c>SliderDrag</c>'s handler is installed by the style, ahead of the
/// overlay's own), while a press on the knob reaches them the other way round; a press during a
/// drag therefore keeps the drag. <see cref="Phase.TornDown"/> absorbs everything up to the next
/// press, which is the only thing that leaves it.
/// </para>
/// </summary>
internal sealed class SeekGesture
{
    internal enum Phase
    {
        /// <summary>No gesture.</summary>
        Idle,
        /// <summary>Button down on the track, no thumb drag — a plain click, committed on release.</summary>
        Pressed,
        /// <summary>The thumb is dragging; <c>DragCompleted</c> commits.</summary>
        Dragging,
        /// <summary>The preview-up of a drag has been seen and its <c>DragCompleted</c> has not yet
        /// arrived. A capture loss here is the release's own, not a teardown.</summary>
        Released,
        /// <summary>The gesture was interrupted. Nothing commits until the next press, however the
        /// button eventually comes up.</summary>
        TornDown,
    }

    internal enum Outcome
    {
        None,
        /// <summary>Seek to the slider's value now.</summary>
        Commit,
        /// <summary>A live gesture was just torn down. Returned once per gesture, so the abandon
        /// line is written once however many teardown signals follow.</summary>
        Abandon,
    }

    public Phase Current { get; private set; }

    /// <summary>True while mpv's position feedback must not overwrite the slider — the
    /// <see cref="ViewModels.PlayerViewModel.IsSeeking"/> the overlay mirrors after every event.</summary>
    public bool IsActive => Current is Phase.Pressed or Phase.Dragging or Phase.Released;

    /// <summary>True when the slider has to take the mouse capture itself, so a release off the
    /// bar still reaches it: a plain press that no thumb drag owns (<c>SliderDrag</c> unwound one
    /// whose capture failed). Only meaningful once the press has finished routing. A press on the
    /// knob reads <see cref="Phase.Pressed"/> in the slider's preview-down, because the thumb
    /// starts its drag later, in its own bubbling mouse-down; by then it is
    /// <see cref="Phase.Dragging"/>.</summary>
    public bool WantsSliderCapture => Current == Phase.Pressed;

    /// <summary>Button down on the slider.</summary>
    public void Press()
    {
        if (Current != Phase.Dragging)
            Current = Phase.Pressed;
    }

    /// <summary><c>Thumb.DragStarted</c>.</summary>
    public void DragStarted() => Current = Phase.Dragging;

    /// <summary>Button up over the slider (the tunnelling preview, so before the thumb's own
    /// mouse-up). Commits a plain click; a drag waits for its <c>DragCompleted</c>. A release in
    /// <see cref="Phase.Idle"/> belongs to a press that began somewhere else and commits nothing.</summary>
    public Outcome Release()
    {
        switch (Current)
        {
            case Phase.Pressed:
                Current = Phase.Idle;
                return Outcome.Commit;
            case Phase.Dragging:
                Current = Phase.Released;
                return Outcome.None;
            default:
                return Outcome.None;
        }
    }

    /// <summary><c>Thumb.DragCompleted</c>. Uncancelled, it commits only a drag that is still live —
    /// never one already torn down, which is the Alt+Tab double commit.</summary>
    public Outcome DragCompleted(bool canceled)
    {
        if (canceled)
            return TearDown();
        if (Current is not (Phase.Dragging or Phase.Released))
            return Outcome.None;
        Current = Phase.Idle;
        return Outcome.Commit;
    }

    /// <summary><c>LostMouseCapture</c> on the slider. Expected, and nothing, after the preview-up
    /// of a drag; anywhere else in a live gesture it is a teardown.</summary>
    public Outcome LostCapture()
    {
        if (Current == Phase.Released)
            return Outcome.None;
        return TearDown();
    }

    /// <summary>The window lost activation, or anything else that ends the gesture without a
    /// release.</summary>
    public Outcome TearDown()
    {
        if (!IsActive)
            return Outcome.None;
        Current = Phase.TornDown;
        return Outcome.Abandon;
    }
}
