using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Downloads;
using LightWeaver.Theme;
using LightWeaver.ViewModels;
using LightWeaver.Views;

internal static class DownloadUxFixture
{
    private static int _failures;
    private static int _checks;
    private static DownloadItem Sample(string title = "Example episode") => new()
    {
        ItemId = Guid.NewGuid(), ServerUrl = "http://127.0.0.1", Title = title,
        FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv"),
        Subtitle = "Example series · S1E2", RuntimeTicks = TimeSpan.FromMinutes(45).Ticks,
        AddedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    public static int RunContracts()
    {
        var sample = Sample() with { Status = DownloadStatus.Downloading, TotalBytes = 100 * 1024 * 1024,
            BytesDownloaded = 25 * 1024 * 1024, BytesPerSecond = 1024 * 1024,
            ProgressTimestamp = Stopwatch.GetTimestamp() };
        var row = new DownloadRow(sample);
        Check(row.ProgressPercent == 25 && row.CanPlay && !row.IsIndeterminate,
            "known-size progress and Play agree with bytes written");
        Check(row.SpeedText.Contains("1 MB/s") && row.SpeedText.Contains("left"), "speed and remaining time are visible");
        Check(row.PlaybackText.Contains("Approx.") && row.PlaybackText.Contains("may buffer"),
            "downloaded duration is explicitly approximate and explains catch-up buffering");
        row.Update(sample with { TotalBytes = -1, EstimatedTotalBytes = 50 * 1024 * 1024 });
        Check(row.ProgressPercent == 50 && row.StatusText.Contains("~50%"), "transcode estimate is retained and marked approximate");
        row.Update(sample with { TotalBytes = -1, EstimatedTotalBytes = -1 });
        Check(row.IsIndeterminate && row.StatusText.Contains("unknown"), "unsized downloads show activity without an invented percentage");
        row.Update(sample with { BytesDownloaded = DownloadManager.MinPartialStreamBytes - 1 });
        Check(!row.CanPlay && row.PlaybackText.Contains("startup buffer"), "Play stays disabled before the production startup floor");
        row.Update(sample with { BytesDownloaded = DownloadManager.MinPartialStreamBytes });
        Check(row.CanPlay, "Play becomes available at the production startup floor without a status transition");
        row.Update(sample with { Status = DownloadStatus.Paused });
        Check(!row.CanPlay && row.SpeedText == "" && row.CanResume, "paused files explain resuming and show no stale transfer speed");
        row.Update(sample with { Status = DownloadStatus.Failed });
        Check(!row.CanPlay && row.PlaybackText.Contains("Retry"), "failed files expose retry context");
        row.Update(sample with { Status = DownloadStatus.Completed, BytesDownloaded = sample.TotalBytes });
        Check(row.CanPlay && row.ProgressPercent == 100 && row.PlaybackText.Contains("full 45 min"), "finished files expose full offline availability");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.IsStarting = true;
        Check(!row.CanPlay && row.PlaybackText.StartsWith("Starting playback"),
            "starting playback disables repeated Play and exposes immediate feedback");
        Check(changed.Contains(nameof(DownloadRow.CanPlay)) && changed.Contains(nameof(DownloadRow.PlaybackText)),
            "starting playback notifies both button availability and feedback bindings");
        row.Update(sample with { Status = DownloadStatus.Completed, BytesDownloaded = sample.TotalBytes });
        Check(!row.CanPlay && row.PlaybackText.StartsWith("Starting playback"),
            "download progress updates preserve startup feedback and disabled Play");
        changed.Clear();
        row.IsStarting = false;
        Check(row.CanPlay && row.PlaybackText.Contains("full 45 min")
            && changed.Contains(nameof(DownloadRow.CanPlay)) && changed.Contains(nameof(DownloadRow.PlaybackText)),
            "startup completion restores readiness and notifies both bindings");
        row.Update(sample with { ProgressTimestamp = Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency });
        Check(row.SpeedText.StartsWith("Waiting"), "stalled transfers age out their last speed sample");
        Check(!JsonSerializer.Serialize(sample).Contains("BytesPerSecond"), "transfer rate is never restored as stale persisted state");
        PlaybackMetadataContracts().GetAwaiter().GetResult();
        Transfer(knownSize: true);
        Transfer(knownSize: false);
        return Result();
    }

    private static async Task PlaybackMetadataContracts()
    {
        Check(DownloadPlayback.MetadataBudget == TimeSpan.FromMilliseconds(750),
            "download resume refresh has the specified 750 ms startup budget");
        var download = Sample() with { Status = DownloadStatus.Completed };
        var saved = new LightWeaver.Jellyfin.MediaItem
        {
            Id = download.ItemId, Name = download.Title,
            Type = global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode,
            SeriesId = Guid.NewGuid(), RuntimeTicks = download.RuntimeTicks, ResumePositionTicks = 0,
        };
        download = download with { PlaybackMetadata = saved };
        var fresh = saved.WithUserData(false, false, TimeSpan.FromMinutes(18).Ticks, 40);
        var receivedToken = CancellationToken.None;
        var resolved = await DownloadPlayback.ResolveMetadataAsync(download, token =>
        {
            receivedToken = token;
            return Task.FromResult<LightWeaver.Jellyfin.MediaItem?>(fresh);
        });
        Check(ReferenceEquals(resolved, fresh) && resolved.ResumePositionTicks == TimeSpan.FromMinutes(18).Ticks
            && saved.ResumePositionTicks == 0,
            "fresh Jellyfin resume supersedes the zero position captured when the download was queued");
        Check(resolved.Type == saved.Type && resolved.SeriesId == saved.SeriesId
            && receivedToken.CanBeCanceled && !receivedToken.IsCancellationRequested,
            "fresh metadata retains episode context and fetch receives a live cancellable token");
        resolved = await DownloadPlayback.ResolveMetadataAsync(download, null);
        Check(ReferenceEquals(resolved, saved), "offline playback retains saved episode metadata without a fetch");
        resolved = await DownloadPlayback.ResolveMetadataAsync(download,
            _ => Task.FromException<LightWeaver.Jellyfin.MediaItem?>(new System.Net.Http.HttpRequestException("Synthetic offline server")));
        Check(ReferenceEquals(resolved, saved), "an unreachable server falls back to saved playback metadata");
        resolved = await DownloadPlayback.ResolveMetadataAsync(download,
            _ => Task.FromResult<LightWeaver.Jellyfin.MediaItem?>(null));
        Check(ReferenceEquals(resolved, saved), "a missing server item falls back to saved playback metadata");
        var legacyJson = JsonSerializer.Serialize(download with { PlaybackMetadata = null },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var legacy = JsonSerializer.Deserialize<DownloadItem>(legacyJson)!;
        resolved = await DownloadPlayback.ResolveMetadataAsync(legacy, null);
        Check(!legacyJson.Contains(nameof(DownloadItem.PlaybackMetadata)) && legacy.PlaybackMetadata is null
            && resolved.Id == legacy.ItemId && resolved.Name == legacy.Title && resolved.RuntimeTicks == legacy.RuntimeTicks
            && resolved.Type == global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie && resolved.ResumePositionTicks == 0,
            "legacy index entries without saved metadata synthesize playable identity, title and runtime");
        await SlowMetadataContract(download, fresh, neverCompletes: false);
        await SlowMetadataContract(download, fresh, neverCompletes: true);
    }

    private static async Task SlowMetadataContract(DownloadItem download, LightWeaver.Jellyfin.MediaItem fresh,
        bool neverCompletes)
    {
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<LightWeaver.Jellyfin.MediaItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedToken = CancellationToken.None;
        CancellationTokenRegistration registration = default;
        Task<LightWeaver.Jellyfin.MediaItem?>? callback = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var resolution = DownloadPlayback.ResolveMetadataAsync(download, token =>
            {
                receivedToken = token;
                registration = token.Register(() => cancellationObserved.TrySetResult(true));
                callback = neverCompletes ? pending.Task : DelayedMetadata();
                return callback;
            });
            var finished = await Task.WhenAny(resolution, Task.Delay(TimeSpan.FromSeconds(5)));
            elapsed.Stop();
            var scenario = neverCompletes ? "never-completing" : "delayed";
            Check(ReferenceEquals(finished, resolution), $"{scenario} metadata callback cannot hang download startup");
            if (!ReferenceEquals(finished, resolution)) return;
            var resolved = await resolution;
            Check(ReferenceEquals(resolved, download.PlaybackMetadata)
                && elapsed.Elapsed < DownloadPlayback.MetadataBudget + TimeSpan.FromMilliseconds(500),
                $"{scenario} metadata falls back within the startup budget ({elapsed.ElapsedMilliseconds} ms, 500 ms scheduler allowance)");
            await Task.WhenAny(cancellationObserved.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Check(receivedToken.CanBeCanceled && receivedToken.IsCancellationRequested && cancellationObserved.Task.IsCompletedSuccessfully,
                $"{scenario} metadata callback receives cancellation when the startup budget expires");
            if (!neverCompletes)
            {
                await callback!;
                Check(ReferenceEquals(resolved, download.PlaybackMetadata),
                    "metadata arriving after the budget cannot replace the saved startup result");
            }
        }
        finally { registration.Dispose(); }

        async Task<LightWeaver.Jellyfin.MediaItem?> DelayedMetadata()
        {
            await Task.Delay(DownloadPlayback.MetadataBudget + TimeSpan.FromMilliseconds(750));
            return fresh;
        }
    }

    private static void Transfer(bool knownSize)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var lengthHeader = knownSize ? "Content-Length: 262144\r\n" : "";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\n{lengthHeader}Connection: close\r\n\r\n"));
            for (var i = 0; i < 8; i++)
            {
                await stream.WriteAsync(new byte[32768]);
                await Task.Delay(120);
            }
        });
        using var manager = new DownloadManager(() => 1, _ => $"http://127.0.0.1:{port}/media", _ => "synthetic");
        var item = Sample() with { EstimatedTotalBytes = knownSize ? -1 : 300000 };
        var ticks = new ConcurrentQueue<DownloadItem>();
        manager.DownloadsChanged += () =>
        {
            var current = manager.Snapshot().FirstOrDefault(i => i.ItemId == item.ItemId);
            if (current is not null) ticks.Enqueue(current);
        };
        manager.Enqueue(item);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10)
            && manager.Snapshot().First(i => i.ItemId == item.ItemId).Status != DownloadStatus.Completed)
            Thread.Sleep(20);
        var done = manager.Snapshot().First(i => i.ItemId == item.ItemId);
        Check(done.Status == DownloadStatus.Completed && done.BytesDownloaded == 262144,
            $"{(knownSize ? "known" : "unknown")}-size slow transfer completes with exact bytes");
        Check(ticks.Any(i => i.Status == DownloadStatus.Downloading && i.BytesDownloaded is > 0 and < 262144
            && i.BytesPerSecond > 0), "slow transfer publishes progress and speed before the former 512 KB threshold");
        Check(ticks.Any(i => i.Status == DownloadStatus.Downloading
            && i.TotalBytes == (knownSize ? 262144 : -1)), "headers publish exact or unknown total before completion");
        Check(done.EstimatedTotalBytes == item.EstimatedTotalBytes && done.BytesPerSecond == 0,
            "completion preserves estimate metadata and clears live speed");
        listener.Stop();
        server.GetAwaiter().GetResult();
        manager.Remove(item.ItemId);
    }

    // Guest only. Exercises the actual DownloadsView and rail in the production shell.
    public static int RunUi()
    {
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        var app = UiFixtureApplication.Create();
        var main = new MainWindow { Width = 1280, Height = 760 };
        try
        {
            main.Show();
            main.Activate();
            Field<AppViewModel>(main, "_app").LeavePlayback();
            ((FrameworkElement)main.FindName("NavRail")).Visibility = Visibility.Visible;
            var manager = Field<DownloadManager>(main, "_downloads");
            var live = Field<ConcurrentDictionary<Guid, DownloadItem>>(manager, "_live");
            var known = Sample("Known-size download") with { Status = DownloadStatus.Downloading,
                LanguageSummary = "Audio: German main · Subtitles: French full",
                BytesDownloaded = 25 * 1024 * 1024, TotalBytes = 100 * 1024 * 1024,
                BytesPerSecond = 3 * 1024 * 1024, ProgressTimestamp = Stopwatch.GetTimestamp() };
            DownloadItem[] items = [known,
                Sample("Preparing playback") with { Status = DownloadStatus.Downloading, BytesDownloaded = 1024 * 1024, TotalBytes = 200 * 1024 * 1024 },
                Sample("Transcoded download") with { Status = DownloadStatus.Downloading, BytesDownloaded = 20 * 1024 * 1024, EstimatedTotalBytes = 80 * 1024 * 1024, Resolution = "720p" },
                Sample("Unknown-size download") with { Status = DownloadStatus.Downloading, BytesDownloaded = 16 * 1024 * 1024 },
                Sample("Paused episode") with { Status = DownloadStatus.Paused, BytesDownloaded = 32 * 1024 * 1024, TotalBytes = 128 * 1024 * 1024 },
                Sample("Ready offline") with { Status = DownloadStatus.Completed, BytesDownloaded = 40 * 1024 * 1024, TotalBytes = 40 * 1024 * 1024 },
                Sample("Queued episode")];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = items[i] with { AddedTimestampMs = known.AddedTimestampMs + i };
                live[items[i].ItemId] = items[i];
            }
            typeof(MainWindow).GetMethod("OnNavDownloads", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [main, new RoutedEventArgs()]);
            Pump(400);
            var view = Field<DownloadsView>(main, "_downloadsView");
            var rows = Elements<Border>(view).Where(e => AutomationProperties.GetAutomationId(e) == "DownloadRow").ToArray();
            Check(rows.Length == items.Length, "production screen renders every synthetic download state");
            var progress = Elements<ProgressBar>(rows[0]).Single();
            var fill = (FrameworkElement)progress.Template.FindName("PART_Indicator", progress);
            Check(progress.Value == 25 && fill.ActualWidth > 0
                && Math.Abs(fill.ActualWidth / progress.ActualWidth - 0.25) < 0.02,
                "production progress fill visibly occupies the downloaded fraction");
            var preparing = Elements<Button>(rows[1]).Single(e => AutomationProperties.GetAutomationId(e) == "DownloadRowPlay");
            Check(preparing.Visibility == Visibility.Visible && !preparing.IsEnabled, "preparing row keeps Play visible and disabled");
            Check(Elements<ProgressBar>(rows[3]).Single().IsIndeterminate, "unknown-size row renders animated activity");
            Check(Elements<TextBlock>(view).Single(e => AutomationProperties.GetAutomationId(e) == "DownloadsSummary").Text
                == "4 downloading · 1 queued · 1 ready offline", "summary distinguishes active, queued and ready downloads");
            var nav = (RadioButton)main.FindName("NavDownloads");
            Check(NavRail.GetBadgeCount(nav) == 4, "rail counts only downloading items");
            Capture(main, "expanded");
            var starting = (DownloadRow)rows[0].DataContext;
            starting.IsStarting = true;
            Pump(100);
            Check(Elements<Button>(rows[0]).Single(e => AutomationProperties.GetAutomationId(e) == "DownloadRowPlay").IsEnabled == false
                && Elements<TextBlock>(rows[0]).Any(e => e.Text == "Starting playback…"),
                "starting playback feedback is visible and duplicate Play is disabled");
            Capture(main, "starting");
            starting.IsStarting = false;
            var scroll = (ScrollViewer)view.FindName("PageScroll");
            scroll.ScrollToBottom(); Pump(100); Capture(main, "ready-offline");
            scroll.ScrollToTop();
            main.Width = 1000;
            Pump(100);
            var badge = (FrameworkElement)nav.Template.FindName("CountBadge", nav);
            Check(badge.IsVisible && badge.ActualWidth >= 20, "collapsed rail keeps count visible");
            var badgePosition = badge.TranslatePoint(new Point(), nav);
            Check(badgePosition.X >= 0 && badgePosition.Y >= 0
                && badgePosition.X + badge.ActualWidth <= nav.ActualWidth,
                "collapsed badge stays inside the scroll viewport without clipping");
            Capture(main, "collapsed");
            main.Width = 700; main.Height = 480;
            Pump(100);
            Check(Elements<TextBlock>(rows[0]).Where(e => e.Text.Length > 0).All(e => e.ActualWidth > 0),
                "narrow layout retains status and readiness text");
            Capture(main, "narrow");
            foreach (var item in items.Where(i => i.Status == DownloadStatus.Downloading))
                live[item.ItemId] = item with { Status = DownloadStatus.Paused };
            Pump(400);
            Check(NavRail.GetBadgeCount(nav) == 0 && !badge.IsVisible, "rail badge disappears after all active transfers pause");
            return Result();
        }
        finally { main.Close(); app.Shutdown(); }
    }

    private static T Field<T>(object owner, string name) where T : class
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var descendant in Elements<T>(VisualTreeHelper.GetChild(parent, i))) yield return descendant;
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(FrameworkElement element, string state)
    {
        if (element is not Window window) throw new InvalidOperationException("Capture requires the tested window");
        element.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(handle, out var owner);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (owner != Environment.ProcessId || foreground != owner || !GetWindowRect(handle, out var rect))
            throw new InvalidOperationException("Capture requires the visible tested process in the foreground");
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        var directory = Path.Combine(Path.GetTempPath(), "lightweaver-tests"); Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"download-ux-{state}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private static void Check(bool condition, string name)
    {
        _checks++; if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    }
    private static int Result()
    {
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks} download checks, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }
}
