using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Playlists;

/// <summary>How All playlists is ordered (design 06).</summary>
public enum PlaylistSort { DateCreated, Name }

/// <summary>The playlist screens' rules (design 06): summaries, type labels, reordering and removal
/// with undo.</summary>
public static class PlaylistModel
{
    /// <summary>How long Undo is offered after a removal.</summary>
    public static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(6);

    /// <summary>"18 items · 14 h 32 min"; the count alone without a runtime.</summary>
    public static string Summary(int count, long? totalTicks)
    {
        var items = count == 1 ? L.Text("Playlist.OneItem") : L.Format("Playlist.Items", count);
        return DetailModel.Runtime(totalTicks) is { Length: > 0 } runtime ? $"{items} · {runtime}" : items;
    }

    public static string Summary(IReadOnlyList<MediaItem> items) => Summary(items.Count, items.Sum(i => i.RuntimeTicks ?? 0));

    /// <summary>A playlist card's line, from its child count and cumulative runtime. The server keeps
    /// a runtime its entries no longer add up to once they leave the library, so an empty
    /// playlist shows no runtime, as on its page.</summary>
    public static string CardLine(MediaItem playlist)
        => HasEntries(playlist) ? Summary(playlist.ChildCount!.Value, playlist.RuntimeTicks) : Summary(0, null);

    /// <summary>Whether the playlist has entries, by its live child count. The server keeps an
    /// emptied playlist's old collage and a played state no entry backs, so an empty one shows the
    /// playlist glyph and no watched check, whatever it reports.</summary>
    public static bool HasEntries(MediaItem playlist) => playlist.ChildCount is > 0;

    /// <summary>The image tag of the playlist's artwork while it has <paramref name="entries"/>;
    /// none when it is empty.</summary>
    public static string? ArtworkTag(MediaItem playlist, int entries)
        => entries > 0 && playlist.PrimaryImageTag is { Length: > 0 } tag ? tag : null;

    public static IReadOnlyList<MediaItem> Sorted(IEnumerable<MediaItem> playlists, PlaylistSort sort) => sort switch
    {
        PlaylistSort.Name => [.. playlists.OrderBy(p => p.SortName ?? p.Name, StringComparer.CurrentCultureIgnoreCase)],
        _ => [.. playlists.OrderByDescending(p => p.DateCreated ?? DateTimeOffset.MinValue).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)],
    };

    /// <summary>The Type column.</summary>
    public static string TypeLabel(MediaItem item) => item.Type switch
    {
        BaseItemDto_Type.Movie => L.Text("Playlist.TypeMovie"),
        BaseItemDto_Type.Episode => L.Text("Playlist.TypeEpisode"),
        BaseItemDto_Type.Series => L.Text("Playlist.TypeShow"),
        BaseItemDto_Type.Audio => L.Text("Playlist.TypeSong"),
        BaseItemDto_Type.MusicVideo => L.Text("Playlist.TypeMusicVideo"),
        _ => L.Text("Playlist.TypeVideo"),
    };

    /// <summary>The row's title: the show for an episode, else the name.</summary>
    public static string RowTitle(MediaItem item) => item.Type == BaseItemDto_Type.Episode && item.SeriesName is { Length: > 0 } show ? show : item.Name;

    /// <summary>The row's second line: "S1 · E1 — Pilot" for an episode, else "2019 · Drama".</summary>
    public static string RowSubtitle(MediaItem item)
    {
        if (item.Type == BaseItemDto_Type.Episode)
        {
            var tag = DetailModel.EpisodeTag(item);
            return tag.Length > 0 ? $"{tag} — {item.Name}" : item.Name;
        }
        return string.Join(" · ", new[] { item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", item.Genres.FirstOrDefault() ?? "" }.Where(p => p.Length > 0));
    }

    /// <summary>"1:58:20" or "52:10".</summary>
    public static string Duration(long? ticks)
    {
        if (ticks is not > 0) return "";
        var t = TimeSpan.FromTicks(ticks.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>A playlist name is usable when it has something besides spaces.</summary>
    public static string? CleanName(string? name) => name?.Trim() is { Length: > 0 } clean ? clean : null;

    /// <summary>Moves one entry; returns false when the move goes nowhere.</summary>
    public static bool Move<T>(IList<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count) return false;
        to = Math.Clamp(to, 0, list.Count - 1);
        if (from == to) return false;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return true;
    }

    /// <summary>Removes the given positions and returns what was removed with where it was, in
    /// ascending order, so <see cref="Restore"/> puts it all back.</summary>
    public static IReadOnlyList<(int Index, T Item)> Remove<T>(IList<T> list, IEnumerable<int> indices)
    {
        var removed = indices.Where(i => i >= 0 && i < list.Count).Distinct().Order().Select(i => (i, list[i])).ToList();
        for (var i = removed.Count - 1; i >= 0; i--) list.RemoveAt(removed[i].i);
        return removed;
    }

    public static void Restore<T>(IList<T> list, IReadOnlyList<(int Index, T Item)> removed)
    {
        foreach (var (index, item) in removed) list.Insert(Math.Min(index, list.Count), item);
    }

    /// <summary>A shuffled copy, for Shuffle.</summary>
    public static IReadOnlyList<T> Shuffled<T>(IEnumerable<T> items, Random random)
    {
        var copy = items.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
