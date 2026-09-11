using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace LightWeaver.Imaging;

/// <summary>
/// Disk-backed image cache: %LOCALAPPDATA%\LightWeaver\imagecache\&lt;sha1&gt;.img, evicted
/// LRU by last access time down to 500 MB. All IO and decoding run off the UI thread;
/// decoded bitmaps are frozen so they can cross back to it.
/// </summary>
public static class ImageCache
{
    private static readonly string CacheDir = Path.Combine(AppPaths.Root, "imagecache");

    /// <summary>Eviction cap (M19): a field so Settings can size it (M21). Seeded from
    /// AppSettings at startup; default matches the shipped 500 MB.</summary>
    public static long MaxCacheBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>Total bytes on disk right now (M21 readout).</summary>
    public static long CurrentSizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            return dir.Exists ? dir.GetFiles("*.img").Sum(f => f.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---- Summary counters (M3 verbose logging) ------------------------------------------
    //
    // SUMMARY ONLY, never per image. This cache is asked for every poster and backdrop on screen,
    // so a line per request would be the single loudest thing in app.log and would make the file
    // useless for everything else - one cold Home load is already ~13 images, a browse session is
    // thousands. The hot path therefore does nothing but bump an interlocked counter, and one
    // record is emitted per FLUSH.
    //
    // Flushes are deterministic, which is what makes them assertable: every SummaryEveryOps
    // recorded operations, on Clear (the Settings button), and once at app exit. A window with
    // nothing recorded writes nothing at all, so the "a clean run writes no file" rule survives.

    /// <summary>Operations per automatic summary flush.</summary>
    private const int SummaryEveryOps = 50;

    private static readonly Lock SummaryLock = new();
    private static int _memHits, _diskHits, _downloads, _downloadFailures, _opsSinceFlush;
    private static long _bytesDownloaded;

    private static void Record(ref int counter, long bytes = 0)
    {
        // A bump racing a flush can land in the adjacent window, so `ops` need not equal the sum of
        // the parts on any one record; over a session they agree, which is all a counter is for.
        Interlocked.Increment(ref counter);
        if (bytes > 0)
            Interlocked.Add(ref _bytesDownloaded, bytes);
        if (Interlocked.Increment(ref _opsSinceFlush) >= SummaryEveryOps)
            FlushSummary("threshold");
    }

    /// <summary>Writes one <c>[imagecache] event=summary</c> record covering everything counted
    /// since the previous flush, then resets the window. Called at the op threshold, from
    /// <see cref="Clear"/>, and from app exit; a window with no activity writes nothing.</summary>
    public static void FlushSummary(string reason)
    {
        // The flag check precedes the formatting and the LRU lock, the same shape as
        // DiskJsonCache.Detail and JellyfinService.RequestDetail: Record() arrives here every 50th
        // operation on the busiest path in the app whether or not anyone is reading.
        if (!Diagnostics.AppLog.Verbose)
        {
            CloseWindow();
            return;
        }
        int memHits, diskHits, downloads, failures, ops;
        long bytes;
        lock (SummaryLock)
        {
            ops = Interlocked.Exchange(ref _opsSinceFlush, 0);
            if (ops == 0)
                return;
            memHits = Interlocked.Exchange(ref _memHits, 0);
            diskHits = Interlocked.Exchange(ref _diskHits, 0);
            downloads = Interlocked.Exchange(ref _downloads, 0);
            failures = Interlocked.Exchange(ref _downloadFailures, 0);
            bytes = Interlocked.Exchange(ref _bytesDownloaded, 0);
        }
        // The LRU's own state comes from under its own lock: MemCache is a plain Dictionary and
        // GetImageAsync mutates it from worker threads.
        int memEntries;
        long memBytes;
        lock (MemLock)
        {
            memEntries = MemCache.Count;
            memBytes = _memBytes;
        }
        Diagnostics.AppLog.Detail("imagecache", FormattableString.Invariant(
            $"event=summary reason={reason} ops={ops} mem_hit={memHits} disk_hit={diskHits} download={downloads} download_failed={failures} bytes_downloaded={bytes} mem_entries={memEntries} mem_bytes={memBytes}"));
    }

    /// <summary>Ends the window without writing anything, for a flush that arrives while verbose is
    /// off. The counters are still RESET: returning with them standing would let _opsSinceFlush
    /// climb for the whole verbose-off run, so enabling verbose mid-session would open with one
    /// record covering thousands of operations nobody was watching — a worse version of the skew
    /// the early return exists to bound.</summary>
    private static void CloseWindow()
    {
        lock (SummaryLock)
        {
            Interlocked.Exchange(ref _opsSinceFlush, 0);
            Interlocked.Exchange(ref _memHits, 0);
            Interlocked.Exchange(ref _diskHits, 0);
            Interlocked.Exchange(ref _downloads, 0);
            Interlocked.Exchange(ref _downloadFailures, 0);
            Interlocked.Exchange(ref _bytesDownloaded, 0);
        }
    }

    /// <summary>Deletes every cached image (disk + the decoded-bitmap LRU).</summary>
    public static void Clear()
    {
        FlushSummary("clear");
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            if (dir.Exists)
                foreach (var f in dir.GetFiles("*.img"))
                    try { f.Delete(); } catch { /* in use — skip */ }
        }
        catch { /* best-effort */ }
        lock (MemLock)
        {
            MemCache.Clear();
            MemOrder.Clear();
            _memBytes = 0;
        }
    }

    /// <summary>
    /// Same handler configuration as <see cref="Jellyfin.JellyfinService"/>, and for the same
    /// reasons — this client had none of it, which was the B13 finding. It matters MORE here, not
    /// less: the metadata client makes a few requests at a time, while this one is asked for every
    /// poster and backdrop on screen at once, so it is what actually produces the parallel
    /// connection bursts this LAN is documented to drop SYNs on.
    ///
    /// Measured cold Home load before the change: 13 images, **11 simultaneous connections** —
    /// essentially one per image with no reuse, because MaxConnectionsPerServer defaults to
    /// int.MaxValue.
    /// </summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        // Windows WPAD auto-detection can stall first requests ~20 s when discovery fails.
        UseProxy = false,
        // Cap the fan-out and cut a stalled connect fast instead of riding the ~21 s TCP
        // retransmit ladder; keep the pool warm so a page of posters reuses connections
        // rather than opening one each.
        ConnectTimeout = TimeSpan.FromSeconds(4),
        MaxConnectionsPerServer = 4,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Resolves the Jellyfin auth header for a given absolute image URL.
    /// <b>Takes the URL on purpose</b> (B14): with warm multi-account sessions the correct token
    /// depends on which server the URL points at, not on which profile is active. Returning null
    /// means "no session owns this URL" and the request goes out unauthenticated.</summary>
    public static Func<string, string?>? AuthorizationHeaderProvider { get; set; }

    // One download per URL no matter how many cards bind it concurrently.
    private static readonly ConcurrentDictionary<string, Task<string?>> Inflight = new();

    // Small decoded-bitmap LRU: virtualized rows re-create Image elements when scrolling
    // back; this avoids re-decoding from disk each time.
    //
    // Bounded by BYTES as well as by count (B20). Count alone was meaningless here: a 300 px
    // poster decodes to ~540 KB while a 1280 px backdrop is ~3.7 MB, so the same 256 entries was
    // anywhere from ~135 MB to ~940 MB depending on what the user had been looking at — and
    // decodePixelWidth is part of the key, so one image can occupy several slots at once.
    private static readonly object MemLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bitmap)>> MemCache = new();
    private static readonly LinkedList<(string Key, BitmapSource Bitmap)> MemOrder = new();
    private const int MemCapacity = 256;
    private const long MemCapacityBytes = 192L * 1024 * 1024;
    private static long _memBytes;

    /// <summary>Decoded footprint of a frozen bitmap. Uses the real stride when the source
    /// exposes one, else 4 bytes/pixel — either way an estimate, which is all a cap needs.</summary>
    private static long BitmapBytes(BitmapSource b)
    {
        try
        {
            var bpp = Math.Max(8, b.Format.BitsPerPixel);
            return (long)b.PixelWidth * b.PixelHeight * bpp / 8;
        }
        catch
        {
            return (long)b.PixelWidth * b.PixelHeight * 4;
        }
    }

    private static int _evictionScheduled;

    /// <summary>Decoded (frozen) bitmap for the URL, or null if it can't be fetched.
    /// decodePixelWidth &gt; 0 decodes at that width to keep memory flat.</summary>
    public static async Task<BitmapSource?> GetImageAsync(string url, int decodePixelWidth = 0)
    {
        var memKey = $"{decodePixelWidth}|{url}";
        BitmapSource? cached = null;
        lock (MemLock)
        {
            if (MemCache.TryGetValue(memKey, out var hit))
            {
                MemOrder.Remove(hit);
                MemOrder.AddFirst(hit);
                cached = hit.Value.Bitmap;
            }
        }
        if (cached is not null)
        {
            // Counted outside MemLock: a flush reads the LRU's own state under that same lock.
            Record(ref _memHits);
            return cached;
        }

        var path = await GetFileAsync(url).ConfigureAwait(false);
        if (path is null)
            return null;

        var bitmap = await Task.Run(() => Decode(path, decodePixelWidth)).ConfigureAwait(false);
        if (bitmap is null)
            return null;

        lock (MemLock)
        {
            if (!MemCache.ContainsKey(memKey))
            {
                MemCache[memKey] = MemOrder.AddFirst((memKey, bitmap));
                _memBytes += BitmapBytes(bitmap);
                // Evict on EITHER bound, and keep at least one entry so a single oversized image
                // can't spin this into emptying the cache it was just added to.
                while (MemCache.Count > 1 && (MemCache.Count > MemCapacity || _memBytes > MemCapacityBytes))
                {
                    var oldest = MemOrder.Last!;
                    MemOrder.RemoveLast();
                    MemCache.Remove(oldest.Value.Key);
                    _memBytes -= BitmapBytes(oldest.Value.Bitmap);
                }
            }
        }
        return bitmap;
    }

    /// <summary>Local cache file for the URL (downloading on miss), or null on failure.
    /// Also usable directly for raw assets like trickplay tiles.</summary>
    public static async Task<string?> GetFileAsync(string url)
    {
        ScheduleEviction();
        var path = Path.Combine(CacheDir, CacheFileName(url));
        if (File.Exists(path))
        {
            // LRU stamp — NTFS last-access updates are often disabled, so set it explicitly.
            _ = Task.Run(() =>
            {
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* best-effort */ }
            });
            Record(ref _diskHits);
            return path;
        }
        var download = Inflight.GetOrAdd(url, u => DownloadAsync(u, path));
        try
        {
            return await download.ConfigureAwait(false);
        }
        finally
        {
            Inflight.TryRemove(url, out _);
        }
    }

    /// <summary>One retry on a transport-level stall, mirroring
    /// <c>JellyfinService.WithRetry</c>: with a 4 s connect timeout a dropped SYN now fails fast,
    /// and without a retry that would turn a recoverable hiccup into a permanently missing
    /// poster (a failed fetch caches nothing, so the card stays blank until re-navigated).</summary>
    private static async Task<HttpResponseMessage> SendWithRetryAsync(string url)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (AuthorizationHeaderProvider?.Invoke(url) is { } auth)
                request.Headers.TryAddWithoutValidation("Authorization", auth);
            try
            {
                return await Http.SendAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && ex is TaskCanceledException or HttpRequestException)
            {
                // fall through to one retry, which lands on a warm pool
            }
        }
    }

    private static async Task<string?> DownloadAsync(string url, string path)
    {
        try
        {
            using var response = await SendWithRetryAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Record(ref _downloadFailures);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                Record(ref _downloadFailures);
                return null;
            }
            Directory.CreateDirectory(CacheDir);
            // temp + move: a torn write must never become a cache entry
            var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            Record(ref _downloads, bytes.Length);
            return path;
        }
        catch
        {
            Record(ref _downloadFailures);
            return null;
        }
    }

    private static BitmapSource? Decode(string path, int decodePixelWidth)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (decodePixelWidth > 0)
                bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // corrupt cache entry: drop it so the next request re-downloads
            try { File.Delete(path); } catch { /* best-effort */ }
            return null;
        }
    }

    private static string CacheFileName(string url)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img";

    /// <summary>Trims the cache back under the cap: once shortly after first use, then
    /// periodically for the life of the process.
    ///
    /// <para>This used to be strictly once per session (B21), so the cap was enforced at most once
    /// per app run against whatever the cache happened to hold 15 s after startup — a long browsing
    /// session could then exceed it without bound for the rest of its life. The one-shot flag now
    /// guards starting the loop rather than running the sweep.</para></summary>
    private static void ScheduleEviction()
    {
        if (Interlocked.Exchange(ref _evictionScheduled, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            while (true)
            {
                EvictOnce();
                await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>One LRU sweep of the disk cache down to <see cref="MaxCacheBytes"/>.</summary>
    private static void EvictOnce()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            if (!dir.Exists)
                return;
            var files = dir.GetFiles("*.img");
            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes)
                return;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                }
                catch { /* in use — skip */ }
                if (total <= MaxCacheBytes)
                    break;
            }
        }
        catch { /* eviction is best-effort */ }
    }
}
