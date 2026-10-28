using System.ComponentModel;
using System.Globalization;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>The SyncPlay flyout (design 09), 340 px wide. Outside a group: Create group (named after
/// you) and the server's groups with members and Join. In a group: its state in one line (playing,
/// paused or waiting, and why), the members and Leave group. Only the latest refresh may fill the
/// list, so overlapping refreshes (a leave raises several) cannot append the same group twice.</summary>
internal sealed partial class SyncPlayPanel : UserControl
{
    private readonly SyncPlayCoordinator _sync;
    private readonly PlayerViewModel _vm;
    private readonly string _userName;
    private readonly StackPanel _root = new() { Spacing = 10, Width = 340 };
    private bool _busy;
    private int _generation;
    private bool _refocus;

    public SyncPlayPanel(SyncPlayCoordinator sync, PlayerViewModel vm, string userName)
    {
        _sync = sync;
        _vm = vm;
        _userName = userName;
        AutomationProperties.SetAutomationId(this, "SyncPlayPanel");
        Content = _root;
        vm.PropertyChanged += OnChanged;
        Unloaded += (_, _) => vm.PropertyChanged -= OnChanged;
        Loaded += async (_, _) => await ShowAsync();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.SyncPlay) or nameof(PlayerViewModel.SyncPlayPending))
            DispatcherQueue.TryEnqueue(() => _ = ShowAsync());
    }

    private async Task ShowAsync()
    {
        var generation = ++_generation;
        _refocus |= XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is DependencyObject focused && IsInside(focused, _root);
        _root.Children.Clear();
        var heading = new TextBlock { Text = L.Text("TopBar.SyncPlay"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        _root.Children.Add(heading);
        if (_vm.SyncPlay is { } group)
        {
            ShowGroup(group);
            return;
        }
        var create = new Button { Style = Ui.Resource<Style>("LwAccentButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Content = L.Text("SyncPlay.Create"), IsEnabled = !_vm.SyncPlayPending };
        AutomationProperties.SetAutomationId(create, "SyncPlayCreate");
        RestoreFocusTo(create);
        create.Click += async (_, _) => await RunAsync(() => _sync.CreateAsync(L.Format("SyncPlay.GroupOf", _userName)));
        _root.Children.Add(create);
        _root.Children.Add(Label(L.Text("SyncPlay.Groups")));
        var status = new TextBlock { Text = L.Text("SyncPlay.Loading"), FontSize = 12.5, Foreground = Ui.Resource<Brush>("LwText2Brush") };
        AutomationProperties.SetAutomationId(status, "SyncPlayStatus");
        _root.Children.Add(status);
        IReadOnlyList<SyncPlayGroupSummary> groups;
        try
        {
            groups = [.. (await _sync.ListGroupsAsync()).DistinctBy(g => g.GroupId)];
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Info("syncplay", $"event=list outcome=failure error={ex.GetType().Name}");
            if (generation == _generation) status.Text = L.Text("SyncPlay.ListFailed");
            return;
        }
        if (generation != _generation || _vm.SyncPlay is not null) return;
        status.Text = groups.Count == 0 ? L.Text("SyncPlay.NoGroups") : "";
        status.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var summary in groups) _root.Children.Add(GroupRow(summary));
    }

    private void ShowGroup(SyncPlayInfo group)
    {
        var name = new TextBlock { Text = group.GroupName, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(name, "SyncPlayGroupName");
        _root.Children.Add(name);
        var state = new TextBlock { Text = _vm.SyncPlayStatus, FontSize = 12.5, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.WrapWholeWords };
        AutomationProperties.SetAutomationId(state, "SyncPlayState");
        _root.Children.Add(state);
        _root.Children.Add(Label(L.Format("SyncPlay.Members", group.Participants.Count.ToString(CultureInfo.CurrentUICulture))));
        foreach (var member in group.Participants)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Avatar(member, 24));
            row.Children.Add(new TextBlock { Text = member, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetName(row, member);
            _root.Children.Add(row);
        }
        var leave = new Button { Style = Ui.Resource<Style>("LwButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Content = L.Text("SyncPlay.Leave") };
        AutomationProperties.SetAutomationId(leave, "SyncPlayLeave");
        RestoreFocusTo(leave);
        leave.Click += async (_, _) => await RunAsync(async () => { await _sync.LeaveAsync(); return true; });
        _root.Children.Add(leave);
    }

    private Grid GroupRow(SyncPlayGroupSummary summary)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = summary.GroupName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = L.Format("SyncPlay.MembersLine", summary.Participants.Count.ToString(CultureInfo.CurrentUICulture), string.Join(", ", summary.Participants)), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        var join = new Button { Style = Ui.Resource<Style>("LwButton"), Content = L.Text("SyncPlay.Join"), IsEnabled = !_busy };
        AutomationProperties.SetAutomationId(join, "SyncPlayJoin");
        AutomationProperties.SetName(join, L.Format("SyncPlay.JoinGroup", summary.GroupName));
        join.Click += async (_, _) => await RunAsync(() => _sync.JoinAsync(summary.GroupId));
        Grid.SetColumn(join, 1);
        row.Children.Add(join);
        return row;
    }

    private async Task RunAsync(Func<Task<bool>> action)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            await ShowAsync();
        }
    }

    /// <summary>A rebuild that removed the focused button hands focus to the new primary button
    /// once it can take it (Create stays disabled while a join is pending).</summary>
    private void RestoreFocusTo(Control target)
        => target.Loaded += (_, _) => { if (_refocus && target.Focus(FocusState.Programmatic)) _refocus = false; };

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Resource<Brush>("LwText3Brush"), Margin = new Thickness(0, 6, 0, 0) };
}
