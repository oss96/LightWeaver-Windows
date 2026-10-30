using LightWeaver.Threading;

namespace LightWeaver.Player;

/// <summary>Tracks cancellable subtitle commands on the UI dispatcher until their native replies arrive.</summary>
public sealed class SubtitleLoadRequests(IUiDispatcher dispatcher, Func<ulong, string[], int> submit, Action<ulong> abort)
{
    private readonly Dictionary<ulong, (TaskCompletionSource<bool> Completion, IUiTimer Timeout)> _pending = [];
    private ulong _nextId;

    public Task<bool> Start(params string[] arguments)
    {
        var id = ++_nextId;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = dispatcher.CreateTimer(TimeSpan.FromSeconds(30), () =>
        {
            abort(id);
            Complete(id, false);
        });
        _pending.Add(id, (completion, timer));
        try
        {
            var result = submit(id, arguments);
            if (result < 0) Complete(id, false);
            else timer.Start();
        }
        catch
        {
            Complete(id, false);
            throw;
        }
        return completion.Task;
    }

    public void Complete(ulong id, bool success)
    {
        if (!_pending.Remove(id, out var pending)) return;
        pending.Timeout.Stop();
        pending.Completion.TrySetResult(success);
    }

    public void CancelAll()
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            abort(id);
            Complete(id, false);
        }
    }
}
