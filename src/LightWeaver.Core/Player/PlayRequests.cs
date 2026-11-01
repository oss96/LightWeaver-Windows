namespace LightWeaver.Player;

/// <summary>The plays that start after a lookup (a card's Play on a show, a season or a playlist,
/// and Shuffle) share one order: only the newest request may still start when its lookup returns.</summary>
public sealed class PlayRequests
{
    private int _latest;

    /// <summary>Takes the next request; every earlier one stops being the newest.</summary>
    public int Claim() => Interlocked.Increment(ref _latest);

    public bool IsLatest(int request) => Volatile.Read(ref _latest) == request;
}
