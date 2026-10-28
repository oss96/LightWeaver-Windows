using LightWeaver.Imaging;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Services;

/// <summary>Where decoded artwork lands on screen. A picture read from disk or the network is
/// queued and the UI thread runs about 2 ms of them per frame, so a grid that gets dozens of
/// pictures at once spreads them over a few frames; one already decoded in memory lands at once,
/// before the page's first frame. The player's grow and shrink and the nav morph's opening hold the
/// queue while they run, and an image held 250 ms lands anyway; a page switch does not hold it.
/// UI thread only, except <see cref="IsHeld"/>.</summary>
internal static class ImageQueue
{
    private static readonly FrameWorkQueue Queue = CreateQueue();
    private static volatile bool _held;
    private static bool _ticking;

    /// <summary>Applications run since the app started, for the frame log.</summary>
    public static long Applied => Queue.Applied;

    /// <summary>Whether an animation holds the queue; readable from any thread.</summary>
    public static bool IsHeld => _held;

    /// <summary>The last hold ended.</summary>
    public static event EventHandler? Released;

    /// <summary>Queues <paramref name="apply"/>; it must check itself that its target still wants
    /// the image (a recycled card shows another title by then).</summary>
    public static void Enqueue(Action apply) => Queue.Enqueue(() => Run(apply));

    /// <summary>Applies a picture: at once when <paramref name="fromMemory"/> (its load had finished
    /// before it was awaited, so the page being shown has it in its first frame), queued otherwise.</summary>
    public static void Land(bool fromMemory, Action apply) => Queue.Submit(() => Run(apply), fromMemory);

    private static void Run(Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("imagecache", $"event=apply outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>Holds the queue until the returned token is disposed.</summary>
    public static IDisposable Hold()
    {
        var hold = Queue.Hold();
        _held = true;
        return new Token(hold);
    }

    private static FrameWorkQueue CreateQueue()
    {
        var queue = new FrameWorkQueue();
        queue.Waiting += (_, _) => StartTicking();
        return queue;
    }

    private static void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, object e)
    {
        Queue.RunFrame();
        if (Queue.Count > 0) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private sealed class Token(IDisposable hold) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            hold.Dispose();
            _held = Queue.IsHeld;
            if (!_held) Released?.Invoke(null, EventArgs.Empty);
        }
    }
}
