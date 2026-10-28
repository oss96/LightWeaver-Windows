using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace LightWeaver.WinUI.Services;

/// <summary>Loads Jellyfin artwork for the cards: read from the disk cache the WPF app shares
/// (<see cref="ImageDiskCache"/>) or fetched with the active session's authorization (the server
/// may require it) and stored there, decoded at the size it is shown, and kept in a small
/// in-memory cache so scrolling back is instant. The read, the download and the size probe run on
/// the thread pool; the UI thread is visited once per image, at low priority, to create the
/// <see cref="BitmapImage"/> that XAML then decodes off the UI thread. Reads and decodes take turns
/// in request order, so a grid that realizes hundreds of cards at once never starts hundreds of
/// them together, and a request cancelled before its turn costs nothing. Decodes take one turn at
/// a time while an animation holds <see cref="ImageQueue"/> and on two cores or fewer. Call
/// <c>LoadAsync</c> from the UI thread.</summary>
internal static class ImageLoader
{
    // Decoded posters at card size are roughly 0.3-0.6 MB each.
    private const int CacheSize = 200;
    // Same cap as the WPF image cache: a larger body is refused, not buffered.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = ImageLimits.MaxImageBytes };
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly SemaphoreSlim Reads = new(ReadSlots);
    private static readonly DecodeSlots Decodes = CreateDecodeSlots();
    private static readonly DecodedCache<BitmapImage> Decoded = new(CacheSize);
    private static readonly SharedLoads<BitmapImage> InFlight = new();
    private static readonly Lock CountersLock = new();
    private const int SummaryEveryOps = 50;
    private static int _memHits, _diskHits, _downloads, _failures, _ops;
    private static long _bytesDownloaded;
    private static DispatcherQueue? _ui;

    /// <summary>The artwork as a local file (one, overwritten each time) for the Windows media
    /// overlay; <paramref name="session"/>'s authorization goes only to an address under its own
    /// server. Null when it fails.</summary>
    public static async Task<string?> DownloadToFileAsync(string url, JellyfinService? session)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (session is not null && IsUnderServer(url, session.ServerUrl))
                request.Headers.TryAddWithoutValidation("Authorization", session.AuthorizationHeader);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var path = Path.Combine(AppPaths.Root, "media-overlay-artwork");
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="url"/> belongs to the server: the WPF image cache's rule,
    /// <see cref="JellyfinService.OwnsUrl"/>.</summary>
    internal static bool IsUnderServer(string url, string? serverUrl)
        => serverUrl is not null && Uri.TryCreate(url, UriKind.Absolute, out var address) && JellyfinService.OwnsUrl(serverUrl, address);

    /// <summary>Disk reads under way at once: half the cores, two to four.</summary>
    private static int ReadSlots => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    private static DecodeSlots CreateDecodeSlots()
    {
        var slots = new DecodeSlots(() => DecodeSlots.Limit(Environment.ProcessorCount, ImageQueue.IsHeld));
        ImageQueue.Released += (_, _) => slots.Pump();
        return slots;
    }

    /// <summary>The artwork decoded <paramref name="decodeWidth"/> physical pixels wide; null when
    /// it fails or <paramref name="cancel"/> is cancelled first (the card was recycled or left).</summary>
    public static Task<BitmapImage?> LoadAsync(JellyfinService session, string url, int decodeWidth, CancellationToken cancel = default)
        => LoadAsync(session, url, null, decodeWidth, cancel);

    /// <summary>Artwork whose address embeds the width to send: <paramref name="urlFor"/> is asked
    /// for the <see cref="ArtworkSizes"/> step at or above <paramref name="decodeWidth"/>, and a copy
    /// of the next step already on disk serves instead of a download (a grid that shrank). Decoded
    /// at <paramref name="decodeWidth"/>; null when <paramref name="urlFor"/> has no address.</summary>
    public static Task<BitmapImage?> LoadAsync(JellyfinService session, Func<int, string?> urlFor, int decodeWidth, CancellationToken cancel = default)
    {
        var step = ArtworkSizes.Step(decodeWidth);
        if (urlFor(step) is not { } url) return Task.FromResult<BitmapImage?>(null);
        var larger = ArtworkSizes.Next(step) is { } next && urlFor(next) is { } wider && wider != url ? wider : null;
        return LoadAsync(session, url, larger, decodeWidth, cancel);
    }

    /// <summary>Memory hits need the same address and a decode at least as wide within the same
    /// step, so nearby card widths share one decoded image.</summary>
    private static Task<BitmapImage?> LoadAsync(JellyfinService session, string url, string? largerOnDisk, int decodeWidth, CancellationToken cancel)
    {
        var ui = _ui ??= DispatcherQueue.GetForCurrentThread();
        var memoryKey = ArtworkSizes.Step(decodeWidth) + "|" + url;
        if (Decoded.Get(memoryKey, decodeWidth) is { } image)
        {
            Record(ref _memHits);
            return Task.FromResult<BitmapImage?>(image);
        }
        return InFlight.GetAsync(decodeWidth + "|" + url, token => Task.Run(() => FetchAsync(session, url, largerOnDisk, decodeWidth, memoryKey, ui, token)), cancel);
    }

    /// <summary>Runs on the thread pool; only <see cref="CreateOnUiAsync"/> visits the UI thread.</summary>
    private static async Task<BitmapImage?> FetchAsync(JellyfinService session, string url, string? largerOnDisk, int decodeWidth, string memoryKey, DispatcherQueue ui, CancellationToken cancel)
    {
        try
        {
            string? cached;
            byte[]? bytes;
            await Reads.WaitAsync(cancel).ConfigureAwait(false);
            try { (cached, bytes) = ReadCached(url, largerOnDisk); }
            finally { Reads.Release(); }

            if (bytes is null)
            {
                bytes = await DownloadAsync(session, url, cancel).ConfigureAwait(false);
                if (bytes is null)
                {
                    Record(ref _failures);
                    return null;
                }
                Record(ref _downloads, bytes.Length);
                var fetched = bytes;
                _ = Task.Run(() => ImageDiskCache.StoreAsync(url, fetched));
            }
            else
            {
                Record(ref _diskHits);
            }

            var image = await DecodeAsync(bytes, decodeWidth, ui, cancel).ConfigureAwait(false);
            if (image is null)
            {
                if (cached is not null) ImageDiskCache.Remove(cached);
                return null;
            }
            Decoded.Remember(memoryKey, image, decodeWidth, onlyIfEmpty: cancel.IsCancellationRequested);
            return image;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            // Network or URL failures: the card keeps its placeholder.
            Record(ref _failures);
            return null;
        }
    }

    /// <summary>The cached file of <paramref name="url"/>, else of <paramref name="larger"/>.</summary>
    private static (string? Path, byte[]? Bytes) ReadCached(string url, string? larger)
    {
        var hit = ReadCached(url);
        return hit.Bytes is null && larger is not null ? ReadCached(larger) : hit;
    }

    /// <summary>The cached file's bytes, or nulls on a miss or an unreadable entry.</summary>
    private static (string? Path, byte[]? Bytes) ReadCached(string url)
    {
        if (ImageDiskCache.Find(url) is not { } path) return (null, null);
        try
        {
            return (path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static async Task<byte[]?> DownloadAsync(JellyfinService session, string url, CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsUnderServer(url, session.ServerUrl))
                request.Headers.TryAddWithoutValidation("Authorization", session.AuthorizationHeader);
            using var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Null when the bytes are not an image (a corrupt or foreign cache entry) or would
    /// decode past <see cref="ImageLimits.MaxDecodedPixels"/>. Never decoded wider than the
    /// source. The header probe runs here, off the UI thread; the decode waits for its turn.</summary>
    private static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int decodeWidth, DispatcherQueue ui, CancellationToken cancel)
    {
        using var stream = new InMemoryRandomAccessStream();
        int width;
        try
        {
            await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancel).ConfigureAwait(false);
            var (sourceWidth, sourceHeight) = await SourceSizeAsync(stream).ConfigureAwait(false);
            if (ImageLimits.DecodedPixels(sourceWidth, sourceHeight, decodeWidth) > ImageLimits.MaxDecodedPixels) return null;
            width = ImageLimits.DecodeWidth(sourceWidth, decodeWidth);
            stream.Seek(0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        await Decodes.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return await CreateOnUiAsync(ui, stream, width).ConfigureAwait(false);
        }
        finally { Decodes.Release(); }
    }

    /// <summary>The one UI-thread step, at low priority: the <see cref="BitmapImage"/>, its decode
    /// width and its source. XAML decodes off the UI thread; the wait for it does not come back here.</summary>
    private static Task<BitmapImage?> CreateOnUiAsync(DispatcherQueue ui, IRandomAccessStream stream, int width)
    {
        var done = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
                if (width > 0) image.DecodePixelWidth = width;
                image.SetSourceAsync(stream).AsTask().ContinueWith(
                    decode => done.TrySetResult(decode.IsCompletedSuccessfully ? image : null),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception)
            {
                done.TrySetResult(null);
            }
        }))
            done.TrySetResult(null);
        return done.Task;
    }

    /// <summary>The image's pixel size from its header, read through a clone of the stream.</summary>
    private static async Task<(int Width, int Height)> SourceSizeAsync(IRandomAccessStream stream)
    {
        using var probe = stream.CloneStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(probe).AsTask().ConfigureAwait(false);
        return ((int)Math.Min(decoder.PixelWidth, int.MaxValue), (int)Math.Min(decoder.PixelHeight, int.MaxValue));
    }

    /// <summary>Empties the disk cache and the decoded images (Settings › Storage).</summary>
    public static void Clear()
    {
        FlushSummary("clear");
        ImageDiskCache.Clear();
        Decoded.Clear();
    }

    /// <summary>Counts one lookup; every <see cref="SummaryEveryOps"/> lookups write one summary
    /// record, never a line per image.</summary>
    private static void Record(ref int counter, long bytes = 0)
    {
        bool flush;
        lock (CountersLock)
        {
            counter++;
            _bytesDownloaded += bytes;
            flush = ++_ops >= SummaryEveryOps;
        }
        if (flush) FlushSummary("threshold");
    }

    /// <summary>One <c>[imagecache] event=summary</c> record for the lookups since the last one,
    /// in the WPF image cache's format.</summary>
    public static void FlushSummary(string reason)
    {
        string line;
        lock (CountersLock)
        {
            if (_ops == 0) return;
            line = FormattableString.Invariant(
                $"event=summary reason={reason} ops={_ops} mem_hit={_memHits} disk_hit={_diskHits} download={_downloads} download_failed={_failures} bytes_downloaded={_bytesDownloaded} mem_entries={Decoded.Count}");
            _ops = _memHits = _diskHits = _downloads = _failures = 0;
            _bytesDownloaded = 0;
        }
        Diagnostics.AppLog.Detail("imagecache", line);
    }
}
