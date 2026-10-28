using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.Library;

/// <summary>The library grid's sort keys (design 02, 2a note 1).</summary>
public enum LibrarySort { Name, DateAdded, ReleaseDate, Rating, Runtime }

/// <summary>The library grid's watched filter.</summary>
public enum LibraryWatched { All, Unplayed, Played }

/// <summary>What the library grid shows: watched state, genres (any of them), sort and order.</summary>
public sealed record LibraryQuery(LibrarySort Sort = LibrarySort.Name, bool Descending = false, LibraryWatched Watched = LibraryWatched.All, IReadOnlyList<string>? Genres = null)
{
    public static LibraryQuery Default { get; } = new();

    public IReadOnlyList<string> SelectedGenres => Genres ?? [];

    /// <summary>Whether anything narrows the list (sort and order do not).</summary>
    public bool IsFiltered => Watched != LibraryWatched.All || SelectedGenres.Count > 0;

    /// <summary>Filters, then sorts with the name as the tie-break. Nulls sort first ascending,
    /// as the server orders them. Pure.</summary>
    public IReadOnlyList<MediaItem> Apply(IReadOnlyList<MediaItem> all)
    {
        IEnumerable<MediaItem> query = all;
        if (Watched == LibraryWatched.Played) query = query.Where(i => i.Played);
        else if (Watched == LibraryWatched.Unplayed) query = query.Where(i => !i.Played);
        if (SelectedGenres.Count > 0) query = query.Where(i => i.Genres.Any(g => SelectedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)));

        var byName = StringComparer.CurrentCultureIgnoreCase;
        var ordered = Sort switch
        {
            LibrarySort.DateAdded => query.OrderBy(i => i.DateCreated).ThenBy(SortKey, byName),
            LibrarySort.ReleaseDate => query.OrderBy(i => i.PremiereDate ?? (i.ProductionYear is { } y ? new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero) : null)).ThenBy(SortKey, byName),
            LibrarySort.Rating => query.OrderBy(i => i.CommunityRating).ThenBy(SortKey, byName),
            LibrarySort.Runtime => query.OrderBy(i => i.RuntimeTicks).ThenBy(SortKey, byName),
            _ => query.OrderBy(SortKey, byName),
        };
        var list = ordered.ThenBy(i => i.Id).ToList();
        if (Descending) list.Reverse();
        return list;
    }

    /// <summary>The A–Z rail's letter for an item: A–Z, or # for anything else.</summary>
    public static string Letter(MediaItem item)
    {
        var key = SortKey(item);
        var first = key.Length > 0 ? char.ToUpperInvariant(RemoveDiacritics(key[0])) : '#';
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }

    /// <summary>The rail from top to bottom: #, then A to Z.</summary>
    public static IReadOnlyList<string> RailLetters { get; } = ["#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    /// <summary>Index of the first item under a letter in a name-sorted list, or the nearest
    /// letter after it when none starts with it; null when nothing follows.</summary>
    public static int? FirstIndexOf(IReadOnlyList<MediaItem> sortedByName, string letter, bool descending = false)
    {
        var order = RailLetters.ToList();
        var wanted = order.IndexOf(letter);
        if (wanted < 0) return null;
        for (var i = 0; i < sortedByName.Count; i++)
        {
            var at = order.IndexOf(Letter(sortedByName[i]));
            if (descending ? at <= wanted : at >= wanted) return i;
        }
        return null;
    }

    public static string SortName(LibrarySort sort) => sort switch
    {
        LibrarySort.DateAdded => L.Text("Library.SortDateAdded"),
        LibrarySort.ReleaseDate => L.Text("Library.SortReleaseDate"),
        LibrarySort.Rating => L.Text("Library.SortRating"),
        LibrarySort.Runtime => L.Text("Library.SortRuntime"),
        _ => L.Text("Library.SortName"),
    };

    private static string SortKey(MediaItem item) => item.SortName ?? item.Name;

    private static char RemoveDiacritics(char c)
    {
        var decomposed = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return decomposed.Length > 0 ? decomposed[0] : c;
    }
}
