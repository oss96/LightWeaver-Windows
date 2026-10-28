using LightWeaver.Home;

using LightWeaver.Jellyfin;

using LightWeaver.Search;

using LightWeaver.Theme;



var checks = 0;

void Check(bool condition, string name)

{

    if (!condition) throw new InvalidOperationException("FAIL " + name);

    checks++;

}



UiZoom.SetLimitToScreen(true);

UiZoom.SetScreenCap(1.25);

UiZoom.Set(2);

Check(UiZoom.Preferred == 2 && UiZoom.Scale == 1.25, "cap preserves preference");

UiZoom.SetScreenCap(2);

Check(UiZoom.Scale == 2, "larger screen restores preference");

UiZoom.SetScreenCap(1);

var changed = 0;

void PreferredChanged() => changed++;

UiZoom.PreferredChanged += PreferredChanged;

UiZoom.Reset();

Check(UiZoom.Preferred == 1 && UiZoom.Scale == 1 && changed == 1, "reset saves preference when applied scale is unchanged");

UiZoom.PreferredChanged -= PreferredChanged;

UiZoom.SetScreenCap(1.25);

UiZoom.Set(2);

UiZoom.StepIn();

Check(UiZoom.Preferred == 2 && UiZoom.AtScreenLimit, "step at cap reports limit");

UiZoom.StepOut();

Check(UiZoom.Scale == 1.1 && UiZoom.Preferred == 1.1, "step out starts from visible scale");

UiZoom.SetLimitToScreen(false);

UiZoom.Set(2);

Check(UiZoom.Scale == 2, "disabled cap applies full preference");

Check(UiZoom.CapFor(1920, 1080, 900, 600) == 1.75, "cap fits both dimensions");

Check(UiZoom.CapFor(800, 500, 900, 600) == 1, "small screens retain baseline");

UiZoom.Reset();

UiZoom.Wheel(40);

UiZoom.Wheel(40);

Check(UiZoom.Scale == 1, "small wheel deltas accumulate");

UiZoom.Wheel(40);

Check(UiZoom.Scale == 1.1, "one wheel notch steps once");

UiZoom.Wheel(80);

UiZoom.Wheel(-40);

UiZoom.Wheel(-80);

Check(UiZoom.Scale == 1, "reversing pinch drops previous remainder");

foreach (var chord in new[] { "Ctrl+OemPlus", "Ctrl+Shift+OemPlus", "Ctrl+OemMinus", "Ctrl+NumPad0", "Ctrl+D0", "Ctrl+Add", "Ctrl+Subtract" })

    Check(UiZoom.IsZoomChord(chord), "reserved " + chord);

foreach (var chord in new[] { "OemPlus", "Shift+OemMinus", "Ctrl+Alt+D0", "Ctrl+Right", "Ctrl+Shift+F" })

    Check(!UiZoom.IsZoomChord(chord), "available " + chord);

foreach (var key in new[] { 0xBB, 0x6B, 0xBD, 0x6D, 0x30, 0x60 })

    Check(UiZoom.ZoomKey(key, true, false) != int.MinValue && UiZoom.ZoomKey(key, true, true) == int.MinValue, "zoom key modifiers " + key);



var a = new MediaItem { Id = Guid.NewGuid(), Name = "A" };

var b = new MediaItem { Id = Guid.NewGuid(), Name = "B" };

var c = new MediaItem { Id = Guid.NewGuid(), Name = "C" };

var removals = new ItemRemovals();

Check(removals.Remove([Guid.Empty, a.Id, a.Id]).SetEquals([a.Id]), "removal ignores invalid and duplicate ids");

Check(removals.Remove([a.Id]).Count == 0, "repeated removal is idempotent");

Check(removals.Filter([a, b]).SequenceEqual([b]), "late result cannot restore tombstone");

var filtered = removals.FilterPage([a, b], 8);

Check(filtered.TotalCount == 7 && filtered.Items.SequenceEqual([b]), "filtered response adjusts count once");

Check(!ItemRemovals.ForProfile(Guid.NewGuid().ToString()).Contains(a.Id), "profiles do not share removals");

var beforeRestore = removals.Mark();

Check(removals.Restore([a.Id]).SetEquals([a.Id]) && !removals.Contains(a.Id), "a re-added item is no longer removed");

Check(removals.Filter([a, b], beforeRestore).SequenceEqual([b]), "a response read before the re-add still cannot show it");

Check(removals.Filter([a, b], removals.Mark()).SequenceEqual([a, b]), "a response read after the re-add shows it");

Check(removals.Restore([b.Id]).Count == 0, "restoring an item that was never removed changes nothing");



var pending = new TaskCompletionSource<(List<MediaItem>, int)>();

var calls = 0;

var search = new AdvancedSearchModel((_, _, _, _) => ++calls == 1 ? pending.Task : Task.FromResult((new List<MediaItem> { a, b }, 2)), removals: removals);

var load = search.RefreshNowAsync();

search.RemoveItems(new HashSet<Guid> { a.Id });

pending.SetResult(([a, b], 2));

await load;

Check(search.Items.SequenceEqual([b]) && search.TotalCount == 1, "removal wins over superseded search response");



var server = Enumerable.Range(0, 120).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"T{i}" }).ToList();

var original = server.ToList();

var firstPage = new TaskCompletionSource<(List<MediaItem>, int)>();

var pageCalls = 0;

var paged = new AdvancedSearchModel((_, start, limit, _) => ++pageCalls == 1 ? firstPage.Task

    : Task.FromResult((server.Skip(start).Take(limit).ToList(), server.Count)), removals: new ItemRemovals());

var firstLoad = paged.RefreshNowAsync();

server.RemoveAt(0);

paged.RemoveItems(new HashSet<Guid> { original[0].Id });

firstPage.SetResult((original.Take(AdvancedSearchModel.PageSize).ToList(), original.Count));

await firstLoad;

Check(paged.Items.Count == 59 && paged.TotalCount == 119, "a deletion during the first page counts once");

await paged.LoadMoreAsync();

Check(paged.Items.SequenceEqual(original.Skip(1)) && paged.TotalCount == 119, "the next page after a deletion skips no surviving title");



List<MediaItem> Titles() => Enumerable.Range(0, 180).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"L{i}" }).ToList();



var pendingServer = Titles();

var pendingOriginal = pendingServer.ToList();

var held = new TaskCompletionSource();

var heldCalls = 0;

var heldModel = new AdvancedSearchModel(async (_, start, limit, _) =>

{

    if (++heldCalls == 2) await held.Task;

    return (pendingServer.Skip(start).Take(limit).ToList(), pendingServer.Count);

}, removals: new ItemRemovals());

await heldModel.RefreshNowAsync();

var more = heldModel.LoadMoreAsync();

pendingServer.RemoveAt(0);

heldModel.RemoveItems(new HashSet<Guid> { pendingOriginal[0].Id });

held.SetResult();

await more;

await PageToEnd(heldModel);

Check(heldModel.Items.SequenceEqual(pendingOriginal.Skip(1)) && heldModel.TotalCount == 179,

    "a deletion while a later page loads leaves no survivor out");



var shiftedServer = Titles();

var shiftedOriginal = shiftedServer.ToList();

var shifted = new AdvancedSearchModel((_, start, limit, _) => Task.FromResult((shiftedServer.Skip(start).Take(limit).ToList(), shiftedServer.Count)),

    removals: new ItemRemovals());

await shifted.RefreshNowAsync();

shiftedServer.RemoveAt(0);

await shifted.LoadMoreAsync();

shifted.RemoveItems(new HashSet<Guid> { shiftedOriginal[0].Id });

await Task.Yield();

Check(shifted.Items.SequenceEqual(shiftedOriginal.Skip(1).Take(120)) && shifted.TotalCount == 179,

    "a deletion reported after a shifted page arrives brings back the title it skipped");



async Task PageToEnd(AdvancedSearchModel model)

{

    for (var i = 0; i < 10 && model.Items.Count < model.TotalCount; i++) await model.LoadMoreAsync();

}



var racedServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"R{i}" }).ToList();

var racedOriginal = racedServer.ToList();

var racedGates = new Dictionary<int, TaskCompletionSource>();

var racedCalls = 0;

var raced = new AdvancedSearchModel(async (_, start, limit, _) =>

{

    if (racedGates.TryGetValue(++racedCalls, out var gate)) await gate.Task;

    return (racedServer.Skip(start).Take(limit).ToList(), racedServer.Count);

}, removals: new ItemRemovals());

await raced.RefreshNowAsync();

racedServer.RemoveAt(0);

await raced.LoadMoreAsync();

racedGates[3] = new TaskCompletionSource();

raced.RemoveItems(new HashSet<Guid> { racedOriginal[0].Id });

var racedPage = raced.LoadMoreAsync();

racedGates[3].SetResult();

await racedPage;

await PageToEnd(raced);

Check(raced.Items.SequenceEqual(racedOriginal.Skip(1)) && raced.TotalCount == 239, "paging during a delayed resync leaves no survivor out");



var twiceServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"W{i}" }).ToList();

var twiceOriginal = twiceServer.ToList();

var twiceGates = new Dictionary<int, TaskCompletionSource>();

var twiceCalls = 0;

var twice = new AdvancedSearchModel(async (_, start, limit, _) =>

{

    if (twiceGates.TryGetValue(++twiceCalls, out var gate)) await gate.Task;

    return (twiceServer.Skip(start).Take(limit).ToList(), twiceServer.Count);

}, removals: new ItemRemovals());

await twice.RefreshNowAsync();

twiceServer.RemoveRange(0, 2);

await twice.LoadMoreAsync();

twiceGates[3] = new TaskCompletionSource();

twice.RemoveItems(new HashSet<Guid> { twiceOriginal[0].Id });

twice.RemoveItems(new HashSet<Guid> { twiceOriginal[1].Id });

twiceGates[3].SetResult();

await PageToEnd(twice);

Check(twice.Items.SequenceEqual(twiceOriginal.Skip(2)) && twice.TotalCount == 238, "two deletions during one resync leave no survivor out");



var lateServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"Z{i}" }).ToList();

var lateOriginal = lateServer.ToList();

var late = new AdvancedSearchModel((_, start, limit, _) => Task.FromResult((lateServer.Skip(start).Take(limit).ToList(), lateServer.Count)),

    removals: new ItemRemovals());

await late.RefreshNowAsync();

lateServer.RemoveAt(0);

await late.LoadMoreAsync();

await late.LoadMoreAsync();

late.RemoveItems(new HashSet<Guid> { lateOriginal[0].Id });

await PageToEnd(late);

Check(late.Items.SequenceEqual(lateOriginal.Skip(1)) && late.TotalCount == 239, "a deletion reported after two shifted pages leaves no survivor out");



var movedServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"M{i}" }).ToList();

var movedOriginal = movedServer.ToList();

var movedGates = new Dictionary<int, TaskCompletionSource>();

var movedCalls = 0;

var moved = new AdvancedSearchModel(async (_, start, limit, _) =>

{

    if (movedGates.TryGetValue(++movedCalls, out var gate)) await gate.Task;

    return (movedServer.Skip(start).Take(limit).ToList(), movedServer.Count);

}, removals: new ItemRemovals());

await moved.RefreshNowAsync();

movedServer.RemoveAt(0);

await moved.LoadMoreAsync();

movedGates[3] = new TaskCompletionSource();

moved.RemoveItems(new HashSet<Guid> { movedOriginal[0].Id });

movedServer.Remove(movedOriginal[61]);

moved.RemoveItems(new HashSet<Guid> { movedOriginal[61].Id });

movedGates[3].SetResult();

await PageToEnd(moved);

Check(moved.Items.SequenceEqual(movedOriginal.Where((_, i) => i != 0 && i != 61)) && moved.TotalCount == 238,

    "a deletion during a resync starts it again");



var failedServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"F{i}" }).ToList();

var failedOriginal = failedServer.ToList();

var failedCalls = 0;

var failed = new AdvancedSearchModel((_, start, limit, _) => ++failedCalls == 3

    ? Task.FromException<(List<MediaItem>, int)>(new HttpRequestException("offline"))

    : Task.FromResult((failedServer.Skip(start).Take(limit).ToList(), failedServer.Count)), removals: new ItemRemovals());

await failed.RefreshNowAsync();

failedServer.RemoveAt(0);

await failed.LoadMoreAsync();

failed.RemoveItems(new HashSet<Guid> { failedOriginal[0].Id });

await PageToEnd(failed);

Check(failed.Items.SequenceEqual(failedOriginal.Skip(1)) && failed.TotalCount == 239, "a failed resync is retried by the next page load");



var splitServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"S{i}" }).ToList();

var splitOriginal = splitServer.ToList();

var split = new AdvancedSearchModel((_, start, limit, _) => Task.FromResult((splitServer.Skip(start).Take(limit).ToList(), splitServer.Count)),

    removals: new ItemRemovals());

await split.RefreshNowAsync();

splitServer.RemoveRange(0, 2);

await split.LoadMoreAsync();

split.RemoveItems(new HashSet<Guid> { splitOriginal[0].Id });

split.RemoveItems(new HashSet<Guid> { splitOriginal[1].Id });

await PageToEnd(split);

Check(split.Items.SequenceEqual(splitOriginal.Skip(2)) && split.TotalCount == 238, "separately reported deletions leave no survivor out");



var lastServer = Enumerable.Range(0, 120).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"E{i}" }).ToList();

var lastOriginal = lastServer.ToList();

var lastCalls = 0;

var last = new AdvancedSearchModel((_, start, limit, _) => ++lastCalls == 3

    ? Task.FromException<(List<MediaItem>, int)>(new HttpRequestException("offline"))

    : Task.FromResult((lastServer.Skip(start).Take(limit).ToList(), lastServer.Count)), removals: new ItemRemovals());

await last.RefreshNowAsync();

lastServer.RemoveAt(0);

await last.LoadMoreAsync();

last.RemoveItems(new HashSet<Guid> { lastOriginal[0].Id });

Check(last.HasMore, "a failed resync after the final page keeps paging open");

await last.LoadMoreAsync();

Check(last.Items.SequenceEqual(lastOriginal.Skip(1)) && last.TotalCount == 119 && !last.HasMore, "a failed resync after the final page succeeds on the next load");



var owedServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"O{i}" }).ToList();
var owedOriginal = owedServer.ToList();
var owedCalls = 0;
var refreshFails = new TaskCompletionSource<(List<MediaItem>, int)>();
var owed = new AdvancedSearchModel((_, start, limit, _) => ++owedCalls == 3 ? refreshFails.Task
    : Task.FromResult((owedServer.Skip(start).Take(limit).ToList(), owedServer.Count)), removals: new ItemRemovals());
await owed.RefreshNowAsync();
owedServer.RemoveAt(0);
await owed.LoadMoreAsync();
var failingRefresh = owed.RefreshNowAsync();
owed.RemoveItems(new HashSet<Guid> { owedOriginal[0].Id });
refreshFails.SetException(new HttpRequestException("offline"));
await failingRefresh;
await PageToEnd(owed);
Check(owed.Items.SequenceEqual(owedOriginal.Skip(1)) && owed.TotalCount == 239, "a resync skipped during a failed refresh stays owed");

var supersededServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"P{i}" }).ToList();
var supersededOriginal = supersededServer.ToList();
var supersededCalls = 0;
var heldResync = new TaskCompletionSource<(List<MediaItem>, int)>();
var supersededRefresh = new TaskCompletionSource<(List<MediaItem>, int)>();
var superseded = new AdvancedSearchModel((_, start, limit, _) => ++supersededCalls switch
{
    3 => heldResync.Task,
    4 => supersededRefresh.Task,
    _ => Task.FromResult((supersededServer.Skip(start).Take(limit).ToList(), supersededServer.Count)),
}, removals: new ItemRemovals());
await superseded.RefreshNowAsync();
supersededServer.RemoveAt(0);
await superseded.LoadMoreAsync();
superseded.RemoveItems(new HashSet<Guid> { supersededOriginal[0].Id });
var replacingRefresh = superseded.RefreshNowAsync();
supersededRefresh.SetException(new HttpRequestException("offline"));
await replacingRefresh;
heldResync.SetResult((supersededServer.Take(120).ToList(), supersededServer.Count));
await PageToEnd(superseded);
Check(superseded.Items.SequenceEqual(supersededOriginal.Skip(1)) && superseded.TotalCount == 239, "a resync replaced by a refresh that fails stays owed");

var burstServer = Enumerable.Range(0, 240).Select(i => new MediaItem { Id = Guid.NewGuid(), Name = $"B{i}" }).ToList();
var burstOriginal = burstServer.ToList();
var burstCalls = 0;
var burstHeld = new TaskCompletionSource();
var burst = new AdvancedSearchModel(async (_, start, limit, _) =>
{
    if (++burstCalls == 3) await burstHeld.Task;
    return (burstServer.Skip(start).Take(limit).ToList(), burstServer.Count);
}, removals: new ItemRemovals());
var resynced = 0;
burst.Resynced += (_, _) => resynced++;
await burst.RefreshNowAsync();
await burst.LoadMoreAsync();
for (var i = 0; i < 30; i++)
{
    burstServer.Remove(burstOriginal[i]);
    burst.RemoveItems(new HashSet<Guid> { burstOriginal[i].Id });
}
burstHeld.SetResult();
await Task.Yield();
Check(burstCalls == 4 && resynced == 1 && burst.Items.SequenceEqual(burstOriginal.Skip(30).Take(120)),
    "a burst of deletions costs one resync and one rerun, then reports the quiet resync once");

var nextTitles = new TaskCompletionSource<List<MediaItem>>();

var feedRemovals = new ItemRemovals();

var feed = new RecentlyAddedFeed([RecentList.Movies], (_, _, _) => Task.FromResult((new List<MediaItem> { a }, 3)),

    (_, _) => Task.FromResult(new List<MediaItem> { a, b, c }), (_, _) => nextTitles.Task, feedRemovals);

await feed.NextAsync(1, CancellationToken.None);

var next = feed.NextAsync(2, CancellationToken.None);

feed.RemoveItems(new HashSet<Guid> { a.Id, b.Id });

nextTitles.SetResult([b, c]);

var inserted = await next;

Check(feed.Shown.SequenceEqual([c]) && inserted.Count == 1 && inserted[0].Index == 0 && feed.Total == 1, "feed removal guards pending titles and insertion indexes");

Console.WriteLine($"App ports: {checks} checks passed");
