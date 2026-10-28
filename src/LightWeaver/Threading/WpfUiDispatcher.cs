using System.Windows.Threading;

namespace LightWeaver.Threading;

/// <summary>The WPF side of <see cref="IUiDispatcher"/>: <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>
/// and <see cref="DispatcherTimer"/>, exactly as the player used them directly before.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool HasThreadAccess => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick, bool urgent = false)
    {
        var timer = new DispatcherTimer(urgent ? DispatcherPriority.Normal : DispatcherPriority.Background, dispatcher) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return new Timer(timer);
    }

    private sealed class Timer(DispatcherTimer timer) : IUiTimer
    {
        public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }

        public void Start() => timer.Start();

        public void Stop() => timer.Stop();
    }
}
