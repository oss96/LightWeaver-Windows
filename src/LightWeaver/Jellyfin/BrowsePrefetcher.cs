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
    private static readonly Dictionary<string, List<TaskCompletionSource<IReadOnlyList<MediaItem>>>> _pageWaiters = new();

    /// <summary>Event raised whenever a prefetch makes progress on a folder entry.
    /// Parameters: cacheKey, have, total, complete, currentItems.</summary>
    public static event Action<string, int, int, bool, IReadOnlyList<MediaItem>>? Progress;

    /// <summary>
    /// Starts a background prefetch for a folder under the given profile if not already running.
    /// </summary>
    public static void Start(JellyfinService service, string profileKey, Guid folderId)
    {
        var key = BrowseFolderCache.Key(profileKey, folderId);
        lock (_lock)
        {
            if (_runningPrefetches.TryGetValue(key, out var existing) && !existing.IsCompleted)
                return;

            if (!_profileTokens.TryGetValue(profileKey, out var cts) || cts.IsCancellationRequested)
            {
                cts = new CancellationTokenSource();
                _profileTokens[profileKey] = cts;
            }

            var token = cts.Token;
            _runningPrefetches[key] = Task.Run(() => RunPrefetchAsync(service, profileKey, folderId, key, token), token);
        }
    }

    /// <summary>
    /// Waits for the next page of prefetched items for the specified folder, or until complete/cancelled.
    /// Used by LibraryView when scrolling outruns background prefetch in non-local mode.
    /// </summary>
    public static async Task<IReadOnlyList<MediaItem>?> WaitForNextPageAsync(string profileKey, Guid folderId, CancellationToken ct)
    {
        var key = BrowseFolderCache.Key(profileKey, folderId);
        TaskCompletionSource<IReadOnlyList<MediaItem>> tcs;
        lock (_lock)
        {
            if (!_pageWaiters.TryGetValue(key, out var list))
            {
                list = [];
                _pageWaiters[key] = list;
            }
            tcs = new TaskCompletionSource<IReadOnlyList<MediaItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Cancels all prefetch tasks for a specific profile (e.g. on session close).</summary>
    public static void CancelProfile(string profileKey)
    {
        lock (_lock)
        {
            if (_profileTokens.Remove(profileKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
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
        }
    }

    private static async Task RunPrefetchAsync(JellyfinService service, string profileKey, Guid folderId,
        string cacheKey, CancellationToken ct)
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
            NotifyProgress(cacheKey, existingEntry.Items.Count, existingEntry.TotalCount, true, existingEntry.Items);
            return;
        }

        List<MediaItem> items;
        int totalCount;

        if (existingEntry is not null && !existingEntry.Complete && !existingEntry.Truncated)
        {
            items = new List<MediaItem>(existingEntry.Items);
            totalCount = existingEntry.TotalCount;
            resumeFrom = items.Count;
        }
        else
        {
            items = [];
            totalCount = 0;
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

            while (items.Count < maxCap)
            {
                ct.ThrowIfCancellationRequested();

                var startIndex = items.Count;
                var (pageItems, serverTotal) = await service.GetFolderPageAsync(folderId, startIndex, pageSize, ct)
                    .ConfigureAwait(false);

                if (startIndex == 0 || totalCount == 0)
                {
                    totalCount = serverTotal;
                }
                else if (serverTotal != totalCount && resumeFrom > 0 && startIndex == resumeFrom)
                {
                    AppLog.Detail("library",
                        $"event=prefetch outcome=restart folder={folderId:N} old_total={totalCount} new_total={serverTotal} reason=count_drift");
                    items.Clear();
                    totalCount = serverTotal;
                    resumeFrom = 0;
                    continue;
                }

                if (pageItems.Count == 0)
                    break;

                var existingIds = new HashSet<Guid>(items.Select(i => i.Id));
                foreach (var pi in pageItems)
                {
                    if (existingIds.Add(pi.Id))
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
                else if (items.Count >= totalCount || pageItems.Count < pageSize)
                {
                    complete = true;
                }

                var entry = new FolderCacheEntry<MediaItem>(
                    items.ToList(),
                    totalCount,
                    complete,
                    truncated,
                    DateTime.UtcNow,
                    BrowseFolderCache.CurrentSchema);

                await BrowseFolderCache.StoreAsync(cacheKey, entry).ConfigureAwait(false);

                AppLog.Detail("library",
                    $"event=prefetch outcome=progress folder={folderId:N} have={items.Count} total={totalCount} page_size={pageSize}");

                NotifyProgress(cacheKey, items.Count, totalCount, complete, items);

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
            NotifyProgress(cacheKey, items.Count, totalCount, false, items);
        }
        catch (Exception ex)
        {
            AppLog.Detail("library",
                $"event=prefetch outcome=failure folder={folderId:N} have={items.Count} total={totalCount} error={ex.Message}");
            NotifyProgress(cacheKey, items.Count, totalCount, false, items);
        }
        finally
        {
            lock (_lock)
            {
                if (_pageWaiters.Remove(cacheKey, out var list))
                {
                    foreach (var waiter in list)
                        waiter.TrySetResult(items);
                }
                _runningPrefetches.Remove(cacheKey);
            }
        }
    }

    private static void NotifyProgress(string key, int have, int total, bool complete, IReadOnlyList<MediaItem> items)
    {
        lock (_lock)
        {
            if (_pageWaiters.Remove(key, out var list))
            {
                foreach (var waiter in list)
                    waiter.TrySetResult(items);
            }
        }
        Progress?.Invoke(key, have, total, complete, items);
    }
}
