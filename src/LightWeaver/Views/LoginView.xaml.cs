using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

public partial class LoginView : UserControl
{
    private readonly AppViewModel _app;

    public LoginView(AppViewModel app)
    {
        InitializeComponent();
        _app = app;
        ServerBox.TextChanged += (_, _) =>
            HttpWarning.Visibility = ServerBox.Text.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        Loaded += (_, _) => ServerBox.Focus();
        // M6.2: hide the build footer when the window is short so it never steals room from
        // the card (which scrolls); the card fits without scrolling at normal heights.
        SizeChanged += (_, _) =>
            BuildLine.Visibility = ActualHeight < 560 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = ConnectAsync();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerBox.Text) || string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            Diagnostics.AppLog.Detail("login", "event=connect outcome=validation-failed");
            ShowError("Server and username are required.");
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("login", "event=connect outcome=start");
        ConnectButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var error = await _app.LoginAsync(ServerBox.Text, UsernameBox.Text, PasswordBox.Password);
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
            ConnectButton.IsEnabled = true;
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
        if (string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            Diagnostics.AppLog.Detail("login", "event=quick_connect outcome=validation-failed");
            ShowError("Enter the server address first.");
            return;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("login", "event=quick_connect outcome=start");
        ErrorText.Visibility = Visibility.Collapsed;
        QuickConnectButton.IsEnabled = false;
        try
        {
            var init = await _app.Jellyfin.InitiateQuickConnectAsync(ServerBox.Text);
            if (init is not { } handshake)
            {
                Diagnostics.AppLog.Detail("login",
                    $"event=quick_connect outcome=unavailable elapsed_ms={started.ElapsedMilliseconds}");
                ShowError("Quick Connect is not enabled on this server (or the server is unreachable).");
                return;
            }
            QcCodeText.Text = handshake.Code;
            QcStatusText.Text = "Waiting for approval…";
            QuickConnectPanel.Visibility = Visibility.Visible;
            _qcCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                while (!_qcCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _qcCts.Token);
                    var result = await _app.Jellyfin.PollQuickConnectAsync(handshake.Secret);
                    if (result is null)
                        continue;   // not approved yet
                    if (result.Success)
                    {
                        Diagnostics.AppLog.Info("login",
                            $"event=quick_connect outcome=success elapsed_ms={started.ElapsedMilliseconds}");
                        _app.CompleteExternalLogin(result.Credentials!);
                        return;
                    }
                    Diagnostics.AppLog.Detail("login",
                        $"event=quick_connect outcome=rejected elapsed_ms={started.ElapsedMilliseconds}");
                    ShowError(result.Error ?? "Quick Connect failed.");
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
            QuickConnectButton.IsEnabled = true;
            _qcCts?.Dispose();
            _qcCts = null;
        }
    }

    private void OnQuickConnectCancel(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("login", "event=quick_connect action=cancel");
        _qcCts?.Cancel();
    }
}
