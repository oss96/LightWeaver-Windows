namespace LightWeaver.Imaging;

/// <summary>Runs at most one load per key and lets each caller give up on its own: callers that
/// ask for a key already loading share that load, a caller whose token is cancelled gets null at
/// once, and the load itself is cancelled only when every caller has given up (a card scrolled
/// away or recycled, a page left). A caller without a cancellable token keeps the load wanted.
/// Thread-safe; a load is settled on whatever thread it finishes, and each caller resumes on its own
/// context.</summary>
public sealed class SharedLoads<T> where T : class
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Load> _loads = [];

    /// <summary>Loads under way that someone still wants.</summary>
    public int Count
    {
        get { lock (_lock) return _loads.Count; }
    }

    /// <summary>The result of the load for <paramref name="key"/>, starting it with
    /// <paramref name="start"/> unless one is under way. Null when the load was cancelled or
    /// <paramref name="cancel"/> was cancelled first.</summary>
    public Task<T?> GetAsync(string key, Func<CancellationToken, Task<T?>> start, CancellationToken cancel = default)
    {
        if (cancel.IsCancellationRequested) return Task.FromResult<T?>(null);
        Load? load;
        var started = false;
        lock (_lock)
        {
            if (!_loads.TryGetValue(key, out load))
            {
                load = new Load();
                _loads[key] = load;
                started = true;
            }
            load.Callers++;
        }
        if (started) _ = RunAsync(key, load, start);
        return WaitAsync(key, load, cancel);
    }

    private async Task RunAsync(string key, Load load, Func<CancellationToken, Task<T?>> start)
    {
        T? result = null;
        try
        {
            result = await start(load.Cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Forget(key, load);
            load.Done.TrySetException(ex);
            return;
        }
        Forget(key, load);
        load.Done.TrySetResult(result);
    }

    private async Task<T?> WaitAsync(string key, Load load, CancellationToken cancel)
    {
        if (!cancel.CanBeCanceled) return await load.Done.Task;
        try
        {
            return await load.Done.Task.WaitAsync(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Leave(key, load);
            return null;
        }
    }

    private void Leave(string key, Load load)
    {
        lock (_lock)
        {
            if (--load.Callers > 0 || load.Done.Task.IsCompleted) return;
            if (_loads.TryGetValue(key, out var current) && ReferenceEquals(current, load)) _loads.Remove(key);
        }
        load.Cancel.Cancel();
    }

    private void Forget(string key, Load load)
    {
        lock (_lock)
        {
            if (_loads.TryGetValue(key, out var current) && ReferenceEquals(current, load)) _loads.Remove(key);
        }
    }

    private sealed class Load
    {
        public readonly CancellationTokenSource Cancel = new();
        public readonly TaskCompletionSource<T?> Done = new();
        public int Callers;
    }
}
