using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightWeaver.Imaging;

/// <summary>
/// Disk-backed JSON cache: %LOCALAPPDATA%\LightWeaver\&lt;dirName&gt;\&lt;sha1(key)&gt;.json
/// with a per-call TTL. Mirrors <see cref="ImageCache"/>'s shape (SHA1-of-key files,
/// atomic temp+move, best-effort everywhere).
///
/// <para>Instantiable so several caches can coexist in their own directories — each
/// instance owns its lock table and its own eviction schedule, and Settings can size
/// and clear each one independently. The static facades over it are
/// <see cref="MetadataCache"/> and <see cref="ExternalMetadataCache"/>.</para>
/// </summary>
public sealed class DiskJsonCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Per-PROCESS salt for the log-side key token. 16 random bytes, generated once, never
    /// logged and never persisted — see <see cref="Detail"/> for why the token has to be salted at
    /// all. Shared by every instance so there is one rule to reason about rather than a
    /// per-cache exception.</summary>
    private static readonly string LogSalt =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly string _cacheDir;
    private readonly string _logPrefix;
    private readonly string _cacheName;

    // One fetch per key no matter how many callers race it.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <param name="dirName">Folder under <see cref="AppPaths.Root"/> holding this cache's entries.</param>
    /// <param name="logPrefix">Tag for the LIGHTWEAVER_CACHE_LOG lines, e.g. "MetadataCache".</param>
    public DiskJsonCache(string dirName, string logPrefix)
    {
        _cacheDir = Path.Combine(AppPaths.Root, dirName);
        _logPrefix = logPrefix;
        _cacheName = dirName;
    }

    /// <summary>Cached value when fresh, else the fetch result (persisted). A failed
    /// or null fetch is never cached; on JSON/IO trouble the fetch runs as if uncached.
    /// Set LIGHTWEAVER_CACHE_LOG to a path to get "hit/miss/stale &lt;key&gt;" lines.</summary>
    public async Task<T?> GetOrFetchAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> fetch)
        where T : class
    {
        var path = Path.Combine(_cacheDir, CacheFileName(key));
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (File.Exists(path))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= ttl)
                    {
                        var cached = JsonSerializer.Deserialize<T>(
                            await File.ReadAllTextAsync(path).ConfigureAwait(false), Options);
                        if (cached is not null)
                        {
                            Log($"hit {key}");
                            Detail("lookup", "hit", key);
                            return cached;
                        }
                    }
                    else
                    {
                        Log($"stale {key}");
                        Detail("lookup", "stale", key);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // unreadable entry: fall through to a live fetch
                Detail("lookup", "unreadable", key, ex: ex);
            }

            Log($"miss {key}");
            Detail("lookup", "miss", key);
            var started = System.Diagnostics.Stopwatch.StartNew();
            T? fresh;
            try
            {
                fresh = await fetch().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The fetch still propagates exactly as before; it just stops being invisible.
                Detail("fetch", "failure", key, started, ex);
                throw;
            }
            if (fresh is not null)
            {
                string? tmp = null;
                try
                {
                    Directory.CreateDirectory(_cacheDir);
                    tmp = $"{path}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
                    await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(fresh, Options)).ConfigureAwait(false);
                    File.Move(tmp, path, overwrite: true);
                    tmp = null; // moved atomically; there is no temporary file left to clean up
                    Detail("fetch", "stored", key, started);
                }
                catch (Exception ex)
                {
                    // persisting is best-effort
                    Detail("fetch", "store_failed", key, started, ex);
                }
                finally
                {
                    if (tmp is not null)
                        try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
                }
            }
            else
            {
                Detail("fetch", "empty", key, started);
            }
            return fresh;
        }
        finally
        {
            gate.Release();
            // Drop the gate once nobody is holding or waiting on it (B19). Locks used to be
            // GetOrAdd-only, so a long browse session retained one SemaphoreSlim per distinct
            // key — `streams:{server}:{itemId}` — for the life of the process. The check races a
            // fresh arrival benignly: the loser creates a new gate for the same key, which at
            // worst allows one duplicate fetch, and GetOrFetchAsync already tolerates that
            // (it is what happens on any cache miss).
            if (gate.CurrentCount == 1)
                _locks.TryRemove(key, out _);
        }
    }

    /// <summary>Reads cached value from disk if it exists, returning null on miss or read error.
    /// Reads stay asynchronous because browse views call this on the UI dispatcher during their
    /// first render.</summary>
    public async Task<T?> ReadAsync<T>(string key) where T : class
    {
        var path = Path.Combine(_cacheDir, CacheFileName(key));
        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(json, Options);
            }
        }
        catch (Exception ex)
        {
            Detail("lookup", "unreadable", key, ex: ex);
        }
        return null;
    }

    /// <summary>Writes data to disk cache best-effort. The temporary filename is unique per
    /// write so concurrent refreshes cannot delete or promote one another's partial JSON.</summary>
    public async Task StoreAsync<T>(string key, T data) where T : class
    {
        if (data is null) return;
        var path = Path.Combine(_cacheDir, CacheFileName(key));
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            tmp = $"{path}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(data, Options)).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            tmp = null; // moved atomically; there is no temporary file left to clean up
        }
        catch (Exception ex)
        {
            Detail("fetch", "store_failed", key, null, ex);
        }
        finally
        {
            if (tmp is not null)
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Deletes entries older than <paramref name="maxAge"/>. Nothing here is
    /// authoritative — every value is a re-fetchable projection with its own TTL — so age is the
    /// right axis and a miss costs one request.
    ///
    /// <para>Exists because this cache had NO eviction at all (B19): only the manual Clear() in
    /// settings, so the cache directory grew for the life of the install and kept entries for items
    /// long deleted from the library. Called opportunistically, so it cannot stall a fetch.</para></summary>
    public void EvictOlderThan(TimeSpan maxAge)
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (!dir.Exists)
                return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var f in dir.GetFiles("*.json"))
            {
                if (f.LastWriteTimeUtc >= cutoff)
                    continue;
                try { f.Delete(); } catch { /* in use — skip */ }
            }
        }
        catch { /* eviction is best-effort */ }
    }

    private int _evictionScheduled;

    /// <summary>Starts the periodic sweep (once per instance). Mirrors ImageCache's shape.</summary>
    public void ScheduleEviction(TimeSpan maxAge)
    {
        if (Interlocked.Exchange(ref _evictionScheduled, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            while (true)
            {
                EvictOlderThan(maxAge);
                await Task.Delay(TimeSpan.FromHours(6)).ConfigureAwait(false);
            }
        });
    }

    public long CurrentSizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            return dir.Exists ? dir.GetFiles("*.json").Sum(f => f.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Clear()
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (dir.Exists)
                foreach (var f in dir.GetFiles("*.json"))
                    try { f.Delete(); } catch { /* in use — skip */ }
        }
        catch { /* best-effort */ }
    }

    private static string CacheFileName(string key)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json";

    /// <summary>The <c>key_hash</c> field's value: 8 hex of SHA-256 over <see cref="LogSalt"/>
    /// followed by the key. Goes through <see cref="Diagnostics.AppLog.ShortHash"/> so the log's two
    /// hashed identities — this and <c>session=</c> — are the same one-way, no-throw, 8-hex token
    /// that <c>RedactCheck --detail-contract</c> already asserts the properties of.</summary>
    private static string LogKeyToken(string key)
        => Diagnostics.AppLog.ShortHash(LogSalt + " " + key);

    /// <summary>
    /// The product-log record for one cache decision. The KEY NEVER APPEARS: cache keys embed the
    /// server URL and an item id (<c>streams:{server}:{itemId}</c>) or a public catalogue id
    /// (<c>omdb:ratings:{imdbId}</c>), none of which belongs in a file meant for sharing.
    ///
    /// <para><c>key_hash</c> is a per-RUN SALTED token, not a digest of the key. An unsalted digest
    /// looked equivalent and is not: the prefix of every key is fixed, this file is its own format
    /// oracle, and <c>omdb:ratings:{imdbId}</c> ranges over ~10^7 public catalogue ids — so a
    /// recipient of an exported bundle could precompute the whole id space against 32 bits of hash
    /// in seconds and read back exactly which titles were opened. Salting with 16 random bytes held
    /// only in this process keeps every property the record is for — the same key gives the same
    /// token all run, different keys give different tokens — while making the table uncomputable
    /// without the salt, which is never written anywhere.</para>
    ///
    /// <para>What that trades away, deliberately: the token no longer matches the entry's SHA-1
    /// FILENAME, so a log line and its cache file cannot be paired by eye. Correlation inside one
    /// log file was the diagnostic value; pairing with the disk was a convenience, and it was the
    /// part that made the token precomputable. The on-disk layout is unchanged
    /// (<see cref="CacheFileName"/>), so no existing cache is invalidated.</para>
    ///
    /// <para>The <c>LIGHTWEAVER_CACHE_LOG</c> hook above is untouched: it is test instrumentation
    /// with its own format, and it prints raw keys precisely because it never leaves the
    /// machine.</para></summary>
    private void Detail(string eventName, string outcome, string key,
        System.Diagnostics.Stopwatch? started = null, Exception? ex = null)
    {
        // Ahead of the token, the formatting and the IO: this runs on every cache decision.
        if (!Diagnostics.AppLog.Verbose)
            return;
        var line = FormattableString.Invariant(
            $"event={eventName} cache={_cacheName} outcome={outcome} key_hash={LogKeyToken(key)}");
        if (started is not null)
            line += FormattableString.Invariant($" elapsed_ms={started.ElapsedMilliseconds}");
        if (ex is not null)
            line += $" error={ex.GetType().Name}";
        Diagnostics.AppLog.Detail("cache", line);
    }

    private void Log(string line)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("LIGHTWEAVER_CACHE_LOG") is { Length: > 0 } path)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{_logPrefix}] {line}{Environment.NewLine}");
        }
        catch { /* logging is best-effort */ }
    }
}
