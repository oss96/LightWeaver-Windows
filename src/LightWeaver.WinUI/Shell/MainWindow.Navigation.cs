using LightWeaver.Details;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Navigation;
using LightWeaver.Search;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Pages;
using LightWeaver.WinUI.Search;
using LightWeaver.WinUI.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LightWeaver.WinUI.Shell;

/// <summary>Navigation, the Esc chain and the offline swap. Pages ask through <see cref="IAppShell"/>.</summary>
public sealed partial class MainWindow : IAppShell
{
    private readonly Navigator _nav = new();
    private Connectivity _connectivity = null!;
    private AdvancedSearchPage? _search;

    private void InitializeNavigation()
    {
        _connectivity = new Connectivity(DispatcherQueue);
        _connectivity.Changed += (_, _) =>
        {
            if (!_connectivity.IsOffline && _restoreWhenOnline) _ = RestoreSavedSessionAsync();
            else ShowCurrent();
        };
        var retry = new Microsoft.UI.Xaml.Controls.Button();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(retry, "CachedRetryNow");
        retry.Click += async (_, _) => await _connectivity.RetryAsync();
        CachedBanner.ActionButton = retry;
        L.Source.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ShowCachedBanner(_cachedBannerAt));
        ToastHost.Current = Toasts;
        _nav.Navigated += (_, _) => ShowCurrent();
        _nav.Shown += OnNavShown;

        Bar.BackRequested += (_, _) => _nav.Back();
        Bar.ForwardRequested += (_, _) => _nav.Forward();
        Bar.HomeRequested += (_, _) => GoHome();
        Bar.DownloadsRequested += (_, _) => OpenDownloads();
        Bar.SettingsRequested += (_, _) => OpenSettings();
        Bar.AdvancedToggled += OnAdvancedToggled;
        Bar.SearchTextChanged += (_, text) => OnSearchText(text);
        Bar.SearchSubmitted += (_, text) => OnSearchSubmitted(text);
        Bar.RecentProfile = () => Model.ActiveSessionKey ?? "";

        PageHost.LostFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, PageHost)) PageHost.IsTabStop = false; };
        Root.KeyDown += OnRootKeyDown;
        Root.PointerPressed += OnRootPointerPressed;
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), handledEventsToo: true);
        Bar.MorphRequested += OnMorphRequested;
        Bar.MorphItemInvoked += OnMorphItemInvoked;
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _nav.Back());
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _nav.Forward());
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, Bar.FocusSearch);
        AddAccelerator((VirtualKey)0xBC, VirtualKeyModifiers.Control, () => { if (_nav.Current?.Key != "login") OpenSettings(); });
        InitializeSessions();
    }

    /// <summary>Called once the saved session has been restored (or not).</summary>
    public async void OnSessionReady()
    {
        FollowSessions();
        ResumeInterruptedDownloads();
        if (Model.State == AppState.Login && CredentialStore.Load() is { } saved && !await _connectivity.ProbeAsync(saved.ServerUrl))
        {
            _sessionServer = new Login.ServerInfo(saved.ServerUrl, saved.ServerName is { Length: > 0 } name ? name : Login.ServerAddress.Display(saved.ServerUrl));
            _sessionUser = saved.Username;
            _offlineProfileKey = AppViewModel.SessionKey(saved);
            _restoreWhenOnline = true;
            _connectivity.Report(saved.ServerUrl, new HttpRequestException(_connectivity.Reason), confirmed: true);
            _nav.Reset("home", () => new HomePage(this));
            return;
        }
        if (Model.State == AppState.Login)
        {
            ShowLogin();
            return;
        }
        _connectivity.Track(Model.Jellyfin.ServerUrl);
        var page = Environment.GetCommandLineArgs().SkipWhile(a => a != "--page").Skip(1).FirstOrDefault();
        _nav.Reset("home", () => new HomePage(this));
        if (page == "advanced-search") OpenAdvancedSearch();
        PlayStartupFile();
    }

    public void GoHome() => _nav.Go("home", () => new HomePage(this), reuse: true);

    public void ReturnHome() { if (!_nav.BackTo("home")) GoHome(); }

    public void OpenItem(MediaItem item)
    {
        if (item.Type == global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Playlist) OpenPlaylist(item);
        else _nav.Go("item:" + item.Id.ToString("N"), () => new DetailPage(this, item));
    }

    public void OpenCast(MediaItem item) => _nav.Go("cast:" + item.Id.ToString("N"), () => new CastPage(this, item));

    public void OpenPerson(Guid personId, string name) => _nav.Go("person:" + personId.ToString("N"), () => new PersonPage(this, personId, name));

    public void OpenLibrary(MediaItem library) => _nav.Go("library:" + library.Id.ToString("N"), () => new LibraryPage(this, library), reuse: true);

    public void OpenSection(HomeSection section) => _nav.Go("section:" + section, () => new SectionPage(this, section));

    public void OpenPlaylists() => _nav.Go("playlists", () => new PlaylistsPage(this));

    public void OpenPlaylist(MediaItem playlist) => _nav.Go("playlist:" + playlist.Id.ToString("N"), () => new PlaylistPage(this, playlist));

    public void OpenDownloads() => _nav.Go("downloads", () => new DownloadsPage(this));

    public void OpenSettings() => _nav.Go("settings", () => new SettingsPage(this));

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public IReadOnlyList<LightWeaver.Player.MpvAudioDevice>? AudioDevices() => _playback?.Player?.GetAudioDevices();


    public int PlaybackIntent => _playback?.Intent ?? 0;

    public void Quit() => Close();

    public bool PlayerCoversPage => _playerMode == PlayerMode.Full;

    public void ReportFailure(Exception ex)
    {
        if (JellyfinService.IsNotFound(ex))
        {
            ToastHost.Show(ToastKind.Info, L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7"), mergeKey: "item-missing");
            return;
        }
        _connectivity.Report(Model.Jellyfin.ServerUrl ?? CredentialStore.Load()?.ServerUrl, ex);
    }

    /// <summary>Shows Advanced Search with its panel open. The page (and its filters) lives for the
    /// session; opening from a person's page shows that person and nothing else.</summary>
    public void OpenAdvancedSearch(AdvancedSearchFilters? seed = null) => ShowAdvancedSearch(seed, null);

    public void OpenAdvancedSearch(AdvancedSearchFilters seed, string term) => ShowAdvancedSearch(seed, term);

    /// <summary>A seed from a page (a person, More like this) starts from those filters with no
    /// text; More filters keeps the typed query; without a seed the page is as it was left.</summary>
    private void ShowAdvancedSearch(AdvancedSearchFilters? seed, string? term)
    {
        var text = term ?? (seed is null ? Bar.SearchText : "");
        if (_search is null)
        {
            _search = new AdvancedSearchPage(this, seed) { Term = text };
            _search.PanelOpenChanged += (_, _) => Bar.IsAdvancedOpen = _search.IsPanelOpen && ReferenceEquals(_nav.CurrentPage, _search);
            _search.FocusReturnRequested += (_, _) => Bar.FocusTune();
        }
        else if (seed is not null)
        {
            _search.ShowFilters(seed, text);
        }
        else
        {
            _search.Term = text;
        }
        var page = _search;
        _nav.Go("advanced-search", () => page);
        if (Bar.SearchText != text) Bar.SearchText = text;
        _search.SetPanelOpen(true);
        Bar.IsAdvancedOpen = true;
    }

    private void OnAdvancedToggled(object? sender, EventArgs e)
    {
        if (_search is not null && ReferenceEquals(_nav.CurrentPage, _search)) _search.SetPanelOpen(!_search.IsPanelOpen);
        else if (_nav.CurrentPage is SearchPage quick) OpenAdvancedSearch(QuickSearch.MoreFilters(quick.Category), Bar.SearchText.Trim());
        else OpenAdvancedSearch();
    }

    /// <summary>Typing searches live (design 04): the results replace the page, and clearing the
    /// field returns to the page as it was. On Advanced Search the text is its query.</summary>
    private void OnSearchText(string text)
    {
        if (_search is not null && ReferenceEquals(_nav.CurrentPage, _search))
        {
            _search.Term = text;
            return;
        }
        if (_nav.CurrentPage is SearchPage quick)
        {
            if (text.Trim().Length == 0) _nav.Back();
            else quick.Term = text;
            return;
        }
        if (text.Trim().Length == 0 || _nav.Current?.Key == "login") return;
        var page = new SearchPage(this) { Term = text };
        _nav.Go("search", () => page, refresh: true);
    }

    private void OnSearchSubmitted(string text)
    {
        if (_search is not null && ReferenceEquals(_nav.CurrentPage, _search)) return;
        if (_nav.CurrentPage is SearchPage quick) quick.OpenFirst();
        else OnSearchText(text);
    }

    /// <summary>Focus on the page about to leave would fall to the bar's first button, Back, and open
    /// its tooltip. It moves to the page host instead, which draws no ring and has no tooltip, so
    /// the new page can take it and the next Tab enters the page. A bar button the user pressed
    /// keeps it.</summary>
    private void HoldFocusForPage()
    {
        if (ShownPage is not DependencyObject leaving || Root.XamlRoot is not { } root
            || FocusManager.GetFocusedElement(root) is not DependencyObject focused || !IsInside(focused, leaving)) return;
        PageHost.IsTabStop = true;
        if (!PageHost.Focus(FocusState.Programmatic)) PageHost.IsTabStop = false;
    }

    /// <summary>The page on screen: Home in its own host, or the page in <c>PageHost</c>.</summary>
    private object? ShownPage => HomeHost.Visibility == Visibility.Visible ? HomeHost.Content : PageHost.Content;

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    public string ServerName => _sessionServer?.Name ?? Login.ServerAddress.Display(Model.Jellyfin.ServerUrl ?? "");

    private string? _offlineProfileKey;

    public string CacheProfileKey => Model.ActiveSessionKey ?? _offlineProfileKey ?? "";

    private void ShowCurrent()
    {
        Bar.CanGoBack = _nav.CanGoBack;
        Bar.CanGoForward = _nav.CanGoForward;
        var key = _nav.Current?.Key ?? "";
        Bar.SelectedDestination = key switch
        {
            "home" => TopBarDestination.Home,
            "downloads" => TopBarDestination.Downloads,
            "settings" => TopBarDestination.Settings,
            "playlists" => TopBarDestination.Playlists,
            _ when key.StartsWith("playlist:", StringComparison.Ordinal) => TopBarDestination.Playlists,
            _ when key.StartsWith("library:", StringComparison.Ordinal) => TopBarDestination.Libraries,
            _ => TopBarDestination.None,
        };
        Bar.ShowChoice(_nav.CurrentPage is LibraryPage library ? LibraryChoice(library.Library) : null,
            _nav.CurrentPage is PlaylistPage playlist ? PlaylistChoice(playlist.Playlist) : null);
        Bar.IsAdvancedOpen = _search is not null && ReferenceEquals(_nav.CurrentPage, _search) && _search.IsPanelOpen;
        var login = key == "login";
        Bar.IsMinimal = login;
        Layer.Background = login ? null : Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwLayerBrush");
        Layer.BorderThickness = new Thickness(0, login ? 0 : 1, 0, 0);

        var shown = _nav.CurrentPage as INavPage;
        var plan = OfflinePlan.For(_connectivity.IsOffline, key, shown?.CachedAtUtc);
        ShowCachedBanner(plan == OfflineShow.CachedPage ? shown?.CachedAtUtc : null);
        if (plan == OfflineShow.Unreachable)
        {
            var offline = new ServerUnreachableView(this, _connectivity, CredentialStore.Load()?.ServerName, Model.Jellyfin.ServerUrl ?? CredentialStore.Load()?.ServerUrl);
            offline.OpenDownloadsRequested += (_, _) => OpenDownloads();
            offline.SwitchServerRequested += (_, _) => Bar.OpenProfileMenu();
            Show(offline);
            _wasOffline = true;
            shown?.LoadCached();
            return;
        }
        if (_nav.CurrentPage is { } page)
        {
            Show(page);
            if (_wasOffline && plan == OfflineShow.Page) shown?.Reload();
        }
        _wasOffline = _connectivity.IsOffline;
    }

    public void CachedDataShown(INavPage page)
    {
        if (!_connectivity.IsOffline || !ReferenceEquals(page, _nav.CurrentPage)) return;
        if (!ReferenceEquals(ShownPage, page)) ShowCurrent();
        else if (OfflinePlan.For(true, _nav.Current?.Key ?? "", page.CachedAtUtc) == OfflineShow.CachedPage) ShowCachedBanner(page.CachedAtUtc);
    }

    private DateTime? _cachedBannerAt;

    /// <summary>The offline banner over a page that shows cached data (design 08c): when that data
    /// came from the server, and Retry now; closed for any other page.</summary>
    private void ShowCachedBanner(DateTime? cachedAtUtc)
    {
        _cachedBannerAt = cachedAtUtc;
        CachedBanner.IsOpen = cachedAtUtc is not null;
        if (cachedAtUtc is not { } at) return;
        CachedBanner.Message = L.Format("LibraryView.OfflineShowingCachedResultsFrom.fc87ef69", at.ToLocalTime());
        if (CachedBanner.ActionButton is Microsoft.UI.Xaml.Controls.Button retry) retry.Content = L.Text("Downloads.RetryNow");
    }

    /// <summary>Times a page switch for the log: <c>sync_ms</c> is the synchronous part (building
    /// or re-attaching the page, its OnShown), <c>elapsed_ms</c> runs until the first frame after
    /// the page loaded, and <c>walk_ms</c> is the FastUi pass inside it. Only the kind of place is
    /// logged (item, library, …), never an id.</summary>
    private void OnNavShown(object? sender, NavShownEventArgs e)
    {
        var syncMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(e.Started).TotalMilliseconds;
        var kind = e.Key.Split(':')[0];
        var page = _nav.CurrentPage;
        if (page is null || !ReferenceEquals(ShownPage, page))
        {
            Diagnostics.AppLog.Info("nav", FormattableString.Invariant($"event=nav_shown key={kind} mode={e.Mode} created={(e.Created ? "true" : "false")} outcome=not_shown sync_ms={syncMs}"));
            return;
        }
        var frames = Services.FrameLog.Start("nav_shown:" + kind, e.Started);
        if (page.IsLoaded) WaitForFrame();
        else page.Loaded += OnLoaded;

        void OnLoaded(object s, RoutedEventArgs a)
        {
            page.Loaded -= OnLoaded;
            WaitForFrame();
        }

        void WaitForFrame() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;

        void OnFrame(object? s, object a)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
            frames?.StopAfter(NavSettle);
            if (!ReferenceEquals(_nav.CurrentPage, page)) return;
            if (page is DetailPage detail) Services.FrameLog.Start("detail_settle", layout: () => detail.Geometry)?.StopAfter(DetailSettle);
            var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(e.Started).TotalMilliseconds;
            Diagnostics.AppLog.Info("nav", FormattableString.Invariant($"event=nav_shown key={kind} mode={e.Mode} created={(e.Created ? "true" : "false")} outcome=success sync_ms={syncMs} walk_ms={_lastWalkMs} elapsed_ms={elapsedMs}"));
        }
    }

    /// <summary>How long the frame log keeps timing a page switch after its first frame, while rows fill in.</summary>
    private static readonly TimeSpan NavSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the frame log watches a detail page's layout after its first frame, while its
    /// lookups answer: the <c>detail_settle</c> line's <c>layout_changes</c>.</summary>
    private static readonly TimeSpan DetailSettle = TimeSpan.FromSeconds(2);

    private bool _wasOffline;
    private bool _restoreWhenOnline;
    private int _restoreGeneration;
    private bool _signInAfterPlayback;

    /// <summary>The model enters Login so a later sign-in raises the switch to the new session;
    /// offline playback finishes first.</summary>
    private void RequireSignIn()
    {
        if (Model.State == AppState.Playing) _signInAfterPlayback = true;
        else if (Model.State == AppState.Login) ShowLogin();
        else Model.RequireSignIn();
    }

    /// <summary>Stops a pending or running restore of the saved session: a sign-in or another
    /// profile has taken over.</summary>
    private void CancelSessionRestore()
    {
        _restoreWhenOnline = false;
        _offlineProfileKey = null;
        _restoreGeneration++;
    }

    /// <summary>The saved session that could not be checked at startup, signed in once its server
    /// answers (playback keeps playing). A refused token shows Login; a server that went away again
    /// waits for the next recovery. A sign-in or profile switch meanwhile wins.</summary>
    private async Task RestoreSavedSessionAsync()
    {
        _restoreWhenOnline = false;
        var generation = _restoreGeneration;
        var serverUrl = CredentialStore.Load()?.ServerUrl;
        try
        {
            var restored = await Model.RestoreSavedAsync(() => generation == _restoreGeneration);
            if (generation != _restoreGeneration) return;
            if (restored != false) { ShowCurrent(); return; }
            if (await _connectivity.ProbeAsync(serverUrl))
            {
                if (generation == _restoreGeneration) RequireSignIn();
                return;
            }
            if (generation != _restoreGeneration) return;
            _restoreWhenOnline = true;
            _connectivity.Report(serverUrl, new HttpRequestException(_connectivity.Reason), confirmed: true);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("startup", $"event=session_restore outcome=failure error={ex.GetType().Name}");
        }
    }
    private List<MediaItem>? _libraries;
    private List<MediaItem>? _playlists;

    /// <summary>Libraries and Playlists morph the nav group (design 01). The lists load once per
    /// session; Playlists reloads each time, since playlists change from this app.</summary>
    private async void OnMorphRequested(object? sender, MorphKind kind)
    {
        try
        {
            var profileKey = CacheProfileKey;
            var removals = ItemRemovals.ForProfile(profileKey);
            var readAt = removals.Mark();
            if (kind == MorphKind.Libraries)
            {
                var choices = _libraries;
                if (choices is null)
                {
                    if (_connectivity.IsOffline || !Model.Jellyfin.IsConnected)
                    {
                        var cached = await JellyfinService.ReadCachedLibrariesAsync(profileKey) ?? [];
                        if (profileKey != CacheProfileKey) return;
                        choices = removals.Filter(cached, readAt);
                    }
                    else
                    {
                        try
                        {
                            var libraries = await Model.Jellyfin.GetLibrariesAsync();
                            if (profileKey != CacheProfileKey) return;
                            choices = removals.Filter(libraries, readAt);
                        }
                        catch (Exception ex) when (Connectivity.IsUnreachable(ex))
                        {
                            ReportFailure(ex);
                            var cached = await JellyfinService.ReadCachedLibrariesAsync(profileKey) ?? [];
                            if (profileKey != CacheProfileKey) return;
                            choices = removals.Filter(cached, readAt);
                        }
                    }
                }
                choices = removals.Filter(choices, readAt);
                _libraries = removals.Mark() == readAt ? choices : null;
                var current = _nav.Current?.Key is { } key && key.StartsWith("library:", StringComparison.Ordinal) ? key[8..] : null;
                Bar.OpenMorph(kind, [.. choices.Select(LibraryChoice)], current);
            }
            else
            {
                var playlists = await Model.Jellyfin.GetPlaylistsAsync();
                if (profileKey != CacheProfileKey) return;
                var choices = removals.Filter(playlists, readAt);
                _playlists = removals.Mark() == readAt ? choices : null;
                var current = _nav.Current?.Key is { } key ? key == "playlists" ? "all" : key.StartsWith("playlist:", StringComparison.Ordinal) ? key[9..] : null : null;
                Bar.OpenMorph(kind, [new MorphItem("all", L.Text("TopBar.AllPlaylists"), Theme.Icons.PlaylistPlay), .. Playlists.PlaylistModel.Sorted(choices, PlaylistsPage.Order).Select(PlaylistChoice)], current);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OnMorphItemInvoked(object? sender, string key)
    {
        if (key == "all") { OpenPlaylists(); return; }
        if (_libraries?.FirstOrDefault(l => l.Id.ToString("N") == key) is { } library) OpenLibrary(library);
        else if (_playlists?.FirstOrDefault(p => p.Id.ToString("N") == key) is { } playlist) OpenPlaylist(playlist);
    }

    private static MorphItem LibraryChoice(MediaItem library) => new(library.Id.ToString("N"), library.LibraryDisplayName, LibraryGlyph(library));

    private static MorphItem PlaylistChoice(MediaItem playlist) => new(playlist.Id.ToString("N"), playlist.Name, Theme.Icons.List);

    private static string LibraryGlyph(MediaItem library) => library.CollectionType switch
    {
        "movies" => Theme.Icons.Movie,
        "tvshows" => Theme.Icons.Tv,
        "music" => Theme.Icons.MusicNote,
        "homevideos" => Theme.Icons.Videocam,
        "photos" => Theme.Icons.Photo,
        "playlists" => Theme.Icons.PlaylistPlay,
        _ => Theme.Icons.Folder,
    };

    /// <summary>A click anywhere outside the morphed group contracts it.</summary>
    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Bar.IsMorphOpen && !Bar.IsInNavGroup(e.GetCurrentPoint(null).Position)) Bar.CloseMorph(returnFocus: false);
    }

    // Esc closes the innermost layer; flyouts and menus handle it themselves first.
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            if (!IsTextInputFocused() && HandlePlayerKey(e)) e.Handled = true;
            return;
        }
        if (HandlePlayerEscape()) { e.Handled = true; return; }
        if (Bar.HandleEscape() || (_nav.CurrentPage as INavPage)?.HandleEscape() == true) e.Handled = true;
        else if (_nav.CurrentPage is SearchPage) { Bar.SearchText = ""; e.Handled = true; }
        else if (Bar.IsSearchFocused) { Bar.BlurSearch(); e.Handled = true; }
    }

    private bool IsTextInputFocused()
        => FocusManager.GetFocusedElement(Root.XamlRoot) is Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.RichEditBox or Microsoft.UI.Xaml.Controls.AutoSuggestBox;

    // The mouse's back and forward buttons.
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Root).Properties;
        if (props.IsXButton1Pressed) { _nav.Back(); e.Handled = true; }
        else if (props.IsXButton2Pressed) { _nav.Forward(); e.Handled = true; }
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => { action(); args.Handled = true; };
        Root.KeyboardAccelerators.Add(accelerator);
    }
}
