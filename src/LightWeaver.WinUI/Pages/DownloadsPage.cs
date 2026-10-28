using System.Globalization;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Pages;

/// <summary>Downloads (design 08): Downloading, Queued, Failed and Downloaded in a fixed order (two
/// columns, one below 1000 px), the quality for new downloads, a storage meter and Pause all.
/// Offline, an InfoBar explains that only downloaded titles play and running ones wait.</summary>
public sealed partial class DownloadsPage : Grid, INavPage
{
    private const double Narrow = 1000;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(400);
    private readonly IAppShell _shell;
    private readonly DownloadManager _downloads;
    private readonly SpeedMeter _speed = new();
    private readonly Dictionary<Guid, Row> _rows = [];
    private readonly HashSet<Guid> _pendingDelete = [];
    private readonly InfoBar _offline = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, Margin = new Thickness(24, 12, 24, 0) };
    private readonly TextBlock _usedText = new() { FontSize = 12 };
    private readonly TextBlock _freeText = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _diskText = new() { FontSize = 11 };
    private readonly ColumnDefinition _downloadsShare = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _otherShare = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _freeShare = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly TextBlock _qualityText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _quality;
    private readonly Button _pauseAll;
    private readonly StackPanel _left = new() { Spacing = 18 };
    private readonly StackPanel _right = new() { Spacing = 18 };
    private readonly Grid _columns = new() { ColumnSpacing = 24, Padding = new Thickness(24, 0, 24, 24) };
    private readonly StackPanel _empty;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;
    private string _layoutKey = "";
    private bool _dirty = true;
    private int _dragFrom = -1;

    public DownloadsPage(IAppShell shell)
    {
        _shell = shell;
        _downloads = shell.Downloads;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AutomationProperties.SetAutomationId(_offline, "DownloadsOffline");
        var retry = new Button { Content = L.Text("Downloads.RetryNow") };
        AutomationProperties.SetAutomationId(retry, "DownloadsRetryNow");
        retry.Click += async (_, _) => await _shell.Connectivity.RetryAsync();
        _offline.ActionButton = retry;
        Children.Add(_offline);

        var title = new TextBlock { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -10, Text = L.Text("Downloads.Title"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(title, "DownloadsTitle");
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);

        var meterBar = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Resource<Brush>("LwTrackBrush") };
        meterBar.ColumnDefinitions.Add(_downloadsShare);
        meterBar.ColumnDefinitions.Add(_otherShare);
        meterBar.ColumnDefinitions.Add(_freeShare);
        meterBar.Children.Add(new Border { Background = Ui.Resource<Brush>("LwAccentFgBrush") });
        var other = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF)) };
        SetColumn(other, 1);
        meterBar.Children.Add(other);
        var meterTop = new Grid();
        meterTop.Children.Add(_usedText);
        _freeText.Foreground = _diskText.Foreground = Ui.Resource<Brush>("LwText3Brush");
        meterTop.Children.Add(_freeText);
        var meter = new StackPanel { Width = 260, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        meter.Children.Add(meterTop);
        meter.Children.Add(meterBar);
        meter.Children.Add(_diskText);
        AutomationProperties.SetAutomationId(_usedText, "DownloadsUsed");
        AutomationProperties.SetAutomationId(_freeText, "DownloadsFree");

        var qualityContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        qualityContent.Children.Add(new TextBlock { Text = L.Text("Downloads.Quality"), FontSize = 12.5, Foreground = Ui.Resource<Brush>("LwText2Brush"), VerticalAlignment = VerticalAlignment.Center });
        qualityContent.Children.Add(_qualityText);
        var chevron = Ui.Icon(Theme.Icons.ExpandMore, 14);
        chevron.Foreground = Ui.Resource<Brush>("LwText2Brush");
        qualityContent.Children.Add(chevron);
        _quality = new Button { Style = Ui.Resource<Style>("LwButton"), Padding = new Thickness(10, 0, 8, 0), Content = qualityContent };
        AutomationProperties.SetAutomationId(_quality, "DownloadsQuality");
        _quality.Click += (_, _) => ShowQualityMenu();

        _pauseAll = new Button { Style = Ui.Resource<Style>("LwButton"), Padding = new Thickness(10, 0, 12, 0) };
        AutomationProperties.SetAutomationId(_pauseAll, "DownloadsPauseAll");
        _pauseAll.Click += (_, _) => TogglePauseAll();

        var header = new WrapPanel { HorizontalSpacing = 12, VerticalSpacing = 10, Margin = new Thickness(24, 16, 24, 16) };
        var titleSlot = new Grid { MinWidth = 220 };
        titleSlot.Children.Add(title);
        header.Children.Add(titleSlot);
        header.Children.Add(meter);
        header.Children.Add(_quality);
        header.Children.Add(_pauseAll);
        SetRow(header, 1);
        Children.Add(header);

        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _columns.Children.Add(_left);
        _columns.Children.Add(_right);
        var scroller = new ScrollViewer { Content = _columns, HorizontalScrollMode = ScrollMode.Disabled };
        AutomationProperties.SetAutomationId(scroller, "DownloadsScroller");
        SetRow(scroller, 2);
        Children.Add(scroller);

        var emptyTitle = new TextBlock { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Text = L.Text("Downloads.EmptyTitle") };
        AutomationProperties.SetAutomationId(emptyTitle, "DownloadsEmpty");
        _empty = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 80), Visibility = Visibility.Collapsed };
        _empty.Children.Add(emptyTitle);
        _empty.Children.Add(new TextBlock { FontSize = 13, MaxWidth = 380, TextWrapping = TextWrapping.WrapWholeWords, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Resource<Brush>("LwText2Brush"), Text = L.Text("Downloads.EmptyHint") });
        SetRow(_empty, 2);
        Children.Add(_empty);

        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = RefreshEvery;
        _refresh.Tick += (_, _) => { if (_dirty) Show(); };
        SizeChanged += (_, _) => { _layoutKey = ""; Show(); };
        KeyDown += OnKeyDown;
        Loaded += (_, _) =>
        {
            _downloads.DownloadsChanged += OnDownloadsChanged;
            _shell.Connectivity.Changed += OnConnectivityChanged;
            _refresh.Start();
            Show();
        };
        Unloaded += (_, _) =>
        {
            _downloads.DownloadsChanged -= OnDownloadsChanged;
            _shell.Connectivity.Changed -= OnConnectivityChanged;
            _refresh.Stop();
            foreach (var id in _pendingDelete.ToList()) CommitDelete(id);
        };
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            title.Text = L.Text("Downloads.Title");
            ((TextBlock)qualityContent.Children[0]).Text = L.Text("Downloads.Quality");
            retry.Content = L.Text("Downloads.RetryNow");
            _layoutKey = "";
            Show();
        }));
    }

    public void OnShown() => Show();

    private void OnDownloadsChanged() => _dirty = true;

    private void OnConnectivityChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { _layoutKey = ""; Show(); });

    private bool Offline => _shell.Connectivity.IsOffline;

    /// <summary>Rebuilds the sections when items move between them, else updates progress in place.</summary>
    private void Show()
    {
        _dirty = false;
        var items = _downloads.Snapshot().Where(i => !_pendingDelete.Contains(i.ItemId)).ToList();
        var sections = DownloadsModel.Sections(items);
        var narrow = ActualWidth > 0 && ActualWidth < Narrow;
        var key = (narrow ? "n" : "w") + Offline + string.Join(";", sections.Select(s => s.Section + ":" + string.Join(",", s.Items.Select(i => i.ItemId.ToString("N") + i.Status))));
        if (key != _layoutKey)
        {
            _layoutKey = key;
            Rebuild(sections, narrow);
        }
        else
        {
            foreach (var item in items)
                if (_rows.TryGetValue(item.ItemId, out var row)) row.Update(item, _speed.Sample(item.ItemId, item.BytesDownloaded, DateTimeOffset.Now), Offline);
        }
        UpdateHeader(items);
    }

    private void Rebuild(IReadOnlyList<(DownloadSection Section, IReadOnlyList<DownloadItem> Items)> sections, bool narrow)
    {
        _rows.Clear();
        _left.Children.Clear();
        _right.Children.Clear();
        _columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _columns.ColumnSpacing = narrow ? 0 : 24;
        foreach (var (section, items) in sections)
        {
            var target = section == DownloadSection.Downloaded && !narrow ? _right : _left;
            target.Children.Add(Section(section, items));
        }
        _empty.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _offline.IsOpen = Offline;
        _offline.Title = L.Format("Errors.CantReach", _shell.ServerName);
        _offline.Message = L.Text("Downloads.OfflineMessage");
    }

    private StackPanel Section(DownloadSection section, IReadOnlyList<DownloadItem> items)
    {
        var name = new TextBlock { Text = SectionName(section), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(name, "DownloadsSection" + section);
        var count = section == DownloadSection.Downloaded
            ? $"{items.Count.ToString(CultureInfo.CurrentUICulture)} · {DownloadManager.FormatBytes(items.Sum(i => FileBytes(i)))}"
            : items.Count.ToString(CultureInfo.CurrentUICulture);
        var header = new Grid { Padding = new Thickness(4, 0, 4, 0), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(name);
        var countText = new TextBlock { Text = count, FontSize = 12, Foreground = Ui.Resource<Brush>("LwText3Brush"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(countText, "DownloadsCount" + section);
        SetColumn(countText, 1);
        header.Children.Add(countText);
        if (SectionAction(section) is { } action)
        {
            var button = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12, Foreground = Ui.Resource<Brush>("LwAccentFgBrush"), Content = action.Label };
            AutomationProperties.SetAutomationId(button, "DownloadsAction" + section);
            button.Click += (_, _) => action.Run();
            SetColumn(button, 3);
            header.Children.Add(button);
        }

        var box = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var row = new Row(this, items[i], section, i);
            _rows[items[i].ItemId] = row;
            row.Update(items[i], _speed.Sample(items[i].ItemId, items[i].BytesDownloaded, DateTimeOffset.Now), Offline);
            box.Children.Add(row.Root);
        }
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Ui.Resource<Brush>("LwBoxBrush"),
            BorderBrush = Ui.Resource<Brush>("LwStrokeBrush"),
            BorderThickness = new Thickness(1),
            Child = box,
        };
        AutomationProperties.SetAutomationId(frame, "DownloadsList" + section);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        panel.Children.Add(frame);
        return panel;
    }

    private (string Label, Action Run)? SectionAction(DownloadSection section) => section switch
    {
        DownloadSection.Downloading when !Offline => (L.Text("Downloads.PauseAll"), _downloads.PauseAll),
        DownloadSection.Failed => (L.Text("Downloads.RetryAll"), () => { foreach (var i in _downloads.Snapshot().Where(i => i.Status == DownloadStatus.Failed)) _downloads.Resume(i.ItemId); }),
        DownloadSection.Downloaded when !Offline => (L.Text("Downloads.DeleteWatched"), () => _ = DeleteWatchedAsync()),
        _ => null,
    };

    private static string SectionName(DownloadSection section) => section switch
    {
        DownloadSection.Downloading => L.Text("Downloads.SectionDownloading"),
        DownloadSection.Queued => L.Text("Downloads.SectionQueued"),
        DownloadSection.Failed => L.Text("Downloads.SectionFailed"),
        _ => L.Text("Downloads.SectionDownloaded"),
    };

    private void UpdateHeader(IReadOnlyList<DownloadItem> items)
    {
        var quality = DownloadQualityStore.Load();
        _qualityText.Text = DownloadQuality.ById(quality) is { } q ? (q.IsOriginal ? q.Label : $"{q.Label} · {q.Note}") : L.Text("Downloads.AskEachTime");
        AutomationProperties.SetName(_quality, L.Format("Downloads.QualityIs", _qualityText.Text));

        var running = items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        var paused = items.Any(i => i.Status == DownloadStatus.Paused);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Ui.Icon(running || !paused ? Theme.Icons.Pause : Theme.Icons.Play, 16));
        var label = running || !paused ? L.Text("Downloads.PauseAll") : L.Text("Downloads.ResumeAll");
        content.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        _pauseAll.Content = content;
        _pauseAll.IsEnabled = !Offline && (running || paused);
        AutomationProperties.SetName(_pauseAll, label);

        var used = items.Sum(FileBytes);
        _usedText.Text = L.Format("Downloads.UsedBy", DownloadManager.FormatBytes(used));
        var directory = DownloadDirectory();
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            _freeText.Text = L.Format("Downloads.Free", DownloadManager.FormatBytes(drive.AvailableFreeSpace));
            _diskText.Text = L.Format("Downloads.Disk", drive.VolumeLabel is { Length: > 0 } label2 ? $"{label2} ({drive.Name.TrimEnd('\\')})" : drive.Name.TrimEnd('\\'), DownloadManager.FormatBytes(drive.TotalSize));
            var (mine, other) = DownloadsModel.StorageShares(used, drive.TotalSize, drive.AvailableFreeSpace);
            _downloadsShare.Width = new GridLength(mine, GridUnitType.Star);
            _otherShare.Width = new GridLength(other, GridUnitType.Star);
            _freeShare.Width = new GridLength(Math.Max(0, 1 - mine - other), GridUnitType.Star);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _freeText.Text = _diskText.Text = "";
        }
    }

    private static string DownloadDirectory()
    {
        var custom = App.Settings.DownloadDirectory;
        return custom is { Length: > 0 } ? custom : DownloadStore.DefaultDirectory;
    }

    private static long FileBytes(DownloadItem item)
    {
        try { return File.Exists(item.FilePath) ? new FileInfo(item.FilePath).Length : 0; }
        catch (IOException) { return 0; }
    }

    private void ShowQualityMenu()
    {
        var current = DownloadQualityStore.Load();
        var menu = FastUi.Flyout(new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        var heading = new MenuFlyoutItem { Text = L.Text("Downloads.QualityHeading"), IsEnabled = false };
        menu.Items.Add(heading);
        foreach (var quality in DownloadQuality.All)
        {
            var item = new RadioMenuFlyoutItem { Text = quality.Label, KeyboardAcceleratorTextOverride = quality.Note, IsChecked = quality.Id == current, GroupName = "DownloadQuality" };
            AutomationProperties.SetAutomationId(item, "DownloadsQualityOption");
            AutomationProperties.SetName(item, $"{quality.Label} · {quality.Note}");
            item.Click += (_, _) => { DownloadQualityStore.Save(quality.Id); Show(); };
            menu.Items.Add(item);
            if (quality.IsOriginal) menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var ask = new RadioMenuFlyoutItem { Text = L.Text("Downloads.AskEachTime"), IsChecked = current == DownloadQuality.AskId, GroupName = "DownloadQuality" };
        AutomationProperties.SetAutomationId(ask, "DownloadsQualityAsk");
        ask.Click += (_, _) => { DownloadQualityStore.Save(DownloadQuality.AskId); Show(); };
        menu.Items.Add(ask);
        menu.ShowAt(_quality);
    }

    private void TogglePauseAll()
    {
        var items = _downloads.Snapshot();
        if (items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued)) _downloads.PauseAll();
        else foreach (var paused in items.Where(i => i.Status == DownloadStatus.Paused)) _downloads.Resume(paused.ItemId);
    }

    /// <summary>Delete removes the local file with no confirmation; the row goes at once and Undo
    /// keeps the file for 6 s.</summary>
    private void Delete(IReadOnlyList<DownloadItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items) _pendingDelete.Add(item.ItemId);
        Show();
        var ids = items.Select(i => i.ItemId).ToList();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = Playlists.PlaylistModel.UndoTime;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { timer.Stop(); foreach (var id in ids) CommitDelete(id); };
        timer.Start();
        var message = items.Count == 1 ? L.Format("Downloads.Deleted", items[0].Title) : L.Format("Downloads.DeletedMany", items.Count);
        ToastHost.Show(ToastKind.Success, message, L.Text("Toast.Undo"), () =>
        {
            timer.Stop();
            foreach (var id in ids) _pendingDelete.Remove(id);
            _layoutKey = "";
            Show();
        });
    }

    private void CommitDelete(Guid id)
    {
        if (!_pendingDelete.Remove(id)) return;
        _speed.Forget(id);
        _downloads.Remove(id);
    }

    private async Task DeleteWatchedAsync()
    {
        var watched = new List<DownloadItem>();
        try
        {
            foreach (var item in _downloads.Snapshot().Where(i => i.Status == DownloadStatus.Completed))
                if (await _shell.Model.Jellyfin.GetItemAsync(item.ItemId) is { Played: true }) watched.Add(item);
        }
        catch (Exception ex)
        {
            _shell.ReportFailure(ex);
            return;
        }
        if (watched.Count == 0) ToastHost.Show(ToastKind.Info, L.Text("Downloads.NothingWatched"));
        else Delete(watched);
    }

    private void Play(DownloadItem item) => _shell.PlayDownload(item);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _dragFrom >= 0)
        {
            EndDrag(commit: false, to: -1);
            e.Handled = true;
        }
    }

    private StackPanel? _dragBox;
    private Border? _insertion;

    /// <summary>Reordering the queue by a row's handle: an insertion line marks the drop position
    /// and the rows stay still until the drop; Esc cancels.</summary>
    private void BeginDrag(Row row, PointerRoutedEventArgs e)
    {
        if (row.Root.Parent is not StackPanel box) return;
        _dragFrom = box.Children.IndexOf(row.Root);
        _dragBox = box;
        row.Root.Opacity = 0.6;
        row.Handle.CapturePointer(e.Pointer);
        Focus(FocusState.Programmatic);
    }

    private void MoveDrag(PointerRoutedEventArgs e)
    {
        if (_dragFrom < 0 || _dragBox is null) return;
        var to = DropIndex(e.GetCurrentPoint(_dragBox).Position.Y);
        _insertion ??= new Border { Height = 2, Margin = new Thickness(8, -1, 8, -1), Background = Ui.Resource<Brush>("LwAccentFgBrush"), IsHitTestVisible = false };
        if (_insertion.Parent is Panel old) old.Children.Remove(_insertion);
        _dragBox.Children.Insert(Math.Min(to, _dragBox.Children.Count), _insertion);
    }

    private int DropIndex(double y)
    {
        var rows = _dragBox!.Children.Where(c => c != _insertion).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var top = rows[i].TransformToVisual(_dragBox).TransformPoint(default).Y;
            if (y < top + rows[i].ActualSize.Y / 2) return i;
        }
        return rows.Count;
    }

    private void EndDrag(bool commit, int to)
    {
        if (_dragBox is not null && _insertion?.Parent == _dragBox) _dragBox.Children.Remove(_insertion);
        var from = _dragFrom;
        _dragFrom = -1;
        if (_dragBox is not null && from >= 0 && from < _dragBox.Children.Count) _dragBox.Children[from].Opacity = 1;
        var box = _dragBox;
        _dragBox = null;
        if (!commit || box is null) return;
        var queue = _downloads.Snapshot().Where(i => i.Status == DownloadStatus.Queued).OrderBy(i => i.AddedTimestampMs).ToList();
        if (from >= queue.Count) return;
        var target = to > from ? to - 1 : to;
        if (target != from) _downloads.MoveInQueue(queue[from].ItemId, target);
    }

    /// <summary>One download row: thumbnail, title and year or episode, progress, a status line and
    /// the buttons its state allows.</summary>
    private sealed class Row
    {
        private readonly DownloadsPage _page;
        private readonly TextBlock _meta = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _languages = new() { FontSize = 12, Foreground = Ui.Resource<Brush>("LwText3Brush"), TextWrapping = TextWrapping.Wrap };
        private readonly Grid _track = new() { Height = 4, CornerRadius = new CornerRadius(2) };
        private readonly ColumnDefinition _done = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _rest = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly Border _fill = new();
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        private DownloadStatus? _shownStatus;
        private bool _shownOffline;
        private Button? _playWhileDownloading;

        public Row(DownloadsPage page, DownloadItem item, DownloadSection section, int index)
        {
            _page = page;
            Root = new Grid { Padding = new Thickness(12, 10, 10, 10), ColumnSpacing = 12, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            if (index > 0) Root.BorderBrush = Ui.Resource<Brush>("LwSeparatorBrush");
            Root.BorderThickness = new Thickness(0, index > 0 ? 1 : 0, 0, 0);
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Handle = new Border { Width = 16, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = section == DownloadSection.Queued ? Visibility.Visible : Visibility.Collapsed };
            var grip = Ui.Icon(Theme.Icons.Drag, 16);
            grip.Foreground = Ui.Resource<Brush>("LwText3Brush");
            Handle.Child = grip;
            Handle.PointerPressed += (_, e) => page.BeginDrag(this, e);
            Handle.PointerMoved += (_, e) => page.MoveDrag(e);
            Handle.PointerReleased += (_, e) => page.EndDrag(commit: true, to: page._dragBox is null ? -1 : page.DropIndex(e.GetCurrentPoint(page._dragBox).Position.Y));
            Handle.PointerCaptureLost += (_, _) => { if (page._dragFrom >= 0) page.EndDrag(commit: false, to: -1); };
            Root.Children.Add(Handle);

            var thumb = new Border { Width = 80, Height = 45, CornerRadius = new CornerRadius(4), Background = Ui.Resource<Brush>("LwPlaceholderBrush") };
            var image = new Image { Stretch = Stretch.UniformToFill };
            thumb.Child = image;
            SetColumn(thumb, 1);
            Root.Children.Add(thumb);
            if (item.PosterUrl is { } url && page._shell.Model.FindSessionForUrl(url) is { } session)
                _ = LoadAsync(image, session, url);

            var title = new TextBlock { Text = item.Title, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleLine.Children.Add(title);
            if (item.Subtitle is { Length: > 0 } subtitle)
                titleLine.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Ui.Resource<Brush>("LwText3Brush"), VerticalAlignment = VerticalAlignment.Bottom });
            _track.ColumnDefinitions.Add(_done);
            _track.ColumnDefinitions.Add(_rest);
            _track.Background = Ui.Resource<Brush>("LwTrackBrush");
            _track.Children.Add(_fill);
            var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(titleLine);
            text.Children.Add(_track);
            text.Children.Add(_meta);
            text.Children.Add(_languages);
            SetColumn(text, 2);
            Root.Children.Add(text);
            SetColumn(_buttons, 3);
            Root.Children.Add(_buttons);
            AutomationProperties.SetAutomationId(_meta, "DownloadRowStatus");
            Item = item;
        }

        public Grid Root { get; }

        public Border Handle { get; }

        public DownloadItem Item { get; private set; }

        public void Update(DownloadItem item, double bytesPerSecond, bool offline)
        {
            Item = item;
            var section = SectionOf(item);
            _languages.Text = DownloadLanguages.FormatSummary(item);
            AutomationProperties.SetAutomationId(_languages, "DownloadRowLanguages");
            _track.Visibility = section == DownloadSection.Downloading ? Visibility.Visible : Visibility.Collapsed;
            var progress = Math.Clamp(item.Progress, 0, 1);
            _done.Width = new GridLength(progress, GridUnitType.Star);
            _rest.Width = new GridLength(1 - progress, GridUnitType.Star);
            _fill.Background = item.Status == DownloadStatus.Downloading && !offline
                ? Ui.Resource<Brush>("LwAccentFgBrush")
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            _meta.Text = section switch
            {
                DownloadSection.Failed => DownloadsModel.FailedLine(item),
                DownloadSection.Downloaded => DownloadsModel.DoneLine(item, FileBytes(item), DateTimeOffset.Now),
                _ => DownloadsModel.ProgressLine(item, bytesPerSecond, offline),
            };
            _meta.Foreground = Ui.Resource<Brush>(section switch
            {
                DownloadSection.Failed => "LwErrorBrush",
                DownloadSection.Queued => "LwText3Brush",
                _ => "LwText2Brush",
            });
            var name = item.Subtitle is { Length: > 0 } s ? $"{item.Title}, {s}" : item.Title;
            AutomationProperties.SetName(_meta, $"{name}: {_meta.Text}");
            if (_shownStatus != item.Status || _shownOffline != offline) BuildButtons(item, offline);
            if (_playWhileDownloading is { } play)
            {
                var hint = DownloadsModel.PlayWhileDownloadingHint(item);
                ToolTipService.SetToolTip(play, hint);
                AutomationProperties.SetHelpText(play, hint);
            }
        }

        private static DownloadSection SectionOf(DownloadItem item) => DownloadsModel.SectionOf(item);

        private void BuildButtons(DownloadItem item, bool offline)
        {
            _shownStatus = item.Status;
            _shownOffline = offline;
            _playWhileDownloading = null;
            _buttons.Children.Clear();
            var name = item.Subtitle is { Length: > 0 } s ? $"{item.Title}, {s}" : item.Title;
            switch (item.Status)
            {
                case DownloadStatus.Downloading:
                    _playWhileDownloading = Add("DownloadPlay", Theme.Icons.Play, L.Format("Downloads.PlayItem", name), () => _page.Play(item), L.Text("Downloads.Play"), accent: true);
                    if (!offline) Add("DownloadPause", Theme.Icons.Pause, L.Format("Downloads.PauseItem", name), () => _page._downloads.Pause(item.ItemId));
                    Add("DownloadCancel", Theme.Icons.Close, L.Format("Downloads.CancelItem", name), () => _page._downloads.Remove(item.ItemId));
                    break;
                case DownloadStatus.Paused:
                    Add("DownloadResume", Theme.Icons.Play, L.Format("Downloads.ResumeItem", name), () => _page._downloads.Resume(item.ItemId));
                    Add("DownloadCancel", Theme.Icons.Close, L.Format("Downloads.CancelItem", name), () => _page._downloads.Remove(item.ItemId));
                    break;
                case DownloadStatus.Queued:
                    Add("DownloadRemoveQueued", Theme.Icons.Close, L.Format("Downloads.RemoveFromQueue", name), () => _page._downloads.Remove(item.ItemId));
                    break;
                case DownloadStatus.Failed:
                    Add("DownloadRetry", Theme.Icons.Refresh, L.Format("Downloads.RetryItem", name), () => _page._downloads.Resume(item.ItemId), L.Text("Downloads.Retry"));
                    Add("DownloadRemoveFailed", Theme.Icons.Close, L.Format("Downloads.RemoveItem", name), () => _page._downloads.Remove(item.ItemId));
                    break;
                default:
                    Add("DownloadPlay", Theme.Icons.Play, L.Format("Downloads.PlayItem", name), () => _page.Play(item), L.Text("Downloads.Play"), accent: true);
                    Add("DownloadDelete", Theme.Icons.Delete, L.Format("Downloads.DeleteItem", name), () => _page.Delete([item]));
                    break;
            }
        }

        private Button Add(string automationId, string glyph, string name, Action action, string? label = null, bool accent = false)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(Ui.Icon(glyph, 16));
            if (label is not null) content.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Style = Ui.Resource<Style>(accent ? "LwAccentButton" : label is not null ? "LwButton" : "LwSubtleButton"),
                Height = 28,
                MinWidth = 28,
                Padding = label is null ? new Thickness(0) : new Thickness(7, 0, 10, 0),
                Content = content,
            };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => action();
            _buttons.Children.Add(button);
            return button;
        }

        private static async Task LoadAsync(Image image, JellyfinService session, string url)
        {
            var load = ImageLoader.LoadAsync(session, url, 160);
            var fromMemory = load.IsCompleted;
            if (await load is { } bitmap) ImageQueue.Land(fromMemory, () => image.Source = bitmap);
        }
    }
}
