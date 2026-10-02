using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

public partial class SyncPlayFlyout : UserControl, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly Func<Task<IReadOnlyList<SyncPlayGroupSummary>>> _list;
    private readonly Func<Guid, Task<bool>> _join;
    private readonly Func<Task> _leave;
    private readonly Func<string, Task<bool>> _create;
    private bool _busy;
    private bool _closed;
    private bool _focusWhenReady;

    public SyncPlayFlyout(PlayerViewModel player,
        Func<Task<IReadOnlyList<SyncPlayGroupSummary>>> list, Func<Guid, Task<bool>> join,
        Func<Task> leave, Func<string, Task<bool>> create)
    {
        InitializeComponent();
        _player = player;
        _list = list;
        _join = join;
        _leave = leave;
        _create = create;
        _player.PropertyChanged += OnPlayerChanged;
        Loaded += async (_, _) => await RefreshAsync();

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseRequested?.Invoke(); } };
        UpdateState();
    }

    public event Action? CloseRequested;

    public void FocusFirstControl()
    {
        if (_closed) return;
        var target = _player.SyncPlay is not null ? LeaveButton
            : _player.SyncPlayPending ? CancelJoinButton : (Control)GroupNameBox;
        _focusWhenReady = !target.Focus();
        if (_focusWhenReady) Focus();
    }

    public void Dispose()
    {
        _closed = true;
        _player.PropertyChanged -= OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.SyncPlay) or nameof(PlayerViewModel.SyncPlayPending))
        {
            StatusText.Text = "";
            UpdateState();
        }
    }

    private void UpdateState()
    {
        var group = _player.SyncPlay;
        CurrentGroupPanel.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;
        AvailablePanel.Visibility = group is null ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = group is null && !_player.SyncPlayPending ? Visibility.Visible : Visibility.Collapsed;
        CancelJoinButton.Visibility = group is null && _player.SyncPlayPending ? Visibility.Visible : Visibility.Collapsed;
        CancelJoinButton.IsEnabled = !_busy;
        GroupNameText.Text = group?.GroupName ?? "";
        GroupStateText.Text = group?.State ?? "Connected";
        ParticipantsText.Text = group is null ? "" : string.Join(Environment.NewLine, group.Participants);
        if (_player.SyncPlayPending)
            StatusText.Text = "Waiting for the server…";
        else if (!_busy && StatusText.Text.Length == 0)
            StatusText.Text = group is null ? "Choose a group or create one." : "You are in this group.";
        var ready = !_busy && !_player.SyncPlayPending;
        RefreshButton.IsEnabled = ready;
        JoinButton.IsEnabled = ready && GroupsList.SelectedItem is GroupRow;
        CreateButton.IsEnabled = ready;
        GroupNameBox.IsEnabled = ready;
        GroupsList.IsEnabled = ready;
        // Leave also cancels a pending join, so recovery never depends on closing the app.
        LeaveButton.IsEnabled = !_busy;
        if (_focusWhenReady && !_busy && IsKeyboardFocusWithin)
            FocusFirstControl();
    }

    private async Task RefreshAsync()
    {
        if (_busy || _closed || _player.SyncPlay is not null) return;
        _busy = true;
        StatusText.Text = "Looking for groups…";
        UpdateState();
        try
        {
            var groups = await _list();
            if (_closed) return;
            GroupsList.ItemsSource = groups.Select(g => new GroupRow(g)).ToArray();
            StatusText.Text = groups.Count == 0 ? "No groups are available. Create one to watch together." : "Choose a group to join.";
        }
        catch (Exception)
        {
            if (_closed) return;
            GroupsList.ItemsSource = null;
            StatusText.Text = "Could not load groups. Check the connection and try Refresh.";
        }
        finally { _busy = false; if (!_closed) UpdateState(); }
    }

    private async Task RunAsync(Func<Task<bool>> operation, string failed)
    {
        if (_busy || _closed) return;
        _busy = true;
        StatusText.Text = "Contacting the server…";
        UpdateState();
        try
        {
            var accepted = await operation();
            if (!_closed) StatusText.Text = accepted ? "" : failed;
        }
        catch (Exception) { if (!_closed) StatusText.Text = failed; }
        finally { _busy = false; if (!_closed) UpdateState(); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is GroupRow row)
            await RunAsync(() => _join(row.Group.GroupId), "Could not join the group. Refresh and try again.");
    }
    private async void OnLeave(object sender, RoutedEventArgs e)
        => await RunAsync(async () => { await _leave(); return true; }, "Could not leave the group. Try again.");
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Enter a name for the group."; GroupNameBox.Focus(); return; }
        await RunAsync(() => _create(name), "Could not create the group. Check your server permissions and try again.");
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private sealed record GroupRow(SyncPlayGroupSummary Group)
    {
        public string GroupName => string.IsNullOrWhiteSpace(Group.GroupName) ? "Unnamed group" : Group.GroupName;
        public string ParticipantLabel => $"{Group.Participants.Count} participant{(Group.Participants.Count == 1 ? "" : "s")}";
    }
}
