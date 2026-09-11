using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace LightWeaver.Views;

/// <summary>
/// In-app confirm/alert layer (Phase 7 M23): hosted as the top layer of the shell root,
/// never a native MessageBox. Entry animates fade + a small rise over 480 ms
/// (LwDurDrift). One request at a time; a superseding call cancels the previous one.
/// </summary>
public partial class ModalHost : UserControl
{
    private TaskCompletionSource<bool>? _pending;

    public ModalHost()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CancelButton.Visibility == Visibility.Visible)
            {
                Close(false);
                e.Handled = true;
            }
        };
    }

    /// <summary>Two-button confirm; true when the user confirms. Destructive styles the
    /// confirm button ember instead of garnet.</summary>
    public Task<bool> ConfirmAsync(string title, string body, string confirmLabel = "Confirm",
        bool destructive = false)
        => ShowAsync(title, body, confirmLabel, destructive, showCancel: true);

    /// <summary>Single-button alert.</summary>
    public Task AlertAsync(string title, string body, string confirmLabel = "OK")
        => ShowAsync(title, body, confirmLabel, destructive: false, showCancel: false);

    /// <summary>Single-select option list (M20 resolution picker): returns the chosen
    /// index, or null when cancelled/dismissed.</summary>
    public async Task<int?> ChooseAsync(string title, string body, IReadOnlyList<string> options,
        string confirmLabel = "Select", int preselect = 0)
    {
        ChoiceList.ItemsSource = options;
        ChoiceList.SelectedIndex = Math.Clamp(preselect, 0, options.Count - 1);
        ChoiceList.Visibility = Visibility.Visible;
        try
        {
            var confirmed = await ShowAsync(title, body, confirmLabel,
                destructive: false, showCancel: true);
            return confirmed && ChoiceList.SelectedIndex >= 0 ? ChoiceList.SelectedIndex : null;
        }
        finally
        {
            ChoiceList.Visibility = Visibility.Collapsed;
            ChoiceList.ItemsSource = null;
        }
    }

    private Task<bool> ShowAsync(string title, string body, string confirmLabel,
        bool destructive, bool showCancel)
    {
        if (_pending?.TrySetResult(false) == true)
            Diagnostics.AppLog.Detail("modal", "event=close outcome=superseded");
        _pending = new TaskCompletionSource<bool>();
        TitleText.Text = title;
        BodyText.Text = body;
        ConfirmButton.Content = confirmLabel;
        ConfirmButton.Style = (Style)FindResource(destructive ? "LwButtonDestructive" : "LwButtonPrimary");
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        var kind = ChoiceList.Visibility == Visibility.Visible ? "choice"
            : showCancel ? "confirm" : "alert";
        Diagnostics.AppLog.Detail("modal",
            $"event=open kind={kind} destructive={destructive} options={ChoiceList.Items.Count}");

        // fade + rise (spec: 480 ms drift)
        var dur = TimeSpan.FromMilliseconds(480);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        CardRise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, dur) { EasingFunction = ease });

        Focus();
        ConfirmButton.Focus();
        return _pending.Task;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

    /// <summary>Click-away acts as cancel for confirms; alerts require the button.</summary>
    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        if (CancelButton.Visibility == Visibility.Visible)
            Close(false);
    }

    private void Close(bool result)
    {
        Diagnostics.AppLog.Detail("modal", $"event=close outcome={(result ? "confirm" : "cancel")}");
        Visibility = Visibility.Collapsed;
        _pending?.TrySetResult(result);
        _pending = null;
    }
}
