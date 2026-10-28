using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;

// Store failures are logged under AppPaths; keep those logs out of an app's real profile.
var appData = Path.Combine(Path.GetTempPath(), "lw-library-appdata-" + Guid.NewGuid().ToString("N"));
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

MediaItem Item(string name, bool played = false, string[]? genres = null, int? year = null, float? rating = null, int minutes = 0, int addedDaysAgo = 0, string? sortName = null) => new()
{
    Id = Guid.NewGuid(), Name = name, SortName = sortName, Type = BaseItemDto_Type.Movie, Played = played, Genres = genres ?? [],
    ProductionYear = year, CommunityRating = rating, RuntimeTicks = minutes == 0 ? null : minutes * TimeSpan.TicksPerMinute,
    DateCreated = DateTimeOffset.Now.AddDays(-addedDaysAgo),
};

var all = new List<MediaItem>
{
    Item("Kestrel", genres: ["Drama"], year: 2017, rating: 7.1f, minutes: 101, addedDaysAgo: 3),
    Item("Arrival", played: true, genres: ["Drama", "Science Fiction"], year: 2016, rating: 7.9f, minutes: 116, addedDaysAgo: 30),
    Item("Mid Ferry", genres: ["Thriller"], year: 2001, rating: 6.0f, minutes: 95, addedDaysAgo: 1, sortName: "Midnight Ferry"),
    Item("12 Monkeys", genres: ["Thriller", "Science Fiction"], year: 1995, rating: 8.0f, minutes: 129, addedDaysAgo: 100),
    Item("Élan", played: true, genres: ["Comedy"], year: 2020, minutes: 88, addedDaysAgo: 10),
};
string Names(IReadOnlyList<MediaItem> items) => string.Join(", ", items.Select(i => i.Name));

var byName = LibraryQuery.Default.Apply(all);
Check(Names(byName) == "12 Monkeys, Arrival, Élan, Kestrel, Mid Ferry", "default: name order, by sort name: " + Names(byName));
Check(!LibraryQuery.Default.IsFiltered, "defaults filter nothing");
Check(Names(new LibraryQuery(Watched: LibraryWatched.Unplayed).Apply(all)) == "12 Monkeys, Kestrel, Mid Ferry", "Unplayed");
Check(Names(new LibraryQuery(Watched: LibraryWatched.Played).Apply(all)) == "Arrival, Élan", "Played");
Check(Names(new LibraryQuery(Genres: ["drama", "Thriller"]).Apply(all)) == "12 Monkeys, Arrival, Kestrel, Mid Ferry", "genres match any of them, case-insensitive");
Check(new LibraryQuery(Genres: ["Drama"]).IsFiltered && new LibraryQuery(Watched: LibraryWatched.Played).IsFiltered, "genre and watched count as filters");
Check(!new LibraryQuery(Sort: LibrarySort.Rating, Descending: true).IsFiltered, "sort and order do not");
Check(Names(new LibraryQuery(Sort: LibrarySort.DateAdded, Descending: true).Apply(all)) == "Mid Ferry, Kestrel, Élan, Arrival, 12 Monkeys", "Date added, newest first");
Check(Names(new LibraryQuery(Sort: LibrarySort.ReleaseDate).Apply(all)) == "12 Monkeys, Mid Ferry, Arrival, Kestrel, Élan", "Release date falls back to the year");
Check(new LibraryQuery(Sort: LibrarySort.Rating).Apply(all)[0].Name == "Élan", "unrated sorts first ascending");
Check(Names(new LibraryQuery(Sort: LibrarySort.Runtime, Descending: true).Apply(all)) == "12 Monkeys, Arrival, Kestrel, Mid Ferry, Élan", "Runtime, longest first");

// --- A–Z rail -----------------------------------------------------------------------------------
Check(LibraryQuery.Letter(all[3]) == "#" && LibraryQuery.Letter(all[4]) == "E" && LibraryQuery.Letter(all[2]) == "M", "letters: digits are #, accents fold, the sort name counts");
Check(LibraryQuery.RailLetters.Count == 27 && LibraryQuery.RailLetters[0] == "#" && LibraryQuery.RailLetters[^1] == "Z", "rail: # then A to Z");
Check(LibraryQuery.FirstIndexOf(byName, "K") == 3, "jump to K lands on Kestrel");
Check(LibraryQuery.FirstIndexOf(byName, "B") == 2, "a letter without items jumps to the next one (B to Élan)");
Check(LibraryQuery.FirstIndexOf(byName, "Z") is null, "nothing after Z");
var descending = new LibraryQuery(Descending: true).Apply(all);
Check(LibraryQuery.FirstIndexOf(descending, "K", descending: true) == 1, "descending: K lands on Kestrel");

// --- Display options, remembered per library ----------------------------------------------------
var folder = Path.Combine(Path.GetTempPath(), "lw-library-tests-" + Guid.NewGuid().ToString("N"));
LibraryViewStore.FilePath = Path.Combine(folder, "library-views.json");
var movies = Guid.NewGuid();
Check(LibraryViewStore.Load("p1", movies) == new LibraryView(), "no file: defaults");
LibraryViewStore.Save("p1", movies, new LibraryView(3, false));
Check(LibraryViewStore.Load("p1", movies) == new LibraryView(3, false), "saved options come back");
Check(LibraryViewStore.Load("p2", movies) == new LibraryView() && LibraryViewStore.Load("p1", Guid.NewGuid()) == new LibraryView(), "other profiles and libraries keep their own");
File.WriteAllText(LibraryViewStore.FilePath, "{ not json");
Check(LibraryViewStore.Load("p1", movies) == new LibraryView(), "a damaged file reads as defaults");
LibraryViewStore.Save("p1", movies, new LibraryView(9, true));
Check(LibraryViewStore.Load("p1", movies).SizeStep == LibraryViewStore.PosterWidths.Count - 1, "an out-of-range size snaps to the largest");
Directory.Delete(folder, true);

// --- Refresh diffs: keep the cards of an unchanged answer ---------------------------------------
var kestrel = all[0];
var kestrelAgain = kestrel.WithUserData(kestrel.Played, kestrel.IsFavorite, kestrel.ResumePositionTicks, kestrel.PlayedPercentage);
var kestrelWatched = kestrel.WithUserData(true, kestrel.IsFavorite, kestrel.ResumePositionTicks, kestrel.PlayedPercentage);
Check(ItemLists.SameIds(all, [.. all]), "the same items in the same order have the same ids");
Check(!ItemLists.SameIds(all, [.. all.AsEnumerable().Reverse()]), "a reordered answer is not the same list");
Check(!ItemLists.SameIds(all, all.Take(4).ToList()), "a shorter answer is not the same list");
Check(ItemLists.SameShown(kestrel, kestrelAgain), "an unchanged copy shows the same");
Check(!ItemLists.SameShown(kestrel, kestrelWatched), "a copy now watched shows differently");
Check(!ItemLists.SameShown([kestrel, all[1]], [kestrelWatched, all[1]]), "one changed card makes the list show differently");

// --- Image disk cache: names shared with the WPF app, least recently used evicted first ----------
Check(LightWeaver.Imaging.ImageDiskCache.FileName("abc") == "a9993e364706816aba3e25717850c26c9cd0d89d.img", "cache file name is lowercase hex SHA-1 of the address");
var images = Path.Combine(Path.GetTempPath(), "lw-imagecache-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(images);
string Image(string name, int minutesAgo)
{
    var path = Path.Combine(images, name + ".img");
    File.WriteAllBytes(path, new byte[100]);
    File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    return path;
}
var oldest = Image("oldest", 30);
var older = Image("older", 20);
var newest = Image("newest", 1);
File.WriteAllBytes(Path.Combine(images, "partial.img.1.tmp"), new byte[1000]);
Check(LightWeaver.Imaging.ImageDiskCache.Evict(images, 300) == 0, "nothing is evicted while the cache fits");
Check(LightWeaver.Imaging.ImageDiskCache.Evict(images, 150) == 2, "eviction stops once the rest fits");
Check(!File.Exists(oldest) && !File.Exists(older) && File.Exists(newest), "the least recently used images go first");
Directory.Delete(images, true);

// --- History: a library opened again from the top bar shows its kept page ------------------------
var built = 0;
Func<object> Build() => () => { built++; return new object(); };
var history = new LightWeaver.Navigation.NavHistory<object>();
history.Reset("home", Build());
history.Realize();
history.Go("library:a", Build(), reuse: true);
history.Realize();
var libraryA = history.Current!.Page;
history.Go("library:b", Build(), reuse: true);
history.Realize();
Check(history.Holds(libraryA!), "the page a step back stays held");
built = 0;
history.Go("library:a", Build(), reuse: true);
Check(!history.Realize() && built == 0 && ReferenceEquals(history.Current!.Page, libraryA), "reopening a library from Back history reuses its page");
history.Back();
Check(history.Current!.Key == "library:b" && !history.Realize(), "Back still shows the library left");
Check(history.Holds(libraryA!) && history.Holds(history.Current!.Page!), "the page shown and the page a step forward stay held");
history.Back();
Check(history.Current!.Key == "library:a" && history.Realize(), "the entry whose page moved on rebuilds when reached");
var rebuiltA = history.Current!.Page;
history.Forward();
history.Realize();
history.Back();
history.Realize();
history.Back();
history.Realize();
Check(history.Current!.Key == "home" && history.CanGoForward, "back on Home with the libraries ahead");
built = 0;
history.Go("library:a", Build(), reuse: true);
Check(!history.Realize() && ReferenceEquals(history.Current!.Page, rebuiltA) && !history.CanGoForward, "a library held in Forward history is reused before Forward is cleared");
built = 0;
history.Go("library:c", Build());
Check(history.Realize() && built == 1, "without reuse a place is built");
history.Go("library:c", Build());
Check(!history.Realize() && built == 1, "going to the place already shown builds nothing");
for (var i = 0; i < 10; i++)
{
    history.Go("item:" + i, Build());
    history.Realize();
}
built = 0;
history.Go("library:a", Build(), reuse: true);
Check(history.Realize() && built == 1, "a page trimmed from the history is built again");
Check(history.Holds(history.Current!.Page!) && !history.Holds(rebuiltA!), "the history holds the page on show and lets go of a page it trimmed");

// --- Artwork widths: a few fixed steps, never narrower than shown ------------------------------
var sizes = LightWeaver.Imaging.ArtworkSizes.Steps;
Check(sizes.Zip(sizes.Skip(1)).All(p => p.Second > p.First && p.Second <= p.First * 1.5), "steps grow by at most half: " + string.Join("/", sizes));
Check(Enumerable.Range(1, sizes[^1]).All(w => LightWeaver.Imaging.ArtworkSizes.Step(w) >= w), "a step is never narrower than the width shown");
Check(LightWeaver.Imaging.ArtworkSizes.Step(150) == 160 && LightWeaver.Imaging.ArtworkSizes.Step(160) == 160 && LightWeaver.Imaging.ArtworkSizes.Step(161) == 240, "the next step up is chosen");
Check(new[] { 185, 192, 200, 210 }.Select(LightWeaver.Imaging.ArtworkSizes.Step).Distinct().Count() == 1, "a 185 px poster the grid stretches as the window grows keeps one address");
Check(LightWeaver.Imaging.ArtworkSizes.Step(296) == 320 && LightWeaver.Imaging.ArtworkSizes.Step(444) == 480 && LightWeaver.Imaging.ArtworkSizes.Step(592) == 640, "wide Home cards at 100, 150 and 200 %");
Check(LightWeaver.Imaging.ArtworkSizes.Step(5000) == 1920, "anything wider asks for the widest step");
Check(LightWeaver.Imaging.ArtworkSizes.Next(160) == 240 && LightWeaver.Imaging.ArtworkSizes.Next(1920) is null && LightWeaver.Imaging.ArtworkSizes.Next(300) is null, "the next step, none past the widest or off the ladder");

// --- Decoded images in memory: one entry per key, the widest kept ------------------------------
var decodedCache = new LightWeaver.Imaging.DecodedCache<string>(2);
decodedCache.Remember("320|poster", "at 300", 300);
Check(decodedCache.Get("320|poster", 300) == "at 300" && decodedCache.Get("320|poster", 290) == "at 300", "a decode serves its width and narrower");
Check(decodedCache.Get("320|poster", 310) is null, "a wider card needs a wider decode");
decodedCache.Remember("320|poster", "at 280", 280);
Check(decodedCache.Get("320|poster", 300) == "at 300" && decodedCache.Count == 1, "a narrower decode does not replace a wider one");
decodedCache.Remember("320|poster", "at 316", 316);
Check(decodedCache.Get("320|poster", 316) == "at 316" && decodedCache.Count == 1, "a wider decode replaces the entry instead of adding one");
decodedCache.Remember("320|poster", "abandoned", 320, onlyIfEmpty: true);
Check(decodedCache.Get("320|poster", 316) == "at 316", "a load nobody waits for does not replace the image its replacement stored");
decodedCache.Remember("320|other", "abandoned", 300, onlyIfEmpty: true);
Check(decodedCache.Get("320|other", 300) == "abandoned", "but fills an empty key");
decodedCache.Remember("320|third", "third", 300);
Check(decodedCache.Count == 2 && decodedCache.Get("320|poster", 300) is null, "the least recently used key goes first");
decodedCache.Remember("320|other", "again", 310);
decodedCache.Remember("320|fourth", "fourth", 300);
Check(decodedCache.Count == 2 && decodedCache.Get("320|other", 310) == "again" && decodedCache.Get("320|fourth", 300) == "fourth",
    "evicting after a replacement never drops the newer image's entry");

// --- Artwork loads: one per key, dropped once nobody wants them ---------------------------------
var shared = new LightWeaver.Imaging.SharedLoads<string>();
var starts = 0;
var release = new TaskCompletionSource<string?>();
CancellationToken loadToken = default;
Task<string?> Start(CancellationToken token)
{
    starts++;
    loadToken = token;
    return release.Task.WaitAsync(token);
}
using (var first = new CancellationTokenSource())
using (var second = new CancellationTokenSource())
{
    var a = shared.GetAsync("poster", Start, first.Token);
    var b = shared.GetAsync("poster", Start, second.Token);
    Check(starts == 1 && shared.Count == 1, "two cards asking for the same poster share one load");
    first.Cancel();
    Check(await a is null && !loadToken.IsCancellationRequested, "a card that gives up gets nothing, and the other keeps the load");
    release.SetResult("art");
    Check(await b == "art" && shared.Count == 0, "the card still waiting gets the poster");
}
starts = 0;
release = new TaskCompletionSource<string?>();
using (var only = new CancellationTokenSource())
{
    var c = shared.GetAsync("backdrop", Start, only.Token);
    only.Cancel();
    Check(await c is null && loadToken.IsCancellationRequested && shared.Count == 0, "when every card gives up, the load is cancelled");
    var d = shared.GetAsync("backdrop", Start);
    Check(starts == 2, "asking again after that starts a new load");
    release.SetResult("again");
    Check(await d == "again", "and it delivers");
}
starts = 0;
release = new TaskCompletionSource<string?>();
using (var leaving = new CancellationTokenSource())
{
    var kept = shared.GetAsync("photo", Start);
    var left = shared.GetAsync("photo", Start, leaving.Token);
    leaving.Cancel();
    Check(await left is null && !loadToken.IsCancellationRequested, "a caller without a token keeps the load wanted");
    release.SetResult("photo");
    Check(await kept == "photo", "and gets it");
}
using (var gone = new CancellationTokenSource())
{
    gone.Cancel();
    Check(await shared.GetAsync("never", Start, gone.Token) is null && shared.Count == 0, "an already cancelled request starts nothing");
}
Check(await shared.GetAsync("refused", _ => Task.FromCanceled<string?>(new CancellationToken(true))) is null, "a cancelled load reads as no artwork");

// --- Decode turns: fewer while an animation runs or on two cores -------------------------------
Check(LightWeaver.Imaging.DecodeSlots.Limit(8, false) == 4 && LightWeaver.Imaging.DecodeSlots.Limit(4, false) == 2 && LightWeaver.Imaging.DecodeSlots.Limit(32, false) == 4, "half the cores, two to four");
Check(LightWeaver.Imaging.DecodeSlots.Limit(2, false) == 1 && LightWeaver.Imaging.DecodeSlots.Limit(1, false) == 1, "one on a machine with two cores or fewer");
Check(LightWeaver.Imaging.DecodeSlots.Limit(16, true) == 1, "one while an animation runs");
var limit = 2;
var turns = new LightWeaver.Imaging.DecodeSlots(() => limit);
var turnA = turns.WaitAsync();
var turnB = turns.WaitAsync();
var turnC = turns.WaitAsync();
var turnD = turns.WaitAsync();
Check(turnA.IsCompletedSuccessfully && turnB.IsCompletedSuccessfully && !turnC.IsCompleted && turns.Waiting == 2, "two turns at once, the rest wait in line");
limit = 1;
turns.Release();
Check(!turnC.IsCompleted && turns.Active == 1, "a lower limit takes effect as turns come back");
limit = 3;
turns.Pump();
await Task.WhenAll(turnC, turnD).WaitAsync(TimeSpan.FromSeconds(5));
Check(turns.Active == 3 && turns.Waiting == 0, "a higher limit gives the waiting turns once pumped");
using (var gone = new CancellationTokenSource())
{
    var dropped = turns.WaitAsync(gone.Token);
    var kept = turns.WaitAsync();
    gone.Cancel();
    Check(dropped.IsCanceled && turns.Waiting == 1, "a cancelled wait leaves the line");
    turns.Release();
    await kept.WaitAsync(TimeSpan.FromSeconds(5));
    Check(kept.IsCompletedSuccessfully, "and the next in line gets its turn");
}
var threaded = new LightWeaver.Imaging.DecodedCache<string>(50);
Parallel.For(0, 2000, i =>
{
    threaded.Remember("k" + (i % 80), "v" + i, i % 7);
    threaded.Get("k" + ((i * 7) % 80), 0);
});
Check(threaded.Count == 50, "the decoded cache stays consistent when loads finish on several threads at once: " + threaded.Count);

// --- A scrollbar drag: 110 recycled cards handed 5000 titles in turn ---------------------------
const int Titles = 5000, Cards = 110, Slots = 4;
var slots = new SemaphoreSlim(0);
var decoded = 0;
var scroll = new LightWeaver.Imaging.SharedLoads<string>();
var cardLoads = new CancellationTokenSource?[Cards];
var requests = new List<Task<string?>>();
for (var title = 0; title < Titles; title++)
{
    var card = title % Cards;
    cardLoads[card]?.Cancel();
    cardLoads[card] = new CancellationTokenSource();
    var name = "title" + title;
    requests.Add(scroll.GetAsync(name, async token =>
    {
        await slots.WaitAsync(token);
        try
        {
            Interlocked.Increment(ref decoded);
            await Task.Yield();
            return name;
        }
        finally { slots.Release(); }
    }, cardLoads[card]!.Token));
}
Check(scroll.Count == Cards, $"after the drag only the {Cards} titles on cards are still wanted (of {Titles} requested)");
slots.Release(Slots);
var results = await Task.WhenAll(requests);
Check(decoded == Cards && results.Count(r => r is not null) == Cards, $"reads and decodes after the drag: {decoded} of {Titles} requests");
foreach (var load in cardLoads) load?.Dispose();

var cachedFolder = Guid.NewGuid();
var cachedAt = DateTime.UtcNow.AddDays(-2);
var cacheEntry = new FolderCacheEntry<MediaItem>(all, all.Count, true, false, cachedAt, BrowseFolderCache.CurrentSchema);
await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key("offline-user@tower", cachedFolder), cacheEntry);
var offlineCopy = await LibraryLoader.ReadCachedAsync("offline-user@tower", cachedFolder);
Check(offlineCopy?.CachedAtUtc == cachedAt && offlineCopy.Items.Count == all.Count, "cold offline folder reads retain the server timestamp");
Check(await LibraryLoader.ReadCachedAsync("other-user@tower", cachedFolder) is null
    && await LibraryLoader.ReadCachedAsync("offline-user@other-server", cachedFolder) is null, "cached folders cannot cross user or server boundaries");
Check(await LibraryLoader.ReadCachedAsync("", cachedFolder) is null, "anonymous navigation never reads a cached folder");
var emptyFolder = Guid.NewGuid();
await BrowseFolderCache.StoreAsync(BrowseFolderCache.Key("offline-user@tower", emptyFolder), cacheEntry with { Items = [], TotalCount = 0 });
Check((await LibraryLoader.ReadCachedAsync("offline-user@tower", emptyFolder)) is { Items.Count: 0, CachedAtUtc: var emptyAt } && emptyAt == cachedAt,
    "a cached empty library still has a valid offline timestamp");
var cachedProgress = new List<(int Count, bool Done, DateTime At)>();
await LibraryLoader.LoadAsync(new JellyfinService(), "offline-user@tower", emptyFolder,
    (values, total, done, timestamp) => cachedProgress.Add((values.Count, done, timestamp)), CancellationToken.None, cachedOnly: true);
Check(cachedProgress.SequenceEqual([(0, true, cachedAt)]), "cold offline load completes an empty cached folder without requesting the server");
Check(LightWeaver.Navigation.OfflinePlan.For(true, "library:any", cachedAt) == LightWeaver.Navigation.OfflineShow.CachedPage
    && LightWeaver.Navigation.OfflinePlan.For(true, "home", null) == LightWeaver.Navigation.OfflineShow.Unreachable,
    "offline navigation uses cached data only when it has a timestamp");
Check(LightWeaver.Navigation.OfflinePlan.For(true, "downloads", null) == LightWeaver.Navigation.OfflineShow.Page
    && LightWeaver.Navigation.OfflinePlan.For(true, "settings", null) == LightWeaver.Navigation.OfflineShow.Page
    && LightWeaver.Navigation.OfflinePlan.For(false, "home", null) == LightWeaver.Navigation.OfflineShow.Page,
    "local pages and online navigation remain available without cached data");

Console.WriteLine($"{checks} checks passed.");
if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
return 0;
