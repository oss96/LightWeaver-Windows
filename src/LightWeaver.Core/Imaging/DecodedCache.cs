namespace LightWeaver.Imaging;

/// <summary>The most recently used decoded images, one per key, each with the width it was
/// decoded at. A lookup is served by an entry at least as wide as asked; storing under a key
/// already held keeps the wider image and never leaves a second entry behind. Thread-safe.</summary>
public sealed class DecodedCache<T>(int capacity) where T : class
{
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = [];
    private readonly LinkedList<Entry> _recent = new();
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>The image under <paramref name="key"/> decoded at least <paramref name="width"/>
    /// wide, marked as just used; null otherwise.</summary>
    public T? Get(string key, int width)
    {
        lock (_lock) return GetLocked(key, width);
    }

    private T? GetLocked(string key, int width)
    {
        if (!_index.TryGetValue(key, out var node) || node.Value.Width < width) return null;
        _recent.Remove(node);
        _recent.AddFirst(node);
        return node.Value.Value;
    }

    /// <summary>Stores an image unless the key holds one at least as wide, or holds any when
    /// <paramref name="onlyIfEmpty"/> (a load nobody waits for any more), then trims the oldest.</summary>
    public void Remember(string key, T value, int width, bool onlyIfEmpty = false)
    {
        lock (_lock) RememberLocked(key, value, width, onlyIfEmpty);
    }

    private void RememberLocked(string key, T value, int width, bool onlyIfEmpty)
    {
        if (_index.TryGetValue(key, out var kept))
        {
            if (onlyIfEmpty || kept.Value.Width >= width) return;
            _recent.Remove(kept);
        }
        _index[key] = _recent.AddFirst(new Entry(key, value, width));
        while (_recent.Count > capacity)
        {
            var last = _recent.Last!;
            _index.Remove(last.Value.Key);
            _recent.RemoveLast();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
            _recent.Clear();
        }
    }

    private sealed record Entry(string Key, T Value, int Width);
}
