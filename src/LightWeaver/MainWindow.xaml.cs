using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using LightWeaver.Player;
using LightWeaver.Updates;
using LightWeaver.ViewModels;
using LightWeaver.Views;

namespace LightWeaver;

/// <summary>
/// Shell window: swaps browse views (login/home/library/detail) in <c>ShellHost</c>
/// and shows the mpv video layer only while playing. The player HWND is created
/// lazily on first playback. Playback controls live in <see cref="OverlayWindow"/>
/// (airspace — see TECHNICAL.md).
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppViewModel _app = new();
    private readonly PlayerViewModel _playerViewModel = new();
    private readonly Settings.AppSettings _settings = Settings.SettingsStore.Load();
    private readonly UpdateService _updateService;

    private MpvPlayer? _player;
    private OverlayWindow? _overlay;
    private SystemMediaControls? _systemMedia;
    private System.Windows.Threading.DispatcherTimer? _timelineTimer;
    private Jellyfin.PlaybackReporter? _reporter;
    private (string Url, string? AuthHeader)? _pendingLoad;
    private long _pendingResumeTicks;
    private Jellyfin.MediaItem? _playingItem;
    private bool _systemSessionEnding;
    private bool _launchUpdateOnClosed;
    private bool _windowClosing;

    /// <summary>The subtitle the user last picked in the player, remembered for the rest of the
    /// session and re-resolved against each new file's tracks (2026-08-05). Stored as a
    /// DESCRIPTION, not a track id: <c>loadfile</c> resets mpv's <c>sid</c> to <c>auto</c> and ids
    /// are per-file, so id 3 in one episode is a different track — or no track — in the next.
    /// <c>Off</c> is a real choice and is remembered too.</summary>
    private sealed record StickySubtitle(bool Off, string? Lang, string? Title, bool Forced);

    private enum StickySubtitleApplyStatus
    {
        NoMatch,
        AlreadySelected,
        SelectionCommandIssued,
    }

    private readonly record struct StickySubtitleApplyResult(
        StickySubtitleApplyStatus Status, int? TrackId);

    private StickySubtitle? _stickySub;

    /// <summary>All subtitle load state is owned by one generation. A newer load replaces the
    /// object, so a delayed FILE_LOADED or track-list callback cannot mutate the incoming file.</summary>
    private sealed record SubtitleGenerationState(int Generation, int PlaybackSequence)
    {
        public bool FileLoaded { get; set; }
        public bool LanguagePreferencesApplied { get; set; }
        public bool StickyApplied { get; set; }
        public SubtitleSelectionRequest? PendingIntent { get; set; }
        public int PendingCycleCount { get; set; }
        public bool DeferPendingCycles { get; set; }
        public bool AwaitingStickySelection { get; set; }
#if DEBUG
        public bool TestLateSubtitleScheduled { get; set; }
#endif
    }

    private SubtitleGenerationState? _subtitleState;
    private int _playbackSequence;

    // Playback negotiation (M0): the decision carries the URL + server session ids;
    // null means negotiation failed and we direct-play with client-side reporting.
    private Jellyfin.PlaybackDecision? _playbackDecision;
    private bool _transcodeRetryDone;

    // External subtitles can arrive before or after load. PlaybackSequence rejects an old metadata
    // fetch; AppliedGeneration lets a transcode retry add the same batch to its replacement load.
    private sealed record ExternalSubtitleState(
        int PlaybackSequence, IReadOnlyList<Jellyfin.ExternalSubtitle> Subtitles)
    {
        public int AppliedGeneration { get; set; }
    }

    private ExternalSubtitleState? _externalSubs;

    /// <summary>Per-profile shell caches (warm multi-account sessions, Phase 7): the
    /// browse history with its live view instances, the Home view, the search view,
    /// and the nav rail's library buttons. Swapping profiles swaps the whole bundle,
    /// so returning to a profile restores its exact browse state with no refetch.
    /// NavFrame.Rail identity depends on the buttons living here.</summary>
    private sealed class ShellSession
    {
        public NavigationService Nav { get; } = new();
        public HomeView? Home;
        public SearchResultsView? Search;
        public bool LibrariesLoaded;
        public List<RadioButton> RailItems { get; } = [];
        public Dictionary<Guid, RadioButton> LibraryNav { get; } = new();
    }

    // Browse navigation history: back + forward stacks (Phase 7 M3). Home is the root;
    // every transition (incl. rail Home / rail library clicks) is recorded so Back
    // returns to the exact prior screen and Forward re-enters.
    private ShellSession _shell = new();
    private readonly Dictionary<string, ShellSession> _shells = new(StringComparer.OrdinalIgnoreCase);
    private NavigationService _nav => _shell.Nav;

    // The session that started the current playback. Queue advancement, Up Next,
    // episode stepping, transcode retries and progress reports pin to it, so a
    // mid-playback profile switch never retargets a running playback.
    private Jellyfin.JellyfinService? _playbackJf;

    // Left nav rail collapse state (< 1160 px); the buttons live on the shell session.
    private bool _railCollapsed;

    private WindowState _preFullscreenState;
    private bool _isFullscreen;

    // Mini-player (Phase 5 M9): compact chrome-less always-on-top mode.
    private bool _isMiniPlayer;
    private Rect _preMiniRect;
    private WindowState _preMiniState;
    // Last mini size this session (M12 corner resize); next mini enter reuses it.
    private Size _miniSize = new(480, 270);

    // Offline downloads (Phase 7 M20): the engine + its screen. _playingLocal marks a
    // local-first playback (completed download) — no negotiation, no progress reports.
    private readonly Downloads.DownloadManager _downloads;
    private DownloadsView? _downloadsView;
    private bool _downloadsResumed;
    private bool _playingLocal;

    public MainWindow()
    {
        App.StartupLog("MainWindow ctor enter");
        // Before anything can log: the crash handlers are already installed (App.OnStartup) and
        // work regardless, but the verbose level has to be known before the first mpv instance
        // and before the first Info() call decides whether to reach app.log.
        Diagnostics.AppLog.Verbose = _settings.VerboseLogging;
        InitializeComponent();
        _updateService = new UpdateService(_settings);
        _updateService.StatusChanged += OnUpdateStatusChanged;
        Application.Current.SessionEnding += OnSessionEnding;
#if DEBUG
        _systemSessionEnding = Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_SIMULATE_SESSION_ENDING") == "1";
#endif
        App.StartupLog($"MainWindow InitializeComponent done, Title='{Title}', "
            + $"Application.MainWindow is {(Application.Current?.MainWindow is null ? "null" : "set")}");
#if DEBUG
        // Visual marker (B29): the isolated debug environment (LightWeaver-Debug app data)
        // must be unmistakable at a glance vs. the installed release build. Appended to the
        // static base title from MainWindow.xaml straight after InitializeComponent, so the
        // window is never once shown without it — see the comment in App.OnStartup for why
        // the deferred-dispatch version was wrong.
        Title += " (Debug)";
        App.StartupLog($"MainWindow debug marker applied, Title='{Title}'");
#endif
        PlayerHost.HwndReady += OnVideoHwndReady;
        // Resolve the token from the session that owns the image's SERVER, not from whichever
        // profile is active — the same rule the download engine uses (B14). The active session is
        // the fallback so a URL we don't recognise still behaves as before.
        Imaging.ImageCache.AuthorizationHeaderProvider = url =>
            (_app.FindSessionForUrl(url) ?? _app.Jellyfin).AuthorizationHeader;
        Imaging.ImageCache.MaxCacheBytes = Math.Max(50, _settings.ImageCacheMaxMb) * 1024L * 1024;   // M19/M21
        Jellyfin.BrowseFolderCache.MaxItems = Math.Max(100, _settings.FolderCacheMaxItems);
        // The metadata cache had no eviction at all, only the manual Clear in settings (B19).
        // 30 days: entries are re-fetchable stream projections with their own 1 h TTL, so the only
        // cost of dropping one is a single request, while keeping them forever retained rows for
        // items long deleted from the library.
        Imaging.MetadataCache.ScheduleEviction(TimeSpan.FromDays(30));
        // Same for the OMDB cache, which shipped with no eviction at all (B19 again). The two
        // numbers govern different things and are not in conflict: the 7-day TTL on each entry
        // decides how FRESH a rating is (a stale one is re-fetched in place), while this pass
        // decides how long the directory keeps entries for items nobody looks at any more.
        Imaging.ExternalMetadataCache.ScheduleEviction(TimeSpan.FromDays(30));

        // Download engine (M20). URLs + auth resolve against the item's recorded
        // server across ALL warm sessions (Phase 7 warm switching) — a profile switch
        // no longer pauses in-flight downloads; only closing the originating session
        // (logout) does, cleanly, via the null resolver.
        _downloads = new Downloads.DownloadManager(
            () => Math.Max(1, _settings.MaxParallelDownloads),
            d => _app.FindSessionByServer(d.ServerUrl)
                ?.GetDownloadUrl(d.ItemId, d.MediaSourceId, d.MaxWidth, d.VideoBitRate, d.Container),
            d => _app.FindSessionByServer(d.ServerUrl)?.AuthorizationHeader);
        _downloads.Completed += d => Dispatcher.BeginInvoke(() =>
        {
            ShowToast($"Download complete: {d.Title}");
            _ = BroadcastDownloadBadgeAsync(d.ItemId);
        });
        _playerViewModel.Configure(_settings);
        _playerViewModel.AttachQueue(_app.Queue);
        // Up Next advancement rides the queue when the item is its next entry.
        _playerViewModel.UpNextPlayRequested += item =>
        {
            _app.Queue.TryAdvanceTo(item);
            PlayItem(item, 0, fromQueue: true);
        };
        _playerViewModel.QueuePlayRequested += item => PlayItem(item, 0, fromQueue: true);
        // Episode-step pair: pure series-order stepping; starting the target episode
        // follows the M15 "watch from here" rule (queue rebuilt from it onward).
        _playerViewModel.EpisodeStepRequested += item => OnEpisodePlayRequested(item, 0);
        _playerViewModel.SubtitleChosen += RememberSubtitleChoice;

        // When the detail view toggles watched/favorite, refresh the matching card in
        // every cached browse view so badges are current on back-navigation. Subscribing
        // here (app-lifetime) rather than per-view avoids leaking popped views, and the
        // browse stack is the authoritative set of live list views.
        _app.ItemUserDataChanged += OnItemUserDataChanged;

        // Warm multi-account sessions (Phase 7): swap the per-profile shell bundle the
        // moment a session activates (before the Browse state re-applies), drop shells
        // whose session closed, and surface background token invalidation.
        _app.SessionActivated += key => Dispatcher.Invoke(() => OnSessionActivated(key));
        _app.SessionClosed += key => Dispatcher.Invoke(() =>
        {
            _shells.Remove(key);
            Jellyfin.BrowsePrefetcher.CancelProfile(key);
        });
        _app.SessionExpired += name => Dispatcher.Invoke(() =>
            ShowToast($"The session for {name} expired — please sign in again."));

        // Card hover action bar (Phase 7 M10): the shared card templates raise these
        // routed commands (cards have no code-behind); handle them once here.
        CommandBindings.Add(new CommandBinding(CardCommands.ToggleWatched, OnCardToggleWatched));
        CommandBindings.Add(new CommandBinding(CardCommands.ShowMediaInfo, OnCardShowMediaInfo));
        CommandBindings.Add(new CommandBinding(CardCommands.CopyStreamUrl, OnCardCopyStreamUrl));
        CommandBindings.Add(new CommandBinding(CardCommands.OpenCardMenu, OnCardMenu));
        CommandBindings.Add(new CommandBinding(CardCommands.PlayItem, OnCardPlayItem));

        // App-chrome chords (P10 M9): the chords themselves live in AppShortcuts.Chrome and the
        // bindings are built from it, so the shortcuts overlay renders the same table the window
        // registers — a chord cannot be in one and not the other. The handlers stay here because
        // their state guards read this window's private state.
        foreach (var shortcut in AppShortcuts.Chrome)
        {
            if (!shortcut.Registered)
                continue;
            var id = shortcut.Id;
            InputBindings.Add(new KeyBinding(
                new RelayCommand(() => InvokeChromeShortcut(id)), shortcut.Key, shortcut.Modifiers));
        }
        // Plain letter/bracket keys can't be KeyBindings (KeyGesture rejects
        // modifier-less alphanumerics) — handled in OnKeyDown instead.

        // Mouse thumb buttons → back/forward (Phase 7 M7). WPF's MouseAction enum has no
        // XButton members, so these can't be InputBindings — a tunneling PreviewMouseDown
        // handler is the correct mechanism. The overlay window handles them over the video.
        PreviewMouseDown += OnWindowMouseDown;
        // '?' opens the shortcuts panel — on the character, not a chord (see OnWindowTextInput).
        TextInput += OnWindowTextInput;

        Loaded += OnLoaded;
        LocationChanged += (_, _) => SyncOverlayBounds();
        SizeChanged += (_, _) => { SyncOverlayBounds(); UpdateRailCollapse(); UpdateHeaderLayout(); };
        // Fires when the video layer becomes visible and gets its real layout size —
        // the state-change itself runs before layout, when ActualWidth is still 0.
        VideoArea.SizeChanged += (_, _) => SyncOverlayBounds();
        StateChanged += (_, _) => SyncOverlayBounds();
        DpiChanged += (_, _) => SyncOverlayBounds();
        IsVisibleChanged += (_, _) => SyncOverlayBounds();
        Activated += (_, _) => SyncOverlayBounds();
        App.StartupLog($"MainWindow ctor exit, Title='{Title}'");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("app", "event=window_load outcome=start");
        _overlay = new OverlayWindow(_playerViewModel) { Owner = this };
        var overlay = _overlay;
        overlay.Closed += (_, _) =>
        {
            // WPF owned windows can close before the owner's last position/visibility
            // messages arrive. A closed Window can never be shown again.
            if (ReferenceEquals(_overlay, overlay))
                _overlay = null;
        };
        _overlay.VideoAreaClicked += () => _playerViewModel.TogglePauseCommand.Execute(null);
        _overlay.VideoAreaDoubleClicked += ToggleFullscreen;
        _overlay.FullscreenRequested += ToggleFullscreen;
        // The only REBINDABLE route in: PlayerAction.ToggleFullscreen. It arrives through the
        // view-model because the dispatcher is handed only that (see PlayerActions.cs) - deliberately
        // an event rather than a special case in HandlePlayerKey, which would leave Dispatch silently
        // no-opping one enum member for whatever calls it next.
        _playerViewModel.FullscreenRequested += ToggleFullscreen;
        _playerViewModel.OpenStreamRequested += () => OnOpenStream(this, null!);
        _overlay.SetFullscreen(_isFullscreen);   // the overlay is new; start it in step
        _overlay.FileDropped += path => PlayUrl(path, null);
        _overlay.BackRequested += StopPlaybackAndReturn;
        _overlay.MiniPlayerRequested += ToggleMiniPlayer;
        _overlay.MiniRestoreRequested += ToggleMiniPlayer;
        _overlay.MiniDragRequested += StartMiniDrag;
        _overlay.MiniResizeRequested += StartMiniResize;
        // A shell gesture aimed at the overlay (it is the foreground window after any video
        // click) moves the whole player instead of tearing the OSD off the video.
        _overlay.ExternalMoveRequested += MoveWithOverlay;
        // WPF focuses the clicked OSD control, so the overlay does hold focus in normal use.
        // Its keys reach the same player handler and our own chords (M1).
        _overlay.KeyPressed += HandleOverlayKey;
        // OSD button and '?' over the video both land here (P10 M9).
        _overlay.ShortcutsRequested += ToggleShortcutsOverlay;

        _app.StateChanged += state => Dispatcher.Invoke(() => ApplyState(state));
        // Start independently of Jellyfin login/connection; updates must not be delayed by a
        // slow or offline media server.
#if DEBUG
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START") != "1")
            _updateService.Start();
#else
        _updateService.Start();
#endif
        await _app.StartupAsync();
        Diagnostics.AppLog.Detail("app", $"event=window_load outcome=success state={_app.State}");

        // A file passed on the command line plays on top of whatever state we landed in.
        if (Application.Current is App { StartupFile: { } startupFile })
            PlayUrl(startupFile, null);

    }

    // ---- Reliable self-updater ---------------------------------------------------------

    private bool _updateBannerDismissed;

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e) => _systemSessionEnding = true;

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnUpdateStatusChanged(status));
            return;
        }
        var update = status.Update;
        UpdateMenuItem.Visibility = update is not null || status.Phase == UpdatePhase.Error
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateMenuItem.Header = status.Phase switch
        {
            UpdatePhase.ReadyToInstall when _settings.UpdatePolicy == Settings.UpdatePolicy.AutoDownloadAndInstall
                => $"Update {update?.DisplayVersion} ready — installs when LightWeaver closes",
            UpdatePhase.ReadyToInstall => $"Update {update?.DisplayVersion} ready — click to install",
            UpdatePhase.Downloading when status.Progress is { } progress
                => $"Downloading update {update?.DisplayVersion} — {progress:P0}",
            UpdatePhase.Downloading => $"Downloading update {update?.DisplayVersion}…",
            UpdatePhase.Installing => $"Installing update → {update?.DisplayVersion}",
            UpdatePhase.Error => "Retry update",
            _ => $"Update available → {update?.DisplayVersion}",
        };
        UpdateMenuItem.IsEnabled = status.Phase is not UpdatePhase.Downloading and not UpdatePhase.Installing;
        UpdateBannerText.Text = status.Phase switch
        {
            UpdatePhase.Downloading => status.Progress is { } progress
                ? $"Downloading update {update?.DisplayVersion} — {progress:P0}"
                : $"Downloading update {update?.DisplayVersion}…",
            UpdatePhase.ReadyToInstall when _settings.UpdatePolicy == Settings.UpdatePolicy.AutoDownloadAndInstall
                => $"Update {update?.DisplayVersion} ready — installs when LightWeaver closes",
            UpdatePhase.ReadyToInstall => $"Update {update?.DisplayVersion} ready — click to install",
            UpdatePhase.Installing => $"Installing update {update?.DisplayVersion}…",
            UpdatePhase.Error => "Update failed — open Settings to retry",
            _ when update?.SetupAssetUri is null => $"Update available: {update?.DisplayVersion} — click to open release page",
            _ => $"Update available: {update?.DisplayVersion} — click to download",
        };
        UpdateBanner.Visibility = _app.State == AppState.Browse && update is not null
            && status.Phase != UpdatePhase.Error && !_updateBannerDismissed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        var status = _updateService.Status;
        if (status.Update?.SetupAssetUri is null && status.Update is not null)
        {
            _updateService.OpenReleasePage();
            return;
        }
        if (status.Phase == UpdatePhase.ReadyToInstall)
        {
            if (await _updateService.InstallNowAsync())
                Close();
            else
                ShowToast("The staged update could not be started.");
            return;
        }
        if (status.Update is null)
        {
            await _updateService.CheckNowAsync();
            return;
        }
        if (_settings.UpdatePolicy == Settings.UpdatePolicy.ManualInstall)
        {
            if (await _updateService.DownloadAndInstallInteractiveAsync())
                Close();
        }
        else
            await _updateService.DownloadAsync();
    }

    private void OnUpdateBannerClick(object sender, MouseButtonEventArgs e)
        => OnInstallUpdate(sender, e);

    private void OnUpdateBannerDismiss(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // don't bubble into the banner's install click
        _updateBannerDismissed = true;   // menu item stays as the quiet reminder
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>A profile session became active: bind its shell bundle and re-hang its
    /// cached rail library buttons. Warm sessions restore instantly (frames, scroll,
    /// rail — all live instances); a first-time session gets a fresh bundle and fills
    /// it through the normal Browse path.</summary>
    private void OnSessionActivated(string key)
    {
        Jellyfin.BrowsePrefetcher.CancelOtherProfiles(key);
        if (!_shells.TryGetValue(key, out var shell))
            _shells[key] = shell = new ShellSession();
        _shell = shell;
        LibrariesNav.Children.Clear();
        foreach (var item in shell.RailItems)
            LibrariesNav.Children.Add(item);
        ApplyRailCollapse(_railCollapsed);
    }

    private void ApplyState(AppState state)
    {
        Diagnostics.AppLog.Info("app", $"event=state state={state}");
        LogoutItem.Visibility = _app.Jellyfin.IsConnected ? Visibility.Visible : Visibility.Collapsed;

        switch (state)
        {
            case AppState.Login:
                // A fresh anonymous shell for the login form; warm shells stay in
                // _shells keyed by their profile (SessionClosed prunes real logouts).
                _shell = new ShellSession();
                LibrariesNav.Children.Clear();
                ShellHost.Content = new LoginView(_app);
                BrowseLayer.Visibility = Visibility.Visible;
                NavRail.Visibility = Visibility.Collapsed;
                VideoArea.Visibility = Visibility.Collapsed;
                break;

            case AppState.Browse:
                if (_nav.Current is null)
                {
                    _shell.Home = new HomeView(_app);
                    _shell.Home.ItemSelected += OnBrowseItemSelected;
                    _shell.Home.SeeAllRequested += OnSeeAllRequested;
                    _nav.Reset(new NavFrame(_shell.Home, "Home", NavHome, IsRailRoot: true));
                }
                ShellHost.Content = _nav.Current!.View;
                BrowseLayer.Visibility = Visibility.Visible;
                NavRail.Visibility = _app.Jellyfin.IsConnected ? Visibility.Visible : Visibility.Collapsed;
                VideoArea.Visibility = Visibility.Collapsed;
                RefreshProfileAffordance();
                _ = LoadRailLibrariesAsync();
                // Restart recovery (M20): once per session, after auth is available,
                // interrupted downloads re-queue and continue from their byte offsets.
                if (!_downloadsResumed && _app.Jellyfin.IsConnected)
                {
                    _downloadsResumed = true;
                    _downloads.ResumeInterrupted();
                }
                _player?.Stop();
                break;

            case AppState.Playing:
                BrowseLayer.Visibility = Visibility.Collapsed;
                VideoArea.Visibility = Visibility.Visible;
                break;
        }
        // The banner lives in the browse layer only (mpv would cover it anyway).
        UpdateBanner.Visibility = state == AppState.Browse && _updateService.Status.Update is not null
            && !_updateBannerDismissed
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateBrowseHeader();
        SyncOverlayBounds();
    }

    /// <summary>Push a new destination onto history. New frames inherit the current
    /// frame's rail highlight (a detail/search opened from a library keeps that library lit).</summary>
    private void NavigateTo(System.Windows.Controls.UserControl view, string title)
    {
        _nav.Navigate(new NavFrame(view, title, _nav.Current?.Rail));
        ShowCurrentFrame();
    }

    private void OnBrowseBack(object sender, RoutedEventArgs e) => BrowseBack();
    private void OnBrowseForward(object sender, RoutedEventArgs e) => BrowseForward();

    /// <summary>Mouse side-buttons: XButton1 = back, XButton2 = forward (standard Windows
    /// mapping). In browse they drive history; in the player XButton1 stops playback and
    /// returns (XButton2 is reserved). The overlay window carries the same mapping over
    /// the video HWND.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1 when _app.State == AppState.Browse:
                BrowseBack(); e.Handled = true; break;
            case MouseButton.XButton1 when _app.State == AppState.Playing:
                StopPlaybackAndReturn(); e.Handled = true; break;
            case MouseButton.XButton2 when _app.State == AppState.Browse:
                BrowseForward(); e.Handled = true; break;
        }
    }

    private void BrowseBack()
    {
        if (!_nav.CanGoBack)
            return;
        _nav.GoBack();
        ShowCurrentFrame();
    }

    /// <summary>Re-enters what Back left (Phase 7 M3; also driven by the mouse forward
    /// button in M7).</summary>
    private void BrowseForward()
    {
        if (!_nav.CanGoForward)
            return;
        _nav.GoForward();
        ShowCurrentFrame();
    }

    /// <summary>Shows the current history frame's view and refreshes the header/rail.</summary>
    private void ShowCurrentFrame()
    {
        if (_nav.Current is { } frame)
            ShellHost.Content = frame.View;
        UpdateBrowseHeader();
    }

    /// <summary>Push a refreshed item into every cached list view in the history so its
    /// card's watched/favorite badges reflect the change (the views are hidden but live
    /// while another view is on top, so this lands before the user navigates back).
    /// Home rails included (M10) — a watched toggle on a Home card must refresh itself.</summary>
    private void OnItemUserDataChanged(Jellyfin.MediaItem fresh)
    {
        foreach (var frame in _nav.Frames)
            switch (frame.View)
            {
                case Views.LibraryView lib: lib.ApplyUserDataUpdate(fresh); break;
                case Views.HomeView home: home.ApplyUserDataUpdate(fresh); break;
                case Views.SectionView section: section.ApplyUserDataUpdate(fresh); break;
                case Views.ItemDetailView detail: detail.ApplyUserDataUpdate(fresh); break;
            }
    }

    /// <summary>A Home rail's see-all header: push the full-screen paged listing for that
    /// section (Phase 7). One generic SectionView; only the query delegate differs.</summary>
    private void OnSeeAllRequested(Settings.HomeSectionId id)
    {
        var jf = _app.Jellyfin;
        (string Title, Func<int, int, Task<(List<Jellyfin.MediaItem>, int)>> Query)? section = id switch
        {
            Settings.HomeSectionId.Resume => ("Continue Watching", jf.GetResumePagedAsync),
            Settings.HomeSectionId.NextUp => ("Next Up", jf.GetNextUpPagedAsync),
            Settings.HomeSectionId.Latest => ("Recently Added", jf.GetLatestPagedAsync),
            Settings.HomeSectionId.Favorites => ("Favorites", jf.GetFavoritesPagedAsync),
            _ => null,   // Libraries has no see-all: the rail is already the complete list
        };
        if (section is not { } s)
            return;
        var view = new Views.SectionView(s.Query);
        view.ItemSelected += OnBrowseItemSelected;
        NavigateTo(view, s.Title);
    }

    // ---- Card hover action bar (Phase 7 M10) ----------------------------------------
    // All handlers mark the routed event handled so a card action never bubbles into the
    // ListBox selection path (which would navigate to the item's detail view).

    private async void OnCardToggleWatched(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not Jellyfin.MediaItem item)
            return;
        Diagnostics.AppLog.Detail("main",
            $"event=card_action action=toggle-watched item={item.Id:N} type={item.Type} target={!item.Played}");
        try
        {
            // Same flow as ItemDetailView.OnToggleWatched: set on the server, refetch,
            // fan out — ApplyUserDataUpdate swaps the card in place (incl. this one).
            if (await _app.Jellyfin.SetPlayedAsync(item.Id, !item.Played)
                && await _app.Jellyfin.GetItemAsync(item.Id) is { } fresh)
                _app.NotifyItemUserDataChanged(fresh);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("main",
                $"event=card_action action=toggle-watched outcome=failure item={item.Id:N} type={item.Type}", ex);
            ShowToast("Could not update watched state.");
        }
    }

    private void OnCardPlayItem(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not Jellyfin.MediaItem item)
            return;
        Diagnostics.AppLog.Detail("main",
            $"event=card_action action=play item={item.Id:N} type={item.Type}");
        OnDetailPlayRequested(item, item.ResumePositionTicks);
    }

    private void OnCardCopyStreamUrl(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not Jellyfin.MediaItem item)
            return;
        Diagnostics.AppLog.Detail("main", $"event=card_action action=copy-stream-url item={item.Id:N} type={item.Type}");
        if (_app.Jellyfin.GetShareableStreamUrl(item.Id) is not { } url)
        {
            ShowToast("Not connected to a server.");
            return;
        }
        try
        {
            Clipboard.SetText(url);
            ShowToast("Stream URL copied.");
        }
        catch
        {
            ShowToast("Could not access the clipboard.");
        }
    }

    /// <summary>Interim media-info popup (upgrades to the styled modal with M23): the
    /// same label/value rows the overlay info panel renders, in a small owned window.</summary>
    private async void OnCardShowMediaInfo(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not Jellyfin.MediaItem item)
            return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("main", $"event=card_action action=media-info outcome=start item={item.Id:N} type={item.Type}");
        Jellyfin.MediaSourceStreams? streams = null;
        var failed = false;
        try { streams = await _app.Jellyfin.GetMediaStreamsAsync(item.Id); }
        catch (Exception ex)
        {
            failed = true;
            Diagnostics.AppLog.Detail("main",
                $"event=card_action action=media-info outcome=failure elapsed_ms={started.ElapsedMilliseconds} item={item.Id:N} type={item.Type}", ex);
        }
        if (streams is null)
        {
            if (!failed)
                Diagnostics.AppLog.Detail("main",
                    $"event=card_action action=media-info outcome=empty elapsed_ms={started.ElapsedMilliseconds} item={item.Id:N} type={item.Type}");
            ShowToast("No media info available.");
            return;
        }

        var rows = new List<Jellyfin.MediaInfoField>(streams.Summary);
        if (streams.Video.Count > 1)
            rows.Add(new Jellyfin.MediaInfoField("Video tracks", streams.Video.Count.ToString()));
        if (streams.Audio.Count > 0)
            rows.Add(new Jellyfin.MediaInfoField("Audio", string.Join(" · ", streams.Audio.Select(a => a.Display))));
        if (streams.Subtitles.Count > 0)
            rows.Add(new Jellyfin.MediaInfoField("Subtitles", string.Join(" · ", streams.Subtitles.Select(s => s.Display))));

        var list = new ItemsControl
        {
            ItemsSource = rows,
            ItemTemplate = (DataTemplate)FindResource("InfoRowTemplate"),
            MaxWidth = 460,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "CardMediaInfoList");
        var title = new TextBlock
        {
            Text = item.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(list);
        var popup = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Title = "Media info",
            Content = new Border
            {
                Background = (Brush)FindResource("LwStorm2Brush"),
                BorderBrush = (Brush)FindResource("LwHairlineBrush"),
                BorderThickness = new Thickness(1),
                // Radii built in code cannot use StaticResource, so the ladder is resolved
                // by name (P10 M7) - a large popover, hence LwR3.
                CornerRadius = (CornerRadius)FindResource("LwR3"),
                Padding = new Thickness(18, 14, 18, 14),
                Child = panel,
            },
        };
        // Non-modal on purpose: click-away (Deactivated) or Esc dismisses; a modal
        // ShowDialog would also block UIA invokes in the verify suite.
        popup.Deactivated += (_, _) => popup.Close();
        popup.KeyDown += (_, args) => { if (args.Key == Key.Escape) popup.Close(); };
        popup.Show();
        popup.Activate();
        Diagnostics.AppLog.Detail("main",
            $"event=card_action action=media-info outcome=success elapsed_ms={started.ElapsedMilliseconds} rows={rows.Count} item={item.Id:N} type={item.Type}");
    }

    /// <summary>Overflow (⋯) menu — mirrors ItemDetailView.OnMore's woven-light menu.</summary>
    private void OnCardMenu(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not Jellyfin.MediaItem item)
            return;
        Diagnostics.AppLog.Detail("main", $"event=card_action action=open-menu item={item.Id:N} type={item.Type}");
        var menu = new ContextMenu { Style = (Style)FindResource("LwContextMenu") };
        menu.Items.Add(CardMenuItem("Media info",
            () => CardCommands.ShowMediaInfo.Execute(item, this)));
        menu.Items.Add(CardMenuItem("Copy stream URL",
            () => CardCommands.CopyStreamUrl.Execute(item, this)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("LwMenuSeparator") });
        menu.Items.Add(CardMenuItem(item.Played ? "Mark as unwatched" : "Mark as watched",
            () => CardCommands.ToggleWatched.Execute(item, this)));
        menu.PlacementTarget = e.OriginalSource as UIElement ?? this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem CardMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("LwMenuItem") };
        item.Click += (_, _) => action();
        return item;
    }

    private void UpdateBrowseHeader()
    {
        // Header is always up while browsing (it hosts the search box); back/forward
        // show only when history can move that way.
        var show = _app.State == AppState.Browse;
        BrowseHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BrowseBackBtn.Visibility = show && _nav.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        BrowseForwardBtn.Visibility = show && _nav.CanGoForward ? Visibility.Visible : Visibility.Collapsed;
        BrowseTitle.Text = show && _nav.Current is { } f ? f.Title : "";
        // ASS redesign P5/P6: on the Advanced Search screen its own field is the single
        // search input — the global quick-search pill and the (redundant) Advanced
        // affordance hide; both return on any navigation away.
        // Settings joins that rule (Phase 9 M6): it is a configuration page, not a place to
        // search from, and its own shortcut-capture flow wants keystrokes — a focused quick-search
        // box next to it invites typing into the wrong field.
        var onAss = show && _nav.Current?.View is AdvancedSearchView or SettingsView or OpenStreamView;
        SearchPill.Visibility = onAss ? Visibility.Collapsed : Visibility.Visible;
        AdvancedSearchBtn.Visibility = onAss ? Visibility.Collapsed : Visibility.Visible;
        // Active rail highlight is history-driven: it follows the current frame's rail;
        // railless frames (e.g. the global Advanced Search) clear it so no library reads active.
        if (show)
        {
            if (_nav.Current?.Rail is { } rail)
                SetActiveRail(rail);
            else
                ClearActiveRail();
        }
    }

    // ---- Left navigation rail (Phase 6 P1) ----

    /// <summary>Marks a rail destination as current (stormlight wash + light bar + lit
    /// glyph). The rail items share GroupName "NavRail", so checking one clears the rest.</summary>
    private static void SetActiveRail(RadioButton active) => active.IsChecked = true;

    /// <summary>Clears the rail selection (no library shown active) — used by railless
    /// destinations like the global Advanced Search screen.</summary>
    private void ClearActiveRail()
    {
        // Every RadioButton in the rail group, not a hand-written subset. NavDownloads used to be
        // missing here, so opening Advanced Search or Settings from Downloads left Downloads lit on
        // a screen that wasn't it (B25). Normal rail navigation never showed it because GroupName
        // mutual exclusion handles that case — only the explicit clear was wrong.
        // Same trap as B7's two hand-written flyout lists: derive the set, don't retype it.
        foreach (var rb in RailRadioButtons())
            rb.IsChecked = false;
    }

    /// <summary>Every rail destination that can read as "active" — the fixed roots plus the
    /// per-profile library buttons. (NavSettings is a Button, not a RadioButton, so it never
    /// highlights and is deliberately absent.)</summary>
    private IEnumerable<RadioButton> RailRadioButtons()
    {
        yield return NavHome;
        yield return NavDownloads;
        yield return NavStream;
        foreach (var rb in LibrariesNav.Children.OfType<RadioButton>())
            yield return rb;
    }

    private void OnNavHome(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("main", "event=interaction action=rail-home");
        SearchBox.Clear();
        if (_shell.Home is null)
            return;   // not yet in a browse session
        // Records history (Back returns to where you were) instead of truncating it.
        _nav.Navigate(new NavFrame(_shell.Home, "Home", NavHome, IsRailRoot: true));
        ShowCurrentFrame();
    }

    private void OnNavSettings(object sender, RoutedEventArgs e) => OnOpenSettings(this, e);

    /// <summary>Downloads screen (M20): one cached instance, a rail root like Home.</summary>
    private void OnNavDownloads(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("main", "event=interaction action=rail-downloads");
        SearchBox.Clear();
        if (_downloadsView is null)
        {
            _downloadsView = new DownloadsView(_downloads,
                (title, body) => Modal.ConfirmAsync(title, body, "Delete", destructive: true));
            _downloadsView.PlayRequested += OnDownloadPlayRequested;
        }
        _nav.Navigate(new NavFrame(_downloadsView, "Downloads", NavDownloads, IsRailRoot: true));
        ShowCurrentFrame();
    }

    /// <summary>Play from the Downloads screen. The fresh item fetch keeps resume/queue
    /// semantics; an unreachable server falls back to raw local playback.</summary>
    private async void OnDownloadPlayRequested(Downloads.DownloadItem download)
    {
        try
        {
            if (await _app.Jellyfin.GetItemAsync(download.ItemId) is { } item)
            {
                OnDetailPlayRequested(item, item.ResumePositionTicks);
                return;
            }
        }
        catch
        {
            // fall through to the offline path
        }
        PlayUrl(download.FilePath, null);
    }

    // ---- Download flows (M20) ---------------------------------------------------------

    /// <summary>Resolution picker: original + lower transcode tiers with size estimates.
    /// The Settings default preselects its row; null = cancelled.</summary>
    private async Task<Jellyfin.DownloadOption?> PickDownloadOptionAsync(Guid sampleItemId, string title)
    {
        var options = await _app.Jellyfin.GetDownloadOptionsAsync(sampleItemId);
        return await PickDownloadOptionAsync(options, title);
    }

    private async Task<Jellyfin.DownloadOption?> PickDownloadOptionAsync(
        IReadOnlyList<Jellyfin.DownloadOption> options, string title)
    {
        if (options.Count == 0)
        {
            ShowToast("No downloadable video source.");
            return null;
        }
        var pre = options.ToList().FindIndex(o =>
            o.Resolution.Equals(_settings.DefaultDownloadResolution, StringComparison.OrdinalIgnoreCase));
        var pick = await Modal.ChooseAsync("Download", title,
            options.Select(o => o.Label).ToList(), "Download", Math.Max(0, pre));
        return pick is { } i ? options[i] : null;
    }

    private async Task OnDownloadRequested(ItemDetailView source, Jellyfin.MediaItem item,
        IReadOnlyList<Jellyfin.DownloadOption>? options)
    {
        // The preload completion returns to the dispatcher. Navigation may already have moved
        // this live history frame to Back/Forward, so this is the final gate before any UI opens.
        if (!ReferenceEquals(_nav.Current?.View, source))
            return;
        if (options is null)
        {
            ShowToast("Could not load download options. Check your connection and try again.");
            return;
        }
        if (options.Count == 0)
        {
            ShowToast("No downloadable video source for this item.");
            return;
        }

        var opt = await PickDownloadOptionAsync(options, item.Name);
        if (opt is null)
            return;
        EnqueueDownload(item, opt, includeSourceId: true);
        ShowToast($"Downloading “{item.Name}”.");
    }

    private async void OnDownloadRemoveRequested(Jellyfin.MediaItem item)
    {
        if (!await Modal.ConfirmAsync("Delete download?",
                $"“{item.Name}” will be removed from this device.", "Delete", destructive: true))
            return;
        _downloads.Remove(item.Id);
        await BroadcastDownloadBadgeAsync(item.Id);
    }

    /// <summary>Batch download (M20): a season/series queues its episodes under the M15
    /// rules — unwatched first, the full ordered list when everything is watched. One
    /// quality pick (sampled from the first episode) applies to the whole batch.</summary>
    private async void OnDownloadAllRequested(Jellyfin.MediaItem container)
    {
        try
        {
            var jf = _app.Jellyfin;
            List<Jellyfin.MediaItem> episodes = container.Type switch
            {
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Season
                    when container.SeriesId is { } seriesId => await jf.GetEpisodesAsync(seriesId, container.Id),
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Series
                    => await jf.GetSeriesEpisodesAsync(container.Id),
                _ => [],
            };
            episodes = episodes.Where(i => i.IsPlayable).ToList();
            var unwatched = episodes.Where(i => !i.Played).ToList();
            if (unwatched.Count > 0)
                episodes = unwatched;
            episodes = episodes.Where(i => !_downloads.IsDownloaded(i.Id)).ToList();
            if (episodes.Count == 0)
            {
                ShowToast("Nothing new to download.");
                return;
            }
            var opt = await PickDownloadOptionAsync(episodes[0].Id,
                $"{container.Name} — {episodes.Count} episode{(episodes.Count == 1 ? "" : "s")}");
            if (opt is null)
                return;
            // Batch items omit the sampled MediaSourceId — it belongs to the first
            // episode only; the server serves each item's default source without it.
            foreach (var ep in episodes)
                EnqueueDownload(ep, opt, includeSourceId: false);
            ShowToast($"Queued {episodes.Count} download{(episodes.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            ShowToast($"Could not queue downloads: {ex.Message}");
        }
    }

    private void EnqueueDownload(Jellyfin.MediaItem item, Jellyfin.DownloadOption opt, bool includeSourceId)
    {
        var dir = _settings.DownloadDirectory is { Length: > 0 } custom
            ? custom
            : Downloads.DownloadStore.DefaultDirectory;
        // Jellyfin reports containers like "mov,mp4,m4a" — the first token is the ext.
        var ext = (opt.IsOriginal ? opt.Container : "mp4") is { Length: > 0 } c
            ? c.Split(',')[0]
            : "mkv";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = string.Concat(item.Name.Select(ch => invalid.Contains(ch) ? '_' : ch));
        if (safe.Length > 100)
            safe = safe[..100];
        _downloads.Enqueue(new Downloads.DownloadItem
        {
            ItemId = item.Id,
            ServerUrl = _app.Jellyfin.ServerUrl ?? "",
            Title = item.Name,
            Subtitle = item.CardSubtitle,
            PosterUrl = item.PosterUrl ?? item.ThumbUrl,
            FilePath = System.IO.Path.Combine(dir, FormattableString.Invariant($"{item.Id:N}_{safe}.{ext}")),
            Resolution = opt.Resolution,
            MediaSourceId = includeSourceId ? opt.MediaSourceId : null,
            Container = ext,
            MaxWidth = opt.MaxWidth,
            VideoBitRate = opt.VideoBitRate,
            TotalBytes = opt.EstimatedSizeBytes ?? -1,
            AddedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>Badge freshness (M20): completion/removal rebroadcasts the item through
    /// the watched/favorite fan-out (cards re-run the downloaded converter on rebind)
    /// and refreshes a currently-open detail view's download glyph.</summary>
    private async Task BroadcastDownloadBadgeAsync(Guid itemId)
    {
        try
        {
            if (await _app.Jellyfin.GetItemAsync(itemId) is { } fresh)
                _app.NotifyItemUserDataChanged(fresh);
        }
        catch
        {
            // offline: cards refresh on their next natural rebind
        }
        if (_nav.Current?.View is ItemDetailView detail)
            detail.RefreshDownloadState();
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        ProfileMenu.PlacementTarget = ProfileButton;
        ProfileMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        ProfileMenu.IsOpen = true;
    }

    /// <summary>Fills the rail's profile avatar + name from the active saved profile.
    /// The two glyphs live inside the button template, so they're reached by name.</summary>
    private void RefreshProfileAffordance()
    {
        var name = Jellyfin.CredentialStore.Load()?.Username;
        if (string.IsNullOrWhiteSpace(name))
            name = "Account";
        ProfileButton.ApplyTemplate();
        if (ProfileButton.Template.FindName("ProfileName", ProfileButton) is TextBlock nameBlock)
            nameBlock.Text = name;
        if (ProfileButton.Template.FindName("ProfileInitial", ProfileButton) is TextBlock initialBlock)
            initialBlock.Text = char.ToUpperInvariant(name.TrimStart()[0]).ToString();
    }

    /// <summary>Populates the rail's library destinations once per profile session.
    /// The buttons cache on the shell bundle, so re-activating a warm profile re-hangs
    /// them with no refetch (and NavFrame.Rail identity survives the round trip).</summary>
    private async Task LoadRailLibrariesAsync()
    {
        var shell = _shell;   // pin: a profile switch mid-await must not fill the wrong shell
        var jf = _app.Jellyfin;
        if (shell.LibrariesLoaded || !jf.IsConnected)
            return;
        shell.LibrariesLoaded = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("main", "event=load outcome=start screen=rail-libraries");
        try
        {
            var libraries = await jf.GetLibrariesAsync();
            shell.RailItems.Clear();
            shell.LibraryNav.Clear();
            foreach (var lib in libraries)
            {
                var item = BuildLibraryNavItem(lib);
                shell.RailItems.Add(item);
                shell.LibraryNav[lib.Id] = item;
            }
            if (ReferenceEquals(shell, _shell))
            {
                LibrariesNav.Children.Clear();
                foreach (var item in shell.RailItems)
                    LibrariesNav.Children.Add(item);
                ApplyRailCollapse(_railCollapsed);
            }
            Diagnostics.AppLog.Detail("main",
                $"event=load outcome=success screen=rail-libraries elapsed_ms={started.ElapsedMilliseconds} count={libraries.Count}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("main",
                $"event=load outcome=failure screen=rail-libraries elapsed_ms={started.ElapsedMilliseconds}", ex);
            shell.LibrariesLoaded = false;   // let the next Browse entry retry
        }
    }

    private RadioButton BuildLibraryNavItem(Jellyfin.MediaItem lib)
    {
        var item = new RadioButton
        {
            Style = (Style)FindResource("LwNavRailItem"),
            GroupName = "NavRail",
            Tag = (string)FindResource(LibraryIcons.ResourceKey(lib.CollectionType)),
            Content = lib.Name,
            ToolTip = lib.Name,
        };
        item.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "NavLibrary_" + lib.Name);
        item.Click += (_, _) => OnRailLibraryClick(lib, item);
        return item;
    }

    private void OnRailLibraryClick(Jellyfin.MediaItem lib, RadioButton item)
    {
        Diagnostics.AppLog.Detail("main", $"event=interaction action=rail-library item={lib.Id:N} type={lib.Type}");
        SearchBox.Clear();
        // Records history (Back no longer nukes the chain you came from); the rail root
        // de-dupe makes re-clicking the library you're already at a no-op.
        _nav.Navigate(new NavFrame(CreateLibraryView(lib), lib.Name, item, IsRailRoot: true));
        ShowCurrentFrame();
    }


    /// <summary>Below 1160 px window width the rail collapses to icons only (labels move
    /// to tooltips), per the spec.</summary>
    /// <summary>Keeps the header search pill from overflowing on narrow windows: it holds its
    /// 360 px design width when there's room and shrinks (down to 200) when there isn't.</summary>
    private void UpdateHeaderLayout()
    {
        if (SearchPill is not null)
            SearchPill.Width = System.Math.Clamp(ActualWidth - 640, 200.0, 360.0);
    }

    private void UpdateRailCollapse()
    {
        var collapsed = ActualWidth < 1160;
        if (collapsed == _railCollapsed)
            return;
        _railCollapsed = collapsed;
        ApplyRailCollapse(collapsed);
    }

    /// <summary>Switches the whole rail between expanded (232) and collapsed (64) layouts.
    /// The inherited theme:NavRail.IsCollapsed flag cascades to every item template.</summary>
    private void ApplyRailCollapse(bool collapsed)
    {
        NavRail.Width = collapsed ? 64 : 232;
        Theme.NavRail.SetIsCollapsed(NavRail, collapsed);
    }

    // ---- Search (debounced, results pushed on the browse stack) ----
    // The results view lives on the shell bundle (per profile).

    private System.Windows.Threading.DispatcherTimer? _searchTimer;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (_searchTimer is null)
        {
            _searchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer!.Stop();
                await RunSearchAsync();
            };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    // Generation counter for the search spinner. The spinner is a SHARED singleton in the
    // top bar, unlike the per-view load-more pill, so ownership has to be tracked: an older
    // search's finally must not switch off the spinner a newer in-flight search owns.
    private int _searchGeneration;

    private async Task RunSearchAsync()
    {
        var gen = ++_searchGeneration;   // entry invalidates any pending reveal
        var term = SearchBox.Text.Trim();
        if (term.Length < 2)
        {
            Diagnostics.AppLog.Detail("search", $"event=query outcome=clear generation={gen} term_length={term.Length}");
            PopSearchView();
            SearchSpinnerHost.Visibility = Visibility.Collapsed;
            return;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var shell = _shell;   // pin: a profile switch mid-search must not cross shells
        var profileKey = _app.ActiveSessionKey;
        Diagnostics.AppLog.Detail("search", $"event=load outcome=start generation={gen} term_length={term.Length}");
        // Tier 2: the search box is already painted, and on a refinement so are the results
        // underneath. Reveal only if the fetch outlives the gate - on this LAN it usually
        // does not, and a flashed spinner is worse than none.
        _ = Task.Delay(SkeletonFactory.RevealDelayMs).ContinueWith(_ =>
        {
            if (gen == _searchGeneration)
                SearchSpinnerHost.Visibility = Visibility.Visible;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var results = await _app.Jellyfin.SearchAsync(term);
            if (gen != _searchGeneration || SearchBox.Text.Trim() != term
                || _app.State != AppState.Browse || !ReferenceEquals(shell, _shell)
                || profileKey != _app.ActiveSessionKey)
            {
                Diagnostics.AppLog.Detail("search",
                    $"event=load outcome=stale generation={gen} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}");
                return; // stale query or state/profile changed while searching
            }
            if (shell.Search is not null && ReferenceEquals(_nav.Current?.View, shell.Search))
            {
                // refine in place instead of stacking a view per keystroke
                _nav.ReplaceCurrent(new NavFrame(shell.Search, $"Search: {term}", _nav.Current!.Rail));
                UpdateBrowseHeader();
            }
            else
            {
                shell.Search = new SearchResultsView();
                shell.Search.ItemSelected += OnBrowseItemSelected;
                NavigateTo(shell.Search, $"Search: {term}");
            }
            shell.Search.SetResults(results);
            Diagnostics.AppLog.Detail("search",
                $"event=load outcome=success generation={gen} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length} count={results.Count}");
        }
        catch (Exception ex)
        {
            var stale = gen != _searchGeneration || SearchBox.Text.Trim() != term
                || _app.State != AppState.Browse || !ReferenceEquals(shell, _shell)
                || profileKey != _app.ActiveSessionKey;
            if (stale)
                Diagnostics.AppLog.Detail("search",
                    $"event=load outcome=stale generation={gen} phase=exception elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}");
            else
                Diagnostics.AppLog.Detail("search",
                    $"event=load outcome=failure generation={gen} elapsed_ms={started.ElapsedMilliseconds} term_length={term.Length}", ex);
            // search is best-effort; whatever view is showing stays
        }
        finally
        {
            // Guarded, NOT unconditional: only the newest search may clear the shared
            // spinner. Unconditional here would let a superseded search switch off the
            // indicator that the search still running behind it needs.
            if (gen == _searchGeneration)
                SearchSpinnerHost.Visibility = Visibility.Collapsed;
        }
    }

    private void PopSearchView()
    {
        if (_shell.Search is not null && ReferenceEquals(_nav.Current?.View, _shell.Search) && _nav.CanGoBack)
            BrowseBack();
    }

    private void OnBrowseItemSelected(Jellyfin.MediaItem item)
    {
        Diagnostics.AppLog.Detail("main", $"event=interaction action=open-item item={item.Id:N} type={item.Type}");
        if (item.IsPlayable)
        {
            var detail = new ItemDetailView(_app, _settings, item);
            detail.PlayRequested += OnDetailPlayRequested;
            detail.ItemSelected += OnBrowseItemSelected;
            // M5: a cast/crew face opens the Advanced Search Screen scoped to that person,
            // across movies + series server-wide.
            detail.PersonSelected += p => OpenAdvancedSearch(new Jellyfin.AdvancedSearchQuery
            {
                PersonIds = [p.Id],
                PersonName = p.Name,   // labels the ASS redesign's removable context chip
                ItemTypes = [global::Jellyfin.Sdk.Generated.Models.BaseItemKind.Movie,
                             global::Jellyfin.Sdk.Generated.Models.BaseItemKind.Series],
            });
            // M6: a genre label opens the Advanced Search Screen scoped to that genre,
            // server-wide (the global genre path; library-scoped genre drill stays in GenresView).
            detail.GenreSelected += g => OpenAdvancedSearch(new Jellyfin.AdvancedSearchQuery { Genres = [g] });
            // M20: the download affordance (picker + enqueue / confirmed removal).
            detail.DownloadRequested += OnDownloadRequested;
            detail.DownloadRemoveRequested += OnDownloadRemoveRequested;
            NavigateTo(detail, item.Name);
        }
        else if (item.IsBrowsable)
        {
            NavigateTo(CreateLibraryView(item), item.Name);
        }
    }

    private LibraryView CreateLibraryView(Jellyfin.MediaItem item, string? genre = null)
    {
        var library = new LibraryView(_app, item, genre);
        library.ItemSelected += OnBrowseItemSelected;
        library.PlayAllRequested += OnPlayAllRequested;
        library.GenresRequested += OnGenresRequested;
        library.DownloadAllRequested += OnDownloadAllRequested;
        return library;
    }

    private void OnGenresRequested(Jellyfin.MediaItem library)
    {
        var genres = new GenresView(_app, library);
        genres.GenreSelected += genre =>
            NavigateTo(CreateLibraryView(library, genre), $"{library.Name} · {genre}");
        NavigateTo(genres, $"{library.Name} · Genres");
    }

    private void OnAdvancedSearch(object sender, RoutedEventArgs e) => OpenAdvancedSearch();

    /// <summary>Push the Advanced Search Screen (Phase 7 M4). An optional seed query preloads
    /// the filters — M5 (person) and M6 (genre) call this with a pre-filled query.</summary>
    private void OpenAdvancedSearch(Jellyfin.AdvancedSearchQuery? seed = null)
    {
        Diagnostics.AppLog.Detail("main",
            $"event=interaction action=open-advanced-search seed={(seed is null ? "none" : seed.PersonIds.Count > 0 ? "person" : seed.Genres.Count > 0 ? "genre" : "filters")}");
        var view = new AdvancedSearchView(_app, seed);
        view.ItemSelected += OnBrowseItemSelected;
        // Global search — belongs to no library, so navigate railless (Rail=null); the
        // header's rail logic then clears any active-library highlight.
        _nav.Navigate(new NavFrame(view, "Advanced search", null));
        ShowCurrentFrame();
    }

    /// <summary>Plays a server item, optionally resuming at the given ticks position.
    /// Non-queue playbacks clear the queue; queue navigation passes fromQueue. A fresh
    /// start pins the ACTIVE session as the playback session; queue advancement keeps
    /// the pin, so auto-advance still talks to the server that built the queue even
    /// after a profile switch.</summary>
    private void PlayItem(Jellyfin.MediaItem item, long resumeTicks, bool fromQueue = false,
        Jellyfin.JellyfinService? session = null)
    {
        _playbackJf = session ?? (fromQueue ? _playbackJf : null) ?? _app.Jellyfin;
        if (!fromQueue)
            _app.Queue.Clear();
        _app.PublishStopReport(_reporter?.Stop());
        _playingItem = item;
        var playbackSequence = ++_playbackSequence;
        _pendingResumeTicks = resumeTicks;
        _externalSubs = null;
        _subtitleState = null;
        _playbackDecision = null;
        _transcodeRetryDone = false;
        _playerViewModel.SetPlayMethod(null);
        _playerViewModel.SetTrickplay(null);
        _playerViewModel.SetUpNext(null);
        _playerViewModel.SetEpisodeNeighbors(false, null, null);
        _playerViewModel.SetMediaInfo(null);
        _playerViewModel.SetNowPlaying(item.PlaybackTitle, item.PlaybackSubtitle, item.PlaybackRelease);
        UpdateSystemMediaDisplay(item.PlaybackTitle, item.PlaybackSubtitle,
            item.ThumbUrl ?? item.PosterUrl);
        _playerViewModel.SetServerChapters(item.Chapters.Select(c => c.StartSeconds));
        _playerViewModel.SetSkipSegments([], _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        // Next-in-queue owns the Up Next card; the AdjacentTo episode lookup in
        // LoadPlaybackMetadataAsync is the no-queue fallback. The card is armed regardless of
        // the AutoPlayNextEpisode setting — that setting only decides whether it ADVANCES on
        // its own (decided 2026-07-30); a queue still overrides nothing here.
        if (_app.Queue.PeekNext() is { } queuedNext)
            _playerViewModel.SetUpNext(queuedNext, _settings.AutoPlayCountdownSeconds,
                _settings.AutoPlayNextEpisode);
        _ = LoadPlaybackMetadataAsync(item, playbackSequence);
        // Local-first (M20): a completed download plays from disk — no negotiation, no
        // auth header, no server session/progress reports. mpv still owns its render
        // HWND and gets the file through the same PlayCore→loadfile path; only the
        // source changes from an authenticated URL to a local path.
        if (_downloads.GetCompletedDownload(item.Id) is { } local)
        {
            _playingLocal = true;
            _playerViewModel.SetPlayMethod("Local file");
            PlayCore(local.FilePath, null);
            return;
        }
        _playingLocal = false;
        _ = StartNegotiatedPlaybackAsync(item);
    }

    /// <summary>Detail-view Play: an episode continues into the rest of its show (M15);
    /// everything else keeps the plain, queue-clearing single play.</summary>
    private void OnDetailPlayRequested(Jellyfin.MediaItem item, long resumeTicks)
    {
        if (item is { Type: global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode, SeriesId: not null })
            OnEpisodePlayRequested(item, resumeTicks);
        else
            PlayItem(item, resumeTicks);
    }

    /// <summary>An explicit episode start means "watch from here": queue the episode plus
    /// every one AFTER it in series order (not watched-filtered — rewatches included).
    /// Mid-playback callers (the OSD episode-step pair) resolve against the pinned
    /// playback session; browse callers use the active one.</summary>
    private async void OnEpisodePlayRequested(Jellyfin.MediaItem ep, long resumeTicks)
    {
        var jf = _app.State == AppState.Playing ? _playbackJf ?? _app.Jellyfin : _app.Jellyfin;
        try
        {
            var episodes = await jf.GetSeriesEpisodesAsync(ep.SeriesId!.Value);
            var idx = episodes.FindIndex(e => e.Id == ep.Id);
            if (idx >= 0)
            {
                var slice = episodes.Skip(idx).Where(e => e.IsPlayable).ToList();
                if (slice.Count > 1)
                {
                    _app.Queue.Set(slice, 0);
                    PlayItem(slice[0], resumeTicks, fromQueue: true, session: jf);
                    return;
                }
            }
        }
        catch
        {
            // continuation is best-effort; fall through to the plain single play
        }
        PlayItem(ep, resumeTicks, session: jf);
    }

    /// <summary>Builds a queue from a container (season/series/playlist) and starts it.</summary>
    private async void OnPlayAllRequested(Jellyfin.MediaItem source, bool shuffle)
    {
        try
        {
            var jf = _app.Jellyfin;
            List<Jellyfin.MediaItem> items = source.Type switch
            {
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Season
                    when source.SeriesId is { } seriesId => await jf.GetEpisodesAsync(seriesId, source.Id),
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Series
                    => await jf.GetSeriesEpisodesAsync(source.Id),
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Playlist
                    => await jf.GetPlaylistItemsAsync(source.Id),
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.MusicAlbum
                    => await jf.GetAlbumTracksAsync(source.Id),
                global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.MusicArtist
                    => await jf.GetArtistTracksAsync(source.Id),
                _ => [],
            };
            items = items.Where(i => i.IsPlayable).ToList();
            if (items.Count == 0)
            {
                ShowToast("Nothing playable in here.");
                return;
            }
            // M15: a show/season plays through its UNWATCHED episodes; a fully-watched
            // container falls back to the full ordered list so Play is never a no-op.
            // Playlists keep full server order.
            if (source.Type is global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Season
                or global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Series)
            {
                var unwatched = items.Where(i => !i.Played).ToList();
                if (unwatched.Count > 0)
                    items = unwatched;
            }
            if (shuffle)
            {
                for (var i = items.Count - 1; i > 0; i--)
                {
                    var j = Random.Shared.Next(i + 1);
                    (items[i], items[j]) = (items[j], items[i]);
                }
            }
            _app.Queue.Set(items, 0);
            PlayItem(items[0], 0, fromQueue: true);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not start the queue: {ex.Message}");
        }
    }

    private int MaxBitrateBps => _settings.MaxStreamingBitrateMbps * 1_000_000;

    /// <summary>Negotiates the play method with the server, then starts playback.
    /// Falls back to plain direct play (pre-negotiation behavior) when the POST fails.</summary>
    private async Task StartNegotiatedPlaybackAsync(Jellyfin.MediaItem item)
    {
        // Switch to the video layer immediately — negotiation is one fast POST, but
        // the UI must not sit on the detail view waiting for the network.
        var jf = _playbackJf ?? _app.Jellyfin;
        _app.EnterPlayback();
        var decision = await jf.NegotiatePlaybackAsync(item.Id, MaxBitrateBps);
        if (_playingItem?.Id != item.Id || _app.State != AppState.Playing)
            return; // user navigated away while negotiating
        _playbackDecision = decision;
        _playerViewModel.SetPlayMethod(decision is { IsTranscode: true } ? "Transcode (HLS)" : "Direct play");
        var url = decision?.Url ?? jf.GetStreamUrl(item.Id);
        // Diagnostics: LIGHTWEAVER_BREAK_DIRECT_PLAY=1 mangles direct-play URLs (404)
        // so automated tests can exercise the transcode retry path.
        if (decision is not { IsTranscode: true }
            && Environment.GetEnvironmentVariable("LIGHTWEAVER_BREAK_DIRECT_PLAY") == "1")
            url = url.Replace("/stream?", "/stream-broken?");
        PlayCore(url, jf.AuthorizationHeader);
    }

    /// <summary>A direct play that failed gets one retry through the server transcoder
    /// before the failure surfaces (codec/container the client can't handle, etc.).</summary>
    private async Task RetryViaTranscodeAsync(Jellyfin.MediaItem item, string reason)
    {
        _app.PublishStopReport(_reporter?.Stop());
        var jf = _playbackJf ?? _app.Jellyfin;
        var decision = await jf.NegotiatePlaybackAsync(item.Id, MaxBitrateBps,
            forceTranscode: true);
        if (_playingItem?.Id != item.Id || _app.State != AppState.Playing)
            return;
        if (decision is { IsTranscode: true })
        {
            _playbackDecision = decision;
            _playerViewModel.SetPlayMethod("Transcode (HLS)");
            PlayCore(decision.Url, jf.AuthorizationHeader);
        }
        else
        {
            StopPlaybackAndReturn();
            ShowToast($"Playback failed: {reason}");
        }
    }

    /// <summary>Chapters and skip segments need the full item — the caller's copy may
    /// predate the detail view's refresh (list queries omit chapters), so re-fetch.
    ///
    /// <para>Each step is INDEPENDENTLY guarded (B26). This was one <c>try</c> around six serial
    /// awaits with an empty catch, so a single flaky call silently killed every later step: a
    /// segments failure meant no external subtitles, no media info, no trickplay and — worst — no
    /// Up Next, with nothing logged, on a network this project documents as intermittently dropping
    /// connections. Order is user-visibility now, not the order it was written in: subtitles and Up
    /// Next before trickplay, which nobody notices arriving late.</para></summary>
    private async Task LoadPlaybackMetadataAsync(Jellyfin.MediaItem item, int playbackSequence)
    {
        var jf = _playbackJf ?? _app.Jellyfin;   // the session that owns this playback

        // Still playing the item this call was started for? Every step re-checks, because each
        // await is a chance for the user to have moved on.
        bool StillCurrent() => _playingItem?.Id == item.Id && _playbackSequence == playbackSequence;

        // Runs one best-effort step. A failure is contained to that step and NAMED — the empty
        // catch is what made this invisible.
        async Task Step(string name, Func<Task> body)
        {
            try
            {
                if (StillCurrent())
                    await body();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"playback metadata: {name} failed - {ex.Message}");
                Diagnostics.AppLog.Error("playback-metadata", $"{name} failed", ex);
            }
        }

        var fresh = item;
        await Step("item refresh", async () =>
        {
            fresh = await jf.GetItemAsync(item.Id) ?? item;
            if (StillCurrent() && fresh.Chapters.Count > 0)
                _playerViewModel.SetServerChapters(fresh.Chapters.Select(c => c.StartSeconds));
        });

        // Up Next first among the network steps: it is the one with a deadline (the card has to be
        // armed before the user reaches the credits), and it only needs `fresh` for its type.
        if (fresh is { Type: global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode, SeriesId: { } seriesId })
            await Step("adjacent episodes", async () =>
            {
                // One AdjacentTo fetch feeds both the episode-step pair and the
                // queue-less Up Next fallback.
                var (prev, next) = await jf.GetAdjacentEpisodesAsync(seriesId, item.Id);
                if (!StillCurrent())
                    return;
                _playerViewModel.SetEpisodeNeighbors(true, prev, next);
                // Arm regardless of the setting: with AutoPlayNextEpisode off this is where
                // the user used to get NOTHING, and now gets a manual card. The setting is
                // passed through as autoAdvance instead of gating the arming.
                if (next is not null && !_app.Queue.IsActive)
                    _playerViewModel.SetUpNext(next, _settings.AutoPlayCountdownSeconds,
                        _settings.AutoPlayNextEpisode);
            });

        await Step("external subtitles", async () =>
        {
            var subs = await jf.GetExternalSubtitlesAsync(item.Id);
            if (StillCurrent() && subs.Count > 0)
            {
                _externalSubs = new ExternalSubtitleState(playbackSequence, subs);
                TryAddExternalSubtitles();
            }
        });

        await Step("skip segments", async () =>
        {
            var segments = await Jellyfin.MediaSegmentsClient.GetSegmentsAsync(jf, fresh);
            if (StillCurrent() && segments.Count > 0)
                _playerViewModel.SetSkipSegments(segments, _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        });

        await Step("media info", async () =>
        {
            var mediaInfo = await jf.GetMediaInfoAsync(item.Id);
            if (StillCurrent() && mediaInfo.Count > 0)
                _playerViewModel.SetMediaInfo(mediaInfo.Select(f => new InfoRow(f.Label, f.Value)));
        });

        await Step("trickplay", async () =>
        {
            var trickplay = await jf.GetTrickplayAsync(item.Id);
            if (StillCurrent() && trickplay is not null)
                _playerViewModel.SetTrickplay(new Jellyfin.TrickplayProvider(jf, item.Id, trickplay));
        });
    }

    /// <summary>Feeds fetched external subtitles to the live loaded generation. A retry generation
    /// gets the same playback's batch again; an old metadata request cannot cross the sequence.</summary>
    private void TryAddExternalSubtitles()
    {
        if (_player is not { } player
            || _subtitleState is not { FileLoaded: true } state
            || state.Generation != player.LoadGeneration
            || _externalSubs is not { } external
            || external.PlaybackSequence != state.PlaybackSequence
            || external.AppliedGeneration == state.Generation)
            return;
        external.AppliedGeneration = state.Generation;
        foreach (var sub in external.Subtitles)
            player.AddExternalSubtitle(sub.Url, sub.Title, sub.Language);
    }

    private void OnVideoHwndReady(nint hwnd)
    {
        // Re-entry guard. Nothing rebuilds the HwndHost child today, but if WPF ever did, this
        // would construct a SECOND MpvPlayer, and PlayerViewModel.Attach would double-subscribe
        // every handler — so each mpv event would fire its handler twice, with two players fighting
        // over one view model — while the first instance leaked, holding its own mpv context and the
        // old HWND. Cheap to prevent, miserable to diagnose.
        if (_player is not null)
        {
            System.Diagnostics.Trace.WriteLine(
                "OnVideoHwndReady fired with a player already attached - ignoring the second HWND.");
            Diagnostics.AppLog.Error("player",
                "OnVideoHwndReady fired with a player already attached - ignored the second HWND");
            return;
        }
        try
        {
            _player = MpvPlayer.Create(hwnd, Dispatcher, _settings);
        }
        catch (Exception ex) when (ex is DllNotFoundException or MpvException)
        {
            // M23: branded in-app alert instead of the native MessageBox.
            Diagnostics.AppLog.Error("player", "mpv failed to initialize", ex);
            _ = Modal.AlertAsync("Playback engine failed",
                $"Failed to initialize mpv:\n{ex.Message}\n\n" +
                "Make sure libmpv-2.dll (mpv ≥ 0.40) is next to the exe or on PATH.");
            return;
        }

        _playerViewModel.Attach(_player);
#if DEBUG
        // The generation-ordering regression suite requests B through the real shell path so its
        // per-load subtitle state changes exactly as a user-initiated local playback would.
        _player.TestLoadFileRequested += path => PlayUrl(path, null);
#endif
        _playerViewModel.ApplyRtxDefaults(_settings);
        _player.PauseChanged += paused =>
        {
            _systemMedia?.SetPlaybackState(playing: !paused);
            ThumbPlayPause.ImageSource = (System.Windows.Media.ImageSource)FindResource(
                paused ? "IconThumbPlay" : "IconThumbPause");
        };
        // Trace only. The diagnostics ring is fed at the two SOURCES instead — mpv's own
        // warn/error/fatal lines on the event thread (so they survive a UI-thread crash, where a
        // Dispatcher post would never run) and the app's player-side messages in MpvPlayer.Say.
        // Breadcrumbing from this subscriber would double-count everything mpv sends, because both
        // kinds arrive here as one undifferentiated event.
        _player.LogMessage += msg => System.Diagnostics.Trace.WriteLine($"mpv: {msg}");
        // Passthrough was asked for and could not be initialised. Worth a toast rather than a
        // silent recovery: without it, a setting the user deliberately enabled would go on
        // doing nothing with no way to find out — which is how the stall it replaces went
        // unnoticed for as long as it did.
        _player.PassthroughUnavailable += reason =>
        {
            Diagnostics.AppLog.Error("audio", $"passthrough unavailable: {reason}");
            ShowToast(reason);
        };
        _player.AudioFilterUnavailable += reason =>
        {
            Diagnostics.AppLog.Error("audio", $"audio filter unavailable: {reason}");
            ShowToast(reason);
        };
        // Audio stayed silent after an output-device change and the automatic resync did not fix it
        // (B39). Only the FAILED repair reaches here — a successful one is silent by design — so
        // this toast exists to hand over the manual fix rather than leave the user with video and
        // no sound and no idea why.
        _player.AudioResyncFailed += reason =>
        {
            Diagnostics.AppLog.Error("audio", $"audio resync failed: {reason}");
            ShowToast(reason);
        };
        // Playback stopped and neither repair stage restarted it (B41). Same contract as the
        // resync toast above: a repair that worked says nothing, so reaching here means the user
        // is looking at a frozen picture and needs to be told it is not going to come back on its
        // own — the alternative is what happened when this was reported, which was quitting.
        _player.PlaybackStalled += reason =>
        {
            Diagnostics.AppLog.Error("player", $"playback stall unrecovered: {reason}");
            ShowToast(reason);
        };
        _player.PlaybackFailed += reason =>
        {
            // Failed direct play of a server item: retry once via the transcoder.
            if (_app.State == AppState.Playing && _playingItem is { } item
                && _playbackDecision is not { IsTranscode: true } && !_transcodeRetryDone)
            {
                _transcodeRetryDone = true;
                // One app.log LINE, not an incident file. A transcode fallback is the NORMAL path
                // for any library holding a container this client cannot direct-play, so writing
                // playback-<ts>.log here meant a working playback produced an incident file — and
                // those files share a ten-slot budget with crash logs, so an evening of
                // transcoded viewing would evict the crash you actually needed. The failure still
                // gets written down (it is a real quality and server-CPU cost, and "why does this
                // always transcode" has to be answerable) and it is in the ring, so if the retry
                // also fails, the file that failure writes carries this line as context.
                Diagnostics.AppLog.Error("player",
                    $"direct play failed ({reason}); retrying via the server transcoder");
                _ = RetryViaTranscodeAsync(item, reason);
                return;
            }
            WritePlaybackFailureLog(reason, "playback stopped");
            if (_app.State == AppState.Playing)
                StopPlaybackAndReturn();
            ShowToast($"Playback failed: {reason}");
        };
        // The provider is sampled at each Start(): reports pin to the session that
        // started the playback, surviving mid-play profile switches.
        _reporter = new Jellyfin.PlaybackReporter(() => (_playbackJf ?? _app.Jellyfin).Client,
            () => (_player?.TimePos ?? 0, _player?.Pause ?? true));

        _player.FileLoaded += generation =>
        {
            // A load started after this event was posted makes it stale. Subtitle work below is
            // entirely generation-gated; resume retains the B10 guard as well.
            var stale = generation != (_player?.LoadGeneration ?? generation);
            // AppState is already Playing before FILE_LOADED. A request made in that interval is
            // retained as a cycle count without consulting an outgoing file's options. The
            // synchronous refresh is the first nonempty list this generation may publish.
            if (!stale && _subtitleState is { } state && state.Generation == generation)
            {
                if (_player?.RefreshTracks(generation) == true)
                {
                    state.FileLoaded = true;
                    state.DeferPendingCycles = true;
                    TryAddExternalSubtitles();
                    ApplyLanguagePreferences(generation);
                    // Preference restoration changes sid synchronously but its observed track-list
                    // can lag. Snapshot once more before applying queued S presses so their base
                    // row is the restored current choice, never the pre-restoration observation.
                    if (state.PendingCycleCount > 0)
                        _player.RefreshTracks(generation);
                    state.DeferPendingCycles = false;
                    TryApplyPendingSubtitleCycles(state);
#if DEBUG
                    ScheduleTestLateSubtitle(state);
#endif
                }
#if DEBUG
                else
                {
                    _player?.TestLateSubtitleSkip("file_loaded_refresh_failed", generation,
                        state.PendingCycleCount);
                }
#endif
            }
            var resumeTicks = _pendingResumeTicks;
            if (resumeTicks > 0 && !stale && _player is { } p)
            {
                p.TimePos = TimeSpan.FromTicks(resumeTicks).TotalSeconds;
                _pendingResumeTicks = 0;
            }
            // Local-first playback (M20) deliberately reports nothing — there is no
            // server session; watched-state sync-back is a noted follow-up.
            if (_playingItem is { } item && !_playingLocal)
                _reporter?.Start(item.Id, resumeTicks, _playbackDecision?.PlaySessionId,
                    _playbackDecision?.MediaSourceId, _playbackDecision?.IsTranscode ?? false);
        };

        _player.TracksChanged += ApplyLanguagePreferences;

        if (_pendingLoad is { } pending)
        {
            _pendingLoad = null;
            _player.SetHttpHeaders(pending.AuthHeader);
            var generation = _player.LoadFile(pending.Url);
            _subtitleState = new SubtitleGenerationState(generation, _playbackSequence);
        }
    }

    /// <summary>
    /// Writes the playback-failure file: mpv's reason, what the app did next, and everything the
    /// app could still see about the stream. The summary is the whole value of the file — mpv's
    /// error string on its own ("Unrecognized file format") names neither the codec, the
    /// container, the play method nor the decoder, which is exactly the set of facts needed to
    /// tell a bad file from a bad transcode from a broken hwdec path.
    ///
    /// <para>Read while the failure is fresh: the properties below are gone the moment mpv moves
    /// on, so this cannot be deferred to a background write.</para>
    /// </summary>
    private void WritePlaybackFailureLog(string reason, string action)
    {
        var summary = new List<string> { $"action: {action}" };
        try
        {
            if (_playingItem is { } item)
            {
                var episode = item.IndexNumber is { } n && item.ParentIndexNumber is { } s
                    ? $" (S{s:00}E{n:00})" : string.Empty;
                summary.Add($"item: {item.Name}{episode} [{item.Type}] {item.Id}");
                if (item.SeriesName is { Length: > 0 } series)
                    summary.Add($"series: {series}");
            }
            summary.Add("source: " + (_playingLocal ? "local file (completed download)"
                : _playbackDecision is { IsTranscode: true } ? "server, transcode (HLS)"
                : _playbackDecision is not null ? "server, direct play"
                : _playingItem is null ? "local file or direct URL (no server item)"
                : "server, direct play (negotiation failed)"));
            if (_playbackDecision?.Url is { Length: > 0 } url)
                summary.Add($"url: {url}");

            if (_player is { } player)
            {
                // Each read is its own statement so one unavailable property cannot cost the rest.
                string? Prop(string name)
                {
                    try { return player.GetPropertyString(name); }
                    catch (Exception ex) when (ex is MpvException or ObjectDisposedException) { return null; }
                }

                summary.Add($"mpv: {Prop("mpv-version") ?? "unknown"} (hwdec-current={Prop("hwdec-current") ?? "none"})");
                summary.Add($"container: {Prop("file-format") ?? "unknown"}");
                // Invariant, like every other number this project logs: a German locale wrote
                // "position: 0,0 s" here, and comma decimals are what broke log parsing before.
                summary.Add(FormattableString.Invariant(
                    $"position: {player.TimePos:F1} s of {Prop("duration") ?? "unknown"} s"));
                var video = player.Tracks.FirstOrDefault(t => t is { Type: "video", Selected: true });
                summary.Add($"video: {video?.Codec ?? Prop("video-codec") ?? "none"} " +
                            $"{video?.Width ?? player.VideoWidth}x{video?.Height ?? player.VideoHeight} " +
                            $"gamma={Prop("video-params/gamma") ?? "unknown"} hdr={player.IsHdrContent}");
                var audio = player.Tracks.FirstOrDefault(t => t is { Type: "audio", Selected: true });
                summary.Add($"audio: {audio?.Codec ?? Prop("audio-codec-name") ?? "none"} " +
                            $"lang={audio?.Lang ?? "?"} " +
                            $"channels={Prop("audio-params/channel-count") ?? "?"} " +
                            $"ao={player.CurrentAudioOutput ?? "none"}");
                summary.Add($"tracks: {player.Tracks.Count(t => t.Type == "video")} video, " +
                            $"{player.Tracks.Count(t => t.Type == "audio")} audio, " +
                            $"{player.Tracks.Count(t => t.Type == "sub")} sub");
                summary.Add($"rtx: vsr={player.RtxVsrActive} hdr={player.RtxHdrActive}" +
                            (player.RtxUnavailableReason is { } why ? $" ({why})" : string.Empty));
            }
            else
            {
                summary.Add("player: no mpv instance (failed before or during init)");
            }

            summary.Add($"settings: hwdec={_settings.HardwareDecoding} " +
                        $"passthrough={_settings.AudioPassthrough} " +
                        $"normalization={_settings.VolumeNormalization} " +
                        $"device={_settings.AudioDevice} " +
                        $"maxBitrate={_settings.MaxStreamingBitrateMbps}Mbps");
        }
        catch (Exception ex)
        {
            // Never let the diagnostics path turn a handled playback failure into a crash.
            summary.Add($"(summary incomplete: {ex.GetType().Name}: {ex.Message})");
        }
        Diagnostics.AppLog.PlaybackFailure(reason, summary);
    }

    /// <summary>Reconciles only the live generation. Same-generation intent is exact; only a later
    /// file may use the remembered language-first fallback.</summary>
    private void ApplyLanguagePreferences(int generation)
    {
        if (_player is not { } player || player.LoadGeneration != generation
            || _subtitleState is not { FileLoaded: true } state || state.Generation != generation)
            return;

        if (state.PendingIntent is { } intent)
        {
            if (ApplyExactSubtitleIntent(player, intent))
            {
                state.PendingIntent = null;
                state.StickyApplied = true;
            }
            ApplyAudioPreference(player, state);
            TryApplyPendingSubtitleCycles(state);
            return;
        }

        // Retry the cross-file choice until late embedded/external tracks make it resolvable.
        if (_stickySub is { } sticky && !state.StickyApplied)
        {
            var stickyResult = ApplyStickySubtitle(player, sticky);
            if (stickyResult.Status == StickySubtitleApplyStatus.SelectionCommandIssued)
            {
                state.AwaitingStickySelection = true;
#if DEBUG
                player.TestLateSubtitleMarker("sticky_command", state.Generation,
                    state.PendingCycleCount, player.Tracks.Count(t => t.Type == "sub"));
#endif
                return;
            }
            if (stickyResult.Status == StickySubtitleApplyStatus.AlreadySelected)
            {
                if (state.AwaitingStickySelection)
                {
                    if (!_playerViewModel.PrimeSubtitleCycleCursor(
                            state.Generation, stickyResult.TrackId))
                    {
#if DEBUG
                        player.TestLateSubtitleSkip("cursor_prime_failed", state.Generation,
                            state.PendingCycleCount);
#endif
                        return;
                    }
#if DEBUG
                    player.TestLateSubtitleMarker("cursor_prime", state.Generation,
                        state.PendingCycleCount, player.Tracks.Count(t => t.Type == "sub"));
                    player.TestLateSubtitleMarker("sticky_ack", state.Generation,
                        state.PendingCycleCount, player.Tracks.Count(t => t.Type == "sub"));
#endif
                }
                state.AwaitingStickySelection = false;
                state.StickyApplied = true;
            }
            else if (state.AwaitingStickySelection)
            {
                // The commanded target disappeared before acknowledgement. Keep both the sticky
                // restoration and queued key count pending for a later matching track wave.
                return;
            }
        }

        if (player.Tracks.Count == 0)
        {
            TryApplyPendingSubtitleCycles(state);
            return;
        }
        if (state.LanguagePreferencesApplied)
        {
            TryApplyPendingSubtitleCycles(state);
            return;
        }
        state.LanguagePreferencesApplied = true;

        if (_settings.PreferredAudioLanguage is { Length: > 0 } audioLang)
        {
            var match = player.Tracks.FirstOrDefault(t => t.Type == "audio"
                && Languages.Matches(t.Lang, audioLang));
            if (match is not null && !match.Selected)
                player.SelectAudioTrack(match.Id);
        }
        // The user's own in-player choice outranks the stored preference: it is the more recent and
        // more specific instruction. Before this, a manual pick was discarded by the next
        // loadfile - pick English subtitles on episode 1 and episode 2 started with none, which is
        // the reported bug. TECHNICAL.md described that as intentional ("an in-player choice never
        // leaks into the next queue item"); it reads as intentional only until you watch two
        // episodes in a row. Applied above, before this one-shot, so a late track still counts.
        if (_stickySub is not null)
        {
            TryApplyPendingSubtitleCycles(state);
            return;
        }
        if (_settings.PreferredSubtitleLanguage is { Length: > 0 } subLang)
        {
            var match = player.Tracks.FirstOrDefault(t => t.Type == "sub"
                && Languages.Matches(t.Lang, subLang));
            if (match is not null && !match.Selected)
                player.SelectSubtitleTrack(match.Id);
        }
        TryApplyPendingSubtitleCycles(state);
    }

    private void TryApplyPendingSubtitleCycles(SubtitleGenerationState state)
    {
        if (state.DeferPendingCycles || state.AwaitingStickySelection || state.PendingCycleCount <= 0
            || _player is null || state.Generation != _player.LoadGeneration)
            return;
        var count = state.PendingCycleCount;
        if (_playerViewModel.ExecuteQueuedSubtitleCycles(count))
        {
            state.PendingCycleCount = 0;
#if DEBUG
            _player.TestLateSubtitleMarker("cycle_applied", state.Generation, count,
                _player.Tracks.Count(t => t.Type == "sub"));
#endif
        }
    }

    private void ApplyAudioPreference(MpvPlayer player, SubtitleGenerationState state)
    {
        if (state.LanguagePreferencesApplied || player.Tracks.Count == 0)
            return;
        state.LanguagePreferencesApplied = true;
        if (_settings.PreferredAudioLanguage is not { Length: > 0 } audioLang)
            return;
        var match = player.Tracks.FirstOrDefault(t => t.Type == "audio"
            && Languages.Matches(t.Lang, audioLang));
        if (match is not null && !match.Selected)
            player.SelectAudioTrack(match.Id);
    }

    /// <summary>Captures the newest exact request before PlayerViewModel changes sid.</summary>
    private void RememberSubtitleChoice(SubtitleSelectionRequest request)
    {
        if (_player is not { } player || request.Generation != player.LoadGeneration
            || _subtitleState is not { } state || state.Generation != request.Generation)
            return;

        _stickySub = new StickySubtitle(request.Off, request.Lang, request.Title, request.Forced);
        state.PendingIntent = request;
        state.StickyApplied = false;
        state.AwaitingStickySelection = false;
    }

    /// <summary>Queues an early S press against the incoming generation without consulting the
    /// still-empty UI collection or any outgoing file rows.</summary>
    private bool TryQueueSubtitleCycleBeforeFileLoaded()
    {
        if (_player is not { } player
            || _subtitleState is not { FileLoaded: false } state
            || state.Generation != player.LoadGeneration)
            return false;
        state.PendingCycleCount++;
#if DEBUG
        player.TestLateSubtitleMarker("queue", state.Generation, state.PendingCycleCount,
            player.Tracks.Count(t => t.Type == "sub"));
#endif
        if (Diagnostics.AppLog.Verbose)
        {
            Diagnostics.AppLog.Detail("shortcut", FormattableString.Invariant(
                $"event=queue action=CycleSubtitle generation={state.Generation} count={state.PendingCycleCount}"));
        }
        return true;
    }

    /// <summary>Applies and acknowledges only the exact generation-local request. Off always sends
    /// sid=no, but an empty list cannot acknowledge it.</summary>
    private static bool ApplyExactSubtitleIntent(
        MpvPlayer player, SubtitleSelectionRequest intent)
    {
        var subs = player.Tracks.Where(t => t.Type == "sub").ToList();
        if (intent.Off)
        {
            player.SelectSubtitleTrack(null);
            return subs.Count > 0 && subs.All(t => !t.Selected);
        }
        var match = subs.FirstOrDefault(t => t.Id == intent.TrackId
            && string.Equals(t.Lang, intent.Lang, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Title, intent.Title, StringComparison.OrdinalIgnoreCase)
            && t.Forced == intent.Forced);
        if (match is null)
            return false;
        if (!match.Selected)
        {
            player.SelectSubtitleTrack(match.Id);
            return false;
        }
        return true;
    }

    /// <summary>Re-resolves a remembered choice against this file's tracks. Best match first:
    /// same language AND same forced-ness, then same language, then the same track title (which
    /// is what carries an external subtitle's identity — "English (SDH)" and the like).
    /// <para>The result distinguishes an acknowledged selection from a command awaiting mpv's
    /// observed track-list update, so queued shortcut presses cannot run from the old row.</para></summary>
    private static StickySubtitleApplyResult ApplyStickySubtitle(MpvPlayer player, StickySubtitle sticky)
    {
        var subs = player.Tracks.Where(t => t.Type == "sub").ToList();
        if (sticky.Off)
        {
            if (subs.Count > 0 && subs.All(t => !t.Selected))
                return new(StickySubtitleApplyStatus.AlreadySelected, null);
            player.SelectSubtitleTrack(null);
            return new(StickySubtitleApplyStatus.SelectionCommandIssued, null);
        }
        var match =
            (sticky.Lang is { Length: > 0 }
                ? subs.FirstOrDefault(t => Languages.Matches(t.Lang, sticky.Lang) && t.Forced == sticky.Forced)
                  ?? subs.FirstOrDefault(t => Languages.Matches(t.Lang, sticky.Lang))
                : null)
            ?? (sticky.Title is { Length: > 0 }
                ? subs.FirstOrDefault(t => string.Equals(t.Title, sticky.Title, StringComparison.OrdinalIgnoreCase))
                : null);
        // No counterpart in this file (yet): leave mpv's own default alone rather than forcing Off,
        // and report failure so a later track-list wave gets another go.
        if (match is null)
            return new(StickySubtitleApplyStatus.NoMatch, null);
        if (match.Selected)
            return new(StickySubtitleApplyStatus.AlreadySelected, match.Id);
        player.SelectSubtitleTrack(match.Id);
        return new(StickySubtitleApplyStatus.SelectionCommandIssued, match.Id);
    }

#if DEBUG
    /// <summary>Injects one late matching external subtitle after a no-subtitle FileLoaded snapshot.
    /// The hook is local-only, generation-scoped, bounded, and never logs the supplied path.</summary>
    private async void ScheduleTestLateSubtitle(SubtitleGenerationState state)
    {
        if (_player is not { } player)
            return;
        var path = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE");
        if (string.IsNullOrWhiteSpace(path))
        {
            player.TestLateSubtitleSkip("path_unset", state.Generation, state.PendingCycleCount);
            return;
        }
        if (!System.IO.File.Exists(path))
        {
            player.TestLateSubtitleSkip("file_missing", state.Generation, state.PendingCycleCount);
            return;
        }
        if (state.TestLateSubtitleScheduled)
        {
            player.TestLateSubtitleSkip("already_scheduled", state.Generation, state.PendingCycleCount);
            return;
        }
        if (state.PendingCycleCount <= 0)
        {
            player.TestLateSubtitleSkip("pending_zero", state.Generation, state.PendingCycleCount);
            return;
        }
        var rawGeneration = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE_GENERATION");
        if (!int.TryParse(rawGeneration, out var targetGeneration))
        {
            player.TestLateSubtitleSkip("generation_invalid", state.Generation, state.PendingCycleCount);
            return;
        }
        if (targetGeneration != state.Generation)
        {
            player.TestLateSubtitleSkip("generation_mismatch", state.Generation, state.PendingCycleCount);
            return;
        }
        const int maxDelayMs = 10_000;
        var rawDelay = Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_LATE_SUBTITLE_DELAY_MS");
        var requestedDelay = int.TryParse(rawDelay, out var parsedDelay) ? parsedDelay : 2_000;
        var delayMs = Math.Clamp(requestedDelay, 1, maxDelayMs);
        state.TestLateSubtitleScheduled = true;
        player.TestLateSubtitleMarker("scheduled", state.Generation, state.PendingCycleCount,
            player.Tracks.Count(t => t.Type == "sub"), delayMs);
        await Task.Delay(delayMs);
        if (_player is not { } livePlayer || livePlayer.LoadGeneration != state.Generation
            || !ReferenceEquals(_subtitleState, state))
        {
            player.TestLateSubtitleSkip("state_stale", state.Generation, state.PendingCycleCount);
            return;
        }
        try
        {
            livePlayer.TestAddLateSubtitle(path, state.Generation, state.PendingCycleCount);
        }
        catch
        {
            livePlayer.TestLateSubtitleSkip("add_exception", state.Generation, state.PendingCycleCount);
        }
    }
#endif

    /// <summary>Plays a local file or non-server URL (no progress reporting).</summary>
    public void PlayUrl(string url, string? authHeader)
    {
        _app.Queue.Clear();
        _app.PublishStopReport(_reporter?.Stop());
        _playingItem = null;
        _playbackSequence++;
        _playbackJf = null;   // no server session owns a local/URL playback
        _playingLocal = false;
        _pendingResumeTicks = 0;
        _externalSubs = null;
        _subtitleState = null;
        _playbackDecision = null;
        _transcodeRetryDone = false;
        _playerViewModel.SetPlayMethod(null);
        _playerViewModel.SetTrickplay(null);
        _playerViewModel.SetUpNext(null);
        _playerViewModel.SetEpisodeNeighbors(false, null, null);
        _playerViewModel.SetMediaInfo(null);
        _playerViewModel.SetNowPlaying(GetDisplayName(url));
        UpdateSystemMediaDisplay(GetDisplayName(url), null, null);
        _playerViewModel.SetServerChapters([]); // fall back to mpv's chapter-list
        _playerViewModel.SetSkipSegments([], _settings.AutoSkipIntro, _settings.AutoSkipCredits);
        PlayCore(url, authHeader);
    }

    /// <summary>OSD name for a local path or URL: file name without extension.</summary>
    private static string GetDisplayName(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var address) && !address.IsFile)
                return StreamAddress.DisplayName(address);
            return System.IO.Path.GetFileNameWithoutExtension(url) is { Length: > 0 } name ? name : url;
        }
        catch
        {
            return url;
        }
    }

    private void PlayCore(string url, string? authHeader)
    {
        _app.EnterPlayback();
        // Discard the outgoing generation state before loadfile clears its published tracks. The
        // new state is created from LoadFile's returned generation before queued mpv events can run.
        // Geometry likewise resets at load initiation; see PlayerViewModel.ResetFileGeometry (B1).
        _subtitleState = null;
        _playerViewModel.ResetFileGeometry();
        // Breadcrumb every load. This is the single most valuable line in a crash file — "what was
        // it playing?" — and it is also what makes a playback-failure file's ring show the retry
        // sequence rather than just the failure. The URL goes through AppLog's redaction.
        // The method label has to match WritePlaybackFailureLog's vocabulary, including the case
        // it kept getting wrong: _playingLocal means "a completed download", so a command-line
        // file or a plain URL is neither local-download nor server direct-play, and calling it
        // "direct" made a local file read as a server stream.
        var source = _playingLocal ? "local_download"
            : _playbackDecision is { IsTranscode: true } ? "transcode"
            : _playingItem is null ? "external"
            : "direct";
        Diagnostics.AppLog.Info("player",
            $"event=load source={source} item={_playingItem?.Id.ToString("N") ?? "none"} type={_playingItem?.Type.ToString() ?? "external"}");
        if (_player is null)
        {
            // VideoArea just became visible; HwndReady will consume the pending load.
            _pendingLoad = (url, authHeader);
            return;
        }
        _player.SetHttpHeaders(authHeader);
        var generation = _player.LoadFile(url);
        _subtitleState = new SubtitleGenerationState(generation, _playbackSequence);
    }

    private void StopPlaybackAndReturn()
    {
        Diagnostics.AppLog.Info("player", FormattableString.Invariant(
            $"event=stop position_seconds={_player?.TimePos ?? 0:F1} item={_playingItem?.Id.ToString("N") ?? "none"} type={_playingItem?.Type.ToString() ?? "external"}"));
        _app.PublishStopReport(_reporter?.Stop());
        _playingItem = null;
        _playbackJf = null;
        _app.Queue.Clear();
        _timelineTimer?.Stop();
        _systemMedia?.ClearDisplay();
        SetThumbButtonsEnabled(false);
        _playerViewModel.SetUpNext(null);
        if (_isFullscreen)
            ToggleFullscreen();
        if (_isMiniPlayer)
            ToggleMiniPlayer();   // browsing happens in the normal window
        _app.LeavePlayback();
    }

    private void SyncOverlayBounds()
    {
        if (_windowClosing || _overlay is null)
            return;

        if (WindowState == WindowState.Minimized || !IsVisible || !VideoArea.IsLoaded
            || VideoArea.Visibility != Visibility.Visible)
        {
            _overlay.Hide();
            return;
        }

        // Just made visible but not yet measured — VideoArea.SizeChanged will re-sync.
        if (VideoArea.ActualWidth < 1 || VideoArea.ActualHeight < 1)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = VideoArea.PointToScreen(new Point(0, 0));
        var left = topLeft.X / dpi.DpiScaleX;
        var top = topLeft.Y / dpi.DpiScaleY;
        // Every write over there is a SetWindowPos on the overlay HWND, and this runs per
        // WM_WINDOWPOSCHANGED (i.e. per step of a drag), so SetBoundsFromOwner skips the writes
        // when nothing moved. It also records the origin it produced, which is how the overlay
        // tells our syncs apart from an external move aimed at it (OverlayWindow.ExternalMoveHook).
        _overlay.SetBoundsFromOwner(left, top, VideoArea.ActualWidth, VideoArea.ActualHeight);
        if (!_overlay.IsVisible)
            _overlay.Show();
        _overlay.EnsureAboveOwner();
    }

    /// <summary>Guards <see cref="MoveWithOverlay"/> against re-entry: the SetWindowPos it makes
    /// synchronously re-runs <see cref="SyncOverlayBounds"/>, which touches the overlay again.</summary>
    private bool _movingOwner;

    /// <summary>
    /// An external <c>SetWindowPos</c> was aimed at the overlay (see
    /// <c>OverlayWindow.ExternalMoveHook</c> for why the shell aims there) and has been vetoed.
    /// Move the OWNER so the whole player follows the gesture instead.
    ///
    /// <para>Normal, mini included: the overlay sits at a fixed inset inside the owner's chrome,
    /// so the owner goes to <c>proposed − inset</c> (both read with <c>GetWindowRect</c>, i.e.
    /// physical pixels, which is also what the proposal is in). The owner's own
    /// <c>WM_WINDOWPOSCHANGED</c> then re-runs <see cref="SyncOverlayBounds"/> and puts the
    /// overlay exactly on the proposed origin — no scaling arithmetic here, and a move across a
    /// DPI boundary is re-synced by the owner's <c>DpiChanged</c>.</para>
    ///
    /// <para>Maximized (plain, or fullscreen, which is <c>None</c> + <c>Maximized</c>): a move
    /// WITHIN the monitor is refused outright — the veto has already kept the OSD on the video,
    /// and dropping a maximized window out of its state because something nudged the overlay is
    /// not what any of these gestures mean. A move to ANOTHER monitor is honoured the way the
    /// shell honours it: restore, place the restored rect at the same offset inside the target
    /// monitor's work area (clamped to it), maximize again. <c>_isFullscreen</c> is untouched,
    /// so fullscreen stays fullscreen on the new monitor.</para>
    /// </summary>
    private void MoveWithOverlay(OverlayExternalMove move)
    {
        // "skipped", not the default "ignore": these are the paths where the owner CANNOT act,
        // and the one log line the overlay writes has to tell them apart from the deliberate
        // same-monitor refusal below, which looks identical from outside.
        if (_overlay is null || _movingOwner)
        {
            move.Action = "skipped";
            return;
        }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var overlayHwnd = new System.Windows.Interop.WindowInteropHelper(_overlay).Handle;
        if (hwnd == nint.Zero || overlayHwnd == nint.Zero)
        {
            move.Action = "skipped";
            return;
        }
        move.OwnerState = _isMiniPlayer ? "Mini"
            : WindowState == WindowState.Maximized ? "Maximized"
            : "Normal";
        _movingOwner = true;
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                var from = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (move.Monitor == from)
                    return;   // same monitor: the veto is the whole answer (action stays "ignore")
                var target = WorkArea(move.Monitor);
                var origin = WorkArea(from);
                WindowState = WindowState.Normal;
                if (GetWindowRect(hwnd, out var restored))
                {
                    var w = restored.Right - restored.Left;
                    var h = restored.Bottom - restored.Top;
                    var x = Math.Clamp(target.Left + (restored.Left - origin.Left),
                        target.Left, Math.Max(target.Left, target.Right - w));
                    var y = Math.Clamp(target.Top + (restored.Top - origin.Top),
                        target.Top, Math.Max(target.Top, target.Bottom - h));
                    SetWindowPos(hwnd, nint.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                WindowState = WindowState.Maximized;
                move.Action = "remaximize";
                return;
            }
            if (!GetWindowRect(hwnd, out var owner) || !GetWindowRect(overlayHwnd, out var overlay))
                return;
            SetWindowPos(hwnd, nint.Zero,
                move.X - (overlay.Left - owner.Left), move.Y - (overlay.Top - owner.Top), 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            move.Action = "translate";
        }
        finally
        {
            _movingOwner = false;
        }
    }

    /// <summary>The work area (screen minus taskbar) of a monitor, in physical pixels; an empty
    /// rect if the monitor cannot be queried, which the clamp then treats as "no room".</summary>
    private static Win32Rect WorkArea(nint monitor)
    {
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info.Work : default;
    }

    /// <summary>Compact always-on-top mode (~480×270, no chrome, drag-to-move via the
    /// video, slim overlay). Mutually exclusive with fullscreen; leaving restores the
    /// exact prior rect and window state.</summary>
    private void ToggleMiniPlayer()
    {
        if (!_isMiniPlayer)
        {
            if (_isFullscreen)
                ToggleFullscreen();
            _preMiniState = WindowState;
            _preMiniRect = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _isMiniPlayer = true;
            WindowStyle = WindowStyle.None;
            // CanResize keeps WS_THICKFRAME so the grip's native HTBOTTOMRIGHT sizing
            // loop works (M12); the WM_SIZING hook enforces 16:9 + clamps.
            ResizeMode = ResizeMode.CanResize;
            // The shell minimums (700x480) would clamp the compact window — lift them
            // for the duration of mini mode and restore them on the way out.
            MinWidth = 0;
            MinHeight = 0;
            WindowState = WindowState.Normal;
            Topmost = true;
            // Land in the bottom-right corner of where the window was, at the last
            // mini size this session (default 480x270).
            var target = new Rect(
                Math.Max(0, _preMiniRect.Right - _miniSize.Width - 24),
                Math.Max(0, _preMiniRect.Bottom - _miniSize.Height - 24),
                _miniSize.Width, _miniSize.Height);
            Left = target.X;
            Top = target.Y;
            Width = target.Width;
            Height = target.Height;
            // Strip WS_MAXIMIZEBOX: Windows' snap/arrange features (drag-snap, size-to-
            // edge vertical maximize, arrange-restore-on-move) all key off it and fight
            // the 16:9 clamp (seen live); HTCAPTION moves and THICKFRAME sizing don't.
            var miniHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(miniHwnd, -16 /* GWL_STYLE */,
                GetWindowLong(miniHwnd, -16) & ~0x00010000 /* WS_MAXIMIZEBOX */);
            _overlay?.SetMiniMode(true);
        }
        else
        {
            _miniSize = new Size(Width, Height);   // remember for the next mini enter
            var exitHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(exitHwnd, -16 /* GWL_STYLE */,
                GetWindowLong(exitHwnd, -16) | 0x00010000 /* WS_MAXIMIZEBOX */);
            _isMiniPlayer = false;
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 700;
            MinHeight = 480;
            Left = _preMiniRect.X;
            Top = _preMiniRect.Y;
            Width = _preMiniRect.Width;
            Height = _preMiniRect.Height;
            WindowState = _preMiniState;
            _overlay?.SetMiniMode(false);
        }
        Dispatcher.BeginInvoke(SyncOverlayBounds, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Moves the chrome-less mini window when the video area is dragged
    /// (the press lands on the overlay, so DragMove can't be used directly).</summary>
    private void StartMiniDrag()
    {
        if (!_isMiniPlayer)
            return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
    }

    /// <summary>Starts a native bottom-right sizing loop when the overlay's mini resize
    /// grip is pressed (M12) — same forwarding trick as StartMiniDrag.</summary>
    private void StartMiniResize()
    {
        if (!_isMiniPlayer)
            return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, 17 /* HTBOTTOMRIGHT */, 0);
    }

    /// <summary>WM_SIZING while in mini mode: lock the drag rect to 16:9 and clamp to
    /// 320x180..960x540 DIP, anchoring the corner opposite the dragged edge. Also strips
    /// the WS_THICKFRAME non-client border (WM_NCCALCSIZE) — CanResize is needed for the
    /// native sizing loop, but its invisible ~10px frame would inset the video/overlay
    /// from the window edge (seen live as a 20px window-vs-overlay mismatch).</summary>
    private nint OnWindowSizing(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // The overlay tracks the video rect, and the managed events alone do not get it there:
        // on a move-ONLY reposition (Win+Shift+arrow to the next monitor, a window-management
        // utility, an external SetWindowPos) the OSD stayed at its old screen position,
        // detached from the video — measured, the inset drifted 11,45 -> 311,245 and never
        // recovered, because the sync at LocationChanged time still computes the pre-move
        // origin and nothing re-runs it afterwards (BUGS.md B11). WM_WINDOWPOSCHANGED arrives
        // AFTER the new position is in effect, so PointToScreen is current here. Deliberately
        // not marked handled — WPF still needs this message for Left/Top/LocationChanged.
        if (msg == 0x0047 /* WM_WINDOWPOSCHANGED */)
            SyncOverlayBounds();
        if (msg == 0x0083 /* WM_NCCALCSIZE */ && wParam != 0 && _isMiniPlayer)
        {
            handled = true;   // client area == whole window: chrome-less stays edge-to-edge
            return 0;
        }
        // Releasing a sizing drag AT the screen edge makes Windows vertically maximize
        // the window ("size to edge" arrangement), bypassing WM_SIZING (seen live:
        // 1440x810 snapped to 2562x2088 on mouse-up). Clamp every size change while
        // mini so no external arrangement can break the 16:9 contract.
        if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */ && _isMiniPlayer)
        {
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPos>(lParam);
            if ((wp.Flags & 0x0001 /* SWP_NOSIZE */) == 0 && wp.Cx > 0 && wp.Cy > 0)
            {
                var d = VisualTreeHelper.GetDpi(this);
                var lo = (int)Math.Round(320 * d.DpiScaleX);
                var hi = (int)Math.Round(960 * d.DpiScaleX);
                var cx = Math.Clamp(wp.Cx, lo, hi);
                var cy = (int)Math.Round(cx * 9.0 / 16.0);
                if (cx != wp.Cx || cy != wp.Cy)
                {
                    wp.Cx = cx;
                    wp.Cy = cy;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            return 0;
        }
        if (msg != 0x0214 /* WM_SIZING */ || !_isMiniPlayer)
            return 0;
        var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32Rect>(lParam);
        var dpi = VisualTreeHelper.GetDpi(this);
        var minW = (int)Math.Round(320 * dpi.DpiScaleX);
        var maxW = (int)Math.Round(960 * dpi.DpiScaleX);
        var w = Math.Clamp(rect.Right - rect.Left, minW, maxW);
        var h = (int)Math.Round(w * 9.0 / 16.0);
        var edge = (int)wParam;
        // WMSZ_: 1 LEFT, 2 RIGHT, 3 TOP, 4 TOPLEFT, 5 TOPRIGHT, 6 BOTTOM, 7 BOTTOMLEFT, 8 BOTTOMRIGHT
        if (edge is 1 or 4 or 7) rect.Left = rect.Right - w; else rect.Right = rect.Left + w;
        if (edge is 3 or 4 or 5) rect.Top = rect.Bottom - h; else rect.Bottom = rect.Top + h;
        System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return 1;   // TRUE — WM_SIZING was processed
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Hwnd, HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Win32Rect Monitor;
        public Win32Rect Work;
        public uint Flags;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int index, int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            if (_isMiniPlayer)
                ToggleMiniPlayer();   // mutual exclusion
            _preFullscreenState = WindowState;
            _isFullscreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _isFullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullscreenState == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }
        // Every route into fullscreen ends here — F11, the video double-click, Escape, and now the
        // OSD button — so this is the one place the button's glyph can be kept honest.
        // Every route into fullscreen ends here — F11, the video double-click, Escape, and the OSD
        // button — so this is the one place the button's glyph can be kept honest. Confirmed by
        // removing it: the button still worked, and both label assertions failed.
        _overlay?.SetFullscreen(_isFullscreen);
        Dispatcher.BeginInvoke(SyncOverlayBounds, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    /// <summary>Shows a transient dismissible message at the top of the shell.</summary>
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        if (_toastTimer is null)
        {
            _toastTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(6),
            };
            _toastTimer.Tick += ToastTimerTick;
        }
        _toastTimer.Start();
    }

    private void ToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer!.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        _toastTimer?.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void OnOpenFile(object sender, RoutedEventArgs e) => OnOpenFileCommand();

    private void OnOpenFileCommand()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media file",
            Filter = "Media files|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.ts;*.m2ts;*.flac;*.mp3;*.m4a|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            PlayUrl(dialog.FileName, null);
    }

    // One cached instance (Phase 9 M6). Settings are global settings.json player config, not
    // per-profile, so sharing it across profile shells is correct — and a cached view keeps the
    // audio-device combos and cache readouts from multiplying on every visit.
    private SettingsView? _settingsView;

    /// <summary>
    /// Opens Settings as an in-app destination (Phase 9 M6) — railless, like Advanced Search, so
    /// the header clears any active-library highlight and back/forward work on it for free.
    ///
    /// Deliberately a **no-op while Playing** (user decision, 2026-07-25): the browse layer is
    /// collapsed during playback and `ApplyState` stops the player when it is shown, so an in-app
    /// page cannot appear over the video. Stopping playback to reach settings would be a nasty
    /// surprise, and keeping a second floating settings surface for that one case would mean two
    /// UIs to maintain. Leave the player first.
    ///
    /// It stays silent rather than hinting: `ShowToast` lives in the browse layer, which the mpv
    /// HWND covers during playback, and the OSD has no general message surface to borrow. Adding
    /// one for this is more than the case is worth.
    /// </summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_app.State == AppState.Playing)
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=open-settings outcome=blocked-playing");
            return;
        }

        _settingsView ??= new SettingsView(_settings, () => _player, _playerViewModel, _updateService);
        if (ReferenceEquals(_nav.Current?.View, _settingsView))
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=open-settings outcome=already-current");
            return;
        }
        Diagnostics.AppLog.Detail("main", "event=interaction action=open-settings outcome=navigate");
        _nav.Navigate(new NavFrame(_settingsView, "Settings", null));
        ShowCurrentFrame();
    }

    /// <summary>Profile switcher (Ctrl+U / File menu). A warm profile activates
    /// instantly with its browse state intact; a cold one validates first. Playback
    /// keeps running under the profile that started it (warm switching) — only the
    /// Add-server path still tears playback down (it lands on the login form).</summary>
    private async void OnSwitchServer(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("main", "event=interaction action=open-profile-switcher");
        var dialog = new Views.SwitchServerWindow(Jellyfin.CredentialStore.LoadProfiles(),
            _app.IsSessionWarm) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=profile-switch outcome=cancel");
            return;
        }
        if (dialog.AddServerRequested)
        {
            if (_app.State == AppState.Playing)
                StopPlaybackAndReturn();
            _app.BeginAddServer();
            Diagnostics.AppLog.Detail("main", "event=interaction action=profile-switch outcome=add-server");
            return;
        }
        if (dialog.SelectedProfile is not { } profile)
            return;
        if (!await _app.SwitchProfileAsync(profile))
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=profile-switch outcome=failure");
            ShowToast($"Could not connect to {profile.ServerName ?? profile.ServerUrl} — the saved session may have expired.");
        }
        else
        {
            Diagnostics.AppLog.Info("main", "event=interaction action=profile-switch outcome=success");
        }
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        // M23: destructive confirm through the in-app modal, not a native MessageBox.
        if (!await Modal.ConfirmAsync("Log out?",
                "This removes the saved session for this profile on this device.",
                "Log out", destructive: true))
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=logout outcome=cancel");
            return;
        }
        if (_app.State == AppState.Playing)
            StopPlaybackAndReturn();
        await _app.LogoutAsync();
        Diagnostics.AppLog.Info("main", "event=interaction action=logout outcome=success");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleStreamShortcut(e);
        HandlePlayerKey(e);
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Keys delivered to the overlay window (Phase 9 M1). WPF focuses whatever OSD control the
    /// user clicked, so the overlay legitimately holds focus much of the time and its keystrokes
    /// never reach this window on their own. Route them through the same player handler this
    /// window uses, then — because <see cref="Window.InputBindings"/> only fire for the window
    /// that owns the key — retry the unhandled ones against our own chords, so Ctrl+M, F11 and
    /// Esc work from the player too. They previously suffered the identical flakiness.
    /// </summary>
    private void HandleOverlayKey(KeyEventArgs e)
    {
        HandlePlayerKey(e);
        if (e.Handled)
            return;

        var mods = Keyboard.Modifiers;
        foreach (var binding in InputBindings)
        {
            if (binding is KeyBinding { Command: { } command } kb
                && kb.Key == e.Key && kb.Modifiers == mods
                && command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// Runs one <see cref="AppShortcuts.Chrome"/> entry. The chords are in the table; the guards
    /// are here, because each reads private window state — which is also why this is a switch on
    /// the id rather than a delegate stored in the table (P10 M9).
    /// </summary>
    private void InvokeChromeShortcut(string id)
    {
        // While the panel is up it owns the keyboard, so Escape (which closes it) is the only
        // chrome chord that acts. One choke point, because every chrome chord passes here.
        if (id != AppShortcuts.Escape && IsShortcutsOpen)
            return;

        Diagnostics.AppLog.Detail("shortcut", $"event=invoke scope=chrome action={id}");

        switch (id)
        {
            case AppShortcuts.OpenFile:
                OnOpenFileCommand();
                break;
            case AppShortcuts.OpenSettings:
                OnOpenSettings(this, null!);
                break;
            case AppShortcuts.SwitchServer:
                OnSwitchServer(this, null!);
                break;
            case AppShortcuts.MiniPlayer:
                if (_app.State == AppState.Playing || _isMiniPlayer) ToggleMiniPlayer();
                break;
            case AppShortcuts.TogglePause:
                if (_app.State == AppState.Playing) _playerViewModel.TogglePauseCommand.Execute(null);
                break;
            case AppShortcuts.Fullscreen:
                if (_app.State == AppState.Playing) ToggleFullscreen();
                break;
            case AppShortcuts.Escape:
                // The panel is the newest meaning of Esc and takes precedence: it is the thing
                // most recently opened, so it is the thing Esc should close.
                if (CloseShortcutsOverlay())
                {
                }
                else if (_isFullscreen)
                {
                    ToggleFullscreen();
                }
                else if (_app.State == AppState.Browse)
                {
                    SearchBox.Clear();
                    PopSearchView();
                }
                break;
            case AppShortcuts.BrowseBack:
                if (_app.State == AppState.Browse) BrowseBack();
                break;
        }
    }

    // ---- Keyboard-shortcuts overlay (P10 M9) ----

    /// <summary>
    /// '?' opens the panel, matched on the typed character rather than on a chord. Two reasons,
    /// both load-bearing: a window <see cref="KeyBinding"/> fires even while the search pill holds
    /// focus and would swallow the character, and '?' is a different virtual key per layout
    /// (Shift+/ on US, Shift+ß on German), so <c>Key.OemQuestion</c> would only work on some
    /// keyboards. Not marking the event handled is deliberate — a '?' typed into a text box must
    /// still reach it.
    /// </summary>
    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "?" || IsTextEntryFocused())
            return;
        ToggleShortcutsOverlay();
        e.Handled = true;
    }

    private bool IsShortcutsOpen => Shortcuts.IsOpen || _overlay?.ShortcutsOpen == true;

    private static bool IsTextEntryFocused()
        => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;

    private void ToggleShortcutsOverlay()
    {
        // Which host: WPF content cannot draw over the mpv child HWND, so the panel exists in
        // both windows and the playing state decides which one shows (TECHNICAL.md, airspace).
        if (_app.State == AppState.Playing && _overlay is { } overlay)
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=toggle-shortcuts host=overlay");
            overlay.ToggleShortcuts(_settings);
        }
        else if (Shortcuts.IsOpen)
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=toggle-shortcuts host=browse outcome=close");
            Shortcuts.Hide();
        }
        else
        {
            Diagnostics.AppLog.Detail("main", "event=interaction action=toggle-shortcuts host=browse outcome=open");
            Shortcuts.Show(_settings);
        }
    }

    /// <summary>Closes whichever host currently shows the panel. Returns whether it closed
    /// one — Esc has three other meanings and must only consume the keystroke if it did.</summary>
    private bool CloseShortcutsOverlay()
    {
        if (_overlay?.ShortcutsOpen == true)
        {
            _overlay.HideShortcuts();
            return true;
        }
        if (Shortcuts.IsOpen)
        {
            Shortcuts.Hide();
            return true;
        }
        return false;
    }

    /// <summary>
    /// The single player-shortcut handler, shared by this window's <see cref="OnKeyDown"/> and
    /// the overlay's <see cref="Views.OverlayWindow.KeyPressed"/> forward (Phase 9 M1) — so a
    /// keystroke reaches the same code no matter which of the two windows Windows delivered it
    /// to. Only acts while playing: browse views may have text inputs, the Playing state has
    /// none. One lookup against the user's binding map (M16); the manual lookup also frees
    /// plain alphanumerics from the KeyGesture restriction. App-chrome chords (Ctrl+M
    /// mini-player, F11, …) stay with their InputBindings and win via e.Handled before this
    /// runs — and the dispatcher requires an exact chord match, so it can't steal them.
    /// </summary>
    private void HandlePlayerKey(KeyEventArgs e)
    {
        if (_app.State != AppState.Playing || e.Handled || IsShortcutsOpen)
            return;

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && _playerViewModel.UpNextVisible)
        {
            if (!e.IsRepeat)
            {
                Diagnostics.AppLog.Detail("shortcut", "event=invoke scope=player action=PlayUpNext");
                _playerViewModel.PlayUpNextNow();
            }
            e.Handled = true;
        }
        else if (Player.PlayerActionDispatcher.TryResolve(e.Key, Keyboard.Modifiers,
                     _settings.KeyBindings, out var action))
        {
            // Key auto-repeat may only drive the continuous actions (B9). Held keys still count
            // as handled — the action is bound, just suppressed for this repeat — so the chord
            // can't fall through to an InputBinding and do something else instead.
            if (!e.IsRepeat || Player.PlayerActionDispatcher.RepeatsWhileHeld(action))
            {
                Diagnostics.AppLog.Detail("shortcut",
                    $"event=invoke scope=player action={action} repeat={e.IsRepeat}");
                if (action != Player.PlayerAction.CycleSubtitle
                    || !TryQueueSubtitleCycleBeforeFileLoaded())
                {
                    Player.PlayerActionDispatcher.Dispatch(action, _playerViewModel);
                }
            }
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        App.StartupLog($"MainWindow.OnSourceInitialized, Title='{Title}', "
            + $"Application.MainWindow is {(Application.Current?.MainWindow is null ? "null" : "set")}");
        base.OnSourceInitialized(e);
        Settings.WindowStateStore.Restore(this);
        InitializeSystemMediaControls();
        // Mini-player 16:9 sizing hook (M12) — active only while _isMiniPlayer.
        if (System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
            source.AddHook(OnWindowSizing);
    }

    // ---- System media controls (SMTC + taskbar thumbnail buttons, Phase 5 M6) ----

    private void InitializeSystemMediaControls()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _systemMedia = SystemMediaControls.TryCreate(hwnd, Dispatcher);
        if (_systemMedia is null)
            return;
        _systemMedia.PlayPressed += () => { if (_player is { } p) p.Pause = false; };
        _systemMedia.PausePressed += () => { if (_player is { } p) p.Pause = true; };
        _systemMedia.StopPressed += () =>
        {
            if (_app.State == AppState.Playing)
                StopPlaybackAndReturn();
        };
        // M17: the media keys follow the transport mapping; the OS buttons stay lit
        // whenever the chosen action is meaningful (always, for Chapter/Seek).
        _systemMedia.NextPressed += () => _playerViewModel.TransportNext();
        _systemMedia.PreviousPressed += () => _playerViewModel.TransportPrev();
        _app.Queue.Changed += UpdateSystemTransportEnabled;
        // Mapping changes (Settings) surface as CanSkip* notifications — follow them.
        _playerViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PlayerViewModel.CanSkipNext) or nameof(PlayerViewModel.CanSkipPrevious))
                UpdateSystemTransportEnabled();
        };
    }

    private void UpdateSystemTransportEnabled()
        // CanSkipNext/CanSkipPrevious, not a second derivation from the queue: the media keys
        // run TransportNext/TransportPrev, whose queue-step case falls back to the armed Up
        // Next episode. Re-deriving here ignored _upNextItem, so a single-episode playback
        // with an armed up-next left the OSD Next enabled and the SMTC/media-key Next dead.
        => _systemMedia?.SetNextPreviousEnabled(
            _playerViewModel.CanSkipNext, _playerViewModel.CanSkipPrevious);

    /// <summary>Pushes the now-playing card to the OS (SMTC) per playback start.</summary>
    private void UpdateSystemMediaDisplay(string title, string? subtitle, string? thumbUrl)
    {
        _ = _systemMedia?.UpdateDisplayAsync(title, subtitle, thumbUrl);
        _systemMedia?.SetPlaybackState(playing: true);
        SetThumbButtonsEnabled(true);
        if (_timelineTimer is null)
        {
            // Coarse OS timeline, same cadence as the progress reporter.
            _timelineTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _timelineTimer.Tick += (_, _) =>
            {
                if (_player is { } p)
                    _systemMedia?.UpdateTimeline(p.TimePos, p.Duration);
            };
        }
        _timelineTimer.Start();
    }

    private void SetThumbButtonsEnabled(bool enabled)
    {
        ThumbJumpBack.IsEnabled = enabled;
        ThumbPlayPause.IsEnabled = enabled;
        ThumbJumpForward.IsEnabled = enabled;
    }

    private void OnThumbJumpBack(object sender, EventArgs e) => _playerViewModel.JumpBack();

    private void OnThumbPlayPause(object sender, EventArgs e)
        => _playerViewModel.TogglePauseCommand.Execute(null);

    private void OnThumbJumpForward(object sender, EventArgs e) => _playerViewModel.JumpForward();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _windowClosing = true;
        // Mini mode borrows a fake rect — restore the real one before saving.
        if (_isMiniPlayer)
            ToggleMiniPlayer();
        // Fullscreen borrows Maximized — save what the user would return to instead.
        Settings.WindowStateStore.Save(this, _isFullscreen ? _preFullscreenState : WindowState);
        // Send the stop report HERE and give it a bounded moment to land (B22). It used to be
        // fire-and-forget in OnClosed, racing process teardown and normally losing: the 5 s
        // progress reports cap the resume-position loss at ~5 s, but the SESSION never ended, so
        // the server kept a phantom "now playing" entry and — for an HLS playback — a live
        // transcode job until its own timeout reaped it. PlaybackReporter.Stop() has always
        // returned an awaitable for exactly this.
        //
        // Not async: OnClosing must decide synchronously whether the close proceeds, and an async
        // void override would let the window close underneath the await. A bounded Wait on the
        // dispatcher thread is the honest trade — 2 s worst case on exit, and only when a playback
        // was actually running. Stop() swallows its own failures, so this cannot throw.
        if (_reporter?.Stop() is { } stopReport)
            stopReport.Wait(TimeSpan.FromSeconds(2));
        // Session ending means Windows is logging off or shutting down; do not start a silent
        // installer into a disappearing desktop.  The normal user Exit/X path gets the update
        // only after reporter and mpv shutdown have completed in OnClosed.
        _launchUpdateOnClosed = !_systemSessionEnding && _updateService.ShouldLaunchInstallOnNormalExit;
        // The suppression itself is the interesting part: "the update did not install" and "Windows
        // was shutting down, so the handoff was skipped on purpose" look identical from the outside.
        Diagnostics.AppLog.Detail("updates", "event=exit_handoff " + (_launchUpdateOnClosed
            ? "outcome=applied"
            : _systemSessionEnding ? "outcome=noop reason=session_ending" : "outcome=noop reason=policy"));
        base.OnClosing(e);
        if (e.Cancel)
        {
            _windowClosing = false;
            SyncOverlayBounds();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowClosing = true;
        _overlay = null;
        Jellyfin.BrowsePrefetcher.CancelAll();
        _player?.Dispose();
        _player = null;
        Application.Current.SessionEnding -= OnSessionEnding;
        if (_launchUpdateOnClosed)
            _updateService.TryLaunchInstallAfterExit();
        _updateService.Dispose();
        base.OnClosed(e);
    }
}
