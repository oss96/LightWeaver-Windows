using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.People;
using LightWeaver.Search;

// Store failures are logged under AppPaths; keep those logs out of an app's real profile.
var appData = Path.Combine(Path.GetTempPath(), "lw-searchperson-tests-" + Guid.NewGuid().ToString("N"));
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

MediaItem Title(string name, BaseItemDto_Type type = BaseItemDto_Type.Movie, int? year = null, params PersonEntry[] people) => new()
{
    Id = Guid.NewGuid(), Name = name, Type = type, ProductionYear = year, People = people,
};
SearchGroup Group(SearchCategory category, int count, int total) => new(category, [.. Enumerable.Range(0, count).Select(i => Title($"{category} {i}"))], total);

// --- Grouping (design 04, 4b) --------------------------------------------------------------------
var result = QuickSearch.Build("har", [Group(SearchCategory.Episodes, 7, 14), Group(SearchCategory.Movies, 6, 6), Group(SearchCategory.Collections, 0, 0), Group(SearchCategory.People, 5, 5), Group(SearchCategory.Shows, 3, 3)]);
Check(result.Groups.Select(g => g.Category).SequenceEqual([SearchCategory.Movies, SearchCategory.Shows, SearchCategory.People, SearchCategory.Episodes]), "groups follow Movies, Shows, People, Collections, Episodes and empty ones are left out");
Check(result.Total == 28 && result.CountOf(SearchCategory.All) == 28, "All counts every result (" + result.Total + ")");
Check(result.CountOf(SearchCategory.Episodes) == 14 && result.CountOf(SearchCategory.Collections) == 0, "each chip carries its group's server total");
Check(result.Shown(SearchCategory.People).Single().Category == SearchCategory.People, "a chip shows only its group");
Check(result.Shown(SearchCategory.All).Count == 4, "All shows every group");
Check(result.First(SearchCategory.All)!.Name == "Movies 0", "Enter opens the first result on screen");
Check(result.First(SearchCategory.Shows)!.Name == "Shows 0", "with a chip, the first result of that group");
Check(QuickSearch.Build("x", [Group(SearchCategory.Movies, 3, 1)]).CountOf(SearchCategory.Movies) == 3, "a total below the items shown is raised to them");
Check(QuickSearch.Build("xqzt", [Group(SearchCategory.Movies, 0, 0)]).IsEmpty, "no results: the empty state");
Check(QuickSearch.Chips[0] == SearchCategory.All, "the chips start with All");
Check(QuickSearch.Debounce == TimeSpan.FromMilliseconds(150), "results follow 150 ms after the last key");
Check(QuickSearch.MoreFilters(SearchCategory.Shows).Types.SequenceEqual([SearchType.Shows]), "More filters turns the Shows chip into the Type filter");
Check(QuickSearch.MoreFilters(SearchCategory.Collections).Types.SequenceEqual([SearchType.Collections]), "More filters turns the Collections chip into the Type filter");
Check(QuickSearch.MoreFilters(SearchCategory.All) == AdvancedSearchFilters.Default, "More filters from All keeps the default filters");
Check(QuickSearch.RowFit(1872, 104, 14) == (16, 15), "1920 wide: a poster row fills the page, its 16th card clipping at the edge, not eight cards and half the page empty");
Check(QuickSearch.RowFit(1872, 220, 14) == (8, 8), "1920 wide: eight episode stills");
Check(QuickSearch.RowFit(800, 104, 14) == (7, 6), "a narrow window: seven posters, the last one clipped");
Check(QuickSearch.RowFit(118 * 3 - 14, 104, 14) == (3, 3), "a row exactly three cards wide shows three whole cards");
Check(QuickSearch.RowFit(50, 104, 14) == (1, 0), "narrower than one card: that card, clipped");
Check(QuickSearch.RowFit(0, 104, 14) == (0, 0), "before layout: no width, no fit");
bool SeeAllAt(double width, int total)
{
    var (fit, whole) = QuickSearch.RowFit(width, 104, 14);
    return QuickSearch.NeedsSeeAll(total, Math.Min(fit, total), whole);
}
Check(QuickSearch.RowFit(1880, 104, 14) == (16, 16) && !SeeAllAt(1880, 16), "16 posters that all fit whole: no See all");
Check(QuickSearch.RowFit(1872, 104, 14) == (16, 15) && SeeAllAt(1872, 16), "narrowed from 1880 to 1872: the 16th poster is cut off, so See all appears");
Check(!SeeAllAt(1880, 16), "widened back to 1880: See all goes again");
Check(SeeAllAt(1880, 17) && !SeeAllAt(2400, 17), "a group with more than the row shows offers See all until the row holds it whole");
Check(QuickSearch.KindOf(SearchCategory.Collections) == BaseItemKind.BoxSet && QuickSearch.KindOf(SearchCategory.People) is null, "collections are box sets; people are searched separately");
Check(result.IsComplete && !result.ShowsEmptyState, "a built result waits for nothing");

// --- Groups arrive one by one (design 04: the slowest request holds up only its own group) -------
var typing = QuickSearch.Start(result, "harb");
Check(typing.Term == "harb" && typing.Groups.SequenceEqual(result.Groups) && typing.Pending.Count == QuickSearch.GroupOrder.Count,
    "a new query keeps the previous groups on screen and waits for every group");
var movies = Group(SearchCategory.Movies, 2, 2);
var merged = QuickSearch.Merge(typing, movies);
Check(merged.Groups[0] == movies && merged.Groups.Skip(1).SequenceEqual(result.Groups.Skip(1)), "an answer replaces only its own group");
Check(!merged.Pending.Contains(SearchCategory.Movies) && merged.Pending.Count == QuickSearch.GroupOrder.Count - 1, "and that group stops waiting");
merged = QuickSearch.Merge(merged, Group(SearchCategory.Shows, 0, 0));
Check(merged.Groups.Select(g => g.Category).SequenceEqual([SearchCategory.Movies, SearchCategory.People, SearchCategory.Episodes]), "an empty answer removes its group and the order holds");
merged = QuickSearch.Merge(merged, Group(SearchCategory.Collections, 4, 9));
Check(merged.Groups.Select(g => g.Category).SequenceEqual([SearchCategory.Movies, SearchCategory.People, SearchCategory.Collections, SearchCategory.Episodes]), "a new group slots into the fixed order");
Check(QuickSearch.Merge(QuickSearch.Start(null, "q"), Group(SearchCategory.Movies, 3, 1)).CountOf(SearchCategory.Movies) == 3, "a merged total below the items shown is raised to them");
var nothing = QuickSearch.Merge(QuickSearch.Start(QuickSearch.Build("x", [Group(SearchCategory.Movies, 1, 1)]), "xq"), Group(SearchCategory.Movies, 0, 0));
Check(nothing.IsEmpty && !nothing.ShowsEmptyState, "no empty state while groups are still coming");
nothing = QuickSearch.GroupOrder.Aggregate(nothing, (r, c) => QuickSearch.Merge(r, Group(c, 0, 0)));
Check(nothing.ShowsEmptyState, "the empty state once every group answered empty");

var withoutEpisodes = QuickSearch.GroupOrder.Where(c => c != SearchCategory.Episodes)
    .Aggregate(QuickSearch.Start(null, "the"), (r, c) => QuickSearch.Merge(r, Group(c, 3, 30)));
Check(QuickSearch.HeaderTotal(withoutEpisodes, null) is null, "a first query shows no total while Episodes is still coming");
var everyGroup = QuickSearch.Merge(withoutEpisodes, Group(SearchCategory.Episodes, 3, 72));
Check(QuickSearch.HeaderTotal(everyGroup, null) == 192, "and the full total once it answered");
Check(QuickSearch.HeaderTotal(QuickSearch.Start(everyGroup, "they"), 192) == 192, "typing on keeps the last total until the new query's groups are all in");

var gates = QuickSearch.GroupOrder.ToDictionary(c => c, _ => new TaskCompletionSource<SearchGroup>(TaskCreationOptions.RunContinuationsAsynchronously));
var arrived = new List<SearchCategory>();
var started = new List<SearchCategory>();
var loading = QuickSearch.LoadAsync((c, _) => { lock (started) started.Add(c); return gates[c].Task; }, g => { lock (arrived) arrived.Add(g.Category); }, CancellationToken.None);
Check(started.Count == QuickSearch.GroupOrder.Count, "every group's request starts at once");
gates[SearchCategory.People].SetResult(Group(SearchCategory.People, 1, 1));
await WaitUntil(() => arrived.Count == 1);
Check(arrived.SequenceEqual([SearchCategory.People]) && !loading.IsCompleted, "a group shows as soon as its own request answers");
gates[SearchCategory.Movies].SetException(new HttpRequestException("down"));
foreach (var c in new[] { SearchCategory.Episodes, SearchCategory.Shows, SearchCategory.Collections }) gates[c].SetResult(Group(c, 1, 1));
var failure = await Record(loading);
Check(arrived.Count == QuickSearch.GroupOrder.Count && arrived.Contains(SearchCategory.Movies), "a failed group arrives empty and the others still arrive");
Check(failure is HttpRequestException, "the failure is reported once every group has answered (" + failure?.GetType().Name + ")");

using var cancel = new CancellationTokenSource();
var late = new TaskCompletionSource<SearchGroup>(TaskCreationOptions.RunContinuationsAsynchronously);
var lateArrivals = 0;
var cancelled = QuickSearch.LoadAsync((c, _) => late.Task, _ => lateArrivals++, cancel.Token);
cancel.Cancel();
late.SetResult(Group(SearchCategory.Movies, 1, 1));
Check(await Record(cancelled) is OperationCanceledException && lateArrivals == 0, "nothing arrives after the query was replaced");

// --- Recent searches (design 04, 4a) ------------------------------------------------------------
var folder = Path.Combine(Path.GetTempPath(), "lw-recent-" + Guid.NewGuid().ToString("N"));
RecentSearches.FilePath = Path.Combine(folder, "recent-searches.json");
try
{
    Check(RecentSearches.Load("p1").Count == 0, "no recent searches at first");
    RecentSearches.Add("p1", "harbor lights");
    RecentSearches.Add("p1", "tomas adler");
    RecentSearches.Add("p1", "  Harbor Lights ");
    var list = RecentSearches.Load("p1");
    Check(list.SequenceEqual(["Harbor Lights", "tomas adler"]), "newest first; the same search in other case moves up (" + string.Join(" | ", list) + ")");
    RecentSearches.Add("p1", "   ");
    Check(RecentSearches.Load("p1").Count == 2, "blank searches are not kept");
    Check(RecentSearches.Load("p2").Count == 0, "the list is per profile");
    for (var i = 0; i < 12; i++) RecentSearches.Add("p1", "q" + i);
    Check(RecentSearches.Load("p1").Count == RecentSearches.Max && RecentSearches.Load("p1")[0] == "q11", "the list keeps the newest " + RecentSearches.Max);
    RecentSearches.Remove("p1", "q11");
    Check(RecentSearches.Load("p1")[0] == "q10", "× removes one entry");
    RecentSearches.Clear("p1");
    Check(RecentSearches.Load("p1").Count == 0, "Clear all empties the list");
    File.WriteAllText(RecentSearches.FilePath, "{ not json");
    Check(RecentSearches.Load("p1").Count == 0, "a damaged file reads as empty");
}
finally
{
    Directory.Delete(folder, recursive: true);
}

// --- Person credits (design 07) ------------------------------------------------------------------
var ada = Guid.NewGuid();
PersonEntry Credit(string? role, string kind) => new(ada, "Ada Moreno", role, null) { Kind = kind };
var harbor = Title("Harbor Lights", year: 2025, people: Credit("Mara Quill", "Actor"));
var debut = Title("First Light", year: 2020, people: [Credit("Ines", "Actor"), Credit("Director", "Director"), Credit("Writer", "Writer")]);
var cameo = Title("Night Shift", BaseItemDto_Type.Series, 2018, Credit("Herself", "GuestStar"));
var unnamed = Title("Old Reel", year: 2009, people: Credit("Actor", "Actor"));
var other = Title("Unrelated", year: 2022, people: new PersonEntry(Guid.NewGuid(), "Someone", "Lead", null) { Kind = "Actor" });
var credits = PersonCredits.From([harbor, debut, cameo, unnamed, other], ada);
Check(credits.Count == 6, "a title with several roles appears once per role; others' credits are ignored (" + credits.Count + ")");
Check(credits.Single(c => c.Item == cameo).Role == PersonCredits.Actor, "a guest star counts as an actor");
Check(credits.Single(c => c.Item == harbor).Character == "Mara Quill", "an actor credit keeps the character");
Check(credits.Single(c => c.Item == unnamed).Character is null, "a role that only names the kind is not a character");
Check(credits.Single(c => c.Item == debut && c.Role == "Director").Character is null, "crew credits have no character");
var roles = PersonCredits.Roles(credits);
Check(roles.Select(r => r.Role).SequenceEqual(["Actor", "Director", "Writer"]) && roles[0].Count == 4, "roles in the fixed order with counts");
Check(PersonCredits.RolesLine(credits) == "Actor · Director · Writer", "the roles line under the name");
var sections = PersonCredits.Sections(credits, null, CreditSort.Newest);
Check(sections.Count == 3 && sections[0].Credits[0].Item == harbor, "All shows a section per role, newest first");
Check(PersonCredits.Sections(credits, "Director", CreditSort.Newest).Single().Role == "Director", "picking a role shows only that grid");
Check(PersonCredits.Sorted(credits.Where(c => c.Role == "Actor"), CreditSort.Oldest)[0].Item == unnamed, "Oldest puts the earliest first");
Check(PersonCredits.Sorted(credits.Where(c => c.Role == "Actor"), CreditSort.Name)[0].Item == debut, "Name sorts by title");
Check(PersonCredits.Subtitle(credits.Single(c => c.Item == harbor)) == "as Mara Quill · 2025", "actor card line: " + PersonCredits.Subtitle(credits.Single(c => c.Item == harbor)));
Check(PersonCredits.Subtitle(credits.Single(c => c.Item == debut && c.Role == "Writer")) == "2020", "crew card line is the year");
Check(PersonCredits.RoleLabel("Lyricist") == "Lyricist", "an unknown role keeps the server's word");
Check(PersonCredits.BornLine(new DateTimeOffset(1978, 3, 12, 0, 0, 0, TimeSpan.Zero), "Lisbon").StartsWith("Born ") && PersonCredits.BornLine(null, "Lisbon") == "Born in Lisbon", "birth line: " + PersonCredits.BornLine(null, "Lisbon"));
Check(PersonCredits.BornLine(null, null) == "", "no birth facts, no line");
var asItem = PersonCredits.AsItem(new PersonEntry(ada, "Ada Moreno", "Mara Quill", "http://img"));
Check(asItem.Type == BaseItemDto_Type.Person && asItem.Overview == "Mara Quill" && asItem.PosterUrl == "http://img", "a person becomes a card item with the role as its line");

Console.WriteLine($"{checks} checks passed.");
if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
return 0;

static async Task WaitUntil(Func<bool> condition)
{
    for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
}

static async Task<Exception?> Record(Task task)
{
    try
    {
        await task;
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}
