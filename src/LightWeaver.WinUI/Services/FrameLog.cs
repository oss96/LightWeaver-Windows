using System.Diagnostics;
using LightWeaver.Details;
using LightWeaver.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Services;

/// <summary>Test hook: <c>LIGHTWEAVER_FRAME_LOG=&lt;path&gt;</c> records the UI thread's frame
/// intervals (<see cref="CompositionTarget.Rendering"/>) while an animation runs and appends one
/// <see cref="FrameStats"/> JSON line per animation, with the artwork <see cref="ImageQueue"/>
/// applied on each of those frames. Unset, <see cref="Start"/> returns null and
/// nothing subscribes. Read by verify/v2-tests/test-capture.ps1.</summary>
internal static class FrameLog
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("LIGHTWEAVER_FRAME_LOG") is { Length: > 0 } path ? path : null;
    private static readonly Lock WriteLock = new();

    /// <summary>A recording nobody ends (its page never loaded) stops by itself after this.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

    /// <summary>Starts timing <paramref name="name"/> from now, or from <paramref name="started"/>
    /// (a <see cref="Stopwatch"/> timestamp); null when the log is off. With <paramref name="layout"/>
    /// it also counts how often that geometry changes from frame to frame (<see cref="DetailSettle"/>).</summary>
    public static Recording? Start(string name, long? started = null, Func<DetailGeometry>? layout = null)
        => LogPath is null ? null : new Recording(name, started ?? Stopwatch.GetTimestamp(), layout);

    private static void Append(FrameStats stats)
    {
        try
        {
            lock (WriteLock) File.AppendAllText(LogPath!, stats.ToJson() + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Detail("frames", $"event=frame_log outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>One animation being timed; <see cref="Dispose"/> ends it and writes its line once.</summary>
    public sealed class Recording : IDisposable
    {
        private readonly string _name;
        private readonly long _started;
        private readonly List<double> _intervals = new(32);
        private readonly List<int> _images = new(32);
        private readonly Func<DetailGeometry>? _layout;
        private readonly DetailSettle _settle = new();
        private long _last;
        private long _applied;
        private long _stopAt;
        private bool _done;

        internal Recording(string name, long started, Func<DetailGeometry>? layout)
        {
            _name = name;
            _layout = layout;
            _started = _last = started;
            _stopAt = started + Ticks(Longest);
            _applied = ImageQueue.Applied;
            CompositionTarget.Rendering += OnFrame;
        }

        /// <summary>Keeps timing for <paramref name="settle"/> more, then ends.</summary>
        public void StopAfter(TimeSpan settle) => _stopAt = Math.Min(_stopAt, Stopwatch.GetTimestamp() + Ticks(settle));

        private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

        private void OnFrame(object? sender, object e)
        {
            var now = Stopwatch.GetTimestamp();
            _intervals.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
            _last = now;
            _images.Add(TakeApplied());
            if (_layout is not null) _settle.Observe(_layout(), Stopwatch.GetElapsedTime(_started, now).TotalMilliseconds);
            if (now >= _stopAt) Dispose();
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            CompositionTarget.Rendering -= OnFrame;
            if (TakeApplied() is > 0 and var late) _images.Add(late);
            Append(FrameStats.From(_name, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, _intervals, _images,
                _layout is null ? null : (_settle.Changes, _settle.SettledMs)));
        }

        /// <summary>Artwork applications since the last call.</summary>
        private int TakeApplied()
        {
            var applied = ImageQueue.Applied;
            var count = (int)(applied - _applied);
            _applied = applied;
            return count;
        }
    }
}
