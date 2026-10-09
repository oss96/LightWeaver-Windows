using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Guest-only. All translations and settings are synthetic, and no server is contacted.
internal static class LocalizationUiFixture
{
    public const string Mode = "--localization-ui";
    public const string RestartMode = "--localization-ui-restart";
    public const string GermanMode = "--localization-ui-german";
    public const string EnglishMode = "--localization-ui-english";
    public const string UpNextMode = "--localization-ui-up-next";
    public const string UpNextGermanMode = "--localization-ui-up-next-german";
    public const string UpNextEpisodeMode = "--localization-ui-up-next-episode";
    public const string UpNextEpisodeGermanMode = "--localization-ui-up-next-episode-german";
    private static int _assertions;
    private static int _failures;
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
    private const string CollidingCategoryLabel = "Gemeinsame Medienkategorie";
    private static StringCatalog Catalog => Field<StringCatalog>(typeof(L), "Catalog");

    public static int Run(string[] args)
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Run localization UI through the lw-localization guest MCP route.");
        var restarting = args[0] == RestartMode;
        var upNextEpisode = args[0] is UpNextEpisodeMode or UpNextEpisodeGermanMode;
        var upNext = upNextEpisode || args[0] is UpNextMode or UpNextGermanMode;
        var german = args[0] is GermanMode or UpNextGermanMode or UpNextEpisodeGermanMode;
        var child = args[0] != Mode;
        if (child && (args.Length != 2 || !Path.IsPathFullyQualified(args[1])))
            throw new ArgumentException("Child process requires the isolated fixture directory.");
        var root = child ? args[1] : Path.Combine(Path.GetTempPath(), "lw-localization-" + Guid.NewGuid().ToString("N"));
        Require(Path.GetDirectoryName(Path.GetFullPath(root))!.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(root).StartsWith("lw-localization-", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(root, "credentials.dat")), "fixture storage is temporary and has no credential file");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", root);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Directory.CreateDirectory(root);
        Require(AppPaths.Root == root && AppPaths.IsDebugEnvironment, "fixture uses isolated Debug app data");
        App? application = null;
        MainWindow? main = null;
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (!child)
            {
                // Each child starts with a clean set of WPF resources and static view choices.
                RunChild(EnglishMode, root, "fresh English process UI checks pass");
                RunChild(GermanMode, root, "fresh German process UI checks pass");
                RunChild(RestartMode, root, "fresh process language persistence and translated login pass");
                // Own processes and time budgets, after the restart check reads the saved language.
                RunChild(UpNextMode, root, "fresh English process Up Next lead checks pass");
                RunChild(UpNextGermanMode, root, "fresh German process Up Next lead checks pass");
                RunChild(UpNextEpisodeMode, root, "fresh English process episode-page Up Next checks pass");
                RunChild(UpNextEpisodeGermanMode, root, "fresh German process episode-page Up Next checks pass");
            }
            else
            {
                InstallTranslations();
                var language = german || restarting ? "de" : "en";
                if (restarting)
                    Check(SettingsStore.Load().UiLanguage == "de", "fresh process loads saved language selection");
                else
                    SettingsStore.Save(new AppSettings { UiLanguage = language });

                // Use the production constructor's culture ordering, then load real App.xaml.
                // Cancel default startup navigation because the fixture owns its main window.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Check(SynchronizationContext.Current is DispatcherSynchronizationContext,
                    language + " fixture installs the WPF dispatcher context before production UI constructors");
                application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var startupNavigationCanceled = false;
                application.Navigating += (_, e) =>
                {
                    e.Cancel = true;
                    startupNavigationCanceled = true;
                };
                Check(L.Language == language, language + " production App constructor selects saved culture before dictionaries");
                application.InitializeComponent();
                main = new MainWindow { Width = 1280, Height = 800, Left = 20, Top = 20 };
                main.Show();
                Pump();
                Check(startupNavigationCanceled, language + " fixture cancels production StartupUri navigation");
                Check(application.Windows.OfType<MainWindow>().SequenceEqual(new[] { main })
                    && ReferenceEquals(application.MainWindow, main),
                    language + " fixture owns exactly one production main window after startup");
                var card = (FrameworkElement)((DataTemplate)application.FindResource("PosterCardTemplate")).LoadContent();
                card.DataContext = new LightWeaver.Jellyfin.MediaItem
                {
                    Id = Guid.NewGuid(), Name = "Synthetic template item", Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
                };
                Check(Descendants(card).OfType<Button>().Any(button => button.ToolTip is string tooltip
                    && tooltip == (language == "de" ? Translation("MediaTemplates", "Play") : "Play")),
                    language + " production MediaTemplates dictionary creates translated card tooltip");
                if (restarting)
                {
                    Check(HasText(main, Translation("LoginView", "USERNAME")), "fresh process renders translated login XAML");
                    Capture(main, "de-restart-login");
                }
                else if (upNext)
                    UpNextUiFixture.Verify(main, language, upNextEpisode, Check, Capture, Pump);
                else
                    VerifyLanguage(main, language);
            }
        }
        catch (Exception ex)
        {
            Check(false, "fixture exception: " + ex);
        }
        finally
        {
            try
            {
                main?.Close();
                application?.Shutdown();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_assertions} localization UI assertions, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void InstallTranslations()
    {
        var translations = Field<Dictionary<string, TranslationFile>>(typeof(L), "Translations");
        var file = new TranslationFile { Language = "de" };
        foreach (var (key, entry) in Catalog.Strings)
        {
            // One real label deliberately has no translation, exercising English fallback.
            if (key.StartsWith("LoginView.", StringComparison.Ordinal) && entry.Text == "PASSWORD") continue;
            file.Strings[key] = entry.Text switch
            {
                "Sign in to your Jellyfin server" => "Mit Ihrem persönlichen Jellyfin-Medienserver verbinden",
                "USERNAME" => "Benutzername für die Anmeldung",
                "SERVER" => "Adresse Ihres Medienservers",
                "Sign in" => "Jetzt am Medienserver anmelden",
                "Settings" => "Programmeinstellungen",
                "Home" => "Startseite und Medienübersicht",
                "Downloads" => "Heruntergeladene Medien",
                "Close" => "Fenster schließen",
                "Refresh" => "Gruppenliste aktualisieren",
                "Create" => "Neue Gruppe erstellen",
                "Create a group" => "Gemeinsame Wiedergabegruppe erstellen",
                "Join group" => "Dieser Wiedergabegruppe beitreten",
                "Available groups" => "Verfügbare Wiedergabegruppen",
                _ => "DE " + entry.Text,
            };
            file.SourceHashes[key] = CatalogJson.Hash(entry.Text);
        }
        file.Strings["SearchResultsView.Movies.049dbe79"] = CollidingCategoryLabel;
        file.Strings["SearchResultsView.Shows.c9405784"] = CollidingCategoryLabel;
        translations["de"] = file;
    }

    private static void VerifyLibraryNames(MainWindow main, SettingsView view, AppSettings settings,
        string originalLanguage, ScrollViewer scroll)
    {
        const string server = "http://library-fixture.invalid/jellyfin";
        var movies = new MediaItem { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, LibraryNameServer = server };
        var shows = new MediaItem { Id = Guid.NewGuid(), Name = "TV-shows", Type = BaseItemDto_Type.CollectionFolder, LibraryNameServer = server };
        var custom = new MediaItem { Id = Guid.NewGuid(), Name = "Family movies", Type = BaseItemDto_Type.CollectionFolder, LibraryNameServer = server };
        var movie = new MediaItem { Id = movies.Id, Name = "Movies", Type = BaseItemDto_Type.Movie };
        view.SetLibraries(server, [movies, shows, custom]);
        Element<Expander>(view, "LibraryNamesExpander").IsExpanded = true;
        var box = Element<ComboBox>(view, "UiLanguageBox");
        var host = Element<ContentControl>(main, "ShellHost");
        var rail = (RadioButton)typeof(MainWindow).GetMethod("BuildLibraryNavItem", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [movies])!;
        typeof(MainWindow).GetMethod("OnRailLibraryClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [movies, rail]);
        var shell = Field<object>(main, "_shell");
        var nav = (NavigationService)shell.GetType().GetProperty("Nav")!.GetValue(shell)!;
        var frame = nav.Current!;
        host.Content = view;
        var card = (FrameworkElement)((DataTemplate)Application.Current.FindResource("LibraryCardTemplate")).LoadContent();
        card.DataContext = movies;
        LiveText.Watch(card);
        var row = Element<StackPanel>(view, "LibraryNameRows");
        row.Children.Add(card);
        Pump();
        row.Children.Remove(card);
        foreach (var code in new[] { "en", "de", "ar", "de" })
        {
            box.SelectedItem = box.Items.Cast<UiLanguage>().Single(choice => choice.Code == code);
            Pump();
            var expected = code == "en" ? "Movies" : L.Text("LibraryNames.Movies");
            Check(movies.LibraryDisplayName == expected && shows.LibraryDisplayName == (code == "en" ? "TV-shows" : L.Text("LibraryNames.TvShows")), code + " conventional library names translate");
            Check(Equals(rail.Content, expected) && Equals(rail.ToolTip, expected) && HasText(card, expected) && frame.DisplayTitle == expected,
                code + " existing rail, tooltip, Home template and cached library frame refresh");
            Check(movie.LibraryDisplayName == "Movies" && custom.LibraryDisplayName == "Family movies" && movies.Name == "Movies",
                code + " movie titles, custom library names and original server metadata stay intact");
            Check(System.Windows.Automation.AutomationProperties.GetAutomationId(rail) == "NavLibrary_Movies",
                code + " library automation identity stays invariant");
        }
        TextBox MovieField() => Descendants(view).OfType<TextBox>().Single(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "LibraryName_" + movies.Id.ToString("N"));
        MovieField().Text = "Meine Filme";
        var save = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "SaveLibraryNames");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(movies.LibraryDisplayName == "Meine Filme" && Equals(rail.Content, "Meine Filme") && frame.DisplayTitle == "Meine Filme" && HasText(card, "Meine Filme"),
            "saving a custom name refreshes all existing library displays immediately");
        LibraryNames.Configure(SettingsStore.Load());
        Check(movies.LibraryDisplayName == "Meine Filme", "custom library name survives settings serialization and reload");
        LibraryNames.Configure(settings);
        var otherServer = new MediaItem { Id = movies.Id, Name = "Movies", Type = movies.Type, LibraryNameServer = "http://other.invalid" };
        var otherLibrary = new MediaItem { Id = Guid.NewGuid(), Name = "Movies", Type = movies.Type, LibraryNameServer = server };
        Check(otherServer.LibraryDisplayName == L.Text("LibraryNames.Movies") && otherLibrary.LibraryDisplayName == L.Text("LibraryNames.Movies"),
            "aliases are isolated by server and library ID");
        L.Initialize("de-AT");
        Check(movies.LibraryDisplayName == "Meine Filme", "regional language inherits neutral library alias");
        LibraryNames.Set(server, movies.Id, "de-AT", "Regionale Filme");
        Check(movies.LibraryDisplayName == "Regionale Filme", "exact regional library alias wins");
        LibraryNames.Set(server, movies.Id, "de-AT", "");
        L.Initialize("de");
        box.SelectedItem = box.Items.Cast<UiLanguage>().Single(choice => choice.Code == "ar");
        Pump();
        Check(movies.LibraryDisplayName == L.Text("LibraryNames.Movies"), "German alias does not leak into Arabic");
        CaptureFocusedSizes(main, scroll, "ar-library-names", Element<StackPanel>(view, "LibraryNameRows"), save);
        box.SelectedItem = box.Items.Cast<UiLanguage>().Single(choice => choice.Code == "de");
        Pump();
        Check(MovieField().Text == "Meine Filme", "returning to a language restores its editable alias");
        CaptureFocusedSizes(main, scroll, "de-library-names", Element<StackPanel>(view, "LibraryNameRows"), save);
        MovieField().Text = " ";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(movies.LibraryDisplayName == L.Text("LibraryNames.Movies") && LibraryNames.Override(server, movies.Id, "de") is null,
            "clearing a custom name restores automatic translation");
        Check(!System.Text.Json.JsonSerializer.Serialize(movies).Contains("LibraryNameServer", StringComparison.Ordinal)
            && !System.Text.Json.JsonSerializer.Serialize(movies).Contains("LibraryDisplayName", StringComparison.Ordinal),
            "client-only library projection is excluded from metadata JSON");
        box.SelectedItem = box.Items.Cast<UiLanguage>().Single(choice => choice.Code == originalLanguage);
        view.SetLibraries("", []);
        nav.Reset(null);
    }

    private static void VerifyLanguage(MainWindow main, string language)
    {
        var translated = language == "de";
        var model = Field<AppViewModel>(main, "_app");
        var vm = Field<PlayerViewModel>(main, "_playerViewModel");
        var overlay = Field<OverlayWindow>(main, "_overlay");
        var settings = Field<AppSettings>(main, "_settings");
        var updates = Field<UpdateService>(main, "_updateService");
        var host = Element<ContentControl>(main, "ShellHost");
        var username = translated ? Translation("LoginView", "USERNAME") : "USERNAME";
        Check(HasText(main, username), language + " login label resolves from XAML markup extension");
        Check(HasText(main, "PASSWORD"), language + " missing translation retains English password label");
        CaptureSizes(main, language + "-login");
        var login = Descendants(main).OfType<LoginView>().Single();
        var loginScroll = Descendants(login).OfType<ScrollViewer>().Single(scroll => scroll.Content is StackPanel);
        var loginOffset = loginScroll.VerticalOffset;
        loginScroll.ScrollToEnd();
        Pump();
        Check(IsInsideViewport(Element<Button>(login, "ConnectButton"), loginScroll),
            language + " narrow login keeps Sign in inside the scrolled viewport");
        Check(IsInsideViewport(Element<Button>(login, "QuickConnectButton"), loginScroll),
            language + " narrow login keeps Quick Connect inside the scrolled viewport");
        Capture(main, language + "-login-narrow-bottom");
        loginScroll.ScrollToVerticalOffset(loginOffset);
        Pump();

        var view = new SettingsView(settings, () => null, vm, updates);
        host.Content = view;
        Pump();
        Check(HasText(view, translated ? Translation("SettingsView", "Settings") : "Settings"),
            language + " Settings heading resolves from catalog");
        var box = Element<ComboBox>(view, "UiLanguageBox");
        var choices = box.Items.Cast<UiLanguage>().ToArray();
        Check(choices.Any(c => c.Code == "en") && choices.Any(c => c.Code == "de"),
            language + " language list contains English and available test German");
        Check(box.SelectedItem is UiLanguage selected && selected.Code == language,
            language + " saved language is selected");
        var selectedContent = (ContentPresenter)box.Template.FindName("ContentSite", box);
        Check(HasText(selectedContent, ((UiLanguage)box.SelectedItem).Name),
            language + " selected language displays its human name in the production dropdown template");
        var format = Catalog.Strings.First(e => e.Key.StartsWith("SettingsView.", StringComparison.Ordinal) && e.Value.Text == "{0} MB");
        Check(L.Format(format.Key, 321) == (translated ? "DE 321 MB" : "321 MB"),
            language + " formatted settings text substitutes placeholder");
        Check(HasText(view, L.Format(format.Key, settings.ImageCacheMaxMb)),
            language + " settings code-behind formatted cache label uses catalog");
        CaptureSizes(main, language + "-settings");
        var settingsScroll = Descendants(view).OfType<ScrollViewer>().Single(scroll => scroll.Content is StackPanel);
        VerifySettingsControls(main, view, settings, language, settingsScroll);
        VerifySubtitleChoices(main, view, settings, language, settingsScroll);
        CaptureFocusedSizes(main, settingsScroll, language + "-language-selection", box);
        // Switch the same loaded controls repeatedly; no navigation or playback is restarted.
        var edited = Element<TextBox>(view, "VideoBufferBox");
        edited.Text = "invalid draft";
        // Decode a local fixture through real libmpv while the existing Settings page switches.
        // Null output keeps this independent of guest GPU/audio capabilities.
        var clip = Path.Combine(Output, "localization-live-switch.mp4");
        Directory.CreateDirectory(Output);
        if (!File.Exists(clip))
        {
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=32x32:r=5:d=120", "-c:v", "libx264", "-pix_fmt", "yuv420p", clip })
                start.ArgumentList.Add(argument);
            using var ffmpeg = Process.Start(start)!;
            if (!ffmpeg.WaitForExit(30000) || ffmpeg.ExitCode != 0) throw new InvalidOperationException("Could not generate live switching clip.");
        }
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
        using var player = LightWeaver.Player.MpvPlayer.Create(0, Dispatcher.CurrentDispatcher, new AppSettings { HardwareDecoding = false });
        vm.Attach(player);
        player.LoadFile(clip);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!player.TryReadPosition(out var position) || position <= 0) && DateTime.UtcNow < deadline) Pump();
        Check(player.TryReadPosition(out var initialPosition) && initialPosition > 0, "live switching begins with real libmpv playback advancing");
        var generation = player.LoadGeneration;
        var trackRows = vm.SubtitleTracks.ToArray();
        var headingKey = Catalog.Strings.Single(e => e.Key.StartsWith("SettingsView.") && e.Value.Text == "Settings").Key;
        var offKey = "PlayerViewModel.Off.830d786d";
        var card = (FrameworkElement)((DataTemplate)Application.Current.FindResource("PosterCardTemplate")).LoadContent();
        card.DataContext = new LightWeaver.Jellyfin.MediaItem { Id = Guid.NewGuid(), Name = "Settings", Type = BaseItemDto_Type.Movie };
        var offRow = new TrackOption(-1, L.Text(offKey), true) { DisplayProvider = () => L.Text(offKey) };
        var trackControl = (FrameworkElement)((DataTemplate)Application.Current.FindResource("TrackOptionTemplate")).LoadContent();
        trackControl.DataContext = offRow;
        LiveText.Watch(trackControl);
        var field = new LightWeaver.Jellyfin.MediaInfoField(L.Text("JellyfinService.Audio.e37cf36e"), "Settings")
            { LabelKey = "JellyfinService.Audio.e37cf36e" };
        var cachedField = System.Text.Json.JsonSerializer.Deserialize<LightWeaver.Jellyfin.MediaInfoField>(
            System.Text.Json.JsonSerializer.Serialize(field))!;
        var infoControl = (FrameworkElement)((DataTemplate)Application.Current.FindResource("InfoRowTemplate")).LoadContent();
        infoControl.DataContext = cachedField;
        LiveText.Watch(infoControl);
        var search = new AdvancedSearchView(model);
        var typeFilter = Element<CheckDropdown>(search, "TypeFilter");
        typeFilter.SetSelectedKeys(["Movie"]);
        var searchLoads = 0;
        search.Loaded += (_, _) => searchLoads++;
        var shortcuts = new ShortcutsOverlay();
        shortcuts.Show(settings);
        var navFrame = new NavFrame(view, L.Text(headingKey), null, TitleProvider: () => L.Text(headingKey));
        var loaded = 0;
        view.Loaded += (_, _) => loaded++;
        foreach (var code in new[] { "en", "de", "ar", "en", "ar", language })
        {
            box.SelectedItem = box.Items.Cast<UiLanguage>().Single(c => c.Code == code);
            Pump();
            Check(L.Language == code && SettingsStore.Load().UiLanguage == code, code + " live selection applies and persists without restart");
            Check(ReferenceEquals(host.Content, view) && loaded == 0, code + " live switch preserves the existing view");
            Check(edited.Text == "invalid draft", code + " live switch preserves invalid unsaved input");
            Check(HasText(view, L.Text(headingKey)), code + " existing Settings XAML heading refreshes immediately");
            Check(HasText(view, L.Format(format.Key, settings.ImageCacheMaxMb)), code + " existing formatted code label refreshes immediately");
            Check(HasText(login, L.Text(Catalog.Strings.First(e => e.Key.StartsWith("LoginView.") && e.Value.Text == "USERNAME").Key)), code + " cached detached login label refreshes");
            Check(Descendants(card).OfType<Button>().Any(b => Equals(b.ToolTip, L.Text(Catalog.Strings.First(e => e.Key.StartsWith("MediaTemplates.") && e.Value.Text == "Play").Key))), code + " existing shared template tooltip refreshes");
            Check(HasText(trackControl, L.Text(offKey)), code + " cached track row refreshes without replacing its identity");
            Check(HasText(infoControl, L.Text("JellyfinService.Audio.e37cf36e")) && HasText(infoControl, "Settings"), code + " serialized metadata labels refresh while user data stays unchanged");
            Check(typeFilter.SelectedKeys.SequenceEqual(new[] { "Movie" }) && searchLoads == 0, code + " cached advanced-search filter retains its invariant selection without loading");
            Check(navFrame.DisplayTitle == L.Text(headingKey), code + " cached navigation title follows the current language");
            var groups = Element<ItemsControl>(shortcuts, "LeftColumn").ItemsSource.Cast<ShortcutGroupView>().ToArray();
            Check(groups[0].Title == LightWeaver.Player.AppShortcuts.GroupTitle(LightWeaver.Player.ShortcutGroup.Playback), code + " visible shortcuts panel refreshes its cached groups");
            Check(player.LoadGeneration == generation && vm.SubtitleTracks.SequenceEqual(trackRows), code + " language change preserves player generation and track collection");
            Check(!player.Pause && player.TryReadPosition(out var livePosition) && livePosition >= initialPosition, code + " live switching keeps real playback running without seeking backward");
            Check(Element<ComboBox>(view, "TransportNextBox").SelectedIndex == (int)settings.MediaNextAction, code + " transport selection retains invariant value");
            if (code == "ar")
            {
                Check(L.IsRightToLeft && L.Text(headingKey) != "Settings", "real embedded Arabic pack is active during live switching");
                CaptureFocusedSizes(main, settingsScroll, "ar-live-language-selection", box);
            }
        }
        Check(edited.Text == "invalid draft", "repeated language switching leaves invalid draft untouched");
        player.Pause = true;
        VerifyLibraryNames(main, view, settings, language, settingsScroll);


        // Offline views exercise real XAML and code-behind, with an unconnected model or local query.
        VerifySearchGroups(main, host, language);
        host.Content = new HomeView(model);
        Pump();
        CaptureSizes(main, language + "-home-offline");
        host.Content = new LibraryView(model, new LightWeaver.Jellyfin.MediaItem
        {
            Id = Guid.NewGuid(), Name = "Synthetic library", Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.CollectionFolder,
        });
        Pump();
        CaptureSizes(main, language + "-library-offline");
        host.Content = new SectionView((_, _) => Task.FromResult((new List<LightWeaver.Jellyfin.MediaItem>(), 0)));
        Pump();
        CaptureSizes(main, language + "-section-empty");

        // Synthetic callbacks keep the production flyout completely independent of Jellyfin.
        var flyout = new SyncPlayFlyout(vm, () => Task.FromResult<IReadOnlyList<LightWeaver.Jellyfin.SyncPlayGroupSummary>>([]),
            _ => Task.FromResult(true), () => Task.CompletedTask, _ => Task.FromResult(true));
        host.Content = flyout;
        main.Width = 1280;
        main.Height = 800;
        Pump();
        Check(HasText(flyout, translated ? Translation("SyncPlayFlyout", "Available groups") : "Available groups"),
            language + " SyncPlay labels resolve from catalog");
        CheckFlyoutLayout(flyout, language + " standard");
        Capture(main, language + "-syncplay-standard");
        flyout.Width = 300;
        flyout.Height = 360;
        main.Width = 700;
        main.Height = 480;
        Pump();
        CheckFlyoutLayout(flyout, language + " narrow");
        Capture(main, language + "-syncplay-narrow");
        var close = FindById<Button>(flyout, "SyncPlayClose");
        Check(close.IsVisible && close.ActualHeight > 0 && close.TranslatePoint(new Point(0, close.ActualHeight), flyout).Y <= flyout.ActualHeight,
            language + " narrow flyout keeps Close reachable");
        VerifyJoinedFlyout(main, flyout, vm, language);
        flyout.Dispose();

        vm.SetNowPlaying("Synthetic media title", "Localization fixture");
        typeof(PlayerViewModel).GetField("_durationSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, 5400d);
        typeof(ObservableObject).GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [nameof(PlayerViewModel.DurationSeconds)]);
        vm.PositionSeconds = 1234;
        vm.IsPaused = false;
        Check(vm.TransportVisible && !vm.IsLoading && vm.DurationSeconds == 5400 && vm.PositionSeconds == 1234,
            language + " overlay uses a settled playing transport with a realistic timeline");
        main.Width = 1280;
        main.Height = 800;
        Pump();
        model.EnterPlayback();
        Pump();
        typeof(MainWindow).GetMethod("SyncOverlayBounds", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
        WakeOverlay(overlay);
        Check(Element<Button>(overlay, "BackButton").ToolTip is string backTip
            && backTip == (translated ? Translation("OverlayWindow", "Back to browsing") : "Back to browsing"),
            language + " overlay tooltip resolves from catalog");
        CheckOverlay(overlay, language);
        var standardOverlaySize = overlay.RenderSize;
        var videoArea = Element<FrameworkElement>(main, "VideoArea");
        Check(standardOverlaySize.Width > 1100 && standardOverlaySize.Height > 700
            && Math.Abs(standardOverlaySize.Width - videoArea.ActualWidth) < 1
            && Math.Abs(standardOverlaySize.Height - videoArea.ActualHeight) < 1,
            language + " standard overlay capture uses the resized standard owner client area");
        Capture(overlay, language + "-overlay-standard");
        main.Width = 700;
        main.Height = 480;
        Pump();
        typeof(MainWindow).GetMethod("SyncOverlayBounds", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
        WakeOverlay(overlay);
        CheckOverlay(overlay, language);
        Check(overlay.ActualWidth >= 650 && overlay.ActualWidth <= 700 && overlay.ActualHeight >= 400 && overlay.ActualHeight <= 480
            && overlay.ActualWidth < standardOverlaySize.Width && overlay.ActualHeight < standardOverlaySize.Height
            && Math.Abs(overlay.ActualWidth - videoArea.ActualWidth) < 1
            && Math.Abs(overlay.ActualHeight - videoArea.ActualHeight) < 1,
            language + " narrow overlay capture uses the resized narrow owner client area and differs from standard");
        Capture(overlay, language + "-overlay-narrow");
        overlay.Hide();
        model.LeavePlayback();
    }

    private static void VerifySearchGroups(MainWindow main, ContentControl host, string language)
    {
        var view = new SearchResultsView();
        host.Content = view;
        var types = new[]
        {
            BaseItemDto_Type.CollectionFolder, BaseItemDto_Type.Series, BaseItemDto_Type.Movie,
            BaseItemDto_Type.Person, BaseItemDto_Type.Episode, BaseItemDto_Type.Movie,
            BaseItemDto_Type.Audio, BaseItemDto_Type.BoxSet,
        };
        var items = types.Select((type, index) => new MediaItem
        {
            Id = Guid.NewGuid(), Name = "Synthetic search item " + index, Type = type,
        }).ToArray();
        view.SetResults(items);
        Pump();
        var grid = Element<ListBox>(view, "ItemsGrid");
        var groups = ((System.ComponentModel.ICollectionView)grid.ItemsSource).Groups!
            .Cast<CollectionViewGroup>().ToArray();
        Check(groups.Length == 6, language + " search retains six distinct category groups");
        Check(groups.Select(group => group.ItemCount).SequenceEqual(new[] { 2, 1, 1, 1, 1, 2 }),
            language + " search group counts remain distinct with colliding translations");
        var expectedOrder = new[] { 2, 5, 1, 4, 7, 3, 0, 6 }.Select(index => items[index].Id);
        Check(groups.SelectMany(group => group.Items.Cast<MediaItem>()).Select(item => item.Id).SequenceEqual(expectedOrder),
            language + " search preserves category rank and relevance order including Other items");
        if (language == "de")
        {
            Check(Translation("SearchResultsView", "Movies") == Translation("SearchResultsView", "Shows"),
                "German search fixture deliberately collides Movies and Shows labels");
            Check(Descendants(view).OfType<TextBlock>().Count(block => block.Text == CollidingCategoryLabel) == 2,
                "German search renders two separate translated headers with the same label");
        }
        else
            Check(HasText(view, "Movies") && HasText(view, "Shows"), "English search renders category headers");
        CaptureSizes(main, language + "-search-groups");
        var containers = Descendants(grid).OfType<GroupItem>().ToArray();
        Check(containers.Length == 6 && containers.Zip(containers.Skip(1)).All(pair =>
        {
            var first = pair.First.TransformToAncestor(grid).TransformBounds(new Rect(pair.First.RenderSize));
            var next = pair.Second.TransformToAncestor(grid).TransformBounds(new Rect(pair.Second.RenderSize));
            return Math.Abs(first.Left - next.Left) < 1 && next.Top >= first.Bottom - 1;
        }), language + " search category sections stack vertically without touching headers at narrow width");
    }

    private static void WakeOverlay(OverlayWindow overlay)
    {
        Field<DispatcherTimer>(overlay, "_idleTimer").Stop();
        typeof(OverlayWindow).GetMethod("ShowControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(overlay, null);
        Pump();
        Pump();
    }

    private static void CheckFlyoutLayout(SyncPlayFlyout flyout, string size)
    {
        var heading = Element<TextBlock>(flyout, "AvailableGroupsLabel");
        var scroll = Element<ScrollViewer>(flyout, "FlyoutBodyScroll");
        scroll.ScrollToTop();
        Pump();
        var text = new FormattedText(heading.Text, System.Globalization.CultureInfo.GetCultureInfo(L.Language),
            heading.FlowDirection, new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight, heading.FontStretch),
            heading.FontSize, heading.Foreground, VisualTreeHelper.GetDpi(heading).PixelsPerDip)
        { MaxTextWidth = heading.ActualWidth };
        Check(heading.TextWrapping == TextWrapping.Wrap && heading.ActualHeight >= text.Height - 1,
            size + " SyncPlay heading allocates enough height for the full wrapped translation");
        foreach (var control in new FrameworkElement[] { heading, Element<Button>(flyout, "RefreshButton"),
            FindById<Button>(flyout, "SyncPlayClose") })
        {
            var bounds = control.TransformToAncestor(flyout).TransformBounds(new Rect(control.RenderSize));
            Check(control.IsVisible && bounds.Left >= -1 && bounds.Top >= -1
                && bounds.Right <= flyout.ActualWidth + 1 && bounds.Bottom <= flyout.ActualHeight + 1,
                size + " SyncPlay keeps " + (control.Name.Length > 0 ? control.Name : "Close") + " inside the panel");
        }
        var refresh = Element<Button>(flyout, "RefreshButton");
        var available = Element<FrameworkElement>(flyout, "AvailablePanel");
        var refreshBounds = refresh.TransformToAncestor(available).TransformBounds(new Rect(refresh.RenderSize));
        Check(IsInsideViewport(heading, scroll) && IsInsideViewport(refresh, scroll)
            && refreshBounds.Top >= -1 && refreshBounds.Bottom <= available.ActualHeight + 1,
            size + " SyncPlay fully displays its heading and Refresh within the actual body viewport and available section");
        Element<Button>(flyout, "CreateButton").BringIntoView();
        Pump();
        Check(IsInsideViewport(Element<Button>(flyout, "CreateButton"), scroll)
            && IsInsideViewport(Element<TextBox>(flyout, "GroupNameBox"), scroll),
            size + " SyncPlay scrolling keeps the complete Create action and group-name input reachable");
        Capture(Window.GetWindow(flyout), size.Replace(' ', '-') + "-syncplay-bottom");
        scroll.ScrollToTop();
        Pump();
    }

    private static void VerifyJoinedFlyout(MainWindow main, SyncPlayFlyout flyout, PlayerViewModel vm, string language)
    {
        var setGroup = typeof(PlayerViewModel).GetMethod("SetSyncPlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalGroup = vm.SyncPlay;
        var participants = Enumerable.Range(1, 20).Select(index => "Synthetic participant " + index).ToArray();
        var groupName = string.Concat(Enumerable.Repeat("Long synthetic group name ", 4))[..100];
        try
        {
            setGroup.Invoke(vm, [new SyncPlayInfo(groupName, participants, "Paused")]);
            foreach (var (width, height, size) in new[] { (360, 440, "standard"), (300, 360, "narrow") })
            {
                flyout.Width = width;
                flyout.Height = height;
                main.Width = size == "standard" ? 1280 : 700;
                main.Height = size == "standard" ? 800 : 480;
                Pump();
                var panel = Element<FrameworkElement>(flyout, "CurrentGroupPanel");
                var details = Element<ScrollViewer>(flyout, "GroupDetailsScroll");
                var leave = Element<Button>(flyout, "LeaveButton");
                var close = FindById<Button>(flyout, "SyncPlayClose");
                var leaveBounds = leave.TransformToAncestor(flyout).TransformBounds(new Rect(leave.RenderSize));
                var closeBounds = close.TransformToAncestor(flyout).TransformBounds(new Rect(close.RenderSize));
                Check(panel.IsVisible && !Element<ScrollViewer>(flyout, "FlyoutBodyScroll").IsVisible
                    && !Element<FrameworkElement>(flyout, "CreatePanel").IsVisible,
                    language + " " + size + " joined SyncPlay shows only the constrained group panel");
                Check(leave.IsVisible && leave.ActualWidth > 0 && leave.ActualHeight > 0
                    && leaveBounds.Left >= -1 && leaveBounds.Top >= -1
                    && leaveBounds.Right <= flyout.ActualWidth + 1 && leaveBounds.Bottom <= closeBounds.Top,
                    language + " " + size + " joined SyncPlay pins the complete Leave action above Close for a 100-character group and 20 participants");
                details.ScrollToTop();
                Pump();
                Capture(main, language + "-syncplay-joined-" + size);
                details.ScrollToEnd();
                Pump();
                Check(details.ScrollableHeight > 0 && details.VerticalOffset > 0
                    && leaveBounds == leave.TransformToAncestor(flyout).TransformBounds(new Rect(leave.RenderSize))
                    && closeBounds == close.TransformToAncestor(flyout).TransformBounds(new Rect(close.RenderSize)),
                    language + " " + size + " joined SyncPlay scrolls long details while Leave and Close stay fixed");
                Capture(main, language + "-syncplay-joined-" + size + "-bottom");
            }
        }
        finally
        {
            setGroup.Invoke(vm, [originalGroup]);
        }
    }

    private static void CheckOverlay(OverlayWindow overlay, string language)
    {
        var controls = Element<FrameworkElement>(overlay, "ControlsPanel");
        var title = FindById<TextBlock>(overlay, "NowPlayingTitle");
        Check(controls.IsVisible && controls.Opacity > 0.99 && title.IsVisible && title.Text == "Synthetic media title"
            && Element<Button>(overlay, "PauseButton").IsVisible,
            language + " captured overlay visibly renders the title and awake playback controls");
        Check(FindById<Button>(overlay, "JumpBackButton").ToolTip is string tip
            && tip == (language == "de" ? Translation("OverlayWindow", "Jump back (Left arrow)") : "Jump back (Left arrow)"),
            language + " visible transport tooltip resolves from catalog");
    }

    private static void RunChild(string mode, string root, string description)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Restart process was not started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            // The child's last PASS line is the only clue to whether it hung or ran out of time.
            Console.Write(stdout.GetAwaiter().GetResult());
            Console.Write(stderr.GetAwaiter().GetResult());
            throw new TimeoutException("Isolated language restart did not finish.");
        }
        Console.Write(stdout.GetAwaiter().GetResult());
        Console.Write(stderr.GetAwaiter().GetResult());
        Check(process.ExitCode == 0, description);
    }

    private static string Translation(string prefix, string english)
    {
        var key = Catalog.Strings.First(e => e.Key.StartsWith(prefix + ".", StringComparison.Ordinal) && e.Value.Text == english).Key;
        return Field<Dictionary<string, TranslationFile>>(typeof(L), "Translations")["de"].Strings[key];
    }

    private static T Field<T>(object owner, string name) where T : class
        => (T)((owner as Type ?? owner.GetType()).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!
            .GetValue(owner is Type ? null : owner)!);
    private static T Element<T>(FrameworkElement root, string name) where T : class
        => (T)(root.FindName(name) ?? throw new InvalidOperationException("Missing control " + name));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T FindById<T>(DependencyObject root, string id) where T : DependencyObject
        => Descendants(root).OfType<T>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == id);
    private static bool HasText(DependencyObject root, string text)
        => Descendants(root).OfType<TextBlock>().Any(c => c.Text == text)
            || Descendants(root).OfType<ContentControl>().Any(c => c.Content is string value && value == text);
    private static void VerifySettingsControls(MainWindow main, SettingsView view, AppSettings settings,
        string language, ScrollViewer scroll)
    {
        var quality = Element<ComboBox>(view, "DefaultResolutionBox");
        foreach (var (index, value) in new[] { (0, "Original"), (1, "1080p"), (2, "720p"), (3, "480p") })
        {
            quality.SelectedIndex = index;
            Pump();
            var content = (ContentPresenter)quality.Template.FindName("ContentSite", quality);
            var label = value == "Original" ? L.Text("SettingsView.Original.a8d63619") : value;
            Check(HasText(content, label) && settings.DefaultDownloadResolution == value,
                language + " selected download quality renders " + value + " and saves its invariant value");
        }
        quality.SelectedIndex = 0;
        var vsr = Element<CheckBox>(view, "RtxVsrBox");
        var hdr = Element<CheckBox>(view, "RtxHdrBox");
        var savedVsr = settings.RtxVsrDefault;
        var savedHdr = settings.RtxHdrDefault;
        if (LightWeaver.Player.GpuCapabilities.RtxUnavailableReason is { } gpuReason)
        {
            Check(!vsr.IsEnabled && !hdr.IsEnabled && !Element<TextBox>(view, "MaxLumaBox").IsEnabled,
                language + " RTX switches and brightness are disabled without a supported GPU");
            Check(Equals(Element<Border>(view, "RtxVsrRow").ToolTip, gpuReason)
                && ToolTipService.GetShowOnDisabled(Element<Border>(view, "RtxVsrRow")),
                language + " disabled RTX row exposes its translated reason on hover");
        }
        Check(settings.RtxVsrDefault == savedVsr && settings.RtxHdrDefault == savedHdr,
            language + " capability checks preserve saved RTX preferences");
        CaptureFocusedSizes(main, scroll, language + "-settings-rtx-availability", vsr, hdr, Element<TextBox>(view, "MaxLumaBox"));

        var autoplay = Element<CheckBox>(view, "AutoPlayNextBox");
        var savedAutoplay = settings.AutoPlayNextEpisode;
        var savedCountdown = settings.AutoPlayCountdownSeconds;
        autoplay.IsChecked = false;
        Check(!Element<TextBox>(view, "AutoPlayCountdownBox").IsEnabled && settings.AutoPlayCountdownSeconds == savedCountdown,
            language + " disabling auto-play disables countdown editing without changing its saved value");
        autoplay.IsChecked = true;
        Check(Element<TextBox>(view, "AutoPlayCountdownBox").IsEnabled,
            language + " enabling auto-play restores countdown editing");
        autoplay.IsChecked = savedAutoplay;

        var passthrough = Element<CheckBox>(view, "PassthroughBox");
        var normalization = Element<CheckBox>(view, "NormalizationBox");
        var savedPassthrough = settings.AudioPassthrough;
        var savedNormalization = settings.VolumeNormalization;
        passthrough.IsChecked = false;
        normalization.IsChecked = true;
        Check(!passthrough.IsEnabled && passthrough.ToolTip is string,
            language + " normalization disables passthrough with an explanation");
        normalization.IsChecked = false;
        passthrough.IsChecked = true;
        Check(!normalization.IsEnabled && normalization.ToolTip is string,
            language + " passthrough disables normalization with an explanation");
        passthrough.IsChecked = false;
        var probeField = typeof(SettingsView).GetField("_passthroughProbe", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousProbe = probeField.GetValue(view);
        var updateAudio = typeof(SettingsView).GetMethod("UpdateAudioExclusivity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var (verdict, available) in new[]
        {
            (LightWeaver.Player.PassthroughVerdict.NoneSupported, false),
            (LightWeaver.Player.PassthroughVerdict.NoExclusiveMode, false),
            (LightWeaver.Player.PassthroughVerdict.SomeSupported, true),
            (LightWeaver.Player.PassthroughVerdict.Unknown, true),
        })
        {
            probeField.SetValue(view, new LightWeaver.Player.PassthroughProbe(verdict, "Synthetic endpoint", []));
            updateAudio.Invoke(view, null);
            Check(passthrough.IsEnabled == available && settings.AudioPassthrough == false,
                language + " selected-device passthrough availability follows " + verdict + " without changing its preference");
        }
        probeField.SetValue(view, previousProbe);
        passthrough.IsChecked = savedPassthrough;
        normalization.IsChecked = savedNormalization;
        updateAudio.Invoke(view, null);

        CaptureFocusedSizes(main, scroll, language + "-settings-downloads", Element<TextBox>(view, "DownloadDirBox"), quality);

        var bitrate = Element<ComboBox>(view, "MaxBitrateBox");
        var custom = Element<TextBox>(view, "MaxBitrateCustomBox");
        foreach (var (index, value) in new[] { (0, 0), (1, 1), (2, 4), (3, 10), (4, 20), (5, 40), (6, 80), (7, 120) })
        {
            bitrate.SelectedIndex = index;
            Pump();
            var content = (ContentPresenter)bitrate.Template.FindName("ContentSite", bitrate);
            Check(HasText(content, value == 0 ? L.Text("SettingsView.Unlimited") : L.Format("SettingsView.BitrateMbps", value))
                && settings.MaxStreamingBitrateMbps == value && custom.Visibility == Visibility.Collapsed,
                language + " bitrate preset renders its units or Unlimited and saves " + value);
        }
        bitrate.SelectedIndex = bitrate.Items.Count - 1;
        custom.Text = "37";
        Pump();
        Check(custom.Visibility == Visibility.Visible && SettingsStore.Load().MaxStreamingBitrateMbps == 37,
            language + " custom bitrate accepts and persists a non-preset value");
        CaptureFocusedSizes(main, scroll, language + "-settings-bitrate-custom", bitrate, custom);
        foreach (var invalid in new[] { "0", "10001", "1.5", "invalid" })
        {
            custom.Text = invalid;
            Pump();
            Check(settings.MaxStreamingBitrateMbps == 37 && SettingsStore.Load().MaxStreamingBitrateMbps == 37
                && Element<TextBlock>(view, "MaxBitrateCustomError").Visibility == Visibility.Visible,
                language + " invalid custom bitrate retains the saved cap: " + invalid);
        }
        var languages = Element<ComboBox>(view, "UiLanguageBox");
        languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == (language == "en" ? "ar" : "en"));
        Pump();
        Check(custom.Text == "invalid" && custom.Visibility == Visibility.Visible && settings.MaxStreamingBitrateMbps == 37,
            language + " language switching preserves the custom bitrate draft and saved value");
        languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == language);
        bitrate.SelectedIndex = 0;
        Pump();
        Check(settings.MaxStreamingBitrateMbps == 0 && Element<TextBlock>(view, "MaxBitrateCustomError").Visibility == Visibility.Collapsed,
            language + " Unlimited clears the custom validation error");

        var font = Element<ComboBox>(view, "SubFontBox");
        font.SelectedIndex = 0;
        Pump();
        Check(HasText((ContentPresenter)font.Template.FindName("ContentSite", font), L.Text("SettingsView.DefaultSubtitleFont"))
            && settings.SubtitleFontFamily == "", language + " font picker labels and preserves the player default");
        font.SelectedIndex = 1;
        Pump();
        Check(settings.SubtitleFontFamily.Length > 0 && SettingsStore.Load().SubtitleFontFamily == settings.SubtitleFontFamily,
            language + " choosing a font persists its family name");
        font.SelectedIndex = 0;
        Pump();
        Check(SettingsStore.Load().SubtitleFontFamily == "", language + " returning to the default font saves the empty sentinel");
        CaptureFocusedSizes(main, scroll, language + "-settings-font-default", font);

        var directory = Element<TextBox>(view, "DownloadDirBox");
        Check(directory.Text == LightWeaver.Downloads.DownloadStore.DefaultDirectory && settings.DownloadDirectory == "",
            language + " download folder displays its effective path and preserves the default sentinel");
        directory.Text = Path.Combine(AppPaths.Root, "custom-downloads");
        Check(settings.DownloadDirectory == directory.Text, language + " a custom download folder stays explicit");
        Element<Button>(view, "DownloadClearButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(directory.Text == LightWeaver.Downloads.DownloadStore.DefaultDirectory && SettingsStore.Load().DownloadDirectory == "",
            language + " resetting the download folder shows the default path and saves its sentinel");

        var verbose = Element<CheckBox>(view, "VerboseLoggingBox");
        foreach (var enabled in new[] { true, false })
        {
            verbose.IsChecked = enabled;
            Pump();
            Check(Element<TextBlock>(view, "VerboseLoggingHint").Text == (enabled
                    ? L.Text("SettingsView.VerboseLoggingEnabled") : L.Text("SettingsView.VerboseLoggingDisabled")),
                language + " logging helper describes only the current state: " + enabled);
            Check(settings.DownloadDirectory == "", language + " an unrelated change preserves the default download sentinel");
            CaptureFocusedSizes(main, scroll, language + "-settings-logging-" + (enabled ? "on" : "off"),
                verbose, Element<TextBlock>(view, "VerboseLoggingHint"));
        }

        Check(LightWeaver.Player.DisplayBrightness.ValidPeakNits(12, 603.6f) == 604
            && LightWeaver.Player.DisplayBrightness.ValidPeakNits(0, 80) is null
            && LightWeaver.Player.DisplayBrightness.ValidPeakNits(12, float.NaN) is null
            && LightWeaver.Player.DisplayBrightness.ValidPeakNits(12, 0) is null
            && LightWeaver.Player.DisplayBrightness.ValidPeakNits(12, 10001) is null,
            language + " display brightness accepts an HDR peak and rejects SDR fallback or invalid metadata");
        var luma = Element<TextBox>(view, "MaxLumaBox");
        var saved = settings.RtxHdrMaxLumaNits;
        Element<Button>(view, "UseDisplayBrightnessButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var status = Element<TextBlock>(view, "DisplayBrightnessStatus");
        Check(status.Visibility == Visibility.Visible && (status.Text == L.Text("SettingsView.DisplayBrightnessUnavailable")
                ? settings.RtxHdrMaxLumaNits == saved
                : status.Text == L.Format("SettingsView.DisplayBrightnessReported", settings.RtxHdrMaxLumaNits)),
            language + " native display probe reports a value or preserves manual brightness when unavailable");
        CaptureFocusedSizes(main, scroll, language + "-settings-display-brightness", luma, Element<Button>(view, "UseDisplayBrightnessButton"));
        luma.Text = (saved == 1000 ? 1001 : 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(status.Visibility == Visibility.Collapsed, language + " a manual brightness edit clears the old detection status");
        luma.Text = saved.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void VerifySubtitleChoices(MainWindow main, SettingsView view, AppSettings settings, string language, ScrollViewer scroll)
    {
        var text = Element<ComboBox>(view, "SubColorPreset");
        var border = Element<ComboBox>(view, "SubBorderColorPreset");
        var textField = Element<TextBox>(view, "SubColorBox");
        var borderField = Element<TextBox>(view, "SubBorderColorBox");
        Check(text.SelectedIndex == 0 && border.SelectedIndex == 1
            && textField.Text == "#FFFFFF" && borderField.Text == "#000000",
            language + " color controls select effective white and black defaults");
        CaptureFocusedSizes(main, scroll, language + "-settings-colors-presets", text, border,
            Element<Border>(view, "SubColorPreview"), Element<Border>(view, "SubBorderColorPreview"));
        foreach (var (index, hex) in new[] { (2, "#FFFF00"), (3, "#00FFFF"), (4, "#00FF00"), (5, "#FF0000") })
        {
            text.SelectedIndex = index;
            border.SelectedIndex = index;
            Pump();
            Check(settings.SubtitleTextColor == hex && settings.SubtitleBorderColor == hex
                && textField.Visibility == Visibility.Collapsed && borderField.Visibility == Visibility.Collapsed,
                language + " named color choices save " + hex + " without custom fields");
        }
        text.SelectedIndex = text.Items.Count - 1;
        border.SelectedIndex = border.Items.Count - 1;
        textField.Text = "#123abc";
        borderField.Text = "#654321";
        Pump();
        Check(SettingsStore.Load().SubtitleTextColor == "#123ABC" && settings.SubtitleBorderColor == "#654321",
            language + " custom subtitle colors persist normalized hex values");
        Check(((SolidColorBrush)Element<Border>(view, "SubColorPreview").Background).Color == Color.FromRgb(0x12, 0x3a, 0xbc)
            && ((SolidColorBrush)Element<Border>(view, "SubBorderColorPreview").Background).Color == Color.FromRgb(0x65, 0x43, 0x21),
            language + " custom color swatches display the actual selected colors");
        CaptureFocusedSizes(main, scroll, language + "-settings-colors-custom", text, border, textField, borderField);
        textField.Text = "bad color";
        Pump();
        Check(settings.SubtitleTextColor == "#123ABC" && Element<TextBlock>(view, "SubColorError").Visibility == Visibility.Visible
            && ((SolidColorBrush)Element<Border>(view, "SubColorPreview").Background).Color == Color.FromRgb(0x12, 0x3a, 0xbc),
            language + " invalid custom color retains the saved color and its swatch");
        var languages = Element<ComboBox>(view, "UiLanguageBox");
        languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == "ar");
        Pump();
        Check(textField.Text == "bad color" && text.SelectedIndex == text.Items.Count - 1 && settings.SubtitleTextColor == "#123ABC",
            language + " Arabic switching preserves an invalid custom color draft");
        textField.Text = "#123abc";
        CaptureFocusedSizes(main, scroll, language + "-settings-colors-custom-ar", text, border, textField, borderField);
        languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == language);
        text.SelectedIndex = 0;
        border.SelectedIndex = 1;
        Pump();
        Check(settings.SubtitleTextColor == "" && settings.SubtitleBorderColor == "",
            language + " returning to white and black preserves the player default sentinels");
        foreach (var (prefix, presets, custom, invalid, read) in new (string, double[], string, string[], Func<double>)[]
        {
            ("SubBorderSize", [0, 1, 3, 5], "2.75", ["21", "NaN", "invalid"], () => settings.SubtitleBorderSize),
            ("SubShadow", [0, 1, 2, 4], "-2.5", ["-21", "Infinity", "invalid"], () => settings.SubtitleShadowOffset),
            ("SubPosition", [100, 90, 50, 0], "135", ["151", "20.5", "invalid"], () => settings.SubtitleBasePosition),
        })
        {
            var box = Element<ComboBox>(view, prefix + "Preset");
            var field = Element<TextBox>(view, prefix + "Box");
            foreach (var (value, index) in presets.Select((value, index) => (value, index)))
            {
                box.SelectedIndex = index;
                Pump();
                Check(read() == value && field.Visibility == Visibility.Collapsed,
                    language + " " + prefix + " preset saves " + value);
            }
            box.SelectedIndex = box.Items.Count - 1;
            field.Text = custom;
            Pump();
            var saved = double.Parse(custom, System.Globalization.CultureInfo.InvariantCulture);
            Check(read() == saved && field.Visibility == Visibility.Visible,
                language + " " + prefix + " accepts a custom value including non-preset positions and signed fractional offsets");
            foreach (var draft in invalid)
            {
                field.Text = draft;
                Pump();
                Check(read() == saved && Element<TextBlock>(view, prefix + "Error").Visibility == Visibility.Visible,
                    language + " " + prefix + " rejects invalid draft " + draft + " without changing its saved value");
            }
            languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == "ar");
            Pump();
            Check(field.Text == "invalid" && read() == saved && field.Visibility == Visibility.Visible,
                language + " " + prefix + " custom draft survives Arabic switching");
            languages.SelectedItem = languages.Items.Cast<UiLanguage>().Single(item => item.Code == language);
            field.Text = custom;
        }
        CaptureFocusedSizes(main, scroll, language + "-settings-subtitle-numbers-custom",
            Element<ComboBox>(view, "SubBorderSizePreset"), Element<TextBox>(view, "SubBorderSizeBox"),
            Element<ComboBox>(view, "SubShadowPreset"), Element<TextBox>(view, "SubShadowBox"),
            Element<ComboBox>(view, "SubPositionPreset"), Element<TextBox>(view, "SubPositionBox"));
        Check(SettingsStore.Load().SubtitleBorderSize == 2.75 && SettingsStore.Load().SubtitleShadowOffset == -2.5
            && SettingsStore.Load().SubtitleBasePosition == 135, language + " custom subtitle numeric values survive settings reload");
        Element<ComboBox>(view, "SubBorderSizePreset").SelectedIndex = 2;
        Element<ComboBox>(view, "SubShadowPreset").SelectedIndex = 0;
        Element<ComboBox>(view, "SubPositionPreset").SelectedIndex = 0;
    }

    private static void CaptureSizes(MainWindow main, string name)
    {
        main.Width = 1280;
        main.Height = 800;
        Pump();
        Capture(main, name + "-standard");
        main.Width = 700;
        main.Height = 480;
        Pump();
        Capture(main, name + "-narrow");
    }
    private static void CaptureFocusedSizes(MainWindow main, ScrollViewer scroll, string name, params FrameworkElement[] controls)
    {
        foreach (var (width, height, size) in new[] { (1280, 800, "standard"), (700, 480, "narrow") })
        {
            main.Width = width;
            main.Height = height;
            Pump();
            var content = (FrameworkElement)scroll.Content;
            var bounds = Rect.Empty;
            foreach (var control in controls)
                bounds.Union(control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize)));
            content.BringIntoView(bounds);
            Pump();
            foreach (var control in controls)
                Check(IsInsideViewport(control, scroll), name + " " + size + " keeps " + control.Name + " inside the viewport");
            Capture(main, name + "-" + size);
        }
    }
    private static bool IsInsideViewport(FrameworkElement control, ScrollViewer scroll)
    {
        var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        return control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0
            && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
    private static void Capture(FrameworkElement visual, string name)
    {
        Require(visual.IsVisible && PresentationSource.FromVisual(visual) is not null, "capture target is displayed in fixture window");
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)), Math.Max(1, (int)Math.Ceiling(visual.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Output);
        var path = Path.Combine(Output, "localization-ui-" + name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("CAPTURE: " + path);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {message}");
    }
}
