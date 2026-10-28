namespace LightWeaver.Threading;

/// <summary>Runs work on the UI thread of whichever UI framework hosts the app (WPF or WinUI 3), so
/// the player and the playback code stay framework-neutral.</summary>
public interface IUiDispatcher
{
    /// <summary>Whether the calling thread is the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues work on the UI thread.</summary>
    void Post(Action action);

    /// <summary>A stopped timer that ticks on the UI thread every <paramref name="interval"/>.
    /// <paramref name="urgent"/> ticks ahead of input and rendering where the framework allows it
    /// (a SyncPlay unpause due at a server time).</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick, bool urgent = false);
}

/// <summary>A repeating timer that ticks on the UI thread; it starts stopped.</summary>
public interface IUiTimer
{
    TimeSpan Interval { get; set; }

    void Start();

    void Stop();
}

/// <summary>The app's UI dispatcher, set once at startup, for code that creates UI timers without
/// one handed in (the playback reporter, the player view-model).</summary>
public static class UiDispatcher
{
    private static IUiDispatcher? _current;

    public static IUiDispatcher Current
    {
        get => _current ?? throw new InvalidOperationException("UiDispatcher.Current must be set when the app starts.");
        set => _current = value;
    }
}
