namespace LightWeaver.Imaging;

/// <summary>Turns for image decodes, taken in request order, whose number can change while waiting:
/// <see cref="Limit"/> is read whenever a turn may be given, so a lower limit while an animation runs
/// takes effect as turns are returned and a higher one once <see cref="Pump"/> is called. A waiter
/// whose token is cancelled leaves the line and costs nothing. Thread-safe.</summary>
public sealed class DecodeSlots(Func<int> limit)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = new();
    private int _active;

    /// <summary>Turns under way.</summary>
    public int Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>Requests waiting for a turn.</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>Decodes at once: half the cores, two to four; one while an animation runs or on a
    /// machine with two cores or fewer.</summary>
    public static int Limit(int cores, bool animating) => animating || cores <= 2 ? 1 : Math.Clamp(cores / 2, 2, 4);

    /// <summary>Completes when a turn is free; cancelled with <paramref name="cancel"/> while waiting.</summary>
    public Task WaitAsync(CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_waiting.Count == 0 && _active < Math.Max(1, limit()))
            {
                _active++;
                return Task.CompletedTask;
            }
            node = _waiting.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        if (cancel.CanBeCanceled)
            cancel.Register(() =>
            {
                lock (_lock)
                {
                    if (node.List is null) return;
                    _waiting.Remove(node);
                }
                node.Value.TrySetCanceled(cancel);
            });
        return node.Value.Task;
    }

    /// <summary>Returns a turn and gives the free ones to the next in line.</summary>
    public void Release()
    {
        lock (_lock) _active = Math.Max(0, _active - 1);
        Pump();
    }

    /// <summary>Gives free turns to waiters, after the limit may have risen.</summary>
    public void Pump()
    {
        while (true)
        {
            TaskCompletionSource next;
            lock (_lock)
            {
                if (_waiting.First is not { } first || _active >= Math.Max(1, limit())) return;
                _waiting.RemoveFirst();
                _active++;
                next = first.Value;
            }
            next.TrySetResult();
        }
    }
}
