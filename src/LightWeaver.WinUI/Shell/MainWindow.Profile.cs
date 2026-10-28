using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Login;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LightWeaver.WinUI.Shell;

/// <summary>Sessions and the profile menu (design 11): switch user, switch server, play a link, open
/// a file, settings and log out, plus where Login opens after each of them.</summary>
public sealed partial class MainWindow
{
    private readonly List<SavedCredentials> _forgetOnExit = [];
    private LoginStart? _pendingLogin;
    private bool _sessionChanged;
    private ServerInfo? _sessionServer;
    private string? _sessionUser;

    private void InitializeSessions()
    {
        InitializeDownloads();
        InitializeSyncPlay();
        Bar.ProfileRequested += (_, _) => OpenProfileMenu();
        Closed += (_, _) =>
        {
            _playback?.Dispose();
            if (_forgetOnExit.Count > 0) CredentialStore.RemoveWhere(p => _forgetOnExit.Any(f => SameProfile(f, p)));
        };
    }

    /// <summary>Starts following session changes; called once the saved session has been restored.</summary>
    private void FollowSessions()
    {
        Model.SessionActivated += _ => { _sessionChanged = true; CancelSessionRestore(); _connectivity.Track(Model.Jellyfin.ServerUrl); RememberSession(); ResumeInterruptedDownloads(); };
        Model.SessionExpired += name =>
        {
            ToastHost.Show(ToastKind.Error, L.Format("Profile.SessionExpired", name));
            _pendingLogin = new LoginStart(_sessionServer, _sessionUser);
        };
        Model.StateChanged += state =>
        {
            if (state == AppState.Browse && _signInAfterPlayback)
            {
                _signInAfterPlayback = false;
                if (!Model.Jellyfin.IsConnected) { Model.RequireSignIn(); return; }
            }
            if (state == AppState.Login) ShowLogin();
            else if (state == AppState.Browse && (_sessionChanged || _nav.Current?.Key == "login")) ShowNewSession();
        };
        RememberSession();
    }

    private void RememberSession()
    {
        var jellyfin = Model.Jellyfin;
        if (!jellyfin.IsConnected || jellyfin.ServerUrl is not { } url) return;
        var saved = CredentialStore.Load();
        var name = saved is not null && SameServer(saved.ServerUrl, url) && !string.IsNullOrWhiteSpace(saved.ServerName) ? saved.ServerName : ServerAddress.Display(url);
        _sessionServer = new ServerInfo(url, name);
        _sessionUser = jellyfin.UserName;
        Bar.SetUser(jellyfin.UserName);
    }

    public void ForgetOnExit(SavedCredentials profile) => _forgetOnExit.Add(profile);

    private void ShowLogin()
    {
        CancelSessionRestore();
        var start = _pendingLogin ?? new LoginStart();
        _pendingLogin = null;
        _search = null;
        _nav.Reset("login", () => new LoginPage(this, start));
    }

    private void ShowNewSession()
    {
        _sessionChanged = false;
        _search = null;
        _libraries = null;
        _playlists = null;
        Bar.SearchText = "";
        _nav.Reset("home", () => new HomePage(this));
        _ = RefreshPlaylistsAsync();
    }

    /// <summary>Opens Login from a signed-in session (Add server…, Other user…, Quick Connect…).
    /// The handshake runs on a scratch session; Cancel returns to the current profile.</summary>
    private void OpenLogin(LoginStart start)
    {
        _pendingLogin = start with { CanCancel = CredentialStore.Load() is not null };
        if (Model.State == AppState.Login) ShowLogin();
        else Model.BeginAddServer();
    }

    /// <summary>Signs out of the current profile. Ignored while a profile switch is still running,
    /// so the switch cannot land on top of the login form.</summary>
    public void LogOut()
    {
        if (!Model.TryBeginAccountOperation()) return;
        try
        {
            _pendingLogin = new LoginStart(_sessionServer, _sessionUser);
            Model.SignOut();
        }
        finally { Model.EndAccountOperation(); }
    }

    /// <summary>Runs a profile switch or sign-in under the account gate the WPF shell uses: while
    /// one runs, another, or a sign-out, is ignored rather than acting on a profile about to change.</summary>
    private async Task RunAccountOperationAsync(Func<Task> operation)
    {
        if (!Model.TryBeginAccountOperation()) return;
        try { await operation(); }
        finally { Model.EndAccountOperation(); }
    }

    public void OpenProfileMenu()
    {
        if (_sessionServer is not { } server) return;
        var store = CredentialStore.LoadProfiles();
        var servers = SavedServers.From(store);
        var menu = FastUi.Flyout(new MenuFlyout { MenuFlyoutPresenterStyle = Ui.Resource<Style>("LwProfileMenuPresenter") });
        AutomationProperties.SetAutomationId(menu, "ProfileMenu");

        var header = new MenuFlyoutItem { Style = Ui.Resource<Style>("LwMenuContent"), Tag = AccountHeader(server) };
        AutomationProperties.SetAutomationId(header, "ProfileHeader");
        AutomationProperties.SetName(header, L.Format("Profile.HeaderName", _sessionUser ?? "", server.Name));
        menu.Items.Add(header);
        menu.Items.Add(new MenuFlyoutSeparator());

        var users = new MenuFlyoutSubItem { Text = L.Text("Profile.SwitchUser"), Icon = MenuIcon(Theme.Icons.SwitchUser) };
        AutomationProperties.SetAutomationId(users, "ProfileSwitchUser");
        FillUsers(users, server, store);
        menu.Items.Add(users);

        var serverItems = new MenuFlyoutSubItem { Text = L.Text("Profile.SwitchServer"), Icon = MenuIcon(Theme.Icons.Server) };
        AutomationProperties.SetAutomationId(serverItems, "ProfileSwitchServer");
        FillServers(serverItems, server, servers);
        menu.Items.Add(serverItems);
        menu.Items.Add(new MenuFlyoutSeparator());

        var link = new MenuFlyoutItem { Text = L.Text("Profile.PlayLink"), Icon = MenuIcon(Theme.Icons.Link), KeyboardAcceleratorTextOverride = L.Text("Profile.PlayLinkShortcut") };
        AutomationProperties.SetAutomationId(link, "ProfilePlayLink");
        link.Click += (_, _) => _ = OpenStreamAsync();
        menu.Items.Add(link);
        var file = new MenuFlyoutItem { Text = L.Text("AppShortcuts.OpenFile.a046add4"), Icon = MenuIcon(Theme.Icons.OpenFile), KeyboardAcceleratorTextOverride = L.Text("Profile.OpenFileShortcut") };
        AutomationProperties.SetAutomationId(file, "ProfileOpenFile");
        file.Click += (_, _) => _ = OpenFileAsync();
        menu.Items.Add(file);
        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = L.Text("Profile.Settings"), Icon = MenuIcon(Theme.Icons.Settings), KeyboardAcceleratorTextOverride = L.Text("Profile.SettingsShortcut") };
        AutomationProperties.SetAutomationId(settings, "ProfileSettings");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
        var logout = new MenuFlyoutItem { Text = L.Text("Profile.LogOut"), Icon = MenuIcon(Theme.Icons.SignOut) };
        AutomationProperties.SetAutomationId(logout, "ProfileLogOut");
        logout.Click += (_, _) => LogOut();
        menu.Items.Add(logout);

        Bar.ShowProfileMenu(menu);
    }

    /// <summary>Avatar, user name, connection status with the server name, and the address.</summary>
    private FrameworkElement AccountHeader(ServerInfo server)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 10, 10, 8), Width = 290 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Avatar(_sessionUser, 44));
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = _sessionUser ?? "", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Resource<Brush>("LwTextBrush") });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        status.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Ui.Resource<Brush>(_connectivity.IsOffline ? "LwOffBrush" : "LwOkBrush"), VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(new TextBlock { Text = server.Name, FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(status);
        text.Children.Add(new TextBlock { Text = ServerAddress.Display(server.Url), FontSize = 11.5, Foreground = Ui.Resource<Brush>("LwText3Brush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Users on the current server: saved profiles switch at once, public users sign in
    /// (with their password when they have one), Other user… and Quick Connect… open Login.</summary>
    private void FillUsers(MenuFlyoutSubItem menu, ServerInfo server, CredentialProfiles store)
    {
        menu.Items.Add(new MenuFlyoutItem { Style = Ui.Resource<Style>("LwMenuHeader"), Text = L.Format("Profile.UsersOn", server.Name) });
        var saved = store.Profiles.Where(p => SameServer(p.ServerUrl, server.Url)).ToList();
        var insertAt = 1;
        foreach (var profile in saved)
        {
            var current = string.Equals(profile.Username, _sessionUser, StringComparison.OrdinalIgnoreCase);
            var item = new ToggleMenuFlyoutItem { Text = profile.Username, IsChecked = current };
            item.Click += async (_, _) =>
            {
                if (current) return;
                await RunAccountOperationAsync(async () =>
                {
                    if (!await Model.SwitchProfileAsync(profile)) OpenLogin(new LoginStart(server, profile.Username));
                });
            };
            menu.Items.Insert(insertAt++, item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var other = new MenuFlyoutItem { Text = L.Text("Profile.OtherUser") };
        other.Click += (_, _) => OpenLogin(new LoginStart(server, PreferPassword: true));
        menu.Items.Add(other);
        var quick = new MenuFlyoutItem { Text = L.Text("Profile.QuickConnect") };
        quick.Click += (_, _) => OpenLogin(new LoginStart(server));
        menu.Items.Add(quick);

        _ = AddPublicUsersAsync();

        async Task AddPublicUsersAsync()
        {
            var users = await ServerProbe.Shared.PublicUsersAsync(server.Url);
            foreach (var user in users.Where(u => !saved.Any(p => string.Equals(p.Username, u.Name, StringComparison.OrdinalIgnoreCase))))
            {
                var current = string.Equals(user.Name, _sessionUser, StringComparison.OrdinalIgnoreCase);
                var item = new ToggleMenuFlyoutItem { Text = user.Name, IsChecked = current, KeyboardAcceleratorTextOverride = user.HasPassword ? L.Text("Profile.PasswordNote") : "" };
                item.Click += async (_, _) => { if (!current) await RunAccountOperationAsync(() => SignInAsAsync(server, user)); };
                menu.Items.Insert(insertAt++, item);
            }
        }
    }

    /// <summary>Saved servers; unreachable ones are disabled and say Offline. Switching signs in as
    /// the server's remembered user.</summary>
    private void FillServers(MenuFlyoutSubItem menu, ServerInfo current, IReadOnlyList<SavedServer> servers)
    {
        menu.Items.Add(new MenuFlyoutItem { Style = Ui.Resource<Style>("LwMenuHeader"), Text = L.Text("Profile.SavedServers") });
        foreach (var server in servers)
        {
            var isCurrent = SameServer(server.Url, current.Url);
            var item = new ToggleMenuFlyoutItem { Text = server.Name, IsChecked = isCurrent, KeyboardAcceleratorTextOverride = ServerAddress.Display(server.Url) };
            item.Click += async (_, _) => { if (!isCurrent) await RunAccountOperationAsync(() => SwitchServerAsync(server)); };
            menu.Items.Add(item);
            if (!isCurrent) _ = MarkOfflineAsync(item, server.Url);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = L.Text("Profile.AddServer"), Icon = MenuIcon(Theme.Icons.Add) };
        add.Click += (_, _) => OpenLogin(new LoginStart());
        menu.Items.Add(add);
        var manage = new MenuFlyoutItem { Text = L.Text("Profile.ManageServers") };
        manage.Click += (_, _) => OpenSettings();
        menu.Items.Add(manage);

        static async Task MarkOfflineAsync(ToggleMenuFlyoutItem item, string url)
        {
            if (await ServerProbe.Shared.InfoAsync(url) is not null) return;
            item.IsEnabled = false;
            item.KeyboardAcceleratorTextOverride = L.Text("Login.Offline");
        }
    }

    private async Task SwitchServerAsync(SavedServer server)
    {
        if (await Model.SwitchProfileAsync(server.Profile)) return;
        if (await ServerProbe.Shared.InfoAsync(server.Url) is { } info) OpenLogin(new LoginStart(info with { Name = server.Name }, server.Profile.Username));
        else ToastHost.Show(ToastKind.Error, L.Format("Errors.CantReach", server.Name));
    }

    /// <summary>Signs a public user in without leaving the current session until it succeeds.</summary>
    private async Task SignInAsAsync(ServerInfo server, PublicUser user)
    {
        var password = "";
        if (user.HasPassword)
        {
            var box = new PasswordBox();
            AutomationProperties.SetName(box, L.Text("Login.Password"));
            AutomationProperties.SetAutomationId(box, "ProfilePasswordBox");
            var dialog = new LwDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = L.Format("Profile.SignInAs", user.Name),
                Content = box,
                PrimaryButtonText = L.Text("Login.SignIn"),
                CloseButtonText = L.Text("Login.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            password = box.Password;
        }
        using var scratch = new JellyfinService();
        var result = await scratch.LoginAsync(server.Url, user.Name, password);
        if (!result.Success)
        {
            ToastHost.Show(ToastKind.Error, result.Error ?? L.Text("Profile.SignInFailed"));
            return;
        }
        if (!await Model.SwitchProfileAsync(result.Credentials!)) ToastHost.Show(ToastKind.Error, L.Format("Errors.CantReach", server.Name));
    }

    private static FontIcon MenuIcon(string glyph) => new() { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = glyph };

    private static bool SameServer(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameProfile(SavedCredentials a, SavedCredentials b)
        => SameServer(a.ServerUrl, b.ServerUrl) && string.Equals(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
}
