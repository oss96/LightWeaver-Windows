using System.Windows.Threading;

namespace LightWeaver.Views;

/// <summary>
/// The one rule for a count-paged grid or a preview section that removals can empty: Advanced
/// Search's paged grid, its mixed type sections and the see-all listing (SectionView).
/// <list type="bullet">
/// <item>An empty grid or section whose remaining total is above zero is pending: it never shows
/// "Nothing here" and never closes, and it always has a refill in flight or scheduled.</item>
/// <item>Only a confirmed remaining total of zero shows "Nothing here" or closes a section. A
/// total is confirmed by the server: its count, less the cards dropped as removed, or a page that
/// listed nothing at its offset.</item>
/// <item>A page whose every card was removed while it ran is asked again at the same offset, at
/// most <see cref="MaxRemovedRetries"/> times. Running out of retries is not an answer: the total
/// stays as counted and the view asks again later (<see cref="RefillBackoff"/>).</item>
/// </list>
/// </summary>
internal static class RemovalRefill
{
    /// <summary>How many times one attempt asks a page again when every card it listed was
    /// removed while it ran but the server still counts more past its offset. Bounded so a server
    /// that keeps listing removed items cannot spin; the backoff asks again later.</summary>
    public const int MaxRemovedRetries = 2;

    public enum State
    {
        /// <summary>Cards on screen.</summary>
        Showing,
        /// <summary>No cards, results left: a neutral loading state and a refill.</summary>
        Pending,
        /// <summary>No cards and a confirmed total of zero: "Nothing here", or the section closes.</summary>
        Empty,
    }

    public static State Classify(int shown, int remainingTotal)
        => shown > 0 ? State.Showing : remainingTotal > 0 ? State.Pending : State.Empty;

    /// <summary>Every card the page listed was removed while it ran, and the server still counts
    /// results past <paramref name="offset"/>. After the last retry this is retry exhaustion, not
    /// the end of the results.</summary>
    public static bool AllRemoved(int kept, int listed, int adjustedTotal, int offset)
        => kept == 0 && listed > 0 && adjustedTotal > offset;

    /// <summary>Whether this attempt asks the same page again.</summary>
    public static bool AskAgain(int kept, int listed, int adjustedTotal, int offset, int attempt)
        => AllRemoved(kept, listed, adjustedTotal, offset) && attempt < MaxRemovedRetries;

    /// <summary>The remaining total after a page landed, <paramref name="shown"/> counting its kept
    /// cards. A page that listed nothing confirms the end at what is shown; otherwise the server's
    /// count less the removed cards stands, so an exhausted retry keeps a positive total.</summary>
    public static int RemainingTotal(int shown, int listed, int adjustedTotal)
        => listed == 0 ? shown : Math.Max(shown, adjustedTotal);
}

/// <summary>
/// The later ask for <see cref="RemovalRefill"/>'s pending state once an attempt ran out of
/// retries: one tick after 2 s, doubling per consecutive exhaustion up to 30 s, so a server that
/// keeps listing removed items is asked at a falling rate rather than in a loop. The action
/// re-checks its view's generation, profile and visibility; the request it starts carries the
/// usual append epoch.
/// </summary>
internal sealed class RefillBackoff
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer = new();
    private Action? _due;
    private TimeSpan _next = First;

    public RefillBackoff()
    {
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var due = _due;
            _due = null;
            due?.Invoke();
        };
    }

    /// <summary>Schedules one more ask. One already scheduled keeps its time and takes the newer
    /// action.</summary>
    public void Arm(Action due)
    {
        _due = due;
        if (_timer.IsEnabled)
            return;
        _timer.Interval = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Longest.Ticks));
        _timer.Start();
    }

    /// <summary>An attempt landed cards or confirmed the end: the next exhaustion waits the
    /// shortest time again. A scheduled ask stays, as it may serve another section.</summary>
    public void Succeeded() => _next = First;

    /// <summary>A new load: drop any scheduled ask and start over.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _due = null;
        _next = First;
    }
}
