using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Guest-only real TEST playback. No watched-state reports or stored-settings changes.
internal static class Jellyfin12PlaybackFixture
{
    public const string Mode = "--jellyfin12-playback";
    public static int Run()
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Graphical subtitle playback requires the approved verification guest.");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_LOG", null);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", null);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var credentials = Jellyfin12ProbeFixture.ReadTestProfile();
        if (credentials is null)
        {
            Console.WriteLine("SKIP: existing signed-in TEST profile is required for graphical subtitle playback.");
            Console.WriteLine("RESULT: INCOMPLETE");
            return 2;
        }
        var application = UiFixtureApplication.Create();
        using var service = new JellyfinService();
        Window? window = null;
        OverlayWindow? overlay = null;
        MpvPlayer? player = null;
        var decisions = new List<PlaybackDecision>();
        var checks = 0;
        var mpvErrors = 0;
        var authorizationErrors = 0;
        var graphicsErrors = 0;
        var result = 1;
        void Check(bool condition, string name)
        {
            if (!condition) throw new FixtureCheckException(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        try
        {
            Check(Await(service.ReconnectAsync(credentials)), "existing TEST token validates without authentication changes");
            Check(service.UserName?.Equals("test", StringComparison.OrdinalIgnoreCase) == true,
                "server confirms the playback account is TEST");
            var movies = Await(service.SearchAsync("10 Cloverfield Lane"));
            var movie = movies.FirstOrDefault(m => m.Type == BaseItemDto_Type.Movie
                && m.Name.Equals("10 Cloverfield Lane", StringComparison.OrdinalIgnoreCase))
                ?? throw new FixtureCheckException("The known h264 PGS fixture is not visible to TEST.");
            var dto = Await(service.Client.Items[movie.Id].GetAsync());
            var source = dto?.MediaSources?.FirstOrDefault(s => s.MediaStreams?.Any(t =>
                t.Type == MediaStream_Type.Video && t.Codec?.Equals("h264", StringComparison.OrdinalIgnoreCase) == true) == true
                && s.MediaStreams.Any(t => t.Type == MediaStream_Type.Subtitle
                    && t.Codec?.Equals("pgssub", StringComparison.OrdinalIgnoreCase) == true))
                ?? throw new FixtureCheckException("The known fixture has no h264/PGS source.");
            var subtitle = source.MediaStreams!.Where(s => s.Type == MediaStream_Type.Subtitle
                && s.Codec?.Equals("pgssub", StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(s => s.IsForced == true)
                .ThenByDescending(s => s.Language?.Equals("eng", StringComparison.OrdinalIgnoreCase) == true).First();
            var subtitleOrdinal = source.MediaStreams!.Where(s => s.Type == MediaStream_Type.Subtitle && s.IsExternal != true)
                .OrderBy(s => s.Index).ToList().FindIndex(s => s.Index == subtitle.Index) + 1;
            var vm = new PlayerViewModel();
            var host = new MpvPlayerHost();
            window = new Window { Title = "LightWeaver TEST PGS playback", Width = 1000, Height = 700, Content = host };
            host.HwndReady += hwnd =>
            {
                player = MpvPlayer.Create(hwnd, new LightWeaver.Threading.WpfUiDispatcher(window.Dispatcher), new AppSettings { VideoBufferMiB = 32 });
                // Count fixed error categories without emitting native messages, which may contain URLs.
                player.LogMessage += message =>
                {
                    mpvErrors++;
                    if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("403", StringComparison.OrdinalIgnoreCase)) authorizationErrors++;
                    if (message.Contains("d3d", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("video output", StringComparison.OrdinalIgnoreCase)) graphicsErrors++;
                };
                vm.Attach(player);
                // MainWindow primes the authoritative generation-scoped track list at FILE_LOADED.
                // This standalone owner must do the same before the flyout model can rebuild.
                player.FileLoaded += generation => player.RefreshTracks(generation);
            };
            window.Show();
            Wait(() => player is not null, "mpv host initialization", 10);
            overlay = new OverlayWindow(vm)
            {
                Owner = window, Left = window.Left, Top = window.Top,
                Width = window.ActualWidth, Height = window.ActualHeight,
            };
            overlay.Show();
            var direct = Await(service.NegotiatePlaybackAsync(movie.Id, 0,
                subtitleStreamIndex: subtitle.Index, mediaSourceId: source.Id))
                ?? throw new FixtureCheckException("Direct PGS negotiation returned no decision.");
            decisions.Add(direct);
            Check(!direct.IsTranscode && !direct.SubtitleIsBurnedIn, "original h264 source negotiates direct playback with embedded PGS");
            player!.SetHttpHeaders(service.AuthorizationHeader);
            player.LoadFile(direct.Url);
            Wait(() => player.TimePos > 1 && GraphicalTrack(player) is not null && vm.HasSubtitleChoices,
                "original PGS track and playback", 60);
            var pgsId = subtitleOrdinal;
            Check(IsGraphicalTrack(player, pgsId), "selected source subtitle ordinal identifies its actual PGS track");
            var option = vm.SubtitleTracks.Single(t => t.Id == pgsId);
            Check(option.Id > 0, "production subtitle flyout model includes the real embedded PGS track");
            vm.SelectSubtitle(option);
            Wait(() => player.GetPropertyString("sid") == pgsId.ToString(CultureInfo.InvariantCulture), "PGS selection", 10);
            var popup = (Popup)overlay.FindName("TracksPopup");
            ((Button)overlay.FindName("SubtitleTracksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(300);
            var list = (ListBox)overlay.FindName("SubsList");
            Check(popup.IsOpen && list.Items.Cast<TrackOption>().Any(t => t.Id == pgsId),
                "production track flyout lists the selected real PGS stream");
            Capture(popup.Child, "jellyfin12-pgs-direct-flyout.png");
            popup.IsOpen = false;
            // Seek into dialogue. A finite sub-start means mpv decoded a bitmap event,
            // whereas a selected sid alone would prove only the menu selection.
            player.TimePos = 600;
            Wait(() => double.TryParse(player.GetPropertyString("sub-start"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var start) && start >= 0,
                "decoded PGS subtitle event during dialogue", 40);
            Check(player.GetPropertyString("sid") == pgsId.ToString(CultureInfo.InvariantCulture),
                "mpv decodes selected PGS events during actual direct playback");
            player.Stop();

            var transcode = Await(service.NegotiatePlaybackAsync(movie.Id, 4_000_000, true,
                subtitleStreamIndex: subtitle.Index, mediaSourceId: source.Id))
                ?? throw new FixtureCheckException("Forced PGS negotiation returned no decision.");
            decisions.Add(transcode);
            Check(transcode.IsTranscode && transcode.SubtitleIsBurnedIn
                && transcode.SubtitleStreamIndex == subtitle.Index,
                "current Embed profile negotiates PGS burn-in on forced HLS transcode");
            player.LoadFile(transcode.Url);
            Wait(() => player.TimePos > 2 && player.Tracks.Any(t => t.Type == "video"), "PGS HLS playback", 90);
            Check(GraphicalTrack(player) is null, "burned PGS is not exposed as a second embedded graphical track in HLS");
            player.SelectSubtitleTrack(null);
            Check(player.GetPropertyString("sid") == "no", "burn-in playback can suppress mpv subtitles to avoid a double render");
            result = 0;
        }
        catch (Exception ex)
        {
            // Network/native exceptions can include credential-bearing media URLs. Types only.
            Console.WriteLine("FAIL: TEST graphical-subtitle playback: " + ex.GetType().Name);
            if (ex is TimeoutException or FixtureCheckException) Console.WriteLine("FAIL: " + ex.Message);
            if (player is not null)
                Console.WriteLine(FormattableString.Invariant(
                    $"DIAGNOSTIC: position={player.TimePos:0.000} tracks={player.Tracks.Count} graphical={GraphicalTrack(player) is not null} errors={mpvErrors} authorization={authorizationErrors} graphics={graphicsErrors}"));
        }
        finally
        {
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL: TEST playback cleanup: " + ex.GetType().Name);
                    result = 1;
                }
            }
            // Network cleanup pumps queued UI callbacks. Keep mpv alive until those callbacks
            // finish, rather than publishing track/property events against a disposed player.
            Cleanup(() => player?.Stop());
            foreach (var decision in decisions) Cleanup(() => Await(service.DiscardUnplayedDecisionAsync(decision)));
            Cleanup(() => overlay?.Close());
            Cleanup(() => player?.Dispose());
            Cleanup(() => window?.Close());
            Cleanup(application.Shutdown);
        }
        Console.WriteLine(result == 0
            ? $"RESULT: PASS ({checks} TEST graphical-subtitle playback checks)"
            : "RESULT: FAIL");
        return result;
    }

    private static int? GraphicalTrack(MpvPlayer player)
    {
        using var tracks = JsonDocument.Parse(player.GetPropertyString("track-list") ?? "[]");
        foreach (var t in tracks.RootElement.EnumerateArray())
            if (t.TryGetProperty("type", out var type) && type.GetString() == "sub"
                && t.TryGetProperty("codec", out var codec) && codec.GetString()?.Contains("pgs", StringComparison.OrdinalIgnoreCase) == true)
                return t.GetProperty("id").GetInt32();
        return null;
    }
    private static bool IsGraphicalTrack(MpvPlayer player, int id)
    {
        using var tracks = JsonDocument.Parse(player.GetPropertyString("track-list") ?? "[]");
        return tracks.RootElement.EnumerateArray().Any(t => t.TryGetProperty("type", out var type) && type.GetString() == "sub"
            && t.GetProperty("id").GetInt32() == id && t.TryGetProperty("codec", out var codec)
            && codec.GetString()?.Contains("pgs", StringComparison.OrdinalIgnoreCase) == true);
    }
    private static T Await<T>(Task<T> task) { Wait(() => task.IsCompleted, "network request", 90); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { Wait(() => task.IsCompleted, "session cleanup", 40); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition, string name, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) Pump(50);
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
    private static void Capture(UIElement element, string name)
    {
        element.UpdateLayout();
        var size = element.RenderSize;
        var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var directory = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }
}

/// <summary>A failed fixture check. Its message is fixed fixture text, never a URL, so the catch may
/// print it where production exceptions are reduced to their type.</summary>
internal sealed class FixtureCheckException(string message) : Exception(message);
