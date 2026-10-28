using LightWeaver.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

public partial class LoginView : UserControl
{
    private readonly AppViewModel _app;
    public event Action? SwitchUserRequested;

    public LoginView(AppViewModel app)
    {
        InitializeComponent();
        LiveText.Watch(this);
        _app = app;
        foreach (var control in new Control[] { ConnectButton, QuickConnectButton, SwitchUserButton,
            ServerBox, UsernameBox, PasswordBox, PasswordReveal, PasswordPeek })
            control.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(AppViewModel.IsAccountOperationAvailable))
                { Source = app });
        Unloaded += (_, _) => _qcCts?.Cancel();
        ServerBox.TextChanged += (_, _) =>
            HttpWarning.Visibility = Jellyfin.JellyfinService.UsesPlainHttp(ServerBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            SwitchUserButton.Visibility = Jellyfin.CredentialStore.LoadProfiles().Profiles.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            ServerBox.Focus();
        };
        // M6.2: hide the build footer when the window is short so it never steals room from
        // the card (which scrolls); the card fits without scrolling at normal heights.
        SizeChanged += (_, _) =>
            BuildLine.Visibility = ActualHeight < 560 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePasswordPeek();
        // Leaving the sign-in screen (signed in, profile switch, window closing) masks the field
        // again and empties the plain-text twin, so a revealed password is never what greets the
        // next visit and does not linger in a TextBox nobody can see.
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ConcealPassword(); };
        Unloaded += (_, _) => ConcealPassword();
    }

    private bool PasswordRevealed => PasswordReveal.Visibility == Visibility.Visible;

    /// <summary>The password from whichever twin is showing.</summary>
    private string CurrentPassword => PasswordRevealed ? PasswordReveal.Text : PasswordBox.Password;

    private void OnTogglePasswordPeek(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("login", "event=interaction action=toggle-password-visibility");
        if (PasswordRevealed)
            ConcealPassword(focus: true);
        else
        {
            PasswordReveal.Text = PasswordBox.Password;
            PasswordReveal.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
            UpdatePasswordPeek();
            PasswordReveal.Focus();
            PasswordReveal.CaretIndex = PasswordReveal.Text.Length;
        }
    }

    private void ConcealPassword(bool focus = false)
    {
        if (!PasswordRevealed) return;
        PasswordBox.Password = PasswordReveal.Text;
        PasswordReveal.Clear();
        PasswordBox.Visibility = Visibility.Visible;
        PasswordReveal.Visibility = Visibility.Collapsed;
        UpdatePasswordPeek();
        if (focus) PasswordBox.Focus();
    }

    private void UpdatePasswordPeek()
    {
        PasswordPeek.Content = (string)FindResource(PasswordRevealed ? "IconHidden" : "IconVisible");
        LiveText.Bind(PasswordPeek, "ToolTip", () => PasswordRevealed
            ? L.Text("LoginView.HidePassword") : L.Text("LoginView.ShowPassword"));
        LiveText.Bind(PasswordPeek, System.Windows.Automation.AutomationProperties.NameProperty, () => PasswordRevealed
            ? L.Text("LoginView.HidePassword") : L.Text("LoginView.ShowPassword"));
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = ConnectAsync();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private void OnSwitchUser(object sender, RoutedEventArgs e)
    {
        if (_app.IsAccountOperationAvailable) SwitchUserRequested?.Invoke();
    }

    private async Task ConnectAsync()
    {
        if (!_app.IsAccountOperationAvailable) return;
        if (string.IsNullOrWhiteSpace(ServerBox.Text) || string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            Diagnostics.AppLog.Detail("login", "event=connect outcome=validation-failed");
            ShowError(L.Text("LoginView.ServerAndUsernameAreRequired.b128c904"));
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("login", "event=connect outcome=start");
        if (!_app.TryBeginAccountOperation()) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var error = await _app.LoginAsync(ServerBox.Text, UsernameBox.Text, CurrentPassword);
            if (error is not null)
            {
                Diagnostics.AppLog.Detail("login",
                    $"event=connect outcome=rejected elapsed_ms={started.ElapsedMilliseconds}");
                ShowError(error);
            }
            else
            {
                Diagnostics.AppLog.Info("login",
                    $"event=connect outcome=success elapsed_ms={started.ElapsedMilliseconds}");
            }
        }
        finally
        {
            _app.EndAccountOperation();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ---- Quick Connect (Phase 5 M7) ----

    private CancellationTokenSource? _qcCts;

    private async void OnQuickConnect(object sender, RoutedEventArgs e)
    {
        if (!_app.IsAccountOperationAvailable) return;
        if (string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            Diagnostics.AppLog.Detail("login", "event=quick_connect outcome=validation-failed");
            ShowError(L.Text("LoginView.EnterTheServerAddressFirst.9fb04179"));
            return;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("login", "event=quick_connect outcome=start");
        ErrorText.Visibility = Visibility.Collapsed;
        if (!_app.TryBeginAccountOperation()) return;
        var service = _app.Jellyfin;
        var activated = false;
        _qcCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var init = await service.InitiateQuickConnectAsync(ServerBox.Text);
            if (_qcCts.IsCancellationRequested || !ReferenceEquals(service, _app.Jellyfin)) return;
            if (init is not { } handshake)
            {
                Diagnostics.AppLog.Detail("login",
                    $"event=quick_connect outcome=unavailable elapsed_ms={started.ElapsedMilliseconds}");
                ShowError(L.Text("LoginView.QuickConnectIsNotEnabledOnThis.784bf866"));
                return;
            }
            QcCodeText.Text = handshake.Code;
            LiveText.Bind(QcStatusText, "Text", () => L.Text("LoginView.WaitingForApproval.1da7fbf1"));
            QuickConnectPanel.Visibility = Visibility.Visible;
            try
            {
                while (!_qcCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _qcCts.Token);
                    var result = await service.PollQuickConnectAsync(handshake.Secret);
                    if (_qcCts.IsCancellationRequested || !ReferenceEquals(service, _app.Jellyfin)) return;
                    if (result is null)
                        continue;   // not approved yet
                    if (result.Success)
                    {
                        Diagnostics.AppLog.Info("login",
                            $"event=quick_connect outcome=success elapsed_ms={started.ElapsedMilliseconds}");
                        _app.CompleteExternalLogin(result.Credentials!);
                        activated = true;
                        return;
                    }
                    Diagnostics.AppLog.Detail("login",
                        $"event=quick_connect outcome=rejected elapsed_ms={started.ElapsedMilliseconds}");
                    ShowError(result.Error ?? L.Text("LoginView.QuickConnectFailed.74a65193"));
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                Diagnostics.AppLog.Detail("login",
                    $"event=quick_connect outcome=cancelled elapsed_ms={started.ElapsedMilliseconds}");
                // user cancel or the 3-minute cap — back to the password form
            }
        }
        finally
        {
            QuickConnectPanel.Visibility = Visibility.Collapsed;
            // Polling can authenticate the scratch service before returning. Cancel must discard
            // that session instead of leaving an unactivated user connected on the login form.
            if (!activated && ReferenceEquals(service, _app.Jellyfin) && _app.ActiveSessionKey is null)
                service.Logout();
            _qcCts?.Dispose();
            _qcCts = null;
            _app.EndAccountOperation();
        }
    }

    private void OnQuickConnectCancel(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("login", "event=quick_connect action=cancel");
        _qcCts?.Cancel();
    }
}
