using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Playlists;

L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

const long Minute = TimeSpan.TicksPerMinute;
MediaItem Movie(string name, int year, long minutes, string genre = "Drama") => new()
{
    Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year, RuntimeTicks = minutes * Minute, Genres = [genre],
};

// --- Summaries and row lines (design 06) ---------------------------------------------------------
Check(PlaylistModel.Summary(18, (14 * 60 + 32) * Minute) == "18 items · 14 h 32 min", "summary: " + PlaylistModel.Summary(18, (14 * 60 + 32) * Minute));
Check(PlaylistModel.Summary(1, null) == "1 item", "one item, no runtime");
var harbor = Movie("Harbor Lights", 2019, 118);
Check(PlaylistModel.Summary([harbor, Movie("Glass Season", 2021, 125)]) == "2 items · 4 h 3 min", "summary adds the runtimes: " + PlaylistModel.Summary([harbor, Movie("Glass Season", 2021, 125)]));
Check(PlaylistModel.CardLine(new MediaItem { Id = Guid.NewGuid(), Name = "Road Trip", Type = BaseItemDto_Type.Playlist, ChildCount = 9, RuntimeTicks = 16 * 60 * Minute }) == "9 items · 16 h 0 min", "card line from child count and runtime");
Check(PlaylistModel.CardLine(new MediaItem { Id = Guid.NewGuid(), Name = "LW M18 Adv", Type = BaseItemDto_Type.Playlist, ChildCount = 0, RuntimeTicks = 52 * Minute }) == "0 items",
    "an emptied playlist drops the runtime the server kept from its old entries, as its page does");
Check(PlaylistModel.CardLine(new MediaItem { Id = Guid.NewGuid(), Name = "New", Type = BaseItemDto_Type.Playlist }) == "0 items", "no child count: an empty playlist");
var emptied = new MediaItem { Id = Guid.NewGuid(), Name = "LW Queue Test", Type = BaseItemDto_Type.Playlist, ChildCount = 0, PrimaryImageTag = "stale-collage", Played = true };
Check(!PlaylistModel.HasEntries(emptied) && PlaylistModel.ArtworkTag(emptied, emptied.ChildCount ?? 0) is null,
    "an emptied playlist has no artwork, whatever collage tag the server kept");
Check(!PlaylistModel.HasEntries(new MediaItem { Id = Guid.NewGuid(), Name = "New", Type = BaseItemDto_Type.Playlist, PrimaryImageTag = "tag" }), "no child count: no entries");
var filled = new MediaItem { Id = Guid.NewGuid(), Name = "Road Trip", Type = BaseItemDto_Type.Playlist, ChildCount = 3, PrimaryImageTag = "collage" };
Check(PlaylistModel.HasEntries(filled) && PlaylistModel.ArtworkTag(filled, 3) == "collage", "a playlist with entries keeps its collage");
Check(PlaylistModel.ArtworkTag(filled, 0) is null, "the page drops the collage once its last row is gone");
Check(PlaylistModel.ArtworkTag(new MediaItem { Id = Guid.NewGuid(), Name = "Bare", Type = BaseItemDto_Type.Playlist, ChildCount = 2 }, 2) is null, "no tag, no artwork");
Check(PlaylistModel.RowSubtitle(harbor) == "2019 · Drama", "a movie row: year and genre");
var pilot = new MediaItem { Id = Guid.NewGuid(), Name = "Pilot", Type = BaseItemDto_Type.Episode, SeriesName = "Lantern House", ParentIndexNumber = 1, IndexNumber = 1, RuntimeTicks = 52 * Minute + 10 * TimeSpan.TicksPerSecond };
Check(PlaylistModel.RowTitle(pilot) == "Lantern House" && PlaylistModel.RowSubtitle(pilot) == "S1 · E1 — Pilot", "an episode row: show, then S1 · E1 — Pilot");
Check(PlaylistModel.TypeLabel(pilot) == "Episode" && PlaylistModel.TypeLabel(harbor) == "Movie", "type column");
Check(PlaylistModel.Duration(118 * Minute + 20 * TimeSpan.TicksPerSecond) == "1:58:20" && PlaylistModel.Duration(pilot.RuntimeTicks) == "52:10", "durations as clocks");
Check(PlaylistModel.Duration(null) == "", "no runtime, no duration");
Check(PlaylistModel.CleanName("  Road Trip ") == "Road Trip" && PlaylistModel.CleanName("   ") is null, "names are trimmed; blank names are refused");

var older = new MediaItem { Id = Guid.NewGuid(), Name = "Beta", Type = BaseItemDto_Type.Playlist, DateCreated = DateTimeOffset.Now.AddDays(-3) };
var newer = new MediaItem { Id = Guid.NewGuid(), Name = "Alpha", Type = BaseItemDto_Type.Playlist, DateCreated = DateTimeOffset.Now };
Check(PlaylistModel.Sorted([older, newer], PlaylistSort.DateCreated)[0] == newer, "Date created puts the newest first");
Check(PlaylistModel.Sorted([older, newer], PlaylistSort.Name)[0] == newer, "Name sorts A to Z");

// --- Reordering, removal and undo ----------------------------------------------------------------
var list = new List<string> { "a", "b", "c", "d", "e" };
Check(PlaylistModel.Move(list, 4, 1) && string.Concat(list) == "aebcd", "a row moves to its new place");
Check(!PlaylistModel.Move(list, 0, 0) && !PlaylistModel.Move(list, 9, 1), "a move that goes nowhere does nothing");
Check(PlaylistModel.Move(list, 0, 99) && string.Concat(list) == "ebcda", "a move past the end lands last");
var rows = new List<string> { "a", "b", "c", "d", "e" };
var removed = PlaylistModel.Remove(rows, [3, 1, 1, 9]);
Check(string.Concat(rows) == "ace" && removed.Count == 2 && removed[0] == (1, "b") && removed[1] == (3, "d"), "removing rows closes the gap and remembers where they were");
PlaylistModel.Restore(rows, removed);
Check(string.Concat(rows) == "abcde", "Undo puts every row back in its place");
var shuffled = PlaylistModel.Shuffled(Enumerable.Range(0, 50), new Random(7));
Check(shuffled.Order().SequenceEqual(Enumerable.Range(0, 50)) && !shuffled.SequenceEqual(Enumerable.Range(0, 50)), "Shuffle keeps every item in a new order");
Check(PlaylistModel.UndoTime == TimeSpan.FromSeconds(6), "Undo is offered for 6 s");

Console.WriteLine($"{checks} checks passed.");
return 0;
