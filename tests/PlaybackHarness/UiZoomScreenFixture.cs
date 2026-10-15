using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Theme;
using LightWeaver.Updates;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Guest-only, one child process per simulated screen (LIGHTWEAVER_TEST_WORK_AREA=WxH, the work
// area of that screen in DIPs; the window fills it). Answers two questions per screen: what is the
// largest zoom at which every expected control of the app is present, fully on screen, not
// overlapping its neighbours and clickable (the functional maximum) - and does the app's screen
// cap stay at or below it while never giving the UI less room than the app's own 700x480 minimum
// window has at 100%? The screenshots it writes are for the visual review that chose the latter.
//
// The expected controls are listed per surface below. A control counts as passing when it is
// visible, at least 98% of it survives every clip between it and the window (scroll viewports,
// layout clipping, the window edge) after scrolling it into view, and a hit test at the centre of
// what is visible lands on it. Text that only trims with an ellipsis is reported as SOFT, not as a
// failure: the design trims long titles on purpose.
internal static class UiZoomScreenFixture
{
    /// <summary>The ladder from 100% up; the cap never goes below 100%.</summary>
    private static readonly double[] Steps = [1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    /// <summary>Work areas (DIPs, Windows 11 taskbar) of the screens the cap is tested on. Several
    /// physical screens share one: 1920x1080 at 150% lays out like 1280x720 at 100%.</summary>
    public static readonly (int Width, int Height, string Screens)[] Screens =
    [
        (1024, 720, "1024x768 at 100%"),
        (1280, 672, "1280x720 at 100%, 1920x1080 at 150%"),
        (1366, 720, "1366x768 at 100%"),
        (1280, 752, "1280x800 at 100%, 2560x1600 at 200%"),
        (1536, 816, "1920x1080 at 125%"),
        (1600, 852, "1600x900 at 100%"),
        (1707, 912, "2560x1440 at 150%"),
        (1920, 1032, "1920x1080 at 100%, 3840x2160 at 200%"),
    ];

    private sealed record Expect(string Name, FrameworkElement? Element, bool Required = true, bool Click = true);

    private sealed record Surface(string Name, Func<FrameworkElement> Root, Action Show, Action Hide,
        Func<List<Expect>> Expected, Func<List<List<FrameworkElement>>> Groups, Func<List<TextBlock>> Texts,
        Func<FrameworkElement> CaptureTarget);

    public static void Verify(MainWindow main, int width, int height)
    {
        var tag = $"{width}x{height}";
        var app = Field<AppViewModel>(main, "_app");
        var vm = Field<PlayerViewModel>(main, "_playerViewModel");
        var overlay = Field<OverlayWindow>(main, "_overlay");
        var settings = Field<AppSettings>(main, "_settings");
        var updates = Field<UpdateService>(main, "_updateService");
        var host = (ContentControl)main.FindName("ShellHost");
        var root = (FrameworkElement)main.Content;
        UiZoomFixture.Check(host.Content is LoginView, tag + ": a fresh process starts on the login page");
        var login = (LoginView)host.Content;

        // ---- signed-in chrome: rail with three libraries, header with history and a title ----
        var rail = (FrameworkElement)main.FindName("NavRail");
        var header = (FrameworkElement)main.FindName("BrowseHeader");
        var back = (FrameworkElement)main.FindName("BrowseBackBtn");
        var forward = (FrameworkElement)main.FindName("BrowseForwardBtn");
        var title = (TextBlock)main.FindName("BrowseTitle");
        var libraries = (Panel)main.FindName("LibrariesNav");
        var build = typeof(MainWindow).GetMethod("BuildLibraryNavItem", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var name in new[] { "Movies", "TV shows", "Family movies" })
            libraries.Children.Add((UIElement)build.Invoke(main, [new MediaItem
            {
                Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder,
                LibraryNameServer = "http://zoom-fixture.invalid/jellyfin",
            }])!);
        var railItems = new[] { "NavHome" }.Select(n => (FrameworkElement)main.FindName(n))
            .Concat(libraries.Children.OfType<FrameworkElement>())
            .Append((FrameworkElement)main.FindName("NavDownloads")).ToList();
        var railBottom = new[] { "NavStream", "NavSettings", "ProfileButton" }.Select(n => (FrameworkElement)main.FindName(n)).ToList();
        var headerItems = new[] { "BrowseBackBtn", "BrowseForwardBtn", "BrowseTitle", "AdvancedSearchBtn", "SearchPill", "SyncPlayButton" }
            .Select(n => (FrameworkElement)main.FindName(n)).ToList();
        void Chrome(bool on)
        {
            foreach (var element in new[] { rail, header, back, forward })
                element.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            title.Text = "Synthetic library";
        }
        List<Expect> ChromeExpected() =>
        [
            .. railItems.Select(e => new Expect("rail " + Label(e), e)),
            .. railBottom.Select(e => new Expect("rail " + Label(e), e)),
            new("header back", back), new("header forward", forward), new("header title", title, Click: false),
            new("header advanced search", (FrameworkElement)main.FindName("AdvancedSearchBtn")),
            new("header search box", (FrameworkElement)main.FindName("SearchBox")),
            new("header SyncPlay", (FrameworkElement)main.FindName("SyncPlayButton")),
        ];
        List<List<FrameworkElement>> ChromeGroups() => [headerItems, railItems, railBottom];

        var cards = Cards(out var grid);
        var settingsView = new SettingsView(settings, () => null, vm, updates);
        var detail = new ItemDetailView(app, settings, new MediaItem
        {
            Id = Guid.NewGuid(), Name = "Synthetic movie", Type = BaseItemDto_Type.Movie,
            Overview = "A synthetic movie page for the zoom fixture: a title, an overview and the action row.",
        });
        var shortcuts = (ShortcutsOverlay)main.FindName("Shortcuts");
        var modal = (ModalHost)main.FindName("Modal");
        FrameworkElement S(FrameworkElement owner, string name) => (FrameworkElement)owner.FindName(name);

        var surfaces = new List<Surface>
        {
            new("login", () => root,
                () => { Chrome(false); host.Content = login; ScrollTop(login); },
                () => { },
                () => [.. new[] { "ServerBox", "UsernameBox", "PasswordBox", "ConnectButton", "QuickConnectButton" }
                    .Select(n => new Expect("login " + n, S(login, n)))],
                () => [], () => [], () => root),
            new("library", () => root,
                () => { Chrome(true); host.Content = grid; ScrollTop(grid); },
                () => { },
                () => [.. ChromeExpected(), new("first poster card", cards[0]), new("second poster card", cards[1])],
                () => [.. ChromeGroups(), [rail, cards[0]]],
                () => [title], () => root),
            new("settings", () => root,
                () => { Chrome(true); host.Content = settingsView; ScrollTop(settingsView); },
                () => { },
                () => [.. new[] { "UiLanguageBox", "ZoomLimitBox", "UpdatePolicyBox", "VerboseLoggingBox" }
                    .Select(n => new Expect("settings " + n, S(settingsView, n)))],
                () => [], () => [], () => root),
            new("detail", () => root,
                () => { Chrome(true); host.Content = detail; ScrollTop(detail); },
                () => { },
                () =>
                [
                    new("detail title", S(detail, "TitleText"), Click: false),
                    new("detail play", S(detail, "PlayButton")),
                    .. new[] { "ResumeButton", "TrailerButton", "FavoriteButton", "WatchedButton", "DownloadButton", "VersionsButton", "UpNextButton", "MoreButton" }
                        .Select(n => new Expect("detail " + n, S(detail, n), Required: false)),
                ],
                () => [[.. new[] { "ResumeButton", "PlayButton", "TrailerButton", "FavoriteButton", "WatchedButton", "DownloadButton", "VersionsButton", "UpNextButton", "MoreButton" }
                    .Select(n => S(detail, n))]],
                () => [], () => root),
            new("shortcuts", () => root,
                () => { Chrome(true); host.Content = grid; shortcuts.Show(settings); Settle(shortcuts); },
                () => shortcuts.Hide(),
                () =>
                [
                    new("shortcuts title", Id(shortcuts, "ShortcutsTitle"), Click: false),
                    new("shortcuts close", S(shortcuts, "DismissButton")),
                    new("shortcuts list", S(shortcuts, "Scroller"), Click: false),
                    new("shortcuts first group", Id(shortcuts, "ShortcutGroupTitle"), Click: false),
                ],
                () => [[Id(shortcuts, "ShortcutsTitle"), S(shortcuts, "DismissButton")]],
                () => [], () => root),
            new("dialog", () => root,
                () =>
                {
                    Chrome(true);
                    host.Content = grid;
                    // The real warning the zoom limit toggle raises.
                    _ = modal.ConfirmAsync(L.Text("SettingsView.RemoveTheZoomLimit.71c3e0d4"),
                        L.Text("SettingsView.ZoomLimitWarning.a6f8125b"), L.Text("SettingsView.RemoveLimit.0b9d4e67"));
                    Settle(modal);
                },
                () => { S(modal, "CancelButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UiZoomFixture.Pump(350); },
                () =>
                [
                    new("dialog title", S(modal, "TitleText"), Click: false),
                    new("dialog body", S(modal, "BodyText"), Click: false),
                    new("dialog confirm", S(modal, "ConfirmButton")),
                    new("dialog cancel", S(modal, "CancelButton")),
                ],
                () => [[S(modal, "ConfirmButton"), S(modal, "CancelButton")]],
                () => [(TextBlock)S(modal, "TitleText")], () => root),
            new("player", () => (FrameworkElement)overlay.Content,
                () =>
                {
                    vm.SetNowPlaying("Synthetic media title", "Zoom fixture");
                    typeof(PlayerViewModel).GetField("_durationSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, 5400d);
                    typeof(ObservableObject).GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(vm, [nameof(PlayerViewModel.DurationSeconds)]);
                    vm.PositionSeconds = 1234;
                    if (app.State != AppState.Playing)
                        app.EnterPlayback();
                    UiZoomFixture.Pump();
                    typeof(MainWindow).GetMethod("SyncOverlayBounds", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
                    Wake(overlay);
                },
                // Back to browsing: the other surfaces live in the browse layer, which playback collapses.
                () => { app.LeavePlayback(); UiZoomFixture.Pump(); },
                () =>
                [
                    new("player back", S(overlay, "BackButton")),
                    new("player title", Id(overlay, "NowPlayingTitle"), Click: false),
                    new("player seek bar", S(overlay, "SeekSlider")),
                    new("player time", S(overlay, "TimeText"), Click: false),
                    new("player play/pause", S(overlay, "PauseButton")),
                    new("player jump back", Id(overlay, "JumpBackButton")),
                    new("player jump forward", Id(overlay, "JumpForwardButton")),
                    new("player mute", S(overlay, "MuteButton")),
                    new("player volume", S(overlay, "VolumeSlider")),
                    new("player fullscreen", S(overlay, "FullscreenButton")),
                    new("player mini-player", S(overlay, "MiniPlayerButton")),
                    new("player shortcuts", S(overlay, "ShortcutsButton")),
                    new("player info", S(overlay, "InfoButton")),
                    new("player speed", S(overlay, "SpeedButton")),
                    .. new[] { "PrevButton", "NextButton", "PrevChapterButton", "NextChapterButton", "AudioDeviceButton", "QueueButton",
                            "QualityButton", "SubtitleTracksButton", "AudioTracksButton", "VideoTracksButton", "TracksButton",
                            "RtxHdrToggle", "RtxVsrToggle", "OverlaySyncPlayButton" }
                        .Select(id => new Expect("player " + id, OptionalId(overlay, id), Required: false)),
                ],
                () =>
                [
                    [.. ((Panel)S(overlay, "ControlsRow")).Children.OfType<FrameworkElement>()],
                    [S(overlay, "BackButton"), S(overlay, "TitlePanel"), S(overlay, "GemPillPanel")],
                    [S(overlay, "SeekSlider"), S(overlay, "TimeText")],
                ],
                () => [(TextBlock)Id(overlay, "NowPlayingTitle")], () => overlay),
        };

        // ---- survey: every step, limit off ----
        UiZoom.SetLimitToScreen(false);
        var passed = new Dictionary<double, bool>();
        foreach (var step in Steps)
            passed[step] = true;
        foreach (var surface in surfaces)
        {
            surface.Show();
            foreach (var step in Steps)
            {
                UiZoom.Set(step);
                if (surface.Name == "player") Wake(overlay);
                UiZoomFixture.Pump();
                var (failures, soft) = Evaluate(surface);
                passed[step] &= failures.Count == 0;
                var pct = (int)Math.Round(step * 100);
                Console.WriteLine($"SURVEY: screen={tag} zoom={pct}% surface={surface.Name} "
                    + (failures.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", failures))
                    + (soft.Count == 0 ? "" : " | SOFT: " + string.Join("; ", soft)));
            }
            surface.Hide();
        }
        var maxPass = 1.0;
        foreach (var step in Steps)
        {
            if (!passed[step]) break;
            maxPass = step;
        }
        var layout = $"{root.ActualWidth:F0}x{root.ActualHeight:F0}";
        Console.WriteLine($"SURVEY: screen={tag} client={layout} highest-all-pass={(int)Math.Round(maxPass * 100)}% cap={Pct(UiZoom.ScreenCap)}%");
        UiZoomFixture.Check(passed[1.0], $"{tag}: every expected control passes at 100%");
        UiZoomFixture.Check(UiZoom.ScreenCap <= maxPass + 0.001,
            $"{tag}: the screen cap ({Pct(UiZoom.ScreenCap)}%) is within the functional maximum ({Pct(maxPass)}%)");
        // The cap's floor is the design minimum: the 700x480 window's client at 100%.
        var frame = new Size(main.ActualWidth - root.ActualWidth, main.ActualHeight - root.ActualHeight);
        var designMin = new Size(main.MinWidth - frame.Width, main.MinHeight - frame.Height);
        UiZoomFixture.Check(Math.Abs(UiZoom.MinLayoutWidth - designMin.Width) < 0.5 && Math.Abs(UiZoom.MinLayoutHeight - designMin.Height) < 0.5,
            $"{tag}: the minimum layout ({UiZoom.MinLayoutWidth}x{UiZoom.MinLayoutHeight}) is the 700x480 window's client at 100% ({designMin.Width:F0}x{designMin.Height:F0})");
        var client = new Size(width - frame.Width, height - frame.Height);
        var expected = Steps.Last(s => s <= 1.0 + 0.001 || (client.Width >= designMin.Width * s && client.Height >= designMin.Height * s));
        UiZoomFixture.Check(Math.Abs(UiZoom.ScreenCap - expected) < 0.001,
            $"{tag}: the screen cap ({Pct(UiZoom.ScreenCap)}%) is the highest step that keeps the design minimum ({Pct(expected)}%)");

        // ---- limit on: the ladder stops at the cap, and everything passes there ----
        UiZoom.Set(1.0);
        UiZoom.SetLimitToScreen(true);
        UiZoom.Set(UiZoom.Max);
        UiZoomFixture.Pump();
        UiZoomFixture.Check(Math.Abs(UiZoom.Scale - UiZoom.ScreenCap) < 0.001 && Math.Abs(UiZoom.Preferred - UiZoom.Max) < 0.001,
            $"{tag}: asking for 200% applies the cap ({UiZoom.Percent}%) and keeps 200% as the preferred level");
        UiZoom.StepIn();
        UiZoomFixture.Pump();
        var limitShown = main.FindName("ZoomPillLimit") is FrameworkElement { IsVisible: true };
        UiZoomFixture.Check(Math.Abs(UiZoom.Scale - UiZoom.ScreenCap) < 0.001 && limitShown == UiZoom.ScreenCap < UiZoom.Max - 0.001,
            $"{tag}: Ctrl+Plus at the cap stays there and the readout {(UiZoom.ScreenCap < UiZoom.Max - 0.001 ? "says Screen limit" : "shows no limit at 200%")}");
        var capPct = UiZoom.Percent;
        CaptureAll(surfaces, overlay, $"screen-{tag}-{capPct}-cap", tag + " at the cap", check: true);
        if (UiZoom.ScreenCap < UiZoom.Max - 0.001 && width == Screens[0].Width)
            VerifyLimitPaths(main, settingsView, settings, tag);

        // The smallest window the cap allows: the zoom raises MinWidth/MinHeight, so this is the
        // minimum layout itself, and everything must still pass.
        main.Width = 300;
        main.Height = 200;
        UiZoomFixture.Pump(300);
        var minClient = $"{root.ActualWidth:F0}x{root.ActualHeight:F0}";
        UiZoomFixture.Check(root.ActualWidth >= UiZoom.MinLayoutWidth * UiZoom.Scale - 1 && root.ActualHeight >= UiZoom.MinLayoutHeight * UiZoom.Scale - 1,
            $"{tag}: at the cap the window cannot shrink below the minimum layout ({minClient} client at {capPct}%)");
        CaptureAll(surfaces, overlay, $"screen-{tag}-{capPct}-min", tag + $" in the smallest window at the cap ({minClient})", check: true);
        main.Width = width;
        main.Height = height;
        main.Left = 0;
        main.Top = 0;
        UiZoomFixture.Pump(300);

        // One step past the cap, limit off: what the cap prevents, for the screenshot review.
        if (UiZoom.ScreenCap < UiZoom.Max - 0.001)
        {
            UiZoom.SetLimitToScreen(false);
            UiZoom.Set(Steps.First(s => s > UiZoom.ScreenCap + 0.001));
            UiZoomFixture.Pump();
            CaptureAll(surfaces, overlay, $"screen-{tag}-{UiZoom.Percent}-over", tag + " one step over the cap", check: false);
            UiZoom.SetLimitToScreen(true);
        }
        UiZoom.Set(1.0);
        UiZoomFixture.Pump();
    }

    /// <summary>The paths around the cap that the survey does not walk: the saved level under a
    /// cap, the Settings toggle's confirm and decline, the restore slide and the readout.</summary>
    private static void VerifyLimitPaths(MainWindow main, SettingsView view, AppSettings settings, string tag)
    {
        // Under the cap the applied scale is lower, but settings.json keeps what the user asked for.
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(Math.Abs(SettingsStore.Load().UiZoom - UiZoom.Max) < 0.001 && UiZoom.Scale < UiZoom.Max,
            $"{tag}: settings.json keeps the preferred 200% while the cap applies {UiZoom.Percent}%");
        // A preference change the cap hides from the screen is still the user's to keep.
        var applied = UiZoom.Scale;
        UiZoom.Set(1.5);
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(Math.Abs(SettingsStore.Load().UiZoom - 1.5) < 0.001 && Math.Abs(UiZoom.Scale - applied) < 0.001,
            $"{tag}: a preference change that leaves the capped zoom at {UiZoom.Percent}% is still saved (150%)");
        UiZoom.Reset();
        UiZoomFixture.Pump();
        UiZoomFixture.Check(main.FindName("ZoomPillLimit") is FrameworkElement { IsVisible: false },
            $"{tag}: Ctrl+0 never reports the screen limit");
        UiZoom.Set(UiZoom.Max);

        // The toggle: declining keeps the limit and the box on; confirming lifts it; on again restores it.
        var host = (ContentControl)main.FindName("ShellHost");
        host.Content = view;
        UiZoomFixture.Pump();
        var box = (CheckBox)view.FindName("ZoomLimitBox");
        var asked = 0;
        var answer = false;
        view.ConfirmAsync = (_, _, _) => { asked++; return Task.FromResult(answer); };
        box.IsChecked = false;
        UiZoomFixture.Pump();
        UiZoomFixture.Check(asked == 1 && box.IsChecked == true && UiZoom.LimitToScreen && SettingsStore.Load().LimitUiZoomToScreen,
            $"{tag}: declining the warning keeps the limit on, the toggle checked and the setting saved on");
        answer = true;
        box.IsChecked = false;
        UiZoomFixture.Pump();
        UiZoomFixture.Check(asked == 2 && box.IsChecked == false && !UiZoom.LimitToScreen && !SettingsStore.Load().LimitUiZoomToScreen
            && Math.Abs(UiZoom.Scale - UiZoom.Max) < 0.001,
            $"{tag}: confirming the warning lifts the limit, saves it off and applies the preferred 200%");
        box.IsChecked = true;
        UiZoomFixture.Pump();
        UiZoomFixture.Check(asked == 2 && UiZoom.LimitToScreen && SettingsStore.Load().LimitUiZoomToScreen
            && Math.Abs(UiZoom.Scale - UiZoom.ScreenCap) < 0.001,
            $"{tag}: turning the limit back on asks nothing and returns to the cap ({UiZoom.Percent}%)");
        view.ConfirmAsync = null;

        // Restore slide: a small window parked in the real screen's bottom-right corner is
        // maximized, the zoom rises (raising the minimum), and the restore must land on screen.
        var real = SystemParameters.WorkArea;
        UiZoom.Set(1.0);
        main.Width = 720;
        main.Height = 500;
        main.Left = real.Right - 730;
        main.Top = real.Bottom - 510;
        UiZoomFixture.Pump(300);
        main.WindowState = WindowState.Maximized;
        UiZoomFixture.Pump(300);
        UiZoom.Set(UiZoom.Max);
        UiZoomFixture.Pump(300);
        main.WindowState = WindowState.Normal;
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(main.ActualWidth >= main.MinWidth - 1 && main.ActualWidth > 720
            && main.Left + main.ActualWidth <= real.Right + 1 && main.Top + main.ActualHeight <= real.Bottom + 1
            && main.Left >= real.Left - 1 && main.Top >= real.Top - 1,
            $"{tag}: restoring after a zoom step while maximized grows the window to the new minimum and keeps it on screen ({main.Left:F0},{main.Top:F0} {main.ActualWidth:F0}x{main.ActualHeight:F0})");

        // And the slide's restraint: a window the user parks half off the screen stays put across
        // a minimize/restore and a zoom step out (which lowers the minimum).
        main.Left = real.Right - 300;
        UiZoomFixture.Pump(300);
        var parked = main.Left;
        main.WindowState = WindowState.Minimized;
        UiZoomFixture.Pump(300);
        main.WindowState = WindowState.Normal;
        UiZoomFixture.Pump(600);
        UiZoom.StepOut();
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(Math.Abs(main.Left - parked) < 1,
            $"{tag}: a window parked half off-screen stays put across minimize, restore and a zoom step out ({parked:F0} -> {main.Left:F0})");

        // A step in and back out while maximized leaves the remembered size at or above the
        // minimum, so the restore does not move the parked window either.
        main.WindowState = WindowState.Maximized;
        UiZoomFixture.Pump(300);
        UiZoom.StepIn();
        UiZoomFixture.Pump(300);
        UiZoom.StepOut();
        UiZoomFixture.Pump(300);
        main.WindowState = WindowState.Normal;
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(Math.Abs(main.Left - parked) < 1,
            $"{tag}: a zoom step in and out while maximized does not move the parked window on restore ({parked:F0} -> {main.Left:F0})");

        // A restored window already as large as a raised minimum does not grow, so a step in
        // leaves its placement alone too.
        var size = new Size(main.ActualWidth, main.ActualHeight);
        UiZoom.StepIn();
        UiZoomFixture.Pump(600);
        UiZoomFixture.Check(size.Width >= main.MinWidth - 0.5 && size.Height >= main.MinHeight - 0.5 && Math.Abs(main.Left - parked) < 1,
            $"{tag}: a zoom step in that does not grow the window leaves it parked ({parked:F0} -> {main.Left:F0}, {size.Width:F0} wide, minimum {main.MinWidth:F0})");
        main.WindowState = WindowState.Normal;
        main.Left = 0;
        main.Top = 0;
        main.Width = Screens[0].Width;
        main.Height = Screens[0].Height;
        UiZoomFixture.Pump(300);
    }

    private static void CaptureAll(List<Surface> surfaces, OverlayWindow overlay, string name, string label, bool check)
    {
        foreach (var surface in surfaces)
        {
            surface.Show();
            if (surface.Name == "player") Wake(overlay);
            UiZoomFixture.Pump();
            if (check)
            {
                var (failures, _) = Evaluate(surface);
                UiZoomFixture.Check(failures.Count == 0, $"{label}: {surface.Name} passes"
                    + (failures.Count == 0 ? "" : " - " + string.Join("; ", failures)));
            }
            ScrollTop(surface.Root());
            UiZoomFixture.Pump(100);
            CaptureOpaque(surface.CaptureTarget(), name + "-" + surface.Name);
            surface.Hide();
        }
    }

    // ---- the checks ----

    private static (List<string> Failures, List<string> Soft) Evaluate(Surface surface)
    {
        var failures = new List<string>();
        var soft = new List<string>();
        var root = surface.Root();
        root.UpdateLayout();
        foreach (var expect in surface.Expected())
        {
            if (expect.Element is not { } element)
            {
                if (expect.Required) failures.Add(expect.Name + " does not exist");
                continue;
            }
            // Collapsed or faded out is "not there": a failure only for a control that must be.
            // Visible but squeezed to nothing is a control the layout ran out of room for.
            if (!element.IsVisible || Opacity(element, root) < 0.5)
            {
                if (expect.Required) failures.Add(expect.Name + " is not shown");
                continue;
            }
            if (element.ActualWidth < 1 || element.ActualHeight < 1)
            {
                failures.Add(expect.Name + " has no room (laid out at zero size)");
                continue;
            }
            element.BringIntoView();
            root.UpdateLayout();
            var full = Bounds(element, root);
            var visible = VisibleRect(element, root);
            var fraction = visible.IsEmpty ? 0 : visible.Width * visible.Height / (full.Width * full.Height);
            if (fraction < 0.98)
            {
                failures.Add($"{expect.Name} is cut off ({fraction * 100:F0}% visible)");
                continue;
            }
            if (expect.Click && element.IsEnabled && !HitsSelf(root, element, new Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2)))
                failures.Add(expect.Name + " is covered at its centre (not clickable)");
        }
        foreach (var group in surface.Groups())
        {
            var shown = group.Where(e => e is { IsVisible: true, ActualWidth: >= 1, ActualHeight: >= 1 }).ToList();
            for (var i = 0; i < shown.Count; i++)
                for (var j = i + 1; j < shown.Count; j++)
                {
                    var a = VisibleRect(shown[i], root);
                    var b = VisibleRect(shown[j], root);
                    a.Intersect(b);
                    if (!a.IsEmpty && a.Width * a.Height > 1)
                        failures.Add($"{Label(shown[i])} overlaps {Label(shown[j])}");
                }
        }
        foreach (var text in surface.Texts())
            if (text.IsVisible && Trimmed(text))
                soft.Add($"{Label(text)} \"{text.Text}\" is trimmed to {text.ActualWidth:F0} px");
        return (failures, soft);
    }

    private static int Pct(double scale) => (int)Math.Round(scale * 100);

    private static Rect Bounds(FrameworkElement element, FrameworkElement root)
        => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>The element's bounds after every clip between it and the root: its own layout
    /// clip, each ancestor's (ClipToBounds, Clip, layout clip, scroll viewport) and the root.</summary>
    private static Rect VisibleRect(FrameworkElement element, FrameworkElement root)
    {
        var rect = Bounds(element, root);
        rect.Intersect(new Rect(root.RenderSize));
        for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, root) && !rect.IsEmpty;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement frame)
                continue;
            var clips = new List<Rect>();
            if (frame is ScrollContentPresenter || frame.ClipToBounds)
                clips.Add(new Rect(frame.RenderSize));
            if (frame.Clip is { } clip)
                clips.Add(clip.Bounds);
            if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(frame) is { } layoutClip)
                clips.Add(layoutClip.Bounds);
            foreach (var local in clips)
                rect.Intersect(frame.TransformToAncestor(root).TransformBounds(local));
        }
        return rect;
    }

    private static double Opacity(FrameworkElement element, FrameworkElement root)
    {
        var opacity = 1.0;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui) opacity *= ui.Opacity;
            if (ReferenceEquals(current, root)) break;
        }
        return opacity;
    }

    private static bool HitsSelf(FrameworkElement root, FrameworkElement element, Point point)
    {
        for (var hit = root.InputHitTest(point) as DependencyObject; hit is not null;
             hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
            if (ReferenceEquals(hit, element))
                return true;
        return false;
    }

    private static bool Trimmed(TextBlock text)
    {
        if (text.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(text.Text))
            return false;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 0.5;
    }

    // ---- surfaces and plumbing ----

    private static List<FrameworkElement> Cards(out ScrollViewer scroll)
    {
        var template = (DataTemplate)Application.Current.FindResource("PosterCardTemplate");
        var panel = new WrapPanel { Margin = new Thickness(40, 20, 28, 20) };
        var cards = new List<FrameworkElement>();
        for (var i = 0; i < 18; i++)
        {
            var card = (FrameworkElement)template.LoadContent();
            card.DataContext = new MediaItem { Id = Guid.NewGuid(), Name = "Synthetic poster " + (i + 1), Type = BaseItemDto_Type.Movie };
            panel.Children.Add(card);
            cards.Add(card);
        }
        scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return cards;
    }

    /// <summary>Waits out a panel's fade-in (the modal's is 480 ms). A fixed pause was not enough
    /// with four screen processes sharing the guest's CPU: the check then saw it half transparent.</summary>
    private static void Settle(UIElement panel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do UiZoomFixture.Pump(50);
        while ((!panel.IsVisible || panel.Opacity < 0.99) && DateTime.UtcNow < deadline);
        UiZoomFixture.Pump(100);
    }

    private static void Wake(OverlayWindow overlay)
    {
        Field<DispatcherTimer>(overlay, "_idleTimer").Stop();
        typeof(OverlayWindow).GetMethod("ShowControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(overlay, null);
        UiZoomFixture.Pump();
        Field<DispatcherTimer>(overlay, "_idleTimer").Stop();
    }

    private static void ScrollTop(DependencyObject root)
    {
        foreach (var scroll in Descendants(root).OfType<ScrollViewer>())
            scroll.ScrollToTop();
    }

    /// <summary>Captured alone, the player overlay is transparent over the video and the browse
    /// layer transparent over the window background: light text on nothing. Each is drawn over
    /// what it is seen on - black video, the window's own background.</summary>
    private static void CaptureOpaque(FrameworkElement target, string name)
    {
        target.UpdateLayout();
        var fill = target is Window ? Brushes.Black : Window.GetWindow(target).Background;
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle(fill, null, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        UiZoomFixture.Capture(target, name, backdrop);
    }

    private static string Label(FrameworkElement element)
    {
        var id = System.Windows.Automation.AutomationProperties.GetAutomationId(element);
        return id.Length > 0 ? id : element.Name.Length > 0 ? element.Name : element.GetType().Name;
    }

    internal static FrameworkElement Id(DependencyObject root, string id)
        => OptionalId(root, id) ?? throw new InvalidOperationException("Missing automation id " + id);

    private static FrameworkElement? OptionalId(DependencyObject root, string id)
        => Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    internal static T Field<T>(object owner, string name) where T : class
        => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
}
