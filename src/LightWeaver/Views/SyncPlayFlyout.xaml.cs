using LightWeaver.Localization;
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
        LiveText.Watch(this);
        _player = player;
        _list = list;
        _join = join;
        _leave = leave;
        _create = create;
        _player.PropertyChanged += OnPlayerChanged;
        Loaded += async (_, _) => await RefreshAsync();

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseRequested?.Invoke(); } };
        UpdateState();
        LiveText.Observe(this, (_, _) => { if (!_closed) UpdateState(); });
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
        GroupStateText.Text = PlayerViewModel.SyncPlayStateLabel(group?.State);
        ParticipantsText.Text = group is null ? "" : string.Join(Environment.NewLine, group.Participants);
        if (_player.SyncPlayPending)
            LiveText.Bind(StatusText, "Text", () => L.Text("SyncPlayFlyout.WaitingForTheServer.7c137dd1"));
        else if (!_busy && StatusText.Text.Length == 0)
            LiveText.Bind(StatusText, "Text", () => group is null ? L.Text("SyncPlayFlyout.ChooseAGroupOrCreateOne.fa38305d") : L.Text("SyncPlayFlyout.YouAreInThisGroup.8bb22d79"));
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
        LiveText.Bind(StatusText, "Text", () => L.Text("SyncPlayFlyout.LookingForGroups.684ed5ed"));
        UpdateState();
        try
        {
            var groups = await _list();
            if (_closed) return;
            GroupsList.ItemsSource = groups.Select(g => new GroupRow(g)).ToArray();
            LiveText.Bind(StatusText, "Text", () => groups.Count == 0 ? L.Text("SyncPlayFlyout.NoGroupsAreAvailableCreateOneTo.c65147ff") : L.Text("SyncPlayFlyout.ChooseAGroupToJoin.29471076"));
        }
        catch (Exception)
        {
            if (_closed) return;
            GroupsList.ItemsSource = null;
            LiveText.Bind(StatusText, "Text", () => L.Text("SyncPlayFlyout.CouldNotLoadGroupsCheckTheConnection.c7a23786"));
        }
        finally { _busy = false; if (!_closed) UpdateState(); }
    }

    private async Task RunAsync(Func<Task<bool>> operation, Func<string> failed)
    {
        if (_busy || _closed) return;
        _busy = true;
        LiveText.Bind(StatusText, "Text", () => L.Text("SyncPlayFlyout.ContactingTheServer.4f962b7c"));
        UpdateState();
        try
        {
            var accepted = await operation();
            if (!_closed) LiveText.Bind(StatusText, "Text", () => accepted ? "" : failed());
        }
        catch (Exception) { if (!_closed) LiveText.Bind(StatusText, "Text", () => failed()); }
        finally { _busy = false; if (!_closed) UpdateState(); }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is GroupRow row)
            await RunAsync(() => _join(row.Group.GroupId), () => L.Text("SyncPlayFlyout.CouldNotJoinTheGroupRefreshAnd.edfc697d"));
    }
    private async void OnLeave(object sender, RoutedEventArgs e)
        => await RunAsync(async () => { await _leave(); return true; }, () => L.Text("SyncPlayFlyout.CouldNotLeaveTheGroupTryAgain.ea2628e2"));
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0) { LiveText.Bind(StatusText, "Text", () => L.Text("SyncPlayFlyout.EnterANameForTheGroup.9d97c2b0")); GroupNameBox.Focus(); return; }
        await RunAsync(() => _create(name), () => L.Text("SyncPlayFlyout.CouldNotCreateTheGroupCheckYour.8c060dc3"));
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private sealed record GroupRow(SyncPlayGroupSummary Group)
    {
        public string GroupName => string.IsNullOrWhiteSpace(Group.GroupName) ? L.Text("SyncPlayFlyout.UnnamedGroup.f3cd2d6e") : Group.GroupName;
        public string ParticipantLabel => Group.Participants.Count == 1 ? L.Text("SyncPlayFlyout.1Participant.9336161c") : L.Format("SyncPlayFlyout.Participants.a12917fe", Group.Participants.Count);
    }
}
