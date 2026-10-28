using LightWeaver.Threading;
using Microsoft.UI.Dispatching;

namespace LightWeaver.WinUI.Player;

/// <summary>The WinUI 3 side of <see cref="IUiDispatcher"/>: a <see cref="DispatcherQueue"/> and its timers.</summary>
internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action) => queue.TryEnqueue(() => action());

    public IUiTimer CreateTimer(TimeSpan interval, Action tick, bool urgent = false)
    {
        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        return new Timer(timer);
    }

    private sealed class Timer(DispatcherQueueTimer timer) : IUiTimer
    {
        public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }

        public void Start() => timer.Start();

        public void Stop() => timer.Stop();
    }
}
