using LightWeaver.Jellyfin;

namespace LightWeaver.ViewModels;

/// <summary>
/// The ordered playback queue (Phase 5 M1). One instance lives on
/// <see cref="AppViewModel"/>; starting a non-queue playback clears it.
/// Pure state — playback itself is driven by MainWindow reacting to the
/// PlayerViewModel's queue commands.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<MediaItem> _items = [];

    public int CurrentIndex { get; private set; } = -1;

    public bool IsActive => _items.Count > 0;

    public int Count => _items.Count;

    public IReadOnlyList<MediaItem> Items => _items;

    public MediaItem? Current => CurrentIndex >= 0 && CurrentIndex < _items.Count
        ? _items[CurrentIndex]
        : null;

    /// <summary>Raised on any queue mutation (set/clear/advance/remove).</summary>
    public event Action? Changed;

    public void Set(IEnumerable<MediaItem> items, int startIndex = 0)
    {
        _items.Clear();
        _items.AddRange(items);
        CurrentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0)
            return;
        _items.Clear();
        CurrentIndex = -1;
        Changed?.Invoke();
    }

    public MediaItem? PeekNext() => CurrentIndex >= 0 && CurrentIndex + 1 < _items.Count
        ? _items[CurrentIndex + 1]
        : null;

    public MediaItem? PeekPrevious() => CurrentIndex > 0 ? _items[CurrentIndex - 1] : null;

    public MediaItem? MoveNext()
    {
        if (PeekNext() is null)
            return null;
        CurrentIndex++;
        Changed?.Invoke();
        return Current;
    }

    public MediaItem? MovePrevious()
    {
        if (PeekPrevious() is null)
            return null;
        CurrentIndex--;
        Changed?.Invoke();
        return Current;
    }

    public MediaItem? JumpTo(int index)
    {
        if (index < 0 || index >= _items.Count || index == CurrentIndex)
            return null;
        CurrentIndex = index;
        Changed?.Invoke();
        return Current;
    }

    /// <summary>If <paramref name="item"/> is the next queue entry, advances onto it
    /// (Up Next / EOF advancement flows through the same item-based event).</summary>
    public bool TryAdvanceTo(MediaItem item)
    {
        if (PeekNext()?.Id != item.Id)
            return false;
        CurrentIndex++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes a non-current entry (the playing row can't be removed).</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count || index == CurrentIndex)
            return;
        _items.RemoveAt(index);
        if (index < CurrentIndex)
            CurrentIndex--;
        Changed?.Invoke();
    }
}
