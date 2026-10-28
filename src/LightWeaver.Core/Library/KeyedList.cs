using System.Collections.ObjectModel;

namespace LightWeaver.Library;

/// <summary>An observable list that follows newer answers for the same list by key, so a view
/// keeps the elements it already shows: see <see cref="Sync"/>. It raises only Add, Remove and
/// Reset, the changes an ItemsRepeater tracks reliably.</summary>
public sealed class KeyedList<T, TKey>(Func<T, TKey> key) : ObservableCollection<T> where TKey : notnull
{
    /// <summary>Makes this list equal to <paramref name="source"/> with the fewest changes: items
    /// that left are removed, new ones inserted, and only items whose order changed are taken out
    /// and inserted again at their new place. A kept item takes its newer copy without a
    /// notification, so the view re-reads the copies of the elements it shows by key afterwards.
    /// With nothing to keep, one reset replaces the list.</summary>
    public void Sync(IReadOnlyList<T> source)
    {
        var rank = new Dictionary<TKey, int>();
        for (var i = 0; i < source.Count; i++) rank.TryAdd(key(source[i]), i);

        var first = new Dictionary<TKey, int>();
        for (var i = 0; i < Count; i++) first.TryAdd(key(this[i]), i);
        var keeps = first.Keys.Any(rank.ContainsKey);
        if (!keeps && Count > 0) Clear();
        for (var i = Count - 1; i >= 0; i--)
        {
            var k = key(this[i]);
            if (!rank.ContainsKey(k) || first[k] != i) RemoveAt(i);
        }

        PlaceOutOfOrder(rank, source);

        var comparer = EqualityComparer<TKey>.Default;
        for (var i = 0; i < source.Count; i++)
        {
            if (i < Count && comparer.Equals(key(this[i]), key(source[i])))
            {
                if (!ReferenceEquals(this[i], source[i])) Items[i] = source[i];
            }
            else Insert(i, source[i]);
        }
    }

    /// <summary>Keeps the longest run of items already in the new order where they are and takes
    /// each other item out once, inserting its newer copy just after its nearest predecessor in
    /// the new order.</summary>
    private void PlaceOutOfOrder(Dictionary<TKey, int> rank, IReadOnlyList<T> source)
    {
        var ranks = this.Select(item => rank[key(item)]).ToArray();
        var placed = LongestIncreasingRun(ranks);
        var pending = Enumerable.Range(0, ranks.Length).Where(i => !placed[i]).Select(i => ranks[i]).Order().ToList();
        if (pending.Count == 0) return;
        var isPlaced = new HashSet<int>(Enumerable.Range(0, ranks.Length).Where(i => placed[i]).Select(i => ranks[i]));
        foreach (var r in pending)
        {
            var from = -1;
            var predecessor = -1;
            for (var i = 0; i < Count; i++)
            {
                var current = rank[key(this[i])];
                if (current == r) from = i;
                else if (current < r && isPlaced.Contains(current)) predecessor = i;
            }
            var to = predecessor < 0 ? 0 : from < predecessor ? predecessor : predecessor + 1;
            if (from != to)
            {
                RemoveAt(from);
                Insert(to, source[r]);
            }
            isPlaced.Add(r);
        }
    }

    /// <summary>Marks one longest strictly increasing subsequence of <paramref name="values"/>.</summary>
    private static bool[] LongestIncreasingRun(IReadOnlyList<int> values)
    {
        var tails = new List<int>();
        var previous = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (values[tails[mid]] < values[i]) lo = mid + 1;
                else hi = mid;
            }
            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i);
            else tails[lo] = i;
        }
        var run = new bool[values.Count];
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i]) run[i] = true;
        return run;
    }
}
