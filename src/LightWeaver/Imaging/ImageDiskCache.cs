using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LightWeaver.Imaging;

/// <summary>
/// The framework-free half of the artwork cache, shared by the WPF <c>ImageCache</c> and
/// the WinUI image loader: %LOCALAPPDATA%\LightWeaver\imagecache\&lt;sha1(url)&gt;.img, written
/// atomically, stamped on every hit and evicted least-recently-used down to
/// <see cref="MaxCacheBytes"/>. Both apps use the same folder and names, so a poster one fetched
/// is a disk hit for the other.
/// </summary>
public static class ImageDiskCache
{
    /// <summary>The cache folder.</summary>
    public static string Folder { get; } = Path.Combine(AppPaths.Root, "imagecache");

    /// <summary>Eviction cap; seeded from <see cref="Settings.AppSettings.ImageCacheMaxMb"/> at
    /// startup. The default matches the shipped 500 MB.</summary>
    public static long MaxCacheBytes { get; set; } = 500L * 1024 * 1024;

    private static int _evictionScheduled;

    /// <summary>The file name for an image address: lowercase hex SHA-1 of its UTF-8 bytes.</summary>
    public static string FileName(string url)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img";

    /// <summary>Where the image for <paramref name="url"/> is (or would be) cached.</summary>
    public static string PathFor(string url) => Path.Combine(Folder, FileName(url));

    /// <summary>The cached file for the address, or null on a miss. A hit is stamped as recently
    /// used in the background (NTFS last-access updates are often disabled), and the first call
    /// starts the eviction sweep.</summary>
    public static string? Find(string url)
    {
        ScheduleEviction();
        var path = PathFor(url);
        if (!File.Exists(path))
            return null;
        _ = Task.Run(() =>
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        return path;
    }

    /// <summary>Stores fetched bytes under the address through a temporary file and a move, so a
    /// torn write never becomes an entry. Returns the cached path, or null when nothing was
    /// written.</summary>
    public static async Task<string?> StoreAsync(string url, byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        var path = PathFor(url);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>Drops one entry (a file that would not decode), so the next request fetches it again.</summary>
    public static void Remove(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Total bytes on disk right now.</summary>
    public static long CurrentSizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            return dir.Exists ? dir.GetFiles("*.img").Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes every cached image; files in use stay.</summary>
    public static void Clear()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists)
                return;
            foreach (var f in dir.GetFiles("*.img"))
                try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Trims the cache back under the cap 15 s after first use, then every 10 minutes
    /// for the life of the process.</summary>
    public static void ScheduleEviction()
    {
        if (Interlocked.Exchange(ref _evictionScheduled, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            while (true)
            {
                Evict(Folder, MaxCacheBytes);
                await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>One sweep of <paramref name="directory"/>: deletes the least recently used
    /// <c>*.img</c> files until the rest fit in <paramref name="maxBytes"/>. Returns how many
    /// files went. Best-effort; a file in use is skipped.</summary>
    public static int Evict(string directory, long maxBytes)
    {
        var deleted = 0;
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists)
                return 0;
            var files = dir.GetFiles("*.img");
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes)
                return 0;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                if (total <= maxBytes)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return deleted;
    }
}
