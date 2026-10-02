using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightWeaver.ViewModels;
using LightWeaver.Views;

namespace LightWeaver;

// The same group controls are anchored in the browse shell and the playback overlay.
public partial class MainWindow
{
    private Func<SyncPlayFlyout>? _syncPlayFlyoutFactory = null;
    private SyncPlayFlyoutTrigger? _syncPlayTrigger;

    private void InitializeSyncPlayUi()
    {
        _overlay!.SyncPlayFlyoutFactory = CreateSyncPlayFlyout;
        _syncPlayTrigger = new(SyncPlayPopup, SyncPlayButton);
        _syncPlayTrigger.Attach();
        SyncPlayButton.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Space or Key.Enter)
            {
                if (!e.IsRepeat) SyncPlayButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, SyncPlayButton.IsChecked != true);
                e.Handled = true;
            }
        };
        _playerViewModel.PropertyChanged += OnSyncPlayUiChanged;
        _app.PropertyChanged += OnSyncPlayHostChanged;
        SyncPlayPopup.Closed += (_, _) =>
        {
            if (SyncPlayPopup.Child is SyncPlayFlyout content) content.Dispose();
            SyncPlayPopup.Child = null;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) SyncPlayPopup.IsOpen = false; };
        BrowseHeader.IsVisibleChanged += (_, _) => { if (!BrowseHeader.IsVisible) SyncPlayPopup.IsOpen = false; };
        Closed += (_, _) =>
        {
            _syncPlayTrigger.Dispose();
            _playerViewModel.PropertyChanged -= OnSyncPlayUiChanged;
            _app.PropertyChanged -= OnSyncPlayHostChanged;
            SyncPlayPopup.IsOpen = false;
        };
        UpdateSyncPlayControls();
    }

    private void OnSyncPlayHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.State))
        {
            SyncPlayPopup.IsOpen = false;
            _overlay?.CloseSyncPlayFlyout();
        }
    }

    private void OnSyncPlayUiChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.SyncPlay) or nameof(PlayerViewModel.SyncPlayPending))
            UpdateSyncPlayControls();
    }

    private void UpdateSyncPlayControls()
    {
        SyncPlayButtonLabel.Text = _playerViewModel.SyncPlayStatus;
        SyncPlayButtonLabel.Visibility = SyncPlayButtonLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var label = _playerViewModel.SyncPlay is { } group
            ? $"SyncPlay · {group.GroupName} · {_playerViewModel.SyncPlayStatus}"
            : _playerViewModel.SyncPlayPending ? "SyncPlay · Joining…" : "SyncPlay groups";
        SyncPlayButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(SyncPlayButton, label);
    }

    private SyncPlayFlyout CreateSyncPlayFlyout()
    {
        var content = _syncPlayFlyoutFactory?.Invoke() ?? new(_playerViewModel, ListSyncPlayGroupsAsync,
            JoinSyncPlayGroupAsync, LeaveSyncPlayGroupAsync, CreateSyncPlayGroupAsync);
        var host = _overlay is { IsVisible: true } ? (Window)_overlay : this;
        var work = WorkArea(MonitorFromWindow(new WindowInteropHelper(host).Handle, MONITOR_DEFAULTTONEAREST));
        var dpi = VisualTreeHelper.GetDpi(host);
        var width = (work.Right - work.Left) / dpi.DpiScaleX;
        var height = (work.Bottom - work.Top) / dpi.DpiScaleY;
        if (width <= 0 || height <= 0) { width = host.ActualWidth; height = host.ActualHeight; }
        // WPF flips the anchored popup at screen edges. Bound its content in the monitor's DIPs.
        content.Width = Math.Max(240, Math.Min(360, Math.Min(host.ActualWidth - 24, width - 24)));
        content.Height = Math.Max(240, Math.Min(440, Math.Min(host.ActualHeight - 24, height * 0.75 - 16)));
        return content;
    }

    private void OnSyncPlayChecked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_syncPlayTrigger?.ConsumeDismissal() == true)
        {
            SyncPlayButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            CloseSyncPlayFlyout();
            return;
        }
        _overlay?.CloseSyncPlayFlyout();
        var content = CreateSyncPlayFlyout();
        content.CloseRequested += CloseSyncPlayFlyout;
        // Reopening can cancel Popup's deferred Closed event. Dispose before replacing as well.
        if (SyncPlayPopup.Child is SyncPlayFlyout previous) previous.Dispose();
        SyncPlayPopup.Child = content;
        SyncPlayPopup.HorizontalOffset = SyncPlayButton.ActualWidth - content.Width;
        content.SizeChanged += (_, _) =>
        {
            if (ReferenceEquals(SyncPlayPopup.Child, content))
                SyncPlayPopup.HorizontalOffset = SyncPlayButton.ActualWidth - content.ActualWidth;
        };
        SyncPlayPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (SyncPlayPopup.IsOpen && ReferenceEquals(SyncPlayPopup.Child, content)) content.FocusFirstControl();
        });
    }

    private void CloseSyncPlayFlyout()
    {
        SyncPlayPopup.IsOpen = false;
        SyncPlayButton.Focus();
    }

    private bool SyncPlayFlyoutOpen => SyncPlayPopup.IsOpen || _overlay?.SyncPlayFlyoutOpen == true;
}
