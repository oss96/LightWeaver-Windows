using System.Collections.Concurrent;

namespace LightWeaver.Jellyfin;

/// <summary>Profile-scoped tombstones prevent late responses from restoring deleted items.</summary>
public sealed class ItemRemovals
{
    private static readonly ConcurrentDictionary<string, ItemRemovals> Profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, State> _items = [];
    private readonly object _gate = new();
    private long _revision;

    private readonly record struct State(long Revision, bool Removed);

    public static ItemRemovals ForProfile(string? profileKey) => Profiles.GetOrAdd(profileKey ?? "", _ => new());

    public bool Contains(Guid id)
    {
        lock (_gate) return _items.TryGetValue(id, out var state) && state.Removed;
    }

    public long Mark()
    {
        lock (_gate) return _revision;
    }

    public bool Contains(Guid id, long readAt)
    {
        lock (_gate) return Excludes(id, readAt);
    }

    public IReadOnlySet<Guid> Remove(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var changed = ids.Where(id => id != Guid.Empty && (!_items.TryGetValue(id, out var state) || !state.Removed)).ToHashSet();
            if (changed.Count == 0) return changed;
            var revision = ++_revision;
            foreach (var id in changed) _items[id] = new(revision, true);
            return changed;
        }
    }

    public IReadOnlySet<Guid> Restore(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var changed = ids.Where(id => _items.TryGetValue(id, out var state) && state.Removed).ToHashSet();
            if (changed.Count == 0) return changed;
            var revision = ++_revision;
            foreach (var id in changed) _items[id] = new(revision, false);
            return changed;
        }
    }

    public List<MediaItem> Filter(IEnumerable<MediaItem> items)
    {
        lock (_gate) return items.Where(item => !Excludes(item.Id, _revision)).ToList();
    }

    public List<MediaItem> Filter(IEnumerable<MediaItem> items, long readAt)
    {
        lock (_gate) return items.Where(item => !Excludes(item.Id, readAt)).ToList();
    }

    private bool Excludes(Guid id, long readAt)
        => _items.TryGetValue(id, out var state) && (state.Removed || state.Revision > readAt);

    public (List<MediaItem> Items, int TotalCount) FilterPage(IEnumerable<MediaItem> items, int total)
    {
        var source = items.ToList();
        var kept = Filter(source);
        return (kept, Math.Max(kept.Count, total - source.Count + kept.Count));
    }

    public (List<MediaItem> Items, int TotalCount) FilterPage(IEnumerable<MediaItem> items, int total, long readAt)
    {
        var source = items.ToList();
        var kept = Filter(source, readAt);
        return (kept, Math.Max(kept.Count, total - source.Count + kept.Count));
    }
}
