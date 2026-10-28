using Jellyfin.Sdk.Generated.Models;
using System.Collections.Specialized;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Search;

L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

static MediaItem Item(int i) => new() { Id = Guid.NewGuid(), Name = "Title " + i };

// --- Filters ---------------------------------------------------------------------------------
var d = AdvancedSearchFilters.Default;
Check(d.IsDefault && d.ActiveCount == 0 && !d.Tokens().Any(), "defaults: nothing active, Reset has nothing to do");
Check(!(d with { Sort = SearchSort.Rating }).IsDefault && (d with { Sort = SearchSort.Rating }).ActiveCount == 0,
    "sort is reset by Reset but is not an active filter");
Check((d with { Types = [] }) is { ActiveCount: 0, IsDefault: true }, "no type ticked is the default, not an empty token");

// The design's example (05, 5a): five active filters, shown as five tokens.
var example = (d with
{
    Types = [SearchType.Movies, SearchType.Shows],
    Genres = ["Drama", "Thriller"],
    Watched = WatchedFilter.Unplayed,
    MinRating = 7,
}).WithYears(1995, 2012);
var tokens = example.Tokens().Select(t => t.Label).ToArray();
Check(example.ActiveCount == 5, "design example has five active filters");
Check(tokens.SequenceEqual(["Movies, Shows", "Drama, Thriller", "1995–2012", "Unplayed", "★ 7.0+"]),
    "token labels match the design: " + string.Join(" | ", tokens));
Check(example.HintLabel(FilterKind.Rating) == "Rating ★ 7.0+", "empty-state hint names the rating filter");

Check(d.WithYears(AdvancedSearchFilters.YearFloor, AdvancedSearchFilters.YearCeiling).IsDefault, "year slider ends mean unbounded");
Check(d.WithYears(2012, 1995) is { MinYear: 1995, MaxYear: 2012 }, "year bounds are ordered");
Check(d.WithYears(1995, AdvancedSearchFilters.YearCeiling).Label(FilterKind.Years) == "From 1995", "open upper bound reads From");
Check(d with { Types = [SearchType.Episodes, SearchType.Shows, SearchType.Movies] } is { ActiveCount: 0 }, "type order does not matter");

var person = AdvancedSearchFilters.ForPerson(Guid.NewGuid(), "Ada Moreno");
Check(person.ActiveCount == 1 && person.IsActive(FilterKind.People) && person.Label(FilterKind.People) == "Ada Moreno",
    "opening from a person's page fills in only the person");

var query = example.ToQuery("  night ");
Check(query.Term == "night", "typed text combines with the filters, trimmed");
Check(query.ItemTypes.SequenceEqual([BaseItemKind.Movie, BaseItemKind.Series]), "types map to item kinds");
Check(query.Genres.SequenceEqual(["Drama", "Thriller"]) && query.MinYear == 1995 && query.MaxYear == 2012, "genres and years pass through");
Check(query.Watched == ItemFilter.IsUnplayed && query.MinRating == 7, "watched and rating map to server filters");
Check(query.Sort == ItemSortBy.SortName && query.Order == SortOrder.Ascending, "default sort is name A-Z");
Check((d with { Sort = SearchSort.ReleaseDate, Descending = true }).ToQuery(null) is { Sort: var s, Order: SortOrder.Descending } && s == ItemSortBy.PremiereDate,
    "release date sorts by premiere date");
Check((d with { Types = [] }).ToQuery(null).ItemTypes.Count == AdvancedSearchFilters.DefaultTypes.Count, "no type ticked searches the default kinds");
Check((d with { Types = [SearchType.Collections] }).ToQuery(null).ItemTypes.SequenceEqual([BaseItemKind.BoxSet]), "collections map to box sets");
Check(example.Without(FilterKind.Rating) is { MinRating: 0, ActiveCount: 4 }, "removing a token clears only that filter");

// --- Keyed diff ------------------------------------------------------------------------------
// A refresh hands back new copies of mostly the same titles. Titles that stay must not be removed
// and re-added (their cards would blank and decode again); only real changes may notify, and only
// as Add, Remove or Reset, which an ItemsRepeater tracks (it mishandles Move and Replace).
(KeyedList<Row, string> List, List<NotifyCollectionChangedEventArgs> Changes) Track(params Row[] rows)
{
    var list = new KeyedList<Row, string>(r => r.Id);
    foreach (var row in rows) list.Add(row);
    var changes = new List<NotifyCollectionChangedEventArgs>();
    list.CollectionChanged += (_, e) => changes.Add(e);
    return (list, changes);
}
static Row[] Rows(string ids, int version = 0) => ids.Select(c => new Row(c.ToString(), version)).ToArray();
static string Ids(IEnumerable<Row> rows) => string.Concat(rows.Select(r => r.Id));
static int Count(List<NotifyCollectionChangedEventArgs> changes, NotifyCollectionChangedAction action) => changes.Count(c => c.Action == action);
static bool RepeaterSafe(List<NotifyCollectionChangedEventArgs> changes)
    => changes.All(c => c.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset);
static string Replay(IEnumerable<Row> before, List<NotifyCollectionChangedEventArgs> changes)
{
    var view = before.Select(r => r.Id).ToList();
    foreach (var c in changes)
    {
        if (c.Action == NotifyCollectionChangedAction.Reset) view.Clear();
        else if (c.Action == NotifyCollectionChangedAction.Remove) view.RemoveRange(c.OldStartingIndex, c.OldItems!.Count);
        else if (c.Action == NotifyCollectionChangedAction.Add) view.InsertRange(c.NewStartingIndex, c.NewItems!.Cast<Row>().Select(r => r.Id));
    }
    return string.Concat(view);
}

var (same, sameChanges) = Track(Rows("abcd"));
var newer = Rows("abcd", 1);
same.Sync(newer);
Check(sameChanges.Count == 0 && same.Zip(newer).All(p => ReferenceEquals(p.First, p.Second)),
    "same titles in the same order take their newer copies with no collection change");

var (edited, editedChanges) = Track(Rows("abcd"));
edited.Sync(Rows("acED", 1));
Check(Ids(edited) == "acED" && Count(editedChanges, NotifyCollectionChangedAction.Remove) == 2
      && Count(editedChanges, NotifyCollectionChangedAction.Add) == 2 && editedChanges.Count == 4,
    $"titles that left are removed and new ones inserted, and nothing else changes ({Ids(edited)}, {editedChanges.Count} changes)");

var movedBefore = Rows("abcde");
var (moved, movedChanges) = Track(movedBefore);
moved.Sync(Rows("bcdea", 1));
Check(Ids(moved) == "bcdea" && movedChanges.Count == 2 && RepeaterSafe(movedChanges)
      && movedChanges[0] is { Action: NotifyCollectionChangedAction.Remove, OldStartingIndex: 0 }
      && movedChanges[1] is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 4 } && Replay(movedBefore, movedChanges) == "bcdea",
    "one title changing place is taken out and inserted again, not a Move and not a shift of the others");

var swappedBefore = Rows("abcdef");
var (swapped, swappedChanges) = Track(swappedBefore);
swapped.Sync(Rows("afcdeb", 1));
Check(Ids(swapped) == "afcdeb" && RepeaterSafe(swappedChanges) && swappedChanges.Count == 4 && Replay(swappedBefore, swappedChanges) == "afcdeb",
    $"two titles trading places are two removals and two insertions ({swappedChanges.Count})");

var (replaced, replacedChanges) = Track(Rows("abc"));
replaced.Sync(Rows("xyz"));
Check(Ids(replaced) == "xyz" && Count(replacedChanges, NotifyCollectionChangedAction.Reset) == 1
      && Count(replacedChanges, NotifyCollectionChangedAction.Remove) == 0,
    "with nothing to keep, one reset replaces the list");

var (doubled, _) = Track(Rows("abca"));
doubled.Sync(Rows("bab", 1));
Check(Ids(doubled) == "bab" && doubled.All(r => r.Version == 1), "duplicate keys on either side still end in the new list");

var (emptied, emptiedChanges) = Track(Rows("ab"));
emptied.Sync([]);
var (filled, _) = Track();
filled.Sync(Rows("ab"));
Check(emptied.Count == 0 && emptiedChanges.Count == 1 && Ids(filled) == "ab", "an empty answer clears the list and a first answer fills it");

var random = new Random(20261020);
var fuzzFailures = 0;
for (var round = 0; round < 500; round++)
{
    var pool = Enumerable.Range(0, 40).Select(i => ((char)('!' + i)).ToString()).ToArray();
    var before = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 25)).Select(id => new Row(id, 0)).ToArray();
    var after = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 25)).Select(id => new Row(id, 1)).ToArray();
    var (list, changes) = Track(before);
    list.Sync(after);
    var kept = before.Where(b => after.Any(a => a.Id == b.Id)).Select(b => Array.FindIndex(after, a => a.Id == b.Id)).ToArray();
    var longest = new int[kept.Length];
    for (var i = 0; i < kept.Length; i++)
        longest[i] = 1 + Enumerable.Range(0, i).Where(j => kept[j] < kept[i]).Select(j => longest[j]).DefaultIfEmpty(0).Max();
    var fewestRelocations = kept.Length - longest.DefaultIfEmpty(0).Max();
    var keptIds = kept.Select(i => after[i].Id).ToHashSet();
    var removedKept = changes.Where(c => c.Action == NotifyCollectionChangedAction.Remove)
        .SelectMany(c => c.OldItems!.Cast<Row>()).Where(r => keptIds.Contains(r.Id)).Select(r => r.Id).ToList();
    var addedKept = changes.Where(c => c.Action == NotifyCollectionChangedAction.Add)
        .SelectMany(c => c.NewItems!.Cast<Row>()).Where(r => keptIds.Contains(r.Id)).Select(r => r.Id).ToList();
    if (!list.SequenceEqual(after) || !RepeaterSafe(changes) || Replay(before, changes) != Ids(after)
        || removedKept.Count > fewestRelocations || removedKept.Distinct().Count() != removedKept.Count
        || !removedKept.Order().SequenceEqual(addedKept.Order())
        || (kept.Length > 0 && Count(changes, NotifyCollectionChangedAction.Reset) > 0))
        fuzzFailures++;
}
Check(fuzzFailures == 0, $"500 random answers: right order and copies, notifications replay to the same list, only Add/Remove/Reset, kept titles relocated at most once and no more than needed ({fuzzFailures} failed)");

// --- Watched and favourite overrides ---------------------------------------------------------
// The user's change shows at once. Each field of each title is tracked on its own: its changes
// reach the server in the order they were made, a refusal is undone only while it is the latest
// change, and once a field has settled the next answer from the server replaces it.
var film = new MediaItem { Id = Guid.NewGuid(), Name = "Glass Season" };
async Task CheckOverrides()
{
    var overrides = new UserDataOverrides();
    await overrides.ChangeAsync(film, UserDataField.Favorite, true, () => Task.FromResult(true));
    Check(overrides.Of(film) == (false, true), "a change shows at once and stays once the server accepts it");
    var favouriteOffElsewhere = new MediaItem { Id = film.Id, Name = film.Name, IsFavorite = false };
    var watchedSent = new TaskCompletionSource<bool>();
    var watching = overrides.ChangeAsync(film, UserDataField.Played, true, () => watchedSent.Task);
    overrides.Reconcile(overrides.Mark(), [favouriteOffElsewhere]);
    Check(overrides.Of(favouriteOffElsewhere) == (true, false),
        "a settled favourite gives way to the server's answer while a watched change for the same title is still pending");
    var sentBeforeSettled = overrides.Mark();
    watchedSent.SetResult(true);
    await watching;
    overrides.Reconcile(sentBeforeSettled, [favouriteOffElsewhere]);
    Check(overrides.Of(favouriteOffElsewhere).Played, "an answer to a query sent before the change settled does not replace it");
    overrides.Reconcile(overrides.Mark(), [new MediaItem { Id = Guid.NewGuid(), Name = "Other" }]);
    Check(overrides.Of(favouriteOffElsewhere).Played, "an answer without the title leaves its change alone");
    var serverNow = new MediaItem { Id = film.Id, Name = film.Name, Played = false, IsFavorite = false };
    overrides.Reconcile(overrides.Mark(), [serverNow]);
    Check(overrides.Of(serverNow) == (false, false), "an answer sent after the change settled replaces it with the server's state");

    var show = new MediaItem { Id = Guid.NewGuid(), Name = "Kestrel" };
    var queue = new UserDataOverrides();
    var started = new List<string>();
    var answers = new Dictionary<string, TaskCompletionSource<bool>> { ["A"] = new(), ["B"] = new(), ["C"] = new() };
    Func<Task<bool>> Send(string name) => () => { started.Add(name); return answers[name].Task; };
    var a = queue.ChangeAsync(show, UserDataField.Played, true, Send("A"));
    var b = queue.ChangeAsync(show, UserDataField.Played, false, Send("B"));
    var c = queue.ChangeAsync(show, UserDataField.Played, true, Send("C"));
    Check(started.SequenceEqual(["A"]) && queue.Of(show).Played, "same-field changes go to the server one at a time; the newest choice shows");
    answers["A"].SetResult(false);
    await a;
    await UiThread.Settle();
    Check(queue.Of(show).Played && started.SequenceEqual(["A", "B"]), "an older change that fails does not undo a newer one");
    answers["B"].SetResult(true);
    await UiThread.Settle();
    answers["C"].SetResult(true);
    await Task.WhenAll(b, c);
    Check(queue.Of(show).Played && started.SequenceEqual(["A", "B", "C"]), "the server receives the changes in the order they were made and ends on the last");

    var latest = queue.ChangeAsync(show, UserDataField.Played, false, () => Task.FromResult(false));
    await latest;
    Check(queue.Of(show).Played, "a refused latest change returns to the last value the server accepted");
    var throwing = await queue.ChangeAsync(show, UserDataField.Favorite, true, () => throw new HttpRequestException("down"));
    Check(!throwing && !queue.Of(show).Favorite, "a request that throws counts as refused");

    var olderFails = new TaskCompletionSource<bool>();
    var newerFails = new TaskCompletionSource<bool>();
    var first = queue.ChangeAsync(show, UserDataField.Favorite, true, () => olderFails.Task);
    var second = queue.ChangeAsync(show, UserDataField.Favorite, true, () => newerFails.Task);
    var other = queue.ChangeAsync(show, UserDataField.Played, false, () => Task.FromResult(true));
    await other;
    Check(queue.Of(show) == (false, true), "fields do not wait for each other");
    olderFails.SetResult(false);
    newerFails.SetResult(false);
    await Task.WhenAll(first, second);
    Check(!queue.Of(show).Favorite, "when every pending change fails, the field returns to the server's value");
}

// --- Model -----------------------------------------------------------------------------------
// The model checks run on a single-threaded synchronization context, like the UI thread, and on
// a manual clock, so the 150 ms debounce is stepped rather than slept.
await UiThread.RunAsync(async () =>
{
    await CheckOverrides();

    var uiThread = Environment.CurrentManagedThreadId;
    var clock = new ManualClock();
    var calls = new List<(AdvancedSearchQuery Query, int Start, int Limit)>();
    var offThread = false;
    var model = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        calls.Add((q, start, limit));
        await Task.Yield();
        var total = 130;
        var count = Math.Max(0, Math.Min(limit, total - start));
        return (Enumerable.Range(start, count).Select(Item).ToList(), total);
    }, clock);
    model.Items.CollectionChanged += (_, _) => offThread |= Environment.CurrentManagedThreadId != uiThread;
    var notified = new List<string?>();
    model.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

    model.Filters = model.Filters with { Genres = ["Drama"] };
    model.Filters = model.Filters with { Watched = WatchedFilter.Unplayed };
    model.Term = "night";
    Check(model.IsUpdating && calls.Count == 0, "a change marks the model updating at once and waits for the debounce");
    clock.Advance(TimeSpan.FromMilliseconds(149));
    await UiThread.Settle();
    Check(calls.Count == 0, "nothing is sent before 150 ms");
    clock.Advance(TimeSpan.FromMilliseconds(1));
    await UiThread.Settle();
    Check(calls.Count == 1, $"three quick changes make one request after the debounce ({calls.Count})");
    Check(calls[0].Query.Term == "night" && calls[0].Query.Genres.Count == 1 && calls[0].Query.Watched == ItemFilter.IsUnplayed,
        "the request carries the latest filters and text");
    Check(model.Items.Count == AdvancedSearchModel.PageSize && model.TotalCount == 130 && !model.IsUpdating && model.HasResults,
        "the first page lands");
    Check(notified.Contains(nameof(AdvancedSearchModel.IsEmpty)) && notified.Contains(nameof(AdvancedSearchModel.IsUpdating)),
        "IsUpdating and IsEmpty raise change notifications");
    await model.LoadMoreAsync();
    await model.LoadMoreAsync();
    await model.LoadMoreAsync();
    Check(model.Items.Count == 130, $"scrolling appends pages up to the total ({model.Items.Count})");
    Check(!offThread, "Items only changes on the UI thread");

    // A newer change cancels the request in flight; its late answer is ignored.
    var gate = new TaskCompletionSource();
    var seenTokens = new List<CancellationToken>();
    var stale = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        seenTokens.Add(ct);
        if (q.Term is null) await gate.Task;
        return (new List<MediaItem> { Item(q.Term is null ? 1 : 99) }, 1);
    }, clock);
    var first = stale.RefreshNowAsync();
    stale.Term = "newer";
    Check(seenTokens[0].IsCancellationRequested, "a newer change cancels the request in flight");
    clock.Advance(AdvancedSearchModel.Debounce);
    await UiThread.Settle();
    gate.SetResult();
    await first;
    Check(stale.TotalCount == 1 && stale.Items.Single().Name == "Title 99", "a late answer to an older request is ignored");

    // Failures (server down, timeout) end the update instead of leaving the grid dimmed.
    var failing = new AdvancedSearchModel((q, start, limit, ct) => throw new TaskCanceledException("timeout"), clock);
    await failing.RefreshNowAsync();
    Check(!failing.IsUpdating && failing.HasFailed, "a timed-out request ends the update and reports the failure");
    await failing.LoadMoreAsync();
    Check(true, "a failed page does not throw into the view");

    // Empty state: the filter whose removal frees the most titles is named, with its count,
    // and the empty answer lands together with its hint.
    var hintGate = new TaskCompletionSource();
    var empty = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        var total = q.MinRating is null ? 41 : q.Watched is null ? 12 : 0;
        if (limit == 1) await hintGate.Task;
        return (new List<MediaItem>(), total);
    }, clock);
    empty.Filters = example with { MinRating = 9 };
    clock.Advance(AdvancedSearchModel.Debounce);
    await UiThread.Settle();
    Check(empty.IsUpdating && !empty.HasResults, "the empty answer waits for its hint");
    hintGate.SetResult();
    await UiThread.Settle();
    Check(empty.IsEmpty && empty.Hint is { Kind: FilterKind.Rating, Count: 41 } && empty.Hint.Label == "Rating ★ 9.0+",
        $"empty result names the most helpful filter ({empty.Hint})");

    // A stale hint must not overwrite a newer answer.
    var slowHint = new TaskCompletionSource();
    var race = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        if (q.Term == "old")
        {
            if (limit == 1) await slowHint.Task;
            return (new List<MediaItem>(), q.MinRating is null ? 7 : 0);
        }
        return (new List<MediaItem> { Item(2) }, 1);
    }, clock);
    race.Filters = race.Filters with { MinRating = 9 };
    race.Term = "old";
    clock.Advance(AdvancedSearchModel.Debounce);
    await UiThread.Settle();
    race.Term = "new";
    clock.Advance(AdvancedSearchModel.Debounce);
    await UiThread.Settle();
    slowHint.SetResult();
    await UiThread.Settle();
    Check(race.TotalCount == 1 && race.Hint is null && !race.IsEmpty, "a stale hint does not overwrite a newer answer");

    // Refresh (F5) re-runs the query for everything loaded and applies only what changed.
    var server = Enumerable.Range(0, 150).Select(i => (Id: Guid.NewGuid(), Name: "Title " + i, Played: false)).ToList();
    var reloadGate = (TaskCompletionSource?)null;
    var reloadFails = false;
    var reloadCalls = new List<(int Start, int Limit)>();
    var reload = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        reloadCalls.Add((start, limit));
        await Task.Yield();
        if (reloadGate is { } g) await g.Task;
        if (reloadFails) throw new HttpRequestException("server error");
        var page = server.Skip(start).Take(limit).Select(t => new MediaItem { Id = t.Id, Name = t.Name, Played = t.Played }).ToList();
        return (page, server.Count);
    }, clock);
    await reload.RefreshNowAsync();
    await reload.LoadMoreAsync();
    var loaded = reload.Items.ToList();
    var reloadChanges = new List<NotifyCollectionChangedEventArgs>();
    var answers = 0;
    reload.Items.CollectionChanged += (_, e) => reloadChanges.Add(e);
    reload.Answered += (_, _) => answers++;

    server[5] = server[5] with { Played = true };
    server.RemoveAt(10);
    server.Insert(0, (Guid.NewGuid(), "Newly added", false));
    reloadGate = new TaskCompletionSource();
    var reloading = reload.ReloadAsync();
    Check(reload.IsUpdating && reload.IsReloading, "a refresh shows updating but marks itself a reload, so the grid is not dimmed");
    reloadGate.SetResult();
    reloadGate = null;
    await reloading;
    Check(reloadCalls[^1] == (0, 120), $"a refresh asks again for everything loaded so far ({reloadCalls[^1]})");
    Check(!reload.IsUpdating && !reload.IsReloading && !reload.HasFailed && reload.Items.Count == 120 && reload.TotalCount == 150,
        "the refreshed answer lands and the loaded length stays");
    Check(reload.Items[0].Name == "Newly added" && reload.Items.All(i => i.Id != loaded[10].Id) && reload.Items.Single(i => i.Id == loaded[5].Id).Played,
        "a new title appears, a removed one leaves, and a changed one carries its new state");
    Check(Count(reloadChanges, NotifyCollectionChangedAction.Add) == 1 && Count(reloadChanges, NotifyCollectionChangedAction.Remove) == 1
          && reloadChanges.Count == 2 && answers == 1,
        $"only the new and the removed title notify, and the answer is announced so kept cards refresh ({reloadChanges.Count} changes)");

    var beforeFailure = reload.Items.ToList();
    reloadFails = true;
    await reload.ReloadAsync();
    Check(reload.HasFailed && !reload.IsUpdating && !reload.IsReloading && reload.Items.SequenceEqual(beforeFailure),
        "a failed refresh keeps the results as they were");
    reloadFails = false;

    reloadGate = new TaskCompletionSource();
    _ = reload.ReloadAsync();
    reload.Term = "typed";
    Check(reload.IsUpdating && !reload.IsReloading, "a filter or text change during a refresh takes over and dims as before");
    reloadGate.SetResult();
    reloadGate = null;
    clock.Advance(AdvancedSearchModel.Debounce);
    await UiThread.Settle();
    Check(!reload.IsUpdating && reload.Items.Count == AdvancedSearchModel.PageSize, "the change's own first page lands");
    await reload.ReloadAsync();
    Check(reloadCalls[^1] == (0, AdvancedSearchModel.PageSize), "a refresh with one page loaded asks for one page");

    var answersBefore = answers;
    var changesBefore = reloadChanges.Count;
    await reload.ReloadAsync();
    Check(answers == answersBefore + 1 && reloadChanges.Count == changesBefore,
        "an answer that changes nothing is still announced, so the view can load more to fill the viewport");

    // An answer replaces the page's overrides it already carries.
    var pageOverrides = new UserDataOverrides();
    var watchedOnTv = false;
    var single = new AdvancedSearchModel(async (q, start, limit, ct) =>
    {
        await Task.Yield();
        return (new List<MediaItem> { new() { Id = film.Id, Name = film.Name, Played = watchedOnTv } }, 1);
    }, clock, pageOverrides);
    await single.RefreshNowAsync();
    await pageOverrides.ChangeAsync(single.Items[0], UserDataField.Favorite, true, () => Task.FromResult(true));
    watchedOnTv = true;
    await single.ReloadAsync();
    Check(pageOverrides.Of(single.Items[0]) == (true, false),
        "after a favourite set here and Watched set on another device, Refresh shows the server's state");
});

Console.WriteLine($"{checks} checks passed");
return 0;

/// <summary>A row with a key and a version, standing in for a title and its newer copy.</summary>
sealed record Row(string Id, int Version);

/// <summary>A TimeProvider whose timers fire only when the test advances it.</summary>
sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, _now + dueTime);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        List<ManualTimer> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }
        foreach (var t in due) t.Fire();
    }

    private void Remove(ManualTimer t) { lock (_timers) _timers.Remove(t); }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = clock._now + dueTime; return true; }
        public void Dispose() => clock.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>A single-threaded synchronization context standing in for the UI thread.</summary>
static class UiThread
{
    private static readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue = [];

    public static Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context());
            body().ContinueWith(t =>
            {
                if (t.IsFaulted) done.SetException(t.Exception!.InnerExceptions);
                else done.SetResult();
                Queue.CompleteAdding();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            foreach (var (callback, state) in Queue.GetConsumingEnumerable()) callback(state);
        });
        thread.Start();
        return done.Task;
    }

    /// <summary>Lets queued continuations run before the test looks at the state.</summary>
    public static async Task Settle()
    {
        for (var i = 0; i < 20; i++) await Task.Yield();
    }

    private sealed class Context : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
