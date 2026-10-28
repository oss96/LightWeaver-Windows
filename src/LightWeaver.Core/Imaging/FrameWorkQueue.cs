using System.Diagnostics;

namespace LightWeaver.Imaging;

/// <summary>UI-thread work done a little per frame, oldest first: each <see cref="RunFrame"/> runs
/// items until <see cref="Budget"/> is spent (always at least one). While an animation holds the
/// queue only items that have waited <see cref="MaxDelay"/> run, so nothing lands more than that
/// late. Single-threaded: call every member from the thread that runs the work.</summary>
public sealed class FrameWorkQueue(Func<long>? clock = null)
{
    /// <summary>UI-thread time one frame spends on queued work.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(2);

    /// <summary>The longest an item waits for a held queue.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private readonly Queue<(Action Work, long Queued)> _items = new();
    private int _holds;

    /// <summary>Items waiting.</summary>
    public int Count => _items.Count;

    /// <summary>Items run since the queue was made.</summary>
    public long Applied { get; private set; }

    /// <summary>Whether an animation holds the queue.</summary>
    public bool IsHeld => _holds > 0;

    /// <summary>Raised when an item is queued into an empty queue, so the caller can start ticking.</summary>
    public event EventHandler? Waiting;

    public void Enqueue(Action work)
    {
        _items.Enqueue((work, _clock()));
        if (_items.Count == 1) Waiting?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs <paramref name="work"/> at once when <paramref name="now"/>, held or not and
    /// ahead of the queue (an image already decoded in memory, for a page about to show its first
    /// frame); queues it otherwise.</summary>
    public void Submit(Action work, bool now)
    {
        if (!now)
        {
            Enqueue(work);
            return;
        }
        Applied++;
        work();
    }

    /// <summary>Holds the queue until the returned token is disposed; holds nest.</summary>
    public IDisposable Hold()
    {
        _holds++;
        return new Release(this);
    }

    /// <summary>Runs this frame's share of the queue; the number of items run.</summary>
    public int RunFrame()
    {
        var started = _clock();
        var ran = 0;
        while (_items.TryPeek(out var next))
        {
            var now = _clock();
            if (IsHeld && Elapsed(next.Queued, now) < MaxDelay) break;
            if (ran > 0 && Elapsed(started, now) >= Budget) break;
            _items.Dequeue();
            ran++;
            Applied++;
            next.Work();
        }
        return ran;
    }

    private static TimeSpan Elapsed(long from, long to) => TimeSpan.FromSeconds((double)(to - from) / Stopwatch.Frequency);

    private sealed class Release(FrameWorkQueue queue) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            queue._holds--;
        }
    }
}
