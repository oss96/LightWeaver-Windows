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
        public string Detail => IsWarm && !IsActive ? $"{Profile.ServerUrl}   ·   warm" : Profile.ServerUrl;
        public override string ToString() => Title;
    }

    /// <summary>The profile chosen to switch to; null if none.</summary>
    public SavedCredentials? SelectedProfile { get; private set; }

    /// <summary>True when the user chose "Add server…".</summary>
    public bool AddServerRequested { get; private set; }

    public SwitchServerWindow(CredentialProfiles profiles, Func<SavedCredentials, bool>? isWarm = null)
    {
        InitializeComponent();
        for (var i = 0; i < profiles.Profiles.Count; i++)
            ProfilesList.Items.Add(new ProfileRow(profiles.Profiles[i], i == profiles.ActiveIndex,
                isWarm?.Invoke(profiles.Profiles[i]) ?? false));
        Diagnostics.AppLog.Detail("profile-switch", $"event=load outcome=success count={profiles.Profiles.Count}");
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
