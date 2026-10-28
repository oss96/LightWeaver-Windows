namespace LightWeaver.Imaging;

/// <summary>
/// One async gate per key: holders of the same key run one at a time, different keys never wait
/// on each other. A gate exists only while somebody holds or waits for it, so a long session does
/// not keep one per key it ever touched.
/// <para>Not re-entrant, and a holder must not take a second key: every caller takes one key,
/// does its file work and releases it, so no two holders can wait on each other.</para>
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for <paramref name="key"/>'s gate; disposing the result releases it.</summary>
    public async Task<IDisposable> AcquireAsync(string key)
    {
        Gate gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                gate = new Gate();
                _gates[key] = gate;
            }
            // Counted before the wait, so the gate is not dropped while somebody waits on it.
            gate.Users++;
        }
        await gate.Semaphore.WaitAsync().ConfigureAwait(false);
        return new Releaser(this, key, gate);
    }

    private void Release(string key, Gate gate)
    {
        lock (_gates)
        {
            // The last user drops the gate. A caller arriving after that makes a new one and
            // goes straight in, which is right: the holder's work is over by now.
            if (--gate.Users == 0)
                _gates.Remove(key);
        }
        gate.Semaphore.Release();
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users;
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(key, gate);
        }
    }
}
