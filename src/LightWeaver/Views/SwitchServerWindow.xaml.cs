using LightWeaver.Localization;
using System.Windows;
using System.Windows.Controls;
using LightWeaver.Jellyfin;

namespace LightWeaver.Views;

/// <summary>
/// Profile switcher (Phase 5 M7): pick a saved server/user profile or jump to the
/// login form for a new one. Pure UI — the owner acts on the result properties.
/// </summary>
public partial class SwitchServerWindow : Window
{
    /// <summary>Row model: profile + display strings. Warm = a live session is kept
    /// for it (switching is instant, no reconnect).</summary>
    private sealed record ProfileRow(SavedCredentials Profile, bool IsActive, bool IsWarm)
    {
        public string ActiveMark => IsActive ? "✓" : "";
        public string Title => $"{Profile.Username} @ {Profile.ServerName ?? Profile.ServerUrl}";
        public string Detail => IsWarm && !IsActive ? L.Format("SwitchServerWindow.Warm.52333be5", (Profile.ServerUrl)) : Profile.ServerUrl;
        public override string ToString() => Title;
    }

    /// <summary>The profile chosen to switch to; null if none.</summary>
    public SavedCredentials? SelectedProfile { get; private set; }

    /// <summary>True when the user chose "Add server…".</summary>
    public bool AddServerRequested { get; private set; }

    public SwitchServerWindow(CredentialProfiles profiles, Func<SavedCredentials, bool>? isWarm = null,
        bool hasActiveSession = true)
    {
        InitializeComponent();
        LiveText.Watch(this);
        // The content, not the Window: a right-to-left Window mirrors its HWND (UiDirection).
        UiDirection.Follow((FrameworkElement)Content);
        // The dialog opens at the app's UI zoom (modal, so the zoom cannot change while it is
        // up): its content is scaled and the CLIENT area grows with it, so nothing inside is
        // clipped. Done once the frame is known, because Width/Height include the title bar.
        if (Content is FrameworkElement root && Math.Abs(Theme.UiZoom.Scale - 1.0) > 0.001)
        {
            root.LayoutTransform = Theme.UiZoom.Snapshot();
            Loaded += (_, _) => GrowClientForZoom();
        }
        for (var i = 0; i < profiles.Profiles.Count; i++)
            ProfilesList.Items.Add(new ProfileRow(profiles.Profiles[i], hasActiveSession && i == profiles.ActiveIndex,
                isWarm?.Invoke(profiles.Profiles[i]) ?? false));
        Diagnostics.AppLog.Detail("profile-switch", $"event=load outcome=success count={profiles.Profiles.Count}");
    }

    private void GrowClientForZoom()
    {
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(this) == 0 || System.Windows.Media.VisualTreeHelper.GetChild(this, 0) is not FrameworkElement client)
            return;
        var growth = Theme.UiZoom.Scale - 1.0;
        // The owner's monitor, not SystemParameters.WorkArea (primary only), or a dialog opened
        // over a window on another screen is pulled across to the primary one.
        var work = MonitorWorkArea.Of(this);
        if (work.IsEmpty)
            work = SystemParameters.WorkArea;
        var width = Math.Min(Width + client.ActualWidth * growth, work.Width);
        var height = Math.Min(Height + client.ActualHeight * growth, work.Height);
        // Max(): at a full-height clamp, Bottom - height can round one ulp below Top on a
        // fractional-DIP monitor origin, and Math.Clamp throws when min > max.
        Left = Math.Clamp(Left - (width - Width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
        Top = Math.Clamp(Top - (height - Height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        Width = width;
        Height = height;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => SwitchButton.IsEnabled = ProfilesList.SelectedItem is ProfileRow { IsActive: false };

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProfilesList.SelectedItem is ProfileRow { IsActive: false })
            OnSwitch(sender, e);
    }

    private void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileRow row)
            return;
        Diagnostics.AppLog.Detail("profile-switch",
            $"event=interaction action=switch warm={row.IsWarm} index={ProfilesList.SelectedIndex}");
        SelectedProfile = row.Profile;
        DialogResult = true;
    }

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("profile-switch", "event=interaction action=add-server");
        AddServerRequested = true;
        DialogResult = true;
    }
}
