using LightWeaver.Diagnostics;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Login;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace LightWeaver.WinUI.Pages;

/// <summary>Where Login opens: the server step, or straight at the auth step for a known server
/// (log out, Other user…, Quick Connect…, an expired saved session). <see cref="CanCancel"/> is set
/// when it was opened from a signed-in session, which Cancel returns to.</summary>
public sealed record LoginStart(ServerInfo? Server = null, string? Username = null, bool PreferPassword = false, bool CanCancel = false);

public sealed partial class LoginPage : UserControl, INavPage
{
    /// <summary>How long a Quick Connect code is offered before a fresh one replaces it in place.</summary>
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const double SavedRowHeight = 55;

    private readonly IAppShell _shell;
    private readonly LoginStart _start;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _countdown;
    private readonly Dictionary<string, bool> _online = new(StringComparer.OrdinalIgnoreCase);
    private ServerInfo? _server;
    private CancellationTokenSource? _stepCts;
    private CancellationTokenSource? _codeCts;
    private DateTimeOffset _codeExpires;
    private string _code = "";
    private bool _addressFailed;
    private bool _busy;
    private bool _rowsStale;
    private bool _revealed;

    public LoginPage(IAppShell shell, LoginStart? start = null)
    {
        _shell = shell;
        _start = start ?? new LoginStart();
        InitializeComponent();
        _countdown = DispatcherQueue.CreateTimer();
        _countdown.Interval = TimeSpan.FromSeconds(1);
        _countdown.Tick += (_, _) => UpdateCodeStatus();
        Scroller.SizeChanged += (_, _) =>
        {
            Viewport.MinHeight = Scroller.ViewportHeight;
            SavedScroll.MaxHeight = Scroller.ViewportHeight < 700 ? 3 * SavedRowHeight : double.PositiveInfinity;
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => { StopQuickConnect(); _stepCts?.Cancel(); };
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(OnLanguageChanged));

        if (_start.Server is { } server) ShowAuth(server, _start.Username, _start.PreferPassword);
        else ShowServerStep();
    }

    public bool HandleEscape()
    {
        if (_server is not null)
        {
            ShowServerStep();
            AddressBox.Focus(FocusState.Keyboard);
            return true;
        }
        if (_start.CanCancel)
        {
            OnCancel(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_server is null) AddressBox.Focus(FocusState.Programmatic);
        else if (QuickConnectMode.IsChecked == true && ModeSwitch.Visibility == Visibility.Visible && _codeCts is null) StartQuickConnect();
    }

    // ---- Server step ---------------------------------------------------------------------------

    private void ShowServerStep()
    {
        StopQuickConnect();
        _stepCts?.Cancel();
        _server = null;
        ServerStep.Visibility = Visibility.Visible;
        AuthStep.Visibility = Visibility.Collapsed;
        Subline.Visibility = Visibility.Visible;
        CancelButton.Visibility = _start.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        ApplyTexts();
        LoadSavedServers();
    }

    private void LoadSavedServers()
    {
        var servers = SavedServers.From(CredentialStore.LoadProfiles());
        BuildSavedRows(servers);
        foreach (var server in servers)
        {
            if (_online.ContainsKey(server.Url)) continue;
            _ = ProbeSavedAsync(server.Url);
        }

        async Task ProbeSavedAsync(string url)
        {
            var info = await ServerProbe.Shared.InfoAsync(url);
            _online[url] = info is not null;
            if (_busy) _rowsStale = true;
            else if (_server is null) BuildSavedRows(SavedServers.From(CredentialStore.LoadProfiles()));
        }
    }

    private void BuildSavedRows(IReadOnlyList<SavedServer> servers)
    {
        SavedList.Children.Clear();
        SavedSection.Visibility = servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < servers.Count; i++) SavedList.Children.Add(SavedRow(servers[i], i > 0));
    }

    private FrameworkElement SavedRow(SavedServer server, bool separator)
    {
        var address = ServerAddress.Display(server.Url);
        var known = _online.TryGetValue(server.Url, out var online);

        var tile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = Ui.Resource<CornerRadius>("LwControlRadius"),
            Background = Ui.Resource<Brush>("LwSurface2Brush"),
            Child = new FontIcon { Style = Ui.Resource<Style>("LwIcon"), FontSize = 19, Glyph = Theme.Icons.Server, Foreground = Ui.Resource<Brush>("LwText2Brush") },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = server.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = L.Format("Login.ServerDetail", address, server.Profile.Username), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Visibility = known ? Visibility.Visible : Visibility.Collapsed };
        status.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Ui.Resource<Brush>(online ? "LwOkBrush" : "LwOffBrush"), VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(new TextBlock { Text = online ? L.Text("Login.Online") : L.Text("Login.Offline"), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText3Brush") });
        var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };

        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.Children.Add(tile);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        Grid.SetColumn(status, 2);
        content.Children.Add(status);
        Grid.SetColumn(ring, 3);
        content.Children.Add(ring);

        var row = new Button { Style = Ui.Resource<Style>("LwRowButton"), Content = content };
        AutomationProperties.SetName(row, server.Name);
        AutomationProperties.SetAutomationId(row, "LoginSavedServer");
        var remove = new MenuFlyoutItem { Text = L.Text("Login.RemoveServer"), Icon = new FontIcon { Glyph = Theme.Icons.Delete } };
        remove.Click += (_, _) => RemoveSaved(server);
        row.ContextFlyout = FastUi.Flyout(new MenuFlyout { Items = { remove } });
        row.Click += async (_, _) => await ConnectSavedAsync(server, ring);

        var chevron = new FontIcon { Style = Ui.Resource<Style>("LwDirectionalIcon"), FontSize = 16, Glyph = Theme.Icons.ChevronRightSmall, Foreground = Ui.Resource<Brush>("LwText3Brush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), IsHitTestVisible = false };
        var more = new Button
        {
            Style = Ui.Resource<Style>("LwSubtleButton"),
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Resource<Brush>("LwText2Brush"),
            Content = Ui.Icon(Theme.Icons.MoreHoriz, 16),
            Opacity = 0,
            Flyout = row.ContextFlyout,
        };
        AutomationProperties.SetName(more, L.Format("Login.ServerOptions", server.Name));
        AutomationProperties.SetAutomationId(more, "LoginSavedServerMore");

        var host = new Grid { BorderBrush = Ui.Resource<Brush>("LwSeparatorBrush"), BorderThickness = new Thickness(0, separator ? 1 : 0, 0, 0) };
        host.Children.Add(row);
        host.Children.Add(chevron);
        host.Children.Add(more);
        void ShowMore(bool show) { more.Opacity = show ? 1 : 0; chevron.Opacity = show ? 0 : 1; }
        host.PointerEntered += (_, _) => ShowMore(true);
        host.PointerExited += (_, _) => ShowMore(more.FocusState != FocusState.Unfocused);
        more.GotFocus += (_, _) => ShowMore(true);
        more.LostFocus += (_, _) => ShowMore(false);
        ring.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => chevron.Visibility = ring.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        return host;
    }

    private void RemoveSaved(SavedServer server)
    {
        CredentialStore.RemoveWhere(p => server.Profiles.Contains(p));
        LoadSavedServers();
        if (SavedSection.Visibility == Visibility.Collapsed) AddressBox.Focus(FocusState.Programmatic);
    }

    /// <summary>One click signs in as the server's remembered user; an expired session asks to sign in again.</summary>
    private async Task ConnectSavedAsync(SavedServer server, ProgressRing ring)
    {
        if (_busy) return;
        var address = ServerAddress.Display(server.Url);
        SetBusy(true, ring);
        try
        {
            var info = await ServerProbe.Shared.InfoAsync(server.Url);
            _online[server.Url] = info is not null;
            _rowsStale = true;
            if (info is null)
            {
                AddressBox.Text = address;
                ShowAddressError(L.Format("Login.CantReach", address));
                return;
            }
            if (await _shell.Model.SwitchProfileAsync(server.Profile)) return;
            AppLog.Detail("login", "event=saved_server outcome=session-expired");
            ShowAuth(info with { Name = server.Name }, server.Profile.Username, preferPassword: false);
        }
        finally
        {
            SetBusy(false, ring);
        }
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = ConnectAsync();
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        if (!_addressFailed) return;
        _addressFailed = false;
        AddressFrame.HasError = false;
        AddressError.Visibility = Visibility.Collapsed;
        AddressHelp.Visibility = Visibility.Visible;
        ApplyTexts();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private async Task ConnectAsync()
    {
        if (_busy) return;
        var input = AddressBox.Text.Trim();
        if (ServerAddress.Candidates(input).Count == 0)
        {
            ShowAddressError(L.Text("Login.NotAnAddress"));
            return;
        }
        var ct = NewStepToken();
        var started = System.Diagnostics.Stopwatch.StartNew();
        SetBusy(true, ConnectRing);
        try
        {
            var info = await ServerProbe.Shared.FindAsync(input, ct);
            if (ct.IsCancellationRequested) return;
            AppLog.Detail("login", $"event=find_server outcome={(info is null ? "unreachable" : "found")} elapsed_ms={started.ElapsedMilliseconds}");
            if (info is null) ShowAddressError(L.Format("Login.CantReach", input));
            else ShowAuth(info, null, preferPassword: false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false, ConnectRing);
        }
    }

    private void ShowAddressError(string message)
    {
        _addressFailed = true;
        AddressFrame.HasError = true;
        AddressErrorText.Text = message;
        AddressError.Visibility = Visibility.Visible;
        AddressHelp.Visibility = Visibility.Collapsed;
        ApplyTexts();
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectionStart = AddressBox.Text.Length;
    }

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (CredentialStore.Load() is { } previous)
        {
            SetBusy(true, ConnectRing);
            var back = await _shell.Model.SwitchProfileAsync(previous);
            SetBusy(false, ConnectRing);
            if (back) return;
        }
        CancelButton.Visibility = Visibility.Collapsed;
    }

    // ---- Auth step -----------------------------------------------------------------------------

    private async void ShowAuth(ServerInfo server, string? username, bool preferPassword)
    {
        StopQuickConnect();
        _server = server;
        ServerStep.Visibility = Visibility.Collapsed;
        AuthStep.Visibility = Visibility.Visible;
        Subline.Visibility = Visibility.Collapsed;
        ModeSwitch.Visibility = Visibility.Collapsed;
        QuickConnectPanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Collapsed;
        PublicUsers.Visibility = Visibility.Collapsed;
        UsernameBox.Text = username ?? "";
        PasswordInput.Password = "";
        HidePasswordError();
        ApplyTexts();

        var ct = NewStepToken();
        var quickConnect = ServerProbe.Shared.QuickConnectEnabledAsync(server.Url, ct);
        var users = ServerProbe.Shared.PublicUsersAsync(server.Url, ct);
        try
        {
            await Task.WhenAll(quickConnect, users);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested || !ReferenceEquals(_server, server)) return;

        ShowPublicUsers(server, users.Result);
        ModeSwitch.Visibility = quickConnect.Result ? Visibility.Visible : Visibility.Collapsed;
        var useCode = quickConnect.Result && !preferPassword;
        (useCode ? QuickConnectMode : PasswordMode).IsChecked = true;
        ApplyMode();
    }

    private void OnChangeServer(object sender, RoutedEventArgs e)
    {
        ShowServerStep();
        AddressBox.Focus(FocusState.Programmatic);
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_server is not null && ModeSwitch.Visibility == Visibility.Visible) ApplyMode();
    }

    private void ApplyMode()
    {
        if (QuickConnectMode.IsChecked == true && ModeSwitch.Visibility == Visibility.Visible)
        {
            PasswordPanel.Visibility = Visibility.Collapsed;
            QuickConnectPanel.Visibility = Visibility.Visible;
            if (_codeCts is null) StartQuickConnect();
            return;
        }
        StopQuickConnect();
        QuickConnectPanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Visible;
        if (UsernameBox.Text.Length > 0) PasswordInput.Focus(FocusState.Programmatic);
        else UsernameBox.Focus(FocusState.Programmatic);
    }

    private void ShowPublicUsers(ServerInfo server, IReadOnlyList<PublicUser> users)
    {
        PublicUsers.Children.Clear();
        PublicUsers.Visibility = users.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var user in users)
        {
            var avatar = new Avatar(user.Name, 40);
            var ring = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
            var outline = new Ellipse { Width = 44, Height = 44, Margin = new Thickness(-2), Stroke = Ui.Resource<Brush>("LwAccentFgBrush"), StrokeThickness = 2, Visibility = Visibility.Collapsed };
            ring.Children.Add(avatar);
            ring.Children.Add(outline);
            var label = new TextBlock { Text = user.Name, FontSize = 11.5, Foreground = Ui.Resource<Brush>("LwText2Brush"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 60 };
            var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(ring);
            stack.Children.Add(label);
            var button = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 64, Padding = new Thickness(0, 6, 0, 6), CornerRadius = Ui.Resource<CornerRadius>("LwCardRadius"), Content = stack, Tag = ring };
            AutomationProperties.SetName(button, user.Name);
            AutomationProperties.SetAutomationId(button, "LoginPublicUser");
            button.Click += (_, _) =>
            {
                UsernameBox.Text = user.Name;
                SelectUser(button);
                PasswordInput.Focus(FocusState.Programmatic);
            };
            if (string.Equals(user.Name, UsernameBox.Text, StringComparison.OrdinalIgnoreCase)) SelectUser(button);
            PublicUsers.Children.Add(button);
            if (user.ImageUrl(server.Url, 80) is { } url) _ = LoadPictureAsync(avatar, url);
        }

        async Task LoadPictureAsync(Avatar avatar, string url)
        {
            if (await ImageLoader.LoadAsync(_shell.Model.Jellyfin, url, 80) is { } picture) avatar.SetPicture(picture);
        }
    }

    private void SelectUser(Button button)
    {
        foreach (var child in PublicUsers.Children.OfType<Button>())
        {
            var selected = ReferenceEquals(child, button);
            child.Background = selected ? Ui.Resource<Brush>("LwHoverBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ((Grid)child.Tag).Children[1].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- Quick Connect -------------------------------------------------------------------------

    private async void StartQuickConnect()
    {
        if (_server is not { } server) return;
        StopQuickConnect();
        var cts = _codeCts = new CancellationTokenSource();
        var started = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Detail("login", "event=quick_connect outcome=start");
        try
        {
            while (!cts.IsCancellationRequested)
            {
                ShowCode("");
                WaitRing.IsActive = true;
                var handshake = await _shell.Model.Jellyfin.InitiateQuickConnectAsync(server.Url);
                if (cts.IsCancellationRequested) return;
                if (handshake is not { } code)
                {
                    AppLog.Detail("login", "event=quick_connect outcome=unavailable");
                    WaitRing.IsActive = false;
                    QuickConnectStatus.Text = L.Text("Login.QuickConnectUnavailable");
                    return;
                }
                ShowCode(code.Code);
                _codeExpires = DateTimeOffset.Now + CodeLifetime;
                UpdateCodeStatus();
                _countdown.Start();
                while (DateTimeOffset.Now < _codeExpires)
                {
                    await Task.Delay(PollInterval, cts.Token);
                    var result = await _shell.Model.Jellyfin.PollQuickConnectAsync(code.Secret);
                    if (cts.IsCancellationRequested) return;
                    if (result is null) continue;
                    if (result.Success)
                    {
                        AppLog.Info("login", $"event=quick_connect outcome=success elapsed_ms={started.ElapsedMilliseconds}");
                        StopQuickConnect();
                        _shell.Model.CompleteExternalLogin(result.Credentials!);
                        return;
                    }
                    AppLog.Detail("login", "event=quick_connect outcome=code-replaced");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopQuickConnect()
    {
        _codeCts?.Cancel();
        _codeCts?.Dispose();
        _codeCts = null;
        _countdown.Stop();
        WaitRing.IsActive = false;
    }

    /// <summary>The code in 42 x 56 monospaced digit boxes, grouped in threes.</summary>
    private void ShowCode(string code)
    {
        _code = code;
        CodeDigits.Children.Clear();
        AutomationProperties.SetName(CodeDigits, code);
        CopyCodeButton.IsEnabled = code.Length > 0;
        for (var i = 0; i < code.Length; i++)
        {
            if (i > 0 && i % 3 == 0) CodeDigits.Children.Add(new Border { Width = 4 });
            CodeDigits.Children.Add(new Border
            {
                Width = 42,
                Height = 56,
                CornerRadius = Ui.Resource<CornerRadius>("LwControlRadius"),
                Background = Ui.Resource<Brush>("LwSurface2Brush"),
                Child = new TextBlock
                {
                    Text = code[i].ToString(),
                    FontFamily = Ui.Resource<FontFamily>("LwMonoFont"),
                    FontSize = 30,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        UpdateCodeStatus();
    }

    private void UpdateCodeStatus()
    {
        if (_code.Length == 0)
        {
            QuickConnectStatus.Text = "";
            return;
        }
        var left = _codeExpires - DateTimeOffset.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        QuickConnectStatus.Text = L.Format("Login.WaitingForApproval", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (_code.Length == 0) return;
        var package = new DataPackage();
        package.SetText(_code);
        Clipboard.SetContent(package);
        ToastHost.Show(ToastKind.Info, L.Text("Login.CodeCopied"));
    }

    // ---- Password ------------------------------------------------------------------------------

    private void OnCredentialKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ReferenceEquals(sender, UsernameBox) && PasswordInput.Password.Length == 0) PasswordInput.Focus(FocusState.Keyboard);
        else _ = SignInAsync();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length > 0) HidePasswordError();
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => _ = SignInAsync();

    private async Task SignInAsync()
    {
        if (_busy || _server is not { } server) return;
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            ShowPasswordError(L.Text("Login.UsernameRequired"));
            UsernameBox.Focus(FocusState.Programmatic);
            return;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var stay = StaySignedIn.IsChecked == true;
        SetBusy(true, SignInRing);
        string? error;
        try
        {
            error = await _shell.Model.LoginAsync(server.Url, username, PasswordInput.Password);
        }
        finally
        {
            SetBusy(false, SignInRing);
        }
        if (error is null)
        {
            AppLog.Info("login", $"event=connect outcome=success elapsed_ms={started.ElapsedMilliseconds}");
            if (!stay && CredentialStore.Load() is { } profile) _shell.ForgetOnExit(profile);
            return;
        }
        AppLog.Detail("login", $"event=connect outcome=rejected elapsed_ms={started.ElapsedMilliseconds}");
        ShowPasswordError(error);
        PasswordInput.Password = "";
        PasswordInput.Focus(FocusState.Programmatic);
    }

    private void ShowPasswordError(string message)
    {
        PasswordErrorText.Text = message;
        PasswordError.Visibility = Visibility.Visible;
        PasswordFrame.HasError = true;
    }

    private void HidePasswordError()
    {
        PasswordError.Visibility = Visibility.Collapsed;
        PasswordFrame.HasError = false;
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        PasswordInput.PasswordRevealMode = _revealed ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        ApplyTexts();
        PasswordInput.Focus(FocusState.Programmatic);
    }

    // ---- Shared --------------------------------------------------------------------------------

    private CancellationToken NewStepToken()
    {
        _stepCts?.Cancel();
        _stepCts = new CancellationTokenSource();
        return _stepCts.Token;
    }

    private void SetBusy(bool busy, ProgressRing ring)
    {
        _busy = busy;
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        SavedList.IsHitTestVisible = !busy;
        ApplyTexts();
        if (!busy && _rowsStale && _server is null)
        {
            _rowsStale = false;
            BuildSavedRows(SavedServers.From(CredentialStore.LoadProfiles()));
        }
    }

    private void OnLanguageChanged()
    {
        ApplyTexts();
        if (_server is null) BuildSavedRows(SavedServers.From(CredentialStore.LoadProfiles()));
    }

    /// <summary>Every text set from code, so a language switch re-renders the page in place.</summary>
    private void ApplyTexts()
    {
        Heading.Text = _server is null ? L.Text("Login.ConnectHeading") : L.Text("Login.SignInHeading");
        ConnectText.Text = _busy && ConnectRing.IsActive ? L.Text("Login.Connecting") : _addressFailed ? L.Text("Login.TryAgain") : L.Text("Login.Connect");
        AutomationProperties.SetName(ConnectButton, ConnectText.Text);
        var reveal = _revealed ? L.Text("Login.HidePassword") : L.Text("Login.ShowPassword");
        ToolTipService.SetToolTip(RevealButton, reveal);
        AutomationProperties.SetName(RevealButton, reveal);
        RevealGlyph.Glyph = _revealed ? Theme.Icons.EyeHidden : Theme.Icons.Eye;
        if (_server is { } server)
        {
            ServerChipText.Inlines.Clear();
            ServerChipText.Inlines.Add(new Run { Text = server.Name });
            ServerChipText.Inlines.Add(new Run { Text = L.Format("Login.ServerChipAddress", ServerAddress.Display(server.Url)), Foreground = Ui.Resource<Brush>("LwText3Brush") });
            AutomationProperties.SetName(ServerChipText, server.Name);
            UpdateCodeStatus();
        }
    }
}
