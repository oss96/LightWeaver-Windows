using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

var appData = Path.Combine(Path.GetTempPath(), "lw-home-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", appData);
if (!string.Equals(LightWeaver.AppPaths.Root, Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run: app data resolves to {LightWeaver.AppPaths.Root}, not the test folder. "
        + "Only Debug builds honour LIGHTWEAVER_TEST_APPDATA_ROOT; run this test without -c Release.");
    return 1;
}
L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

const long Minute = TimeSpan.TicksPerMinute;
MediaItem Movie(string name, int? year = null, long position = 0, long runtime = 0, int addedDaysAgo = 0) => new()
{
    Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year,
    ResumePositionTicks = position, RuntimeTicks = runtime == 0 ? null : runtime, DateCreated = DateTimeOffset.Now.AddDays(-addedDaysAgo),
};
MediaItem Episode(string series, int season, int number, string name = "Chapter", long position = 0, long runtime = 0, Guid? seriesId = null) => new()
{
    Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = series, SeriesId = seriesId ?? Guid.NewGuid(),
    ParentIndexNumber = season, IndexNumber = number, ResumePositionTicks = position, RuntimeTicks = runtime == 0 ? null : runtime,
};

// --- Card lines (design 03 and the Home sketch) ----------------------------------------------
var dispatch = Episode("The Dispatch", 2, 3, "Signals", position: 32 * Minute, runtime: 45 * Minute);
Check(HomeSections.Title(dispatch) == "The Dispatch", "an episode card is titled with its show");
Check(HomeSections.Subtitle(HomeSection.ContinueWatching, dispatch) == "S2 · E3 · 13 min left", "Continue Watching episode line: " + HomeSections.Subtitle(HomeSection.ContinueWatching, dispatch));
var moons = Movie("Paper Moons", 2019, position: 40 * Minute, runtime: 105 * Minute);
Check(HomeSections.Subtitle(HomeSection.ContinueWatching, moons) == "1 h 5 min left", "Continue Watching movie line: " + HomeSections.Subtitle(HomeSection.ContinueWatching, moons));
Check(HomeSections.Subtitle(HomeSection.NextUp, Episode("Night Shift", 1, 5, "Chapter 5")) == "S1 · E5 — Chapter 5", "Next Up line names the episode");
Check(HomeSections.Subtitle(HomeSection.RecentlyAdded, Movie("Kestrel", 2021)) == "2021", "Recently Added line is the year");
var season = new MediaItem { Id = Guid.NewGuid(), Name = "Season 7", Type = BaseItemDto_Type.Season, SeriesName = "The Office", IndexNumber = 7 };
Check(HomeSections.Title(season) == "The Office" && HomeSections.Subtitle(HomeSection.RecentlyAdded, season) == "Season 7", "a season card is titled with its show, the season under it");
var orphan = new MediaItem { Id = Guid.NewGuid(), Name = "Season 7", Type = BaseItemDto_Type.Season, IndexNumber = 7 };
Check(HomeSections.Title(orphan) == "Season 7" && HomeSections.Subtitle(HomeSection.RecentlyAdded, orphan) == "", "a season without its show's name keeps its own");
var showId = Guid.NewGuid();
var newEpisode = new MediaItem
{
    Id = Guid.NewGuid(), Name = "Signals", Type = BaseItemDto_Type.Episode, SeriesName = "The Dispatch", SeriesId = showId,
    PrimaryImageTag = "still", SeriesPrimaryImageTag = "poster",
};
Check(HomeSections.SeriesPoster(HomeSection.RecentlyAdded, newEpisode) == (showId, "poster"), "a Recently Added episode card shows its show's poster");
Check(HomeSections.SeriesPoster(HomeSection.Favorites, newEpisode) is null, "other rows keep the episode's own artwork");
Check(HomeSections.SeriesPoster(HomeSection.RecentlyAdded, dispatch) is null, "an episode whose show has no poster keeps its own artwork");
Check(HomeSections.SeriesPoster(HomeSection.RecentlyAdded, season) is null, "a season keeps its own poster");
Check(HomeSections.TimeLeft(Movie("Unstarted", runtime: 90 * Minute)) == "","nothing left to show for an unstarted title");
Check(HomeSections.TimeLeft(Movie("Almost", position: 89 * Minute + 50 * TimeSpan.TicksPerSecond, runtime: 90 * Minute)) == "1 min left", "the last seconds still read 1 min left");

// --- Next Up badge: episodes after the next one ----------------------------------------------
var seriesA = Guid.NewGuid();
var next = Episode("Common Ground", 3, 7, seriesId: seriesA);
Check(HomeSections.EpisodesAfter(next, new Dictionary<Guid, int> { [seriesA] = 5 }) == 4, "badge counts the unwatched episodes after the next one");
Check(HomeSections.EpisodesAfter(next, new Dictionary<Guid, int> { [seriesA] = 1 }) is null, "no badge when the next episode is the last");
Check(HomeSections.EpisodesAfter(next, new Dictionary<Guid, int>()) is null, "no badge without the series count");

// --- Filters and sorts -------------------------------------------------------------------------
var items = new List<MediaItem> { dispatch, moons, Episode("Annex", 1, 1), Movie("Arrival", 2016, addedDaysAgo: 1), Episode("annex", 2, 9) };
Check(HomeSections.Apply(items, SectionFilter.Movies, SectionSort.ServerOrder).All(i => i.Type == BaseItemDto_Type.Movie), "Movies keeps movies only");
Check(HomeSections.Apply(items, SectionFilter.Episodes, SectionSort.ServerOrder).Count == 3, "Episodes keeps the three episodes");
Check(HomeSections.Apply(items, SectionFilter.All, SectionSort.ServerOrder).SequenceEqual(items), "server order is kept as listed");
var byName = HomeSections.Apply(items, SectionFilter.All, SectionSort.Name).Select(i => HomeSections.Title(i)).ToArray();
Check(byName.SequenceEqual(["Annex", "annex", "Arrival", "Paper Moons", "The Dispatch"]), "Name sorts by card title, case-insensitive: " + string.Join(", ", byName));
Check(HomeSections.Apply(items, SectionFilter.NotStarted, SectionSort.ServerOrder).Single().SeriesName == "Annex", "Not started is a show's first episode, unwatched");
Check(HomeSections.Apply(items, SectionFilter.InProgress, SectionSort.ServerOrder).Count == 4, "In progress is everything else");
Check(HomeSections.Filters(HomeSection.ContinueWatching).SequenceEqual([SectionFilter.All, SectionFilter.Movies, SectionFilter.Episodes]), "Continue Watching filters match the design");
Check(HomeSections.Filters(HomeSection.NextUp).SequenceEqual([SectionFilter.All, SectionFilter.InProgress, SectionFilter.NotStarted]), "Next Up filters match the design");
Check(HomeSections.IsWide(HomeSection.ContinueWatching) && HomeSections.IsWide(HomeSection.NextUp) && !HomeSections.IsWide(HomeSection.RecentlyAdded), "wide cards for Continue Watching and Next Up");
var newest = HomeSections.Apply([Movie("Old", addedDaysAgo: 30), Movie("New", addedDaysAgo: 0)], SectionFilter.All, SectionSort.DateAdded);
Check(newest[0].Name == "New", "Date added puts the newest first");
Check(HomeSections.CacheQuery(HomeSection.ContinueWatching, SectionFilter.Movies, SectionSort.Name) == HomeSections.CacheQuery(HomeSection.ContinueWatching, SectionFilter.All, SectionSort.ServerOrder)
    && HomeSections.CacheQuery(HomeSection.NextUp, SectionFilter.All, SectionSort.ServerOrder) != HomeSections.CacheQuery(HomeSection.Favorites, SectionFilter.All, SectionSort.ServerOrder),
    "a section that loads whole caches one answer for every filter and sort");
Check(HomeSections.CacheQuery(HomeSection.RecentlyAdded, SectionFilter.Movies, SectionSort.Name) != HomeSections.CacheQuery(HomeSection.RecentlyAdded, SectionFilter.All, SectionSort.Name)
    && HomeSections.CacheQuery(HomeSection.RecentlyAdded, SectionFilter.All, SectionSort.Name) != HomeSections.CacheQuery(HomeSection.RecentlyAdded, SectionFilter.All, SectionSort.ServerOrder),
    "Recently Added caches each filter and sort, which the server answers");

// --- Recently Added: the row and its See all list each video library's newest titles ------------
MediaItem Library(string name, string? type) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };
var views = new[] { Library("Movies", "movies"), Library("Shows", "tvshows"), Library("Music", "music"), Library("Collections", "boxsets"), Library("Playlists", "playlists"), Library("Mixed", null) };
Check(HomeSections.LatestLibraries(views).SequenceEqual([views[0].Id, views[1].Id, views[5].Id]), "Recently Added lists from the video libraries only");
var fromMovies = new[] { Movie("Totoro", addedDaysAgo: 5), Movie("Nausicaa", addedDaysAgo: 9) };
var newShow = new MediaItem { Id = Guid.NewGuid(), Name = "Avatar: Seven Havens", Type = BaseItemDto_Type.Series, DateCreated = DateTimeOffset.Now.AddDays(-400), DateLastMediaAdded = DateTimeOffset.Now.AddDays(-1) };
var merged = HomeSections.Newest([.. fromMovies, newShow], 2);
Check(merged.Select(i => i.Name).SequenceEqual(["Avatar: Seven Havens", "Totoro"]), "a show sorts by its newest episode, newest first across the libraries, cut at the count: " + string.Join(", ", merged.Select(i => i.Name)));
Check(HomeSections.Newest([.. fromMovies, newShow], 600).Count == 3, "the row keeps everything when it has room");

// --- Recently Added's See all: paged server lists, merged newest first -----------------------------
Check(RecentList.For(SectionFilter.All, SectionSort.ServerOrder).SequenceEqual([RecentList.Movies, RecentList.Shows]), "by date, movies and shows are read apart");
Check(RecentList.Movies.SortBy == ItemSortBy.DateCreated && RecentList.Shows.SortBy == ItemSortBy.DateLastContentAdded
    && RecentList.Shows.Kinds.SequenceEqual([BaseItemKind.Series]), "shows by their newest episode, so episodes stand under their show");
Check(RecentList.For(SectionFilter.Shows, SectionSort.ServerOrder).SequenceEqual([RecentList.Shows]), "the Shows filter reads only shows");
var nameList = RecentList.For(SectionFilter.All, SectionSort.Name);
Check(nameList.Count == 1 && nameList[0].SortBy == ItemSortBy.SortName && nameList[0].Kinds.SequenceEqual([BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.Series]), "by name, one list of both");
MediaItem Show(string name, int newestEpisodeDaysAgo) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Series, DateCreated = DateTimeOffset.Now.AddDays(-900), DateLastMediaAdded = DateTimeOffset.Now.AddDays(-newestEpisodeDaysAgo) };
var serverMovies = Enumerable.Range(0, 7).Select(i => Movie("Movie " + i, addedDaysAgo: 2 * i)).ToList();
var serverShows = Enumerable.Range(0, 5).Select(i => Show("Show " + i, 2 * i + 1)).ToList();
var orderReads = 0;
Task<List<MediaItem>> ReadOrder(RecentList list, CancellationToken ct)
{
    orderReads++;
    return Task.FromResult((ReferenceEquals(list, RecentList.Movies) ? serverMovies : serverShows).ToList());
}
var failNextHead = true;
Task<(List<MediaItem>, int)> ReadHead(RecentList list, int count, CancellationToken ct)
{
    if (failNextHead)
    {
        failNextHead = false;
        throw new HttpRequestException("the server dropped the connection");
    }
    var all = ReferenceEquals(list, RecentList.Movies) ? serverMovies : serverShows;
    return Task.FromResult((all.Take(count).ToList(), all.Count));
}
var failNextTitles = false;
Task<List<MediaItem>> ReadTitles(IReadOnlyList<Guid> ids, CancellationToken ct)
{
    if (failNextTitles)
    {
        failNextTitles = false;
        throw new HttpRequestException("the server dropped the connection");
    }
    return Task.FromResult(serverMovies.Concat(serverShows).Where(t => ids.Contains(t.Id)).Reverse().ToList());
}
var feed = new RecentlyAddedFeed(RecentList.For(SectionFilter.All, SectionSort.ServerOrder), ReadHead, ReadOrder, ReadTitles);
Check(feed.NextAsync(4, CancellationToken.None).Exception is not null && feed.Shown.Count == 0, "a first page that fails to load fails and shows nothing");
var firstPage = feed.NextAsync(4, CancellationToken.None).Result.Select(p => p.Item).ToList();
Check(firstPage.Select(i => i.Name).SequenceEqual(["Movie 0", "Show 0", "Movie 1", "Show 1"]), "the first page interleaves movies and shows newest first: " + string.Join(", ", firstPage.Select(i => i.Name)));
Check(feed.Total == 12 && feed.HasMore, "the total is both lists' once the order is read");
failNextTitles = true;
Check(feed.NextAsync(4, CancellationToken.None).Exception is not null, "a page whose titles fail to load fails");
var retried = feed.NextAsync(4, CancellationToken.None).Result.Select(p => p.Item).ToList();
Check(retried.Select(i => i.Name).SequenceEqual(["Movie 2", "Show 2", "Movie 3", "Show 3"]), "and the next try gives the same titles, none lost: " + string.Join(", ", retried.Select(i => i.Name)));
var movedAhead = serverShows[4];
serverShows.Remove(movedAhead);
serverShows.Insert(0, movedAhead);
serverMovies.RemoveAt(0);
var rest = feed.NextAsync(100, CancellationToken.None).Result.Select(p => p.Item).ToList();
Check(rest.Count == 4 && !feed.HasMore && orderReads == 2, "the order is read once: a show that moves ahead and a title that leaves meanwhile skip nothing");
var everything = firstPage.Concat(retried).Concat(rest).ToList();
Check(everything.Select(i => i.Id).Distinct().Count() == 12, "every title once");
var shelf = Enumerable.Range(0, 5).Select(i => Movie("Shelf " + i, addedDaysAgo: i)).ToList();
var thinned = new RecentlyAddedFeed(RecentList.For(SectionFilter.Movies, SectionSort.ServerOrder),
    (_, count, _) => Task.FromResult((shelf.Take(count).ToList(), shelf.Count)), (_, _) => Task.FromResult(shelf.ToList()), (ids, _) => Task.FromResult(shelf.Where(t => ids.Contains(t.Id)).ToList()));
_ = thinned.NextAsync(1, CancellationToken.None).Result;
shelf.RemoveRange(1, 2);
var afterRemoved = thinned.NextAsync(2, CancellationToken.None).Result;
Check(afterRemoved.Select(t => t.Item.Name).SequenceEqual(["Shelf 3", "Shelf 4"]) && !thinned.HasMore, "a batch whose titles were all removed is passed over, so paging never stalls on an empty page");
var rack = Enumerable.Range(0, 6).Select(i => Movie("Rack " + i, addedDaysAgo: i + 1)).ToList();
var orderReplies = new List<TaskCompletionSource<List<MediaItem>>>();
var early = new RecentlyAddedFeed(RecentList.For(SectionFilter.Movies, SectionSort.ServerOrder),
    (_, count, _) => Task.FromResult((rack.Take(count).ToList(), rack.Count)),
    (_, _) => { orderReplies.Add(new()); return orderReplies[^1].Task; },
    (ids, _) => Task.FromResult(rack.Where(t => ids.Contains(t.Id)).ToList()));
var head = early.NextAsync(2, CancellationToken.None);
Check(head.IsCompletedSuccessfully && early.Shown.Select(t => t.Name).SequenceEqual(["Rack 0", "Rack 1"]) && early.Total == 6,
    "the first page and the count come before the order is read");
rack.Insert(0, Movie("Rack new", addedDaysAgo: 0));
var crossing = rack[4];
rack.Remove(crossing);
rack.Insert(2, crossing);
var orderFails = early.NextAsync(10, CancellationToken.None);
orderReplies[0].SetException(new HttpRequestException("the server dropped the connection"));
Check(orderFails.Exception is not null && early.Shown.Count == 2, "a page whose order fails to load fails and shows nothing more");
var afterHead = early.NextAsync(10, CancellationToken.None);
Check(!afterHead.IsCompleted && orderReplies.Count == 2, "the next try reads the order again and waits for it");
orderReplies[1].SetResult(rack.ToList());
Check(afterHead.Result.Select(p => p.Index).SequenceEqual([0, 2, 4, 5, 6]) && early.Shown.Select(t => t.Id).SequenceEqual(rack.Select(t => t.Id))
    && early.Total == 7 && !early.HasMore,
    "a title added meanwhile and one that moved above the first page go in their places, the rest after: " + string.Join(", ", early.Shown.Select(t => t.Name)));

// --- Home snapshot: last rows on disk, compared before replacing what is shown -------------------
var snapshot = new HomeSnapshot([dispatch], [next], [moons], [], new Dictionary<Guid, int> { [seriesA] = 5 }) { SavedAtUtc = DateTime.UtcNow.AddHours(-3) };
var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
var restored = System.Text.Json.JsonSerializer.Deserialize<HomeSnapshot>(json);
Check(restored is { Schema: HomeSnapshot.CurrentSchema } && restored.SameAs(snapshot), "a stored snapshot reads back as the same rows");
Check(restored!.Unplayed[seriesA] == 5, "the Next Up badge counts survive the round trip");
Check(!snapshot.SameAs(null), "no previous rows means the rows changed");
var watched = dispatch.WithUserData(true, dispatch.IsFavorite, 0, null);
Check(!snapshot.SameAs(snapshot with { Resume = [watched] }), "a card whose progress changed counts as a change");
Check(!snapshot.SameAs(snapshot with { Unplayed = new Dictionary<Guid, int> { [seriesA] = 4 } }), "a changed badge counts as a change");
Check(snapshot.SameAs(snapshot with { Latest = [moons] }), "a new list with the same items is no change");
Check(HomeSnapshot.Key("u@s") != HomeSnapshot.Key("v@s"), "each profile has its own snapshot");
Check(!HomeSnapshot.Key("u@s").Contains(L.CacheIdentity), "a build with other UI text keeps the snapshot");
var englishKey = HomeSnapshot.Key("u@s");
L.Initialize("de");
Check(HomeSnapshot.Key("u@s") != englishKey, "another UI language has its own snapshot");
L.Initialize("en");

// --- Home built again in the same session paints the last rows in its first frame -------------
Check(HomeSnapshot.Last("u@s") is null, "nothing remembered before a read or a store");
await HomeSnapshot.StoreAsync("u@s", snapshot, null);
Check(ReferenceEquals(HomeSnapshot.Last("u@s"), snapshot), "the stored rows are remembered without a disk read");
Check(HomeSnapshot.Last("v@s") is null && HomeSnapshot.Last("") is null, "another profile gets nothing");
L.Initialize("de");
Check(HomeSnapshot.Last("u@s") is null, "another UI language gets nothing");
L.Initialize("en");
var refreshed = snapshot with { Latest = [moons, Movie("Kestrel", 2021)] };
await HomeSnapshot.StoreAsync("u@s", refreshed, snapshot);
Check(ReferenceEquals(HomeSnapshot.Last("u@s"), refreshed), "a newer answer replaces them");
Check((await HomeSnapshot.ReadAsync("u@s"))?.SameAs(refreshed) == true, "and is on disk for the next start");
var diskSnapshot = await HomeSnapshot.ReadAsync("u@s");
Check(diskSnapshot?.SavedAtUtc == snapshot.SavedAtUtc, "cache timestamp survives restart independently of file age");
Check(await HomeSnapshot.ReadAsync("v@s") is null, "another user cannot read Home rows");
var later = refreshed with { SavedAtUtc = DateTime.UtcNow };
await HomeSnapshot.StoreAsync("u@s", later, refreshed);
Check((await HomeSnapshot.ReadAsync("u@s"))?.SavedAtUtc == later.SavedAtUtc, "unchanged server results advance cache timestamp");
L.Initialize("de");
await HomeSnapshot.StoreAsync("u@s", refreshed, null);
L.Initialize("en");
await HomeSnapshot.RemoveAsync("u@s", new HashSet<Guid> { seriesA, moons.Id });
var afterRemoval = await HomeSnapshot.ReadAsync("u@s");
Check(afterRemoval is not null && afterRemoval.NextUp.Count == 0 && afterRemoval.Latest.All(i => i.Id != moons.Id)
    && afterRemoval.SavedAtUtc == later.SavedAtUtc, "removal filters rows and badges without advancing fetch timestamp");
await HomeSnapshot.StoreAsync("u@s", later, null);
Check((await HomeSnapshot.ReadAsync("u@s"))?.NextUp.Count == 0, "a late Home response cannot restore a removed series");
L.Initialize("de");
Check((await HomeSnapshot.ReadAsync("u@s"))?.NextUp.Count == 0, "removals also clean another language's stored Home");
L.Initialize("en");
Check(await HomeSnapshot.ReadAsync("") is null, "an anonymous session never reads cached Home");
var readded = Guid.NewGuid();
var ledger = ItemRemovals.ForProfile("u@s");
ledger.Remove([readded]);
ledger.Restore([readded]);
await HomeSnapshot.RemoveAsync("u@s", new HashSet<Guid> { readded }, recorded: true);
Check(!ledger.Contains(readded), "a delayed cache cleanup keeps a title re-added after its removal");

// --- Card scale: rows grow with the window from 1440 DIP, up to 1.5x (Tokens.xaml values) -------
CardScale ScaleAt(double width) => CardScale.For(width, 1440, 1.5, 0.5);
Check(ScaleAt(900) == CardScale.None && ScaleAt(1440) == CardScale.None, "nothing changes up to 1440 DIP");
var at1920 = ScaleAt(1920);
Check(at1920.Width(296) == 395 && at1920.Width(150) == 200 && at1920.Width(140) == 187 && at1920.Width(120) == 160,
    $"at 1920 DIP cards are 4/3 as wide: {at1920.Width(296)}, {at1920.Width(150)}, {at1920.Width(140)}, {at1920.Width(120)}");
Check(Math.Abs(at1920.Font(12) - 14) < 1e-9, "at 1920 DIP text grows by half as much as the cards: " + at1920.Font(12));
var at2560 = ScaleAt(2560);
Check(at2560 == ScaleAt(2160) && at2560.Cards == 1.5, "the scale stops at 1.5x from 2160 DIP");
Check(at2560.Width(296) == 444 && at2560.Width(150) == 225 && at2560.Width(140) == 210 && at2560.Width(120) == 180, "at the owner's 2560 DIP window wide cards are 444 and posters 225");
Check(at2560.Font(17) == 21.25 && at2560.Font(13) == 16.25 && at2560.Space(28) == 42, "at 1.5x headings and captions are 1.25x, row gaps 1.5x");
Check(CardScale.For(2000, 1440, 1.5, 1).Text == CardScale.For(2000, 1440, 1.5, 1).Cards, "a text share of 1 scales text with the cards");
Check(!CardScale.None.MovesTo(ScaleAt(1460), 1.5), "a 1.4 % wider window does not lay the rows out again");
Check(CardScale.None.MovesTo(ScaleAt(1475), 1.5), "a 2.4 % wider window does");
Check(ScaleAt(1450).MovesTo(CardScale.None, 1.5), "shrinking back to 1440 DIP restores the design exactly");
Check(ScaleAt(2150).MovesTo(ScaleAt(2200), 1.5) && !at2560.MovesTo(ScaleAt(3840), 1.5), "the cap is reached exactly and held");
Check(!CardScale.NeedsSharperArt(666, 760) && CardScale.NeedsSharperArt(666, 770), "artwork loads again only past 15 % growth");
Check(!CardScale.NeedsSharperArt(0, 2000), "a card without artwork loads nothing on resize");

var paging = new CardRowPaging();
Check(paging.Destination(0, 900, 3000, 296, 14, 1) == 620, "wide cards move by the two complete cards that fit");
Check(paging.Destination(0, 916, 3000, 296, 14, 1) == 930, "exactly fitting cards include the final gap allowance");
Check(paging.Destination(0, 100, 3000, 296, 14, 1) == 310, "a narrow viewport still advances one card");
Check(paging.Destination(170, 900, 3000, 296, 14, 1) == 930, "manual partial scrolling resumes at a whole card");
Check(paging.Destination(0, 900, 0, 296, 14, 1) == 0, "a short row stays still");
Check(paging.Destination(0, 900, 3000, 296, 14, -1) == 0, "previous clamps at the reading-order start");
Check(paging.Destination(2790, 900, 3000, 296, 14, 1) == 3000, "the final page clamps to the content edge");
Check(paging.Destination(0, 1200, 4000, 444, 21, 1) == 930, "scaled cards use their scaled gaps");
paging.Track(10, 620);
Check(paging.Destination(100, 900, 3000, 296, 14, 1) == 1240, "rapid next clicks accumulate from the destination");
Check(paging.Destination(100, 900, 3000, 296, 14, -1) == 0, "a rapid reversal returns to the previous page");
paging.Track(11, 1240);
paging.Complete(10);
Check(paging.Target == 1240, "an interrupted operation cannot clear its replacement");
paging.Complete(11);
Check(paging.Target is null, "matching completion releases the requested offset");
paging.Track(12, 1860);
paging.Reset();
paging.Complete(12);
Check(paging.Target is null && paging.Destination(310, 900, 3000, 296, 14, 1) == 930,
    "interaction or geometry changes discard the old destination and late completion");
paging.Track(13, 620);
paging.Track(-1, 930);
Check(paging.Target is null, "a rejected scroll does not retain a destination");

Check(paging.WheelDestination(500, 3000, -120, false, false) == 620,
    "wheel down advances the row in reading order");
Check(paging.WheelDestination(500, 3000, -120, false, true) == 620,
    "wheel down advances the RTL row in reading order too");
Check(paging.WheelDestination(500, 3000, 120, false, true) == 380,
    "wheel up moves toward the RTL row start");
Check(paging.WheelDestination(500, 3000, 120, true, false) == 620
    && paging.WheelDestination(500, 3000, -120, true, false) == 380,
    "horizontal wheel follows physical right and left in LTR");
Check(paging.WheelDestination(500, 3000, 120, true, true) == 380
    && paging.WheelDestination(500, 3000, -120, true, true) == 620,
    "horizontal wheel reverses logical offset in RTL");
Check(paging.WheelDestination(0, 0, -120, false, false) is null
    && paging.WheelDestination(100, 3000, 0, false, false) is null,
    "short rows and zero wheel input leave the event to the page");
Check(paging.WheelDestination(0, 3000, 120, false, false) == 0
    && paging.WheelDestination(3000, 3000, -120, false, false) == 3000,
    "overflowing rows still own outward wheel input at both boundaries");
paging.Track(20, 620);
var wheelTarget = paging.WheelDestination(100, 3000, -30, false, false);
Check(wheelTarget == 650, "wheel during a chevron animation adds to the requested page");
paging.Track(21, wheelTarget!.Value);
paging.Complete(20);
Check(paging.Target == 650 && paging.WheelDestination(110, 3000, -30, false, false) == 680,
    "small rapid wheel deltas accumulate despite an old chevron completion");
Check(paging.WheelDestination(110, 3000, 30, false, false) == 620,
    "wheel reversal subtracts from the pending destination");
Check(paging.Destination(110, 900, 3000, 296, 14, 1) == 1240,
    "chevron after wheel resumes whole-card paging from the pending destination");
paging.Track(22, 1240);
paging.Complete(21);
Check(paging.Target == 1240, "old wheel completion cannot clear a newer chevron request");
paging.Complete(22);
Check(paging.WheelDestination(1240, 3000, -1, false, false) == 1241,
    "completed requests use the actual offset and retain fine wheel deltas");
paging.Track(23, 2990);
Check(paging.WheelDestination(1500, 3000, int.MinValue, false, false) == 3000,
    "large wheel input clamps without integer overflow");
paging.Reset();
Check(paging.WheelDestination(200, 3000, -120, false, false) == 320,
    "layout or native input cancellation discards the pending wheel target");

var homeProfile = "layout-test";
var dragBounds = new[]
{
    (LightWeaver.Settings.HomeSectionId.Resume, 100d, 40d),
    (LightWeaver.Settings.HomeSectionId.NextUp, 144d, 40d),
    (LightWeaver.Settings.HomeSectionId.Latest, 188d, 40d),
};
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, 105) == (LightWeaver.Settings.HomeSectionId.Resume, false), "pointer above the first midpoint inserts before the first row");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, 150) == (LightWeaver.Settings.HomeSectionId.NextUp, false), "pointer in a later row upper half inserts before that row");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, 180) == (LightWeaver.Settings.HomeSectionId.Latest, false), "pointer below a middle row midpoint inserts before the following row");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, 225) == (LightWeaver.Settings.HomeSectionId.Latest, true), "pointer below the last midpoint inserts after the last row");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, 142) == (LightWeaver.Settings.HomeSectionId.NextUp, false), "gaps between rows still select an insertion position");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget(dragBounds, double.NaN) is null, "invalid pointer position cannot commit a move");
Check(LightWeaver.Settings.HomeSectionOrder.DropTarget([], 120) is null, "an unloaded editor cannot commit a move");
var dragLayout = new List<LightWeaver.Settings.HomeSectionPref>
{
    new(LightWeaver.Settings.HomeSectionId.Resume, true),
    new(LightWeaver.Settings.HomeSectionId.Libraries, false),
    new(LightWeaver.Settings.HomeSectionId.NextUp, false),
    new(LightWeaver.Settings.HomeSectionId.Latest, true),
    new(LightWeaver.Settings.HomeSectionId.Favorites, true),
};
var draggedDown = LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, LightWeaver.Settings.HomeSectionId.Resume, LightWeaver.Settings.HomeSectionId.Latest, true);
Check(draggedDown.Select(p => p.Id).SequenceEqual([LightWeaver.Settings.HomeSectionId.NextUp, LightWeaver.Settings.HomeSectionId.Libraries, LightWeaver.Settings.HomeSectionId.Latest, LightWeaver.Settings.HomeSectionId.Resume, LightWeaver.Settings.HomeSectionId.Favorites]), "dropping after a later section moves down without shifting the legacy slot");
var draggedUp = LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, LightWeaver.Settings.HomeSectionId.Favorites, LightWeaver.Settings.HomeSectionId.Resume, false);
Check(draggedUp[0].Id == LightWeaver.Settings.HomeSectionId.Favorites && draggedUp[1] == dragLayout[1], "dropping before the first section moves up and preserves the legacy preference");
Check(LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, LightWeaver.Settings.HomeSectionId.Resume, LightWeaver.Settings.HomeSectionId.Resume, true).SequenceEqual(dragLayout), "dropping a section onto itself changes nothing");
Check(LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, LightWeaver.Settings.HomeSectionId.Resume, LightWeaver.Settings.HomeSectionId.Libraries, true).SequenceEqual(dragLayout), "a legacy or missing target cannot reorder sections");
Check(LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, LightWeaver.Settings.HomeSectionId.Libraries, LightWeaver.Settings.HomeSectionId.Resume, false).SequenceEqual(dragLayout), "the legacy section cannot become a drag source");
var editableIds = dragLayout.Where(p => p.Id != LightWeaver.Settings.HomeSectionId.Libraries).Select(p => p.Id).ToArray();
foreach (var source in editableIds)
    foreach (var target in editableIds)
        foreach (var after in new[] { false, true })
        {
            var reordered = LightWeaver.Settings.HomeSectionOrder.Move(dragLayout, source, target, after);
            Check(reordered.Count == dragLayout.Count && reordered.Select(p => p.Id).Distinct().Count() == reordered.Count
                && reordered.All(p => dragLayout.Single(original => original.Id == p.Id).Enabled == p.Enabled)
                && reordered[1] == dragLayout[1], $"drag {source} {(after ? "after" : "before")} {target} preserves all preferences");
        }
Check(!HomeSections.IsRowVisible(true, false, false, false), "cold Home waits before showing placeholders");
Check(HomeSections.IsRowVisible(true, false, false, true), "cold Home shows enabled placeholders after the delay");
Check(!HomeSections.IsRowVisible(false, false, false, true), "disabling a section during cold loading hides its placeholders");
Check(HomeSections.IsRowVisible(true, false, false, true), "re-enabling during cold loading restores placeholders before items arrive");
Check(HomeSections.IsRowVisible(true, true, true, true), "loaded rows with items replace cold placeholders");
Check(!HomeSections.IsRowVisible(true, false, true, true), "a loaded empty row hides even after the placeholder delay");
Check(!HomeSections.IsRowVisible(false, true, true, false), "disabled cached rows stay hidden");
var otherProfile = "other-layout-test";
var layout = LightWeaver.Settings.HomeLayoutStore.Load(homeProfile);
var reversed = layout.AsEnumerable().Reverse().Select(p => p with { Enabled = false }).ToList();
LightWeaver.Settings.HomeLayoutStore.Save(otherProfile, layout);
LightWeaver.Settings.HomeLayoutStore.Save(homeProfile, reversed);
var savedLayout = LightWeaver.Settings.HomeLayoutStore.Load(homeProfile);
Check(savedLayout.SequenceEqual(reversed), "all-hidden Home layout preserves order, including the legacy Libraries preference");
var favoriteIndex = savedLayout.FindIndex(p => p.Id == LightWeaver.Settings.HomeSectionId.Favorites);
savedLayout[favoriteIndex] = savedLayout[favoriteIndex] with { Enabled = true };
LightWeaver.Settings.HomeLayoutStore.Save(homeProfile, savedLayout);
Check(LightWeaver.Settings.HomeLayoutStore.Load(homeProfile).SequenceEqual(savedLayout), "a section can be enabled again without resetting other toggles or row order");
Check(LightWeaver.Settings.HomeLayoutStore.Load(otherProfile).SequenceEqual(layout), "changing Home switches preserves another profile's choices");
// --- Home layout: the Downloads row is off until turned on, and each app saves its own rows ----
var layoutKey = LightWeaver.Settings.HomeLayoutStore.ProfileKey(Guid.NewGuid(), "https://tower");
var fresh = LightWeaver.Settings.HomeLayoutStore.Load(layoutKey);
Check(fresh.Select(p => p.Id).SequenceEqual(Enum.GetValues<LightWeaver.Settings.HomeSectionId>()), "a fresh profile gets every row in default order");
Check(fresh.All(p => p.Enabled == (p.Id != LightWeaver.Settings.HomeSectionId.Downloads)), "every row is on but Downloads");
Check(LightWeaver.Settings.HomeLayoutStore.Defaults().SequenceEqual(fresh), "Reset gives the fresh layout");
LightWeaver.Settings.HomeLayoutStore.Save(layoutKey, [new(LightWeaver.Settings.HomeSectionId.Favorites, false), new(LightWeaver.Settings.HomeSectionId.Resume, true)]);
var stale = LightWeaver.Settings.HomeLayoutStore.Load(layoutKey);
Check(stale.Take(2).SequenceEqual([new(LightWeaver.Settings.HomeSectionId.Favorites, false), new(LightWeaver.Settings.HomeSectionId.Resume, true)])
    && stale.Single(p => p.Id == LightWeaver.Settings.HomeSectionId.Downloads) is { Enabled: false }, "a layout saved before Downloads existed gets it appended, off");
var withDownloads = stale.Select(p => p.Id == LightWeaver.Settings.HomeSectionId.Downloads ? p with { Enabled = true } : p).ToList();
LightWeaver.Settings.HomeLayoutStore.Save(layoutKey, withDownloads);
LightWeaver.Settings.HomeLayoutStore.Save(layoutKey, [.. withDownloads.Where(p => p.Id != LightWeaver.Settings.HomeSectionId.Downloads).Reverse()]);
Check(LightWeaver.Settings.HomeLayoutStore.Load(layoutKey).Single(p => p.Id == LightWeaver.Settings.HomeSectionId.Downloads) is { Enabled: true },
    "saving the rows one app shows keeps the row only the other app shows");

// --- Downloads row after an offline start: no session, so the server comes from the profile key ---
Check(LightWeaver.Settings.HomeLayoutStore.ServerOf(layoutKey) == "https://tower", "a profile key gives back its server");
Check(LightWeaver.Settings.HomeLayoutStore.ServerOf("") is null && LightWeaver.Settings.HomeLayoutStore.ServerOf("not-a-profile@https://tower") is null
    && LightWeaver.Settings.HomeLayoutStore.ServerOf(LightWeaver.Settings.HomeLayoutStore.ProfileKey(Guid.NewGuid(), null)) is null, "anything else gives no server");
var offlineDownload = new LightWeaver.Downloads.DownloadItem
{
    ItemId = Guid.NewGuid(), ServerUrl = "https://tower/", Title = "Paper Moons", FilePath = "x.mkv", Status = LightWeaver.Downloads.DownloadStatus.Completed,
};
Check(LightWeaver.Downloads.DownloadsModel.HomeItems([offlineDownload], LightWeaver.Settings.HomeLayoutStore.ServerOf(layoutKey)).Single().Id == offlineDownload.ItemId,
    "an offline start lists the profile's downloads without a session");


Directory.Delete(appData, true);
Console.WriteLine($"{checks} checks passed.");
return 0;
