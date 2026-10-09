using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Production views, synthetic HTTP. Guest-only: creates WPF windows and captures them.
internal static class Jellyfin12UiFixture
{
    public const string Mode = "--jellyfin12-ui";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlaylistId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static int _checks;
    private static int _failures;

    public static int Run()
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Jellyfin compatibility UI checks require the approved verification guest.");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var application = UiFixtureApplication.Create();
        using var server = new Server();
        var model = new AppViewModel();
        Window? window = null;
        try
        {
            var credentials = new SavedCredentials(server.Origin,
                "SYNTHETIC", "synthetic-jellyfin12-fixture", UserId);
            var connected = model.Jellyfin.ReconnectAsync(credentials);
            Wait(() => connected.IsCompleted, "synthetic connection");
            Check(connected.GetAwaiter().GetResult(), "synthetic compatibility session connects");
            // ReconnectAsync validates the service only. The real shell also activates
            // the profile key; production views reject work from inactive profiles.
            typeof(AppViewModel).GetProperty(nameof(AppViewModel.ActiveSessionKey))!
                .SetValue(model, AppViewModel.SessionKey(credentials));
            window = new Window
            {
                Title = "LightWeaver compatibility fixture", Width = 1000, Height = 700,
                Background = (Brush)application.Resources["LwStorm1Brush"],
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Show();
            var playlist = new MediaItem { Id = PlaylistId, Name = "Repeated playlist entries", Type = BaseItemDto_Type.Playlist };
            var view = new LibraryView(model, playlist);
            window.Content = view;
            Wait(() => view.IsLoaded && Field<int>(view, "_initialLoadGeneration") == 0
                && Element<ItemsControl>(view, "ItemsGrid").Items.Count == 503, "complete playlist grid");
            var grid = Element<ItemsControl>(view, "ItemsGrid");
            Check(grid.Items.Cast<MediaItem>().Select(i => i.Id).SequenceEqual(Server.PlaylistIds),
                "production playlist grid preserves all 503 occurrences in server order across pages");
            Check(server.PlaylistRequests == 2 && server.GenericPlaylistQueries == 0,
                "playlist grid pages its ordered endpoint and bypasses generic folder queries");
            Check(Element<FrameworkElement>(view, "SortPanel").Visibility == Visibility.Collapsed,
                "playlist sorting controls stay hidden");
            view.ApplyUserDataUpdate(new MediaItem { Id = B, Name = "B repeated movie", Type = BaseItemDto_Type.Movie, Played = true });
            Check(grid.Items.Cast<MediaItem>().Where(i => i.Id == B).All(i => i.Played)
                && grid.Items.Count == 503, "watched updates change every repeated card without collapsing occurrences");
            Capture(window, "playlist-standard");
            window.Width = 700;
            window.Height = 480;
            Pump(200);
            Capture(window, "playlist-minimum");

            // A second view sees the occurrence-preserving cache even before refresh finishes.
            server.HoldPlaylist = true;
            var cachedView = new LibraryView(model, playlist);
            window.Content = cachedView;
            Wait(() => Element<ItemsControl>(cachedView, "ItemsGrid").Items.Count == 503, "cached playlist occurrences");
            Check(Element<ItemsControl>(cachedView, "ItemsGrid").Items.Cast<MediaItem>()
                .Select(i => i.Id).SequenceEqual(Server.PlaylistIds), "warm playlist cache preserves duplicate order");
            server.HoldPlaylist = false;
            Wait(() => Field<int>(cachedView, "_initialLoadGeneration") == 0, "playlist refresh completion");
            Check(Element<ItemsControl>(cachedView, "ItemsGrid").Items.Count == 503, "refresh preserves every cached playlist occurrence");

            // Positive control first: the same production row must show a real result.
            server.SimilarCount = 1;
            var positive = new ItemDetailView(model, new AppSettings(), Movie(A));
            window.Content = positive;
            Wait(() => Field<Guid>(positive, "_similarLoadedFor") == A, "nonempty Similar row");
            Check(Element<FrameworkElement>(positive, "SimilarSection").Visibility == Visibility.Visible
                && Element<ItemsControl>(positive, "SimilarRow").Items.Count == 1,
                "nonempty Similar response shows its production row");

            server.SimilarCount = 0;
            var empty = new ItemDetailView(model, new AppSettings(), Movie(B));
            window.Content = empty;
            Wait(() => Field<Guid>(empty, "_similarLoadedFor") == B && !Field<bool>(empty, "_similarFetching"), "empty Similar row");
            Pump(400); // Past the delayed skeleton reveal, which must not resurrect the section.
            Check(Element<FrameworkElement>(empty, "SimilarSection").Visibility == Visibility.Collapsed
                && Element<ItemsControl>(empty, "SimilarRow").Items.Count == 0,
                "empty Similar response collapses the production row and heading");
            Check(Element<FrameworkElement>(empty, "SimilarSkeleton").Visibility == Visibility.Collapsed,
                "empty Similar response settles its delayed skeleton");
            Capture(window, "similar-empty-minimum");
            window.Width = 1000;
            window.Height = 700;
            Pump(200);
            Capture(window, "similar-empty-standard");
        }
        catch (Exception ex)
        {
            if (window?.Content is LibraryView failedView)
                Console.WriteLine($"FAIL: playlist diagnostics requests={server.PlaylistRequests} generic={server.GenericPlaylistQueries} "
                    + $"grid={Element<ItemsControl>(failedView, "ItemsGrid").Items.Count} initial={Field<int>(failedView, "_initialLoadGeneration")}");
            Check(false, "compatibility UI completes: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            window?.Close();
            model.Jellyfin.Dispose();
            application.Shutdown();
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks} compatibility UI checks, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static MediaItem Movie(Guid id) => new() { Id = id, Name = id == A ? "A movie" : "B repeated movie", Type = BaseItemDto_Type.Movie };
    private static T Element<T>(FrameworkElement view, string name) where T : FrameworkElement
        => (T)(view.FindName(name) ?? throw new InvalidOperationException("Missing " + name));
    private static T Field<T>(object view, string name)
        => (T)view.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    }
    private static void Wait(Func<bool> condition, string name)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until) Pump(20);
        if (!condition()) throw new TimeoutException(name);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var directory = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, "jellyfin12-" + name + ".png"));
        encoder.Save(file);
    }

    private sealed class Server : IDisposable
    {
        internal static Guid[] PlaylistIds { get; } = [B, A, B, .. Enumerable.Repeat(A, 500)];
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        internal string Origin { get; }
        internal int PlaylistRequests;
        internal int GenericPlaylistQueries;
        internal volatile bool HoldPlaylist;
        internal volatile int SimilarCount;

        internal Server()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                    catch (Exception) when (_stop.IsCancellationRequested) { break; }
                    try { await Reply(context); }
                    catch (Exception) when (_stop.IsCancellationRequested) { }
                    finally { context.Response.Abort(); }
                }
            });
        }
        private static object Dto(Guid id) => new { Id = id, Name = id == A ? "A movie" : "B repeated movie", Type = "Movie" };
        private async Task Reply(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            object body = new { Items = Array.Empty<object>(), TotalRecordCount = 0 };
            if (path.Equals("/Users/Me", StringComparison.OrdinalIgnoreCase))
                body = new { Id = UserId, Name = "Synthetic compatibility user" };
            else if (path.Equals($"/Playlists/{PlaylistId}/Items", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref PlaylistRequests);
                while (HoldPlaylist) await Task.Delay(20, _stop.Token);
                int.TryParse(context.Request.QueryString["StartIndex"], out var start);
                int.TryParse(context.Request.QueryString["Limit"], out var limit);
                body = new { Items = PlaylistIds.Skip(start).Take(limit).Select(Dto).ToArray(), TotalRecordCount = PlaylistIds.Length };
            }
            else if (path.Equals("/Items", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(context.Request.QueryString["ParentId"], out var parent) && parent == PlaylistId)
                    Interlocked.Increment(ref GenericPlaylistQueries);
                body = new { Items = new[] { Dto(A), Dto(B) }, TotalRecordCount = 2 };
            }
            else if (path.EndsWith("/Similar", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(300, _stop.Token);
                body = new { Items = Enumerable.Repeat(Dto(A), SimilarCount).ToArray(), TotalRecordCount = SimilarCount };
            }
            else if (path.Equals($"/Items/{A}", StringComparison.OrdinalIgnoreCase)) body = Dto(A);
            else if (path.Equals($"/Items/{B}", StringComparison.OrdinalIgnoreCase)) body = Dto(B);
            else if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
                body = new { MediaSources = Array.Empty<object>() };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            context.Response.Close();
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
