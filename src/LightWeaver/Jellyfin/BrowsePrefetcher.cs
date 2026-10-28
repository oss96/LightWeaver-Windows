using System.IO;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Diagnostics;

namespace LightWeaver.Jellyfin;

/// <summary>
/// Process-wide background prefetcher for library folders.
/// Fills a <see cref="FolderCacheEntry{MediaItem}"/> in canonical order (SortName ascending, unfiltered)
/// page by page, detached from the view's lifetime so navigating away doesn't cancel it.
/// Cancelled on profile switch, session close, or app shutdown.
/// </summary>
public static class BrowsePrefetcher
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, CancellationTokenSource> _profileTokens = new();
    private static readonly Dictionary<string, Task> _runningPrefetches = new();
    private static readonly Dictionary<string, List<TaskCompletionSource<(IReadOnlyList<MediaItem> Items, DateTime DataAtUtc)>>> _pageWaiters = new();

    /// <summary>Items the server removed while a run was filling a folder, keyed like
    /// <see cref="_runningPrefetches"/>. A run holds the folder in memory and writes it back on
    /// every page, so an edit made to the entry beside it is overwritten on the next page and the
    /// deleted item comes back, possibly in an entry marked complete. Removals for a running
    /// folder are queued here instead; the run drains them before each request and each store,
    /// and applies whatever is left to the entry after its last store.</summary>
    private static readonly Dictionary<string, RunState> _pendingRemovals = new();

    /// <summary>What a registered run carries beside its task. <see cref="Settling"/> is set once
    /// the fill is over and the run is only applying its removals to disk: a view that opens the
    /// folder then would otherwise find the run still registered, be turned away, and never ask
    /// again, so its request is kept in <see cref="Restart"/> and the run starts the next one
    /// once it has unregistered.</summary>
    private sealed class RunState(string profileKey)
    {
        public string ProfileKey { get; } = profileKey;
        public HashSet<Guid> Ids { get; } = [];
        public bool Settling { get; set; }
        public (JellyfinService Service, CancellationToken Token)? Restart { get; set; }
    }

    /// <summary>A library change under way for a profile, keyed by profile. Cancelling the
    /// profile's runs and waiting for them covers only the runs that exist at that moment: one a
    /// view starts while the caller deletes the entries reads an entry before the delete and
    /// stores it, deleted items included, after it. While <see cref="InvalidationState.Depth"/> is
    /// above zero a start is only recorded in <see cref="InvalidationState.Requests"/>, and those
    /// runs start when the last invalidation ends. <see cref="InvalidationState.Epoch"/> moves on
    /// every begin and end, so a direct writer can tell that an invalidation overlapped its read
    /// and its write.</summary>
    private static readonly Dictionary<string, InvalidationState> _invalidations = new();

    private sealed class InvalidationState
    {
        public int Depth { get; set; }
        public int Epoch { get; set; }
        public Dictionary<Guid, JellyfinService> Requests { get; } = [];
    }

    /// <summary>Event raised whenever a prefetch makes progress on a folder entry.
    /// Parameters: cacheKey, have, total, complete, currentItems, dataAtUtc. The last is the
    /// entry's <c>CachedAtUtc</c>: the time of the oldest data the list holds, which a view hands
    /// to the user-data ledger exactly as it would for an entry read from disk.</summary>
    public static event Action<string, int, int, bool, IReadOnlyList<MediaItem>, DateTime>? Progress;

    /// <summary>
    /// Starts a background prefetch for a folder under the given profile if not already running.
    /// </summary>
    public static void Start(JellyfinService service, string profileKey, Guid folderId)
    {
        lock (_lock)
        {
            // The profile's entries are being deleted: a run started now could read one first.
            if (_invalidations.TryGetValue(profileKey, out var invalidation) && invalidation.Depth > 0)
            {
                invalidation.Requests[folderId] = service;
                return;
            }
            StartLocked(service, profileKey, folderId);
        }
    }

    /// <summary>Caller holds <see cref="_lock"/> and has checked the profile is not invalidating.</summary>
    private static void StartLocked(JellyfinService service, string profileKey, Guid folderId)
    {
        var key = BrowseFolderCache.Key(profileKey, folderId);
        if (_runningPrefetches.TryGetValue(key, out var existing) && !existing.IsCompleted)
        {
            // A settling run is past its last page and will not answer this view's waiters.
            // Its entry is left alone: SettleRemovalsAsync is still draining its ids.
            if (_pendingRemovals.TryGetValue(key, out var settling) && settling.Settling)
                settling.Restart = (service, ProfileTokenLocked(profileKey));
            return;
        }

        StartRunLocked(service, profileKey, folderId, key, ProfileTokenLocked(profileKey));
    }

    /// <summary>Marks the profile's browse entries as being deleted, then cancels its runs as
    /// <see cref="CancelProfile"/> does; the task completes when they have ended. Until the
    /// matching <see cref="EndInvalidate"/> a start for the profile is recorded, not run. Calls
    /// may overlap; the recorded starts run when the last one ends.</summary>
    public static Task BeginInvalidate(string profileKey)
    {
        lock (_lock)
        {
            if (!_invalidations.TryGetValue(profileKey, out var state))
            {
                state = new InvalidationState();
                _invalidations[profileKey] = state;
            }
            state.Depth++;
            state.Epoch++;
        }
        return CancelRunsAsync(profileKey);
    }

    /// <summary>Called once the entries are deleted. The last end starts a run for every folder
    /// a view asked for meanwhile, which then reads what the delete left.</summary>
    public static void EndInvalidate(string profileKey)
    {
        lock (_lock)
        {
            if (!_invalidations.TryGetValue(profileKey, out var state) || state.Depth == 0)
                return;
            state.Epoch++;
            if (--state.Depth > 0)
                return;
            foreach (var (folderId, service) in state.Requests)
            {
                AppLog.Detail("library", $"event=prefetch outcome=restart folder={folderId:N} reason=requested_while_invalidating");
                StartLocked(service, profileKey, folderId);
            }
            state.Requests.Clear();
        }
    }

    /// <summary>What to hand <see cref="DropIfInvalidatedSince"/> after a direct write of one of
    /// the profile's entries. Taken before the data the write holds is read.</summary>
    public static int InvalidationEpoch(string profileKey)
    {
        lock (_lock)
            return _invalidations.TryGetValue(profileKey, out var state) ? state.Epoch : 0;
    }

    /// <summary>For a write made beside the prefetcher (a view's refresh, a removal edit): when an
    /// invalidation of the profile began, ended or is still under way since
    /// <paramref name="epoch"/> was taken, the write may hold data from before the delete and have
    /// landed after it, so the entry it wrote is deleted again. Returns whether it was.</summary>
    public static bool DropIfInvalidatedSince(string profileKey, string cacheKey, int epoch)
    {
        lock (_lock)
        {
            if (!_invalidations.TryGetValue(profileKey, out var state)
                || (state.Epoch == epoch && state.Depth == 0))
                return false;
        }
        Imaging.MetadataCache.Remove(cacheKey);
        return true;
    }

    /// <summary>Caller holds <see cref="_lock"/>. The profile's live token, made anew after a
    /// cancel.</summary>
    private static CancellationToken ProfileTokenLocked(string profileKey)
    {
        if (!_profileTokens.TryGetValue(profileKey, out var cts) || cts.IsCancellationRequested)
        {
            cts = new CancellationTokenSource();
            _profileTokens[profileKey] = cts;
        }
        return cts.Token;
    }

    /// <summary>Caller holds <see cref="_lock"/> and has checked no run is registered for the key.</summary>
    private static void StartRunLocked(JellyfinService service, string profileKey, Guid folderId, string key,
        CancellationToken token)
    {
        _pendingRemovals[key] = new RunState(profileKey);
        _runningPrefetches[key] = Task.Run(() => RunPrefetchAsync(service, profileKey, folderId, key, token), token);
    }

    /// <summary>Takes removed items out of a folder's entry. With a run filling that folder the
    /// ids are queued on the run (see <see cref="_pendingRemovals"/>); otherwise the entry is
    /// edited directly, and deleted again when a library change's invalidation overlapped the
    /// edit (see <see cref="DropIfInvalidatedSince"/>).</summary>
    public static async Task RemoveItemsAsync(string profileKey, string cacheKey, IReadOnlySet<Guid> itemIds)
    {
        int epoch;
        lock (_lock)
        {
            if (IsRunning(cacheKey) && _pendingRemovals.TryGetValue(cacheKey, out var run))
            {
                run.Ids.UnionWith(itemIds);
                return;
            }
            epoch = _invalidations.TryGetValue(profileKey, out var state) ? state.Epoch : 0;
        }
        await BrowseFolderCache.RemoveItemsAsync(cacheKey, itemIds).ConfigureAwait(false);
        DropIfInvalidatedSince(profileKey, cacheKey, epoch);
    }

    /// <summary>Queues removed items on every run of the profile. The ids do not say which
    /// folder held them, and a folder whose view has left the history would otherwise store
    /// them as present; a run that never held one of them only pays a no-op filter.</summary>
    public static void RemoveItems(string profileKey, IReadOnlySet<Guid> itemIds)
    {
        lock (_lock)
        {
            foreach (var (key, run) in _pendingRemovals)
                if (run.ProfileKey == profileKey && IsRunning(key))
                    run.Ids.UnionWith(itemIds);
        }
    }

    /// <summary>Caller holds <see cref="_lock"/>. A run's task completes only after it has
    /// settled its removals, so a registration whose task is done is a leftover.</summary>
    private static bool IsRunning(string cacheKey)
        => _runningPrefetches.TryGetValue(cacheKey, out var task) && !task.IsCompleted;

    /// <summary>
    /// Waits for the next page of prefetched items for the specified folder, or until complete/cancelled.
    /// Used by LibraryView when scrolling outruns background prefetch in non-local mode. The time
    /// is the list's <c>CachedAtUtc</c> (see <see cref="Progress"/>). Null at once when no run is
    /// registered for the folder.
    /// </summary>
    public static async Task<(IReadOnlyList<MediaItem> Items, DateTime DataAtUtc)?> WaitForNextPageAsync(
        string profileKey, Guid folderId, CancellationToken ct)
    {
        var key = BrowseFolderCache.Key(profileKey, folderId);
        TaskCompletionSource<(IReadOnlyList<MediaItem> Items, DateTime DataAtUtc)> tcs;
        lock (_lock)
        {
            // No run, no page: a truncated folder never gets one, and every append past its cached
            // part used to sit out the caller's whole timeout before going to the server.
            if (!IsRunning(key))
                return null;
            if (!_pageWaiters.TryGetValue(key, out var list))
            {
                list = [];
                _pageWaiters[key] = list;
            }
            tcs = new TaskCompletionSource<(IReadOnlyList<MediaItem> Items, DateTime DataAtUtc)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            list.Add(tcs);
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Cancels all prefetch tasks for a specific profile (e.g. on session close). The
    /// task completes once every run the cancel reached has finished, its removals settled
    /// included. A cancel does not interrupt a store or the settle's read-modify-write already
    /// under way, so a caller about to delete the profile's entries awaits it first; otherwise
    /// that write lands after the delete and brings the entry back.</summary>
    public static Task CancelProfile(string profileKey)
    {
        // A start recorded during an invalidation belongs to the session being closed.
        lock (_lock)
            if (_invalidations.TryGetValue(profileKey, out var state))
                state.Requests.Clear();
        return CancelRunsAsync(profileKey);
    }

    private static Task CancelRunsAsync(string profileKey)
    {
        List<Task> runs;
        lock (_lock)
        {
            if (_profileTokens.Remove(profileKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            runs = _pendingRemovals
                .Where(pair => pair.Value.ProfileKey == profileKey)
                .Select(pair => _runningPrefetches.TryGetValue(pair.Key, out var task) ? task : null)
                .OfType<Task>()
                .Where(task => !task.IsCompleted)
                .ToList();
        }
        return runs.Count == 0 ? Task.CompletedTask : WhenRunsEndAsync(runs);
    }

    /// <summary>A run cancelled before it started is a cancelled task; it has nothing to wait for.
    /// Every other exit is caught inside the run.</summary>
    private static async Task WhenRunsEndAsync(List<Task> runs)
    {
        try
        {
            await Task.WhenAll(runs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Cancels all prefetch tasks for any profile other than the active one (e.g. on profile switch).</summary>
    public static void CancelOtherProfiles(string activeProfileKey)
    {
        lock (_lock)
        {
            var keysToRemove = _profileTokens.Keys.Where(k => k != activeProfileKey).ToList();
            foreach (var key in keysToRemove)
            {
                if (_profileTokens.Remove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
            foreach (var (key, state) in _invalidations)
                if (key != activeProfileKey)
                    state.Requests.Clear();
        }
    }

    /// <summary>Cancels all running prefetch tasks across all profiles (e.g. on app shutdown).</summary>
    public static void CancelAll()
    {
        lock (_lock)
        {
            foreach (var cts in _profileTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _profileTokens.Clear();
            foreach (var state in _invalidations.Values)
                state.Requests.Clear();
        }
    }

    private static async Task RunPrefetchAsync(JellyfinService service, string profileKey, Guid folderId,
        string cacheKey, CancellationToken ct)
    {
        var removedIds = new HashSet<Guid>();
        try
        {
            await FillAsync(service, folderId, cacheKey, removedIds, ct).ConfigureAwait(false);
        }
        finally
        {
            await SettleRemovalsAsync(cacheKey, profileKey, folderId, removedIds).ConfigureAwait(false);
        }
    }

    /// <summary>Moves queued removals into the run's own set and drops them from its list. Returns
    /// how many items the list lost; any loss shifts the server's offsets under the run.</summary>
    private static int DrainRemovals(string cacheKey, HashSet<Guid> removedIds, List<MediaItem> items)
    {
        lock (_lock)
        {
            if (!_pendingRemovals.TryGetValue(cacheKey, out var run) || run.Ids.Count == 0)
                return 0;
            removedIds.UnionWith(run.Ids);
            run.Ids.Clear();
        }
        return items.RemoveAll(item => removedIds.Contains(item.Id));
    }

    /// <summary>Runs after the run's last store, so nothing can write a removed item back over
    /// this edit: applies every removal the run was told about to the stored entry, then
    /// unregisters the run. A removal that arrives during the edit is still queued on the run and
    /// taken by the next pass; the run is unregistered only on a pass with nothing left. A view
    /// that asked for the folder meanwhile gets a new run, started under the same lock, so it
    /// reads the entry this pass left; with none, a waiter registered since the fill ended is
    /// released to fetch from the server instead of sitting out its timeout.</summary>
    private static async Task SettleRemovalsAsync(string cacheKey, string profileKey, Guid folderId,
        HashSet<Guid> removedIds)
    {
        var toApply = removedIds;
        while (true)
        {
            lock (_lock)
            {
                if (_pendingRemovals.TryGetValue(cacheKey, out var run))
                {
                    toApply.UnionWith(run.Ids);
                    run.Ids.Clear();
                }
                if (toApply.Count == 0)
                {
                    _pendingRemovals.Remove(cacheKey);
                    _runningPrefetches.Remove(cacheKey);
                    // A restart asked for before a cancel belongs to the cancelled session.
                    if (run?.Restart is { } restart && !restart.Token.IsCancellationRequested)
                    {
                        // The profile's entries are being deleted; EndInvalidate starts it.
                        if (_invalidations.TryGetValue(profileKey, out var invalidation) && invalidation.Depth > 0)
                            invalidation.Requests[folderId] = restart.Service;
                        else
                        {
                            AppLog.Detail("library", $"event=prefetch outcome=restart folder={folderId:N} reason=requested_while_settling");
                            StartRunLocked(restart.Service, profileKey, folderId, cacheKey, restart.Token);
                        }
                    }
                    else if (_pageWaiters.Remove(cacheKey, out var orphans))
                    {
                        foreach (var waiter in orphans)
                            waiter.TrySetCanceled();
                    }
                    return;
                }
            }
            try
            {
                await BrowseFolderCache.RemoveItemsAsync(cacheKey, toApply).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Detail("library",
                    $"event=prefetch outcome=remove_failure folder={folderId:N} count={toApply.Count} error={ex.GetType().Name}");
            }
            toApply = [];
        }
    }

    private static async Task FillAsync(JellyfinService service, Guid folderId, string cacheKey,
        HashSet<Guid> removedIds, CancellationToken ct)
    {
        var pageSize = BrowseFolderCache.PrefetchPageSize;
        if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_BROWSE_PREFETCH_PAGE"), out var ps) && ps > 0)
            pageSize = ps;

        var maxCap = BrowseFolderCache.MaxItems;
        var resumeFrom = 0;
        var existingEntry = await BrowseFolderCache.ReadAsync(cacheKey).ConfigureAwait(false);

        if (existingEntry is not null && existingEntry.Complete)
        {
            AppLog.Detail("library", $"event=prefetch outcome=noop folder={folderId:N} reason=already_complete");
            NotifyProgress(cacheKey, existingEntry.Items.Count, existingEntry.TotalCount, true, existingEntry.Items,
                existingEntry.CachedAtUtc);
            return;
        }

        List<MediaItem> items;
        int totalCount;
        // What the entry's CachedAtUtc means: the time of the OLDEST data the list holds, not the
        // time of the write. The user-data ledger re-applies every toggle newer than it, so a stamp
        // taken at page 3's write would hide a toggle made after page 1 was fetched and before
        // page 3 was, and the cached view would show page 1's stale badge until the sweep landed.
        // A resumed list keeps its entry's own stamp; a fresh one takes its first request's start.
        DateTime dataAtUtc;

        if (existingEntry is not null && !existingEntry.Complete && !existingEntry.Truncated)
        {
            items = new List<MediaItem>(existingEntry.Items);
            totalCount = existingEntry.TotalCount;
            resumeFrom = items.Count;
            dataAtUtc = existingEntry.CachedAtUtc;
        }
        else
        {
            items = [];
            totalCount = 0;
            dataAtUtc = DateTime.UtcNow;
        }

        AppLog.Detail("library",
            $"event=prefetch outcome=start folder={folderId:N} have={items.Count} total={totalCount} page_size={pageSize} resume_from={resumeFrom}");

        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs, ct).ConfigureAwait(false);

#if DEBUG
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_LIBRARY_TIMEOUT_TEST") == "1")
                throw new TaskCanceledException("Synthetic library query timeout in prefetch.");
#endif

            // Set once a removal took an item out of the list mid-run. The server's listing shrank
            // under the run, so the offset of every later page moved and an item at a page
            // boundary may have been skipped; the run then verifies the folder before storing it
            // as complete, as a resumed run does.
            var removedDuringRun = false;

            while (items.Count < maxCap)
            {
                ct.ThrowIfCancellationRequested();

                // Before the offset is taken: the server no longer counts these either.
                removedDuringRun |= DrainRemovals(cacheKey, removedIds, items) > 0;
                var startIndex = items.Count;
                var requestedAtUtc = DateTime.UtcNow;
                var (pageItems, serverTotal) = await service.GetFolderPageAsync(folderId, startIndex, pageSize, ct)
                    .ConfigureAwait(false);

                if (resumeFrom > 0 && startIndex == resumeFrom && totalCount != 0 && serverTotal != totalCount)
                {
                    AppLog.Detail("library",
                        $"event=prefetch outcome=restart folder={folderId:N} old_total={totalCount} new_total={serverTotal} reason=count_drift");
                    items.Clear();
                    totalCount = serverTotal;
                    resumeFrom = 0;
                    continue;
                }
                // Every page's own count, not page 0's: a removal mid-run shrinks the listing, and
                // page 0's count then waits for items that are gone. When what is left was a
                // multiple of the page size, the run met an empty page short of that count and
                // stopped with the entry partial and the view caching for good.
                totalCount = serverTotal;

                if (pageItems.Count == 0 && items.Count == 0)
                    break;
                // Past items the run holds, an empty page is the end of a listing that shrank under
                // it. Complete through the verify below, which finds what moved.
                var listingShrank = pageItems.Count == 0;

                // The first page of a list (a fresh run, or one the drift check restarted) is its
                // oldest data from here on.
                if (items.Count == 0)
                    dataAtUtc = requestedAtUtc;

                // A page requested before the server dropped an item can still carry it.
                var existingIds = new HashSet<Guid>(items.Select(i => i.Id));
                foreach (var pi in pageItems)
                {
                    if (!removedIds.Contains(pi.Id) && existingIds.Add(pi.Id))
                        items.Add(pi);
                }

                var truncated = false;
                var complete = false;

                if (items.Count >= maxCap)
                {
                    if (items.Count > maxCap)
                        items.RemoveRange(maxCap, items.Count - maxCap);
                    truncated = true;
                    complete = true;
                }
                else if (listingShrank || items.Count >= totalCount || pageItems.Count < pageSize)
                {
                    complete = true;
                }

                removedDuringRun |= DrainRemovals(cacheKey, removedIds, items) > 0;

                if (complete && (resumeFrom > 0 || removedDuringRun || listingShrank))
                {
                    // The first resumeFrom items came from an earlier run's entry and nothing has
                    // checked them since. The count_drift restart above only catches a folder
                    // whose SIZE moved; one deletion plus one addition keeps the count and would
                    // carry the deleted item into an entry marked complete. Verify the whole
                    // folder through the same sweep the view's incremental refresh runs, before
                    // the entry is stored as complete.
                    // The sweep and the refetch after it re-observe every item, so the verified
                    // list is no older than the sweep's start.
                    (items, totalCount, dataAtUtc) = await VerifyResumedAsync(service, folderId, items, ct)
                        .ConfigureAwait(false);
                    truncated = totalCount > maxCap;
                    if (items.Count > maxCap)
                        items.RemoveRange(maxCap, items.Count - maxCap);
                    // The sweep took time; one more removal may have come in during it, and the
                    // sweep's count still holds it.
                    var lateRemoved = DrainRemovals(cacheKey, removedIds, items);
                    if (lateRemoved > 0)
                        totalCount = Math.Max(items.Count, totalCount - lateRemoved);
                }

                var entry = new FolderCacheEntry<MediaItem>(
                    items.ToList(),
                    totalCount,
                    complete,
                    truncated,
                    dataAtUtc,
                    BrowseFolderCache.CurrentSchema);

                // Drained again inside the entry's write gate: the store then holds every removal
                // queued before it, and one queued after is taken by the next page or the settle,
                // whose edit waits for this write.
                entry = await BrowseFolderCache.StoreAsync(cacheKey, entry, _ =>
                {
                    removedDuringRun |= DrainRemovals(cacheKey, removedIds, items) > 0;
                    return removedIds;
                }).ConfigureAwait(false);

                totalCount = entry.TotalCount;
                AppLog.Detail("library",
                    $"event=prefetch outcome=progress folder={folderId:N} have={items.Count} total={totalCount} page_size={pageSize}");

                // The stored snapshot, not the live list: the views read it on the UI thread
                // while this run goes on adding to and removing from its own.
                NotifyProgress(cacheKey, entry.Items.Count, totalCount, complete, entry.Items, dataAtUtc);

                if (complete)
                {
                    AppLog.Detail("library",
                        $"event=prefetch outcome=complete folder={folderId:N} have={items.Count} total={totalCount}");
                    break;
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Detail("library",
                $"event=prefetch outcome=stop folder={folderId:N} have={items.Count} total={totalCount} reason=cancelled");
            NotifyProgress(cacheKey, items.Count, totalCount, false, items, dataAtUtc);
        }
        catch (Exception ex)
        {
            AppLog.Detail("library",
                $"event=prefetch outcome=failure folder={folderId:N} have={items.Count} total={totalCount} error={ex.GetType().Name}");
            NotifyProgress(cacheKey, items.Count, totalCount, false, items, dataAtUtc);
        }
        finally
        {
            lock (_lock)
            {
                // From here on the run only settles its removals; a Start for this folder now
                // is recorded as a restart instead of being turned away.
                if (_pendingRemovals.TryGetValue(cacheKey, out var run))
                    run.Settling = true;
                if (_pageWaiters.Remove(cacheKey, out var list))
                {
                    foreach (var waiter in list)
                        waiter.TrySetResult((items, dataAtUtc));
                }
            }
        }
    }

    /// <summary>Identity sweep + diff + refetch + merge over a resumed folder: the result is in
    /// the sweep's order and holds exactly the ids the server lists now. The time is the sweep's
    /// start, the oldest observation in the result.</summary>
    private static async Task<(List<MediaItem> Items, int TotalCount, DateTime DataAtUtc)> VerifyResumedAsync(
        JellyfinService service, Guid folderId, List<MediaItem> items, CancellationToken ct)
    {
        var sweptAtUtc = DateTime.UtcNow;
        var (sweep, serverTotal) = await service.SweepFolderAsync(folderId, ct).ConfigureAwait(false);
        var (provisional, added, removed, changed, toRefetch) = BrowseFolderCache.Diff(items, sweep);
        var refetched = toRefetch.Count > 0
            ? await service.GetItemsByIdsAsync(toRefetch, ct).ConfigureAwait(false)
            : [];
        var merged = BrowseFolderCache.Merge(sweep, provisional, refetched);
        AppLog.Detail("library",
            $"event=prefetch outcome=verified folder={folderId:N} swept={sweep.Count} total={serverTotal} added={added} removed={removed} changed={changed} refetched={refetched.Count}");
        return (merged, serverTotal, sweptAtUtc);
    }

    private static void NotifyProgress(string key, int have, int total, bool complete, IReadOnlyList<MediaItem> items,
        DateTime dataAtUtc)
    {
        lock (_lock)
        {
            if (_pageWaiters.Remove(key, out var list))
            {
                foreach (var waiter in list)
                    waiter.TrySetResult((items, dataAtUtc));
            }
        }
        Progress?.Invoke(key, have, total, complete, items, dataAtUtc);
    }
}
