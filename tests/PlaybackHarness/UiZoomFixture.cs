using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Theme;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Guest-only. App-wide UI zoom (Ctrl+Plus / Ctrl+Minus / Ctrl+wheel / Ctrl+0) on the production
// MainWindow and OverlayWindow, driven by native input. Synthetic settings and cards only: no
// credential, server, media fixture or playback.
//
// Every leg measures the SCREEN, not the scale property: a step that changed UiZoom.Scale but not
// what the user sees would pass a property check and fail here. Each size check is paired with a
// constant layout size, so "the element grew on screen" cannot be explained by a re-layout that
// simply made it wider.
internal static class UiZoomFixture
{
    public const string Mode = "--ui-zoom";
    public const string InputMode = "--ui-zoom-input";
    public const string RestartMode = "--ui-zoom-restart";
    public const string ScreenMode = "--ui-zoom-screen";
    private static int _assertions;
    private static int _failures;
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "lightweaver-tests");

    private const ushort VkControl = 0x11;
    private const ushort VkOemPlus = 0xBB;
    private const ushort VkOemMinus = 0xBD;
    private const ushort VkAdd = 0x6B;
    private const ushort Vk0 = 0x30;
    private const ushort VkSpace = 0x20;
    private const ushort VkK = 0x4B;

    public static int Run(string[] args)
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("The UI zoom fixture requires the approved Windows verification guest.");
        var child = args[0] != Mode;
        var screen = args[0] == ScreenMode;
        if (child && (args.Length != (screen ? 3 : 2) || !Path.IsPathFullyQualified(args[1])))
            throw new ArgumentException("Child process requires the isolated fixture directory.");
        var (screenWidth, screenHeight) = (0, 0);
        if (screen && (args[2].Split('x') is not [var w, var h] || !int.TryParse(w, out screenWidth) || !int.TryParse(h, out screenHeight)
            || Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_WORK_AREA") != args[2]))
            throw new ArgumentException("Screen child requires WxH matching LIGHTWEAVER_TEST_WORK_AREA.");
        var root = child ? args[1] : Path.Combine(Path.GetTempPath(), "lw-ui-zoom-" + Guid.NewGuid().ToString("N"));
        Require(Path.GetFileName(root).StartsWith("lw-ui-zoom-", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(root, "credentials.dat")), "fixture storage is temporary and has no credential file");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", root);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Directory.CreateDirectory(root);
        Require(AppPaths.Root == root && AppPaths.IsDebugEnvironment, "fixture uses isolated Debug app data");

        if (!child)
        {
            // Two processes: the static zoom state and the window's settings subscription belong
            // to one MainWindow per process, as in production, so a restart is a new process.
            SettingsStore.Save(new AppSettings());
            RunChild(InputMode, root, "input process: zoom chords, wheel, overlay and persistence pass");
            RunChild(RestartMode, root, "restart process: saved zoom applies from the first frame");
            RunScreens();
            Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_assertions} UI zoom assertions, {_failures} failures)");
            return _failures == 0 ? 0 : 1;
        }

        App? application = null;
        MainWindow? main = null;
        OverlayWindow? overlay = null;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Navigating += (_, e) => e.Cancel = true;
            application.InitializeComponent();
            // A screen child's window fills the simulated work area, as a maximized window would.
            main = screen
                ? new MainWindow { Width = screenWidth, Height = screenHeight, Left = 0, Top = 0 }
                : new MainWindow { Width = 1280, Height = 800, Left = 20, Top = 20 };
            main.Show();
            main.Activate();
            Pump(400);

            if (screen)
                UiZoomScreenFixture.Verify(main, screenWidth, screenHeight);
            else if (args[0] == RestartMode)
                VerifyRestart(main);
            else
                overlay = VerifyInput(main);
        }
        catch (Exception ex)
        {
            Check(false, "fixture exception: " + ex);
        }
        finally
        {
            try
            {
                overlay?.Close();
                main?.Close();
                application?.Shutdown();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_assertions} UI zoom assertions, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static OverlayWindow VerifyInput(MainWindow main)
    {
        Check(Math.Abs(UiZoom.Scale - 1.0) < 0.001, "fresh settings start at 100%");
        var browse = (FrameworkElement)main.FindName("BrowseLayer");
        var chrome = (FrameworkElement)main.FindName("ChromeLayer");
        var rail = (FrameworkElement)main.FindName("NavRail");
        Check(ReferenceEquals(browse.LayoutTransform, UiZoom.Transform) && ReferenceEquals(chrome.LayoutTransform, UiZoom.Transform),
            "browse and chrome layers carry the shared zoom transform");
        Check(main.FindName("VideoArea") is FrameworkElement video && video.LayoutTransform == Transform.Identity
            && !IsUnder(video, browse) && !IsUnder(video, chrome),
            "the mpv video layer is outside every zoomed layer");

        // Synthetic poster cards from the production template stand in for a library grid: what
        // the user means by "the thumbnails grow too".
        var cards = ShowCards(main);
        Pump(400);
        var card = cards[0];
        var cardLayout = card.ActualWidth;
        var cardScreen100 = ScreenWidth(card);
        var perRow100 = CardsInFirstRow(cards);
        Check(cardLayout > 100 && cardScreen100 > 100, $"baseline poster card is laid out ({cardLayout:F0} layout, {cardScreen100:F0} screen px)");
        Check(perRow100 >= 4, $"baseline grid fits several cards per row ({perRow100})");
        Check(Math.Abs((double)rail.Width - 232) < 0.5, "at 100% a 1280 px window keeps the expanded rail");
        Capture(main, "100");

        // Ctrl+Plus: one step to 110%.
        Chord(main, VkOemPlus);
        Check(Math.Abs(UiZoom.Scale - 1.1) < 0.001, "Ctrl+Plus steps 100% -> 110%");
        Check(Near(ScreenWidth(card), cardScreen100 * 1.1) && Near(card.ActualWidth, cardLayout),
            $"the poster card is 1.1x wider on screen at an unchanged layout width ({ScreenWidth(card):F1} vs {cardScreen100 * 1.1:F1})");
        Check(Near(browse.ActualWidth, main.ActualWidth / 1.1 - FrameInset(main) / 1.1, 3),
            $"the browse layer lays out in the window width divided by the zoom ({browse.ActualWidth:F0})");
        Check(PillText(main) == "110%", "the zoom readout shows 110%");
        Pump(600);   // the save is debounced by 400 ms
        Check(Math.Abs(SettingsStore.Load().UiZoom - 1.1) < 0.001, "the step is saved to settings.json");
        Check(Math.Abs((double)rail.Width - 232) < 0.5, "at 110% the 1280 px window (1163 layout px) keeps the expanded rail");

        // Numpad alias, then the breakpoint: 125% leaves 1024 layout px, below the 1160 rail threshold.
        Chord(main, VkAdd);
        Check(Math.Abs(UiZoom.Scale - 1.25) < 0.001, "Ctrl+numpad Plus steps 110% -> 125%");
        Check(Math.Abs((double)rail.Width - 64) < 0.5, "at 125% the rail collapses as a 1024 px window would");
        Check(CardsInFirstRow(cards) < perRow100, $"the card grid re-flows to fewer cards per row ({CardsInFirstRow(cards)} < {perRow100})");
        Check(Near(ScreenWidth(card), cardScreen100 * 1.25), "the poster card is 1.25x wider on screen");
        Capture(main, "125");

        // Ctrl+Minus three times: 125 -> 110 -> 100 -> 90.
        for (var i = 0; i < 3; i++) Chord(main, VkOemMinus);
        Check(Math.Abs(UiZoom.Scale - 0.9) < 0.001, "Ctrl+Minus x3 steps 125% -> 90%");
        Check(Near(ScreenWidth(card), cardScreen100 * 0.9), "the poster card is 0.9x on screen");
        Check(Math.Abs((double)rail.Width - 232) < 0.5, "zooming back out re-expands the rail");

        // Ctrl+wheel over the browse view: one notch up = one step.
        Check(Wheel(main, card, +120), "Ctrl+wheel was delivered over the fixture's own main window");
        Check(Math.Abs(UiZoom.Scale - 1.0) < 0.001, "Ctrl+wheel up steps 90% -> 100%");
        Check(Wheel(main, card, -120) && Math.Abs(UiZoom.Scale - 0.9) < 0.001, "Ctrl+wheel down steps 100% -> 90%");
        // A touchpad pinch or high-resolution wheel: half-notch events add up to one step.
        Check(Wheel(main, card, +60) && Math.Abs(UiZoom.Scale - 0.9) < 0.001, "half a notch does not step yet");
        Check(Wheel(main, card, +60) && Math.Abs(UiZoom.Scale - 1.0) < 0.001, "the second half-notch completes one step to 100%");

        // Ctrl+0 resets; the ladder clamps at both ends.
        Chord(main, Vk0);
        Check(Math.Abs(UiZoom.Scale - 1.0) < 0.001 && Near(ScreenWidth(card), cardScreen100), "Ctrl+0 resets to 100% and the original size");
        for (var i = 0; i < 14; i++) Chord(main, VkOemPlus);
        Check(Math.Abs(UiZoom.Scale - UiZoom.Max) < 0.001 && PillText(main) == "200%", "repeated Ctrl+Plus stops at 200%");
        Pump(2000);   // let the readout fade, then press once more at the limit
        Check(PillText(main) == "", "the zoom readout has faded and collapsed after its hold");
        Chord(main, VkOemPlus);
        Check(PillText(main) == "200%", "a press at the limit still shows the 200% readout");
        Capture(main, "200");
        for (var i = 0; i < 14; i++) Chord(main, VkOemMinus);
        Check(Math.Abs(UiZoom.Scale - UiZoom.Min) < 0.001, "repeated Ctrl+Minus stops at 50%");
        Chord(main, Vk0);

        VerifyShortcutConflicts(main);

        // The player overlay: zoomed with the app, NOT in mini mode, and Ctrl+wheel over the video
        // zooms instead of changing the volume (plain wheel there is volume).
        var model = new PlayerViewModel();
        var overlay = new OverlayWindow(model) { Owner = main, Left = 60, Top = 60, Width = 1100, Height = 650 };
        overlay.Show();
        Pump(400);
        var overlayRoot = (FrameworkElement)overlay.FindName("OverlayRoot");
        var back = (FrameworkElement)overlay.FindName("BackButton");
        Check(ReferenceEquals(overlayRoot.LayoutTransform, UiZoom.Transform), "the player overlay carries the shared zoom transform");
        var back100 = ScreenWidth(back);
        Chord(main, VkOemPlus);
        Chord(main, VkOemPlus);
        Pump(200);
        Check(Math.Abs(UiZoom.Scale - 1.25) < 0.001 && Near(ScreenWidth(back), back100 * 1.25),
            $"an open overlay's controls follow a zoom step ({ScreenWidth(back):F1} vs {back100 * 1.25:F1})");
        var volume = model.Volume;
        Check(Wheel(overlay, overlayRoot, +120, new Point(0.5, 0.4)), "Ctrl+wheel was delivered over the fixture's own overlay");
        Check(Math.Abs(UiZoom.Scale - 1.5) < 0.001, "Ctrl+wheel over the player steps 125% -> 150%");
        Check(model.Volume == volume, "Ctrl+wheel over the player leaves the volume alone");

        // Popups: WPF scales one only as it opens, so an open flyout must follow a later step
        // itself; tooltips and context menus open outside the tree and must take their owner's.
        var info = (System.Windows.Controls.Primitives.Popup)overlay.FindName("InfoPopup");
        info.IsOpen = true;
        Pump(300);
        var infoAt150 = DrawnScale(info.Child);
        UiZoom.StepIn();
        Pump(400);
        Check(Math.Abs(infoAt150 - 1.5) < 0.01 && Math.Abs(DrawnScale(info.Child) - 1.75) < 0.01,
            $"an open flyout follows a zoom step ({infoAt150:F2} -> {DrawnScale(info.Child):F2})");
        info.IsOpen = false;
        Pump(200);
        info.IsOpen = true;
        Pump(300);
        Check(Math.Abs(DrawnScale(info.Child) - 1.75) < 0.01, $"reopened, the flyout is drawn at the zoom once ({DrawnScale(info.Child):F2})");
        info.IsOpen = false;
        UiZoom.StepOut();
        Pump(300);
        var tip = new ToolTip { Content = "zoom", PlacementTarget = back };
        tip.IsOpen = true;
        Pump(300);
        Check(Math.Abs(DrawnScale(tip) - 1.5) < 0.01, $"a tooltip is drawn at its owner's zoom ({DrawnScale(tip):F2})");
        tip.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = back };
        menu.Items.Add(new MenuItem { Header = "zoom" });
        menu.IsOpen = true;
        Pump(300);
        Check(Math.Abs(DrawnScale(menu) - 1.5) < 0.01, $"a context menu is drawn at its owner's zoom ({DrawnScale(menu):F2})");
        menu.IsOpen = false;
        Pump(200);
        // A popup created in code has no place in the tree: it takes its target's zoom. Reopened
        // during its close fade, WPF raises Opened again without Closed; the correction must not
        // stack, and closing must leave nothing subscribed to the static zoom event.
        var subscribers = ZoomSubscribers();
        var loose = new System.Windows.Controls.Primitives.Popup
        {
            Child = new Border { Width = 120, Height = 40, Background = Brushes.Gray },
            PlacementTarget = back,
            AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        ZoomPopups.SetFollow(loose, true);
        loose.IsOpen = true;
        Pump(300);
        var looseOpened = DrawnScale(loose.Child);
        UiZoom.StepIn();
        Pump(400);
        var looseStepped = DrawnScale(loose.Child);
        loose.IsOpen = false;
        loose.IsOpen = true;
        Pump(400);
        Check(Math.Abs(looseOpened - 1.5) < 0.01 && Math.Abs(looseStepped - 1.75) < 0.01 && Math.Abs(DrawnScale(loose.Child) - 1.75) < 0.01,
            $"a popup created in code opens at its target's zoom, follows a step and survives a reopen mid-fade ({looseOpened:F2}, {looseStepped:F2}, {DrawnScale(loose.Child):F2})");
        loose.IsOpen = false;
        UiZoom.StepOut();
        Pump(600);
        Check(ZoomSubscribers() == subscribers, $"closed popups leave no zoom subscription behind ({subscribers} -> {ZoomSubscribers()})");
        overlay.ShowZoomPill();
        Pump(200);
        Check(overlay.FindName("ZoomPill") is FrameworkElement { Visibility: Visibility.Visible }
            && overlay.FindName("ZoomPillText") is TextBlock { Text: "150%" }, "the player's own zoom readout shows 150%");
        overlay.SetMiniMode(true);
        Pump(200);
        Check(overlayRoot.LayoutTransform == Transform.Identity, "mini mode drops the zoom from the overlay");
        overlay.SetMiniMode(false);
        Pump(200);
        Check(ReferenceEquals(overlayRoot.LayoutTransform, UiZoom.Transform), "leaving mini mode restores the zoom");
        Pump(600);
        Check(Math.Abs(SettingsStore.Load().UiZoom - 1.5) < 0.001, "150% is the saved level the restart process reads");
        return overlay;
    }

    /// <summary>The Settings key editor, driven by native presses: a key an app shortcut owns
    /// (Ctrl+Plus is Zoom in) is refused with a note, as the main window would never let a player
    /// binding have it, while Space stays open to Play / pause, whose chrome twin it is. A saved
    /// binding that already collides is flagged, not rewritten.</summary>
    private static void VerifyShortcutConflicts(MainWindow main)
    {
        string? Owner(string chord, PlayerAction action) => AppShortcuts.OwnerOf(chord, action)?.Id;
        Check(Owner("Ctrl+OemPlus", PlayerAction.SpeedUp) == AppShortcuts.ZoomIn
            && Owner("Ctrl+NumPad0", PlayerAction.SpeedUp) == AppShortcuts.ZoomReset
            && Owner("Ctrl+M", PlayerAction.SpeedDown) == AppShortcuts.MiniPlayer
            && Owner("Back", PlayerAction.SkipSegment) == AppShortcuts.BrowseBack,
            "listed app chords and numpad aliases own their keys");
        Check(AppShortcuts.OwnerOf("Ctrl+Shift+OemPlus", PlayerAction.SpeedUp) is { Id: AppShortcuts.ZoomIn, Function.Length: > 0 },
            "an alias answers with its listed entry, whose name the note prints");
        Check(Owner("Space", PlayerAction.TogglePause) is null && Owner("Space", PlayerAction.SeekForward) == AppShortcuts.TogglePause
            && Owner("F11", PlayerAction.ToggleFullscreen) is null && Owner("F11", PlayerAction.CycleAudio) == AppShortcuts.Fullscreen,
            "Space and F11 stay open only to the action their chrome twin duplicates");
        Check(Owner("Shift+OemQuestion", PlayerAction.SpeedUp) is null && Owner("OemPlus", PlayerAction.SpeedUp) is null
            && Owner("", PlayerAction.SpeedUp) is null,
            "the unregistered '?', a plain key and an unbound row own nothing");
        Check(PlayerActionDispatcher.Defaults.All(d => Owner(d.Value, d.Key) is null), "no shipped default collides with an app shortcut");

        var settings = UiZoomScreenFixture.Field<AppSettings>(main, "_settings");
        var host = (ContentControl)main.FindName("ShellHost");
        var previous = host.Content;
        SettingsView Show()
        {
            var view = new SettingsView(settings, () => null,
                UiZoomScreenFixture.Field<PlayerViewModel>(main, "_playerViewModel"),
                UiZoomScreenFixture.Field<LightWeaver.Updates.UpdateService>(main, "_updateService"));
            host.Content = view;
            Pump(400);
            return view;
        }
        Button StartCapture(SettingsView view, PlayerAction action)
        {
            var button = (Button)UiZoomScreenFixture.Id(view, "Shortcut" + action);
            button.BringIntoView();
            button.Focus();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(100);
            return button;
        }
        TextBlock Note(SettingsView view, PlayerAction action) => (TextBlock)UiZoomScreenFixture.Id(view, "ShortcutNote" + action);
        string Bound(PlayerAction action) => PlayerActionDispatcher.Resolve(SettingsStore.Load().KeyBindings)[action];
        string ZoomIn() => AppShortcuts.Chrome.Single(c => c.Id == AppShortcuts.ZoomIn).Function;
        string PlayPause() => AppShortcuts.Chrome.Single(c => c.Id == AppShortcuts.TogglePause).Function;

        var settingsView = Show();
        var speedUp = StartCapture(settingsView, PlayerAction.SpeedUp);
        Chord(main, VkOemPlus);
        var note = Note(settingsView, PlayerAction.SpeedUp);
        Check(Math.Abs(UiZoom.Scale - 1.0) < 0.001, "while Settings waits for a key, Ctrl+Plus goes to the key editor, not the zoom");
        Check(Bound(PlayerAction.SpeedUp) == "OemPlus" && Equals(speedUp.Content, PlayerActionDispatcher.DisplayChord("OemPlus")),
            $"Ctrl+Plus is refused for Speed up, which keeps + ({Bound(PlayerAction.SpeedUp)})");
        Check(note.IsVisible && note.Text == L.Format("SettingsView.ShortcutTakenByApp.5c0e93a7",
                PlayerActionDispatcher.DisplayChord("Ctrl+OemPlus"), ZoomIn()),
            $"the row says Zoom in has Ctrl+Plus (\"{note.Text}\")");
        note.BringIntoView();
        Pump(200);
        Capture(main, "shortcut-refused");

        StartCapture(settingsView, PlayerAction.SeekForward);
        Press(main, VkSpace);
        Check(Bound(PlayerAction.SeekForward) == "Right" && Note(settingsView, PlayerAction.SeekForward).Text
                == L.Format("SettingsView.ShortcutTakenByApp.5c0e93a7", PlayerActionDispatcher.DisplayChord("Space"), PlayPause()),
            "Space is refused for Seek forward: the main window's Space always pauses");

        StartCapture(settingsView, PlayerAction.TogglePause);
        Press(main, VkK);
        StartCapture(settingsView, PlayerAction.TogglePause);
        Press(main, VkSpace);
        Check(Bound(PlayerAction.TogglePause) == "Space" && !Note(settingsView, PlayerAction.TogglePause).IsVisible,
            "Play / pause can move to K and back to Space, the key its chrome twin shares");

        StartCapture(settingsView, PlayerAction.SpeedUp);
        Press(main, VkK);
        Check(Bound(PlayerAction.SpeedUp) == "K" && !note.IsVisible, "a free key binds and clears the refusal note");

        // A pre-update or hand-edited settings.json can already hold a collision.
        settings.KeyBindings = new Dictionary<string, string> { [nameof(PlayerAction.SpeedDown)] = "Ctrl+M" };
        SettingsStore.Save(settings);
        var reopened = Show();
        var saved = Note(reopened, PlayerAction.SpeedDown);
        Check(saved.IsVisible && saved.Text == L.Format("SettingsView.ShortcutSharedWithApp.b17d4e2f",
                PlayerActionDispatcher.DisplayChord("Ctrl+M"), AppShortcuts.Chrome.Single(c => c.Id == AppShortcuts.MiniPlayer).Function)
            && Bound(PlayerAction.SpeedDown) == "Ctrl+M",
            $"a saved Ctrl+M on Speed down is flagged and left as saved (\"{saved.Text}\")");
        Check(!Note(reopened, PlayerAction.SpeedUp).IsVisible && !Note(reopened, PlayerAction.TogglePause).IsVisible,
            "rows without a collision show no note");
        saved.BringIntoView();
        Pump(200);
        Capture(main, "shortcut-shared");

        settings.KeyBindings = new Dictionary<string, string>();
        SettingsStore.Save(settings);
        host.Content = previous;
        Pump(200);
    }

    private static void VerifyRestart(MainWindow main)
    {
        Check(Math.Abs(UiZoom.Scale - 1.5) < 0.001, "a fresh process starts at the saved 150%");
        var browse = (FrameworkElement)main.FindName("BrowseLayer");
        var rail = (FrameworkElement)main.FindName("NavRail");
        Check(Near(browse.ActualWidth, (main.ActualWidth - FrameInset(main)) / 1.5, 3),
            $"its first layout already uses the zoomed width ({browse.ActualWidth:F0})");
        Check(Math.Abs((double)rail.Width - 64) < 0.5, "the restored zoom applies the rail breakpoint from startup");
        Capture(main, "restart-150");
    }

    // ---- synthetic content ----

    private static List<FrameworkElement> ShowCards(MainWindow main)
    {
        var template = (DataTemplate)Application.Current.FindResource("PosterCardTemplate");
        var panel = new WrapPanel { Margin = new Thickness(40, 20, 28, 20) };
        var cards = new List<FrameworkElement>();
        for (var i = 0; i < 12; i++)
        {
            var card = (FrameworkElement)template.LoadContent();
            card.DataContext = new MediaItem
            {
                Id = Guid.NewGuid(), Name = "Synthetic poster " + (i + 1),
                Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
            };
            panel.Children.Add(card);
            cards.Add(card);
        }
        ((ContentControl)main.FindName("ShellHost")).Content = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return cards;
    }

    private static int CardsInFirstRow(List<FrameworkElement> cards)
    {
        var parent = (FrameworkElement)VisualTreeHelper.GetParent(cards[0]);
        var firstTop = cards[0].TranslatePoint(new Point(), parent).Y;
        return cards.Count(c => Math.Abs(c.TranslatePoint(new Point(), parent).Y - firstTop) < 1);
    }

    private static string PillText(MainWindow main)
        => main.FindName("ZoomPill") is FrameworkElement { Visibility: Visibility.Visible }
            && main.FindName("ZoomPillText") is TextBlock text ? text.Text : "";

    // ---- measurement ----

    /// <summary>The scale an element is drawn at in its own window or popup: how far a unit step
    /// along X travels to the root, which includes the popup root's own transform.</summary>
    private static double DrawnScale(Visual? visual)
    {
        if (visual is null || PresentationSource.FromVisual(visual)?.RootVisual is not { } root || ReferenceEquals(root, visual))
            return double.NaN;
        var transform = visual.TransformToAncestor(root);
        return (transform.Transform(new Point(1, 0)) - transform.Transform(new Point(0, 0))).Length;
    }

    private static int ZoomSubscribers()
        => (typeof(UiZoom).GetField(nameof(UiZoom.Changed), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;

    private static double ScreenWidth(FrameworkElement element)
    {
        element.UpdateLayout();
        var left = element.PointToScreen(new Point(0, 0));
        var right = element.PointToScreen(new Point(element.ActualWidth, 0));
        return right.X - left.X;
    }

    /// <summary>Window width minus its client width (the resize frame), so the expected browse width
    /// is computed from the client area the layers actually get.</summary>
    private static double FrameInset(Window window)
        => window.ActualWidth - ((FrameworkElement)window.Content).ActualWidth;

    private static bool Near(double actual, double expected, double tolerance = 1.5)
        => Math.Abs(actual - expected) <= tolerance;

    private static bool IsUnder(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    // ---- native input, only ever into this process's own foreground window ----

    private static void Chord(MainWindow main, ushort virtualKey)
    {
        AssertForeground(main);
        Send([Key(VkControl, false), Key(virtualKey, false), Key(virtualKey, true), Key(VkControl, true)]);
        Pump(250);
    }

    private static void Press(MainWindow main, ushort virtualKey)
    {
        AssertForeground(main);
        Send([Key(virtualKey, false), Key(virtualKey, true)]);
        Pump(250);
    }

    /// <summary>Ctrl + one wheel notch at a point inside <paramref name="target"/>, after proving
    /// that the point belongs to <paramref name="window"/>. Returns false, sending nothing, if not.</summary>
    private static bool Wheel(Window window, FrameworkElement target, int delta, Point? fraction = null)
    {
        // Keyboard input goes to the active window; the owner is active while its overlay is hit.
        AssertForeground(window.Owner ?? window);
        var f = fraction ?? new Point(0.5, 0.5);
        var point = target.PointToScreen(new Point(target.ActualWidth * f.X, target.ActualHeight * f.Y));
        var hit = GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2);
        if (hit != new WindowInteropHelper(window).Handle)
            return false;
        var x = (int)((point.X - GetSystemMetrics(76)) * 65535.0 / (GetSystemMetrics(78) - 1));
        var y = (int)((point.Y - GetSystemMetrics(77)) * 65535.0 / (GetSystemMetrics(79) - 1));
        const uint moveAbsolute = 0x0001 | 0x8000 | 0x4000;
        Send([new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute } } }]);
        Pump(150);
        Send([Key(VkControl, false),
              new() { Data = new() { Mouse = new() { Data = unchecked((uint)delta), Flags = 0x0800 } } },
              Key(VkControl, true)]);
        Pump(250);
        return true;
    }

    private static NativeInput Key(ushort virtualKey, bool up)
        => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey, Flags = up ? 2u : 0u } } };

    private static void AssertForeground(Window window)
    {
        window.Activate();
        Pump(100);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (foreground != Environment.ProcessId)
            throw new InvalidOperationException("Native input requires the fixture process in the foreground.");
    }

    private static void Send(NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new InvalidOperationException("Native input was not delivered.");
    }

    // ---- plumbing ----

    private static void RunChild(string mode, string root, string description)
    {
        var (exitCode, output) = StartChild(mode, root, null).GetAwaiter().GetResult();
        Console.Write(output);
        Check(exitCode == 0, description);
    }

    /// <summary>One child per simulated screen, each with its own app data. They need no input
    /// focus (layout, hit tests and captures are all in-process), so four run at once; their
    /// output is printed in screen order.</summary>
    private static void RunScreens()
    {
        using var slots = new SemaphoreSlim(4);
        var runs = UiZoomScreenFixture.Screens.Select(async screen =>
        {
            await slots.WaitAsync();
            try
            {
                var size = $"{screen.Width}x{screen.Height}";
                var root = Path.Combine(Path.GetTempPath(), "lw-ui-zoom-" + Guid.NewGuid().ToString("N"));
                return (screen, await StartChild(ScreenMode, root, size));
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        foreach (var run in runs)
        {
            var (screen, (exitCode, output)) = run.GetAwaiter().GetResult();
            Console.Write(output);
            Check(exitCode == 0, $"screen {screen.Width}x{screen.Height} ({screen.Screens}): every expected control passes at the cap, and the cap is the highest level they all pass at");
        }
    }

    private static async Task<(int ExitCode, string Output)> StartChild(string mode, string root, string? screen)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        if (screen is not null)
        {
            start.ArgumentList.Add(screen);
            start.Environment["LIGHTWEAVER_TEST_WORK_AREA"] = screen;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child process was not started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return (-1, await stdout + await stderr + $"FAIL: UI zoom child {mode} {screen} did not finish in 240 s\n");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    internal static void Capture(FrameworkElement visual, string name, Visual? backdrop = null)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(visual.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        if (backdrop is not null)
            bitmap.Render(backdrop);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Output);
        var path = Path.Combine(Output, "ui-zoom-" + name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("CAPTURE: " + path);
    }

    internal static void Pump(int milliseconds = 150)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Check(bool condition, string name)
    {
        _assertions++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public NativeMouse Mouse;
        [FieldOffset(0)] public NativeKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nint Extra; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
