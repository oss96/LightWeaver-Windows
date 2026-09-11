using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LightWeaver.Downloads;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>Per-row view model — progress ticks update these properties in place so the
/// list never rebuilds mid-download (rows keep UIA identity and scroll position).</summary>
public sealed class DownloadRow : ObservableObject
{
    private string _statusText = "";
    private double _progressPercent;
    private DownloadStatus _status;

    public DownloadRow(DownloadItem item) => Item = item;

    public DownloadItem Item { get; private set; }

    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle ?? "";
    public bool HasSubtitle => Subtitle.Length > 0;
    public string? PosterUrl => Item.PosterUrl;

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    public DownloadStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    public bool IsProgressVisible => Status is DownloadStatus.Downloading or DownloadStatus.Paused;
    public bool CanPlay => Status == DownloadStatus.Completed;
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanCancel => Status != DownloadStatus.Completed;
    public bool CanDelete => Status == DownloadStatus.Completed;

    public void Update(DownloadItem item)
    {
        Item = item;
        ProgressPercent = item.Progress * 100;
        Status = item.Status;
        StatusText = item.Status switch
        {
            DownloadStatus.Queued => $"Queued · {item.Resolution}",
            DownloadStatus.Downloading when item.TotalBytes > 0 => FormattableString.Invariant(
                $"Downloading · {item.Progress * 100:0}% · {DownloadManager.FormatBytes(item.BytesDownloaded)} / {DownloadManager.FormatBytes(item.TotalBytes)}"),
            DownloadStatus.Downloading =>
                $"Downloading · {DownloadManager.FormatBytes(item.BytesDownloaded)}",
            DownloadStatus.Paused when item.TotalBytes > 0 => FormattableString.Invariant(
                $"Paused · {item.Progress * 100:0}% · {DownloadManager.FormatBytes(item.BytesDownloaded)} / {DownloadManager.FormatBytes(item.TotalBytes)}"),
            DownloadStatus.Paused => $"Paused · {DownloadManager.FormatBytes(item.BytesDownloaded)}",
            DownloadStatus.Completed =>
                $"Completed · {item.Resolution} · {DownloadManager.FormatBytes(item.TotalBytes)}",
            DownloadStatus.Failed => $"Failed · {item.ErrorMessage ?? "unknown error"}",
            _ => "",
        };
    }
}

/// <summary>Downloads management screen (Phase 7 M20).</summary>
public partial class DownloadsView : UserControl
{
    private readonly DownloadManager _manager;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly ObservableCollection<DownloadRow> _rows = [];
    private readonly DispatcherTimer _refresh;
    private bool _dirty;

    /// <summary>Play a completed download (MainWindow resolves local-first playback).</summary>
    public event Action<DownloadItem>? PlayRequested;

    public DownloadsView(DownloadManager manager, Func<string, string, Task<bool>> confirm)
    {
        InitializeComponent();
        _manager = manager;
        _confirm = confirm;
        Rows.ItemsSource = _rows;

        // Progress events arrive from download threads many times a second; coalesce
        // to a 300 ms UI tick so binding churn stays negligible.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refresh.Tick += (_, _) =>
        {
            if (_dirty)
            {
                _dirty = false;
                SyncRows();
            }
        };
        _refresh.Start();
        _manager.DownloadsChanged += () => _dirty = true;
        SyncRows();
        Diagnostics.AppLog.Detail("downloads-view", $"event=load outcome=success count={_rows.Count}");
    }

    /// <summary>Keyed reconcile: update rows in place, add new, drop removed — order by
    /// enqueue time, matching the manager's snapshot.</summary>
    private void SyncRows()
    {
        var items = _manager.Snapshot();
        var byId = _rows.ToDictionary(r => r.Item.ItemId);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (byId.TryGetValue(item.ItemId, out var row))
            {
                row.Update(item);
                var at = _rows.IndexOf(row);
                if (at != i)
                    _rows.Move(at, i);
            }
            else
            {
                row = new DownloadRow(item);
                row.Update(item);
                _rows.Insert(Math.Min(i, _rows.Count), row);
            }
        }
        for (var i = _rows.Count - 1; i >= items.Count; i--)
            _rows.RemoveAt(i);

        var empty = _rows.Count == 0;
        if (empty)
            Empty.Show("Nothing downloaded", "Use the download button on a movie, season, or series.");
        else
            Empty.Visibility = Visibility.Collapsed;
        PageScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var anyActive = _rows.Any(r => r.CanPause);
        PauseAllButton.IsEnabled = anyActive;
        ClearCompletedButton.IsEnabled = _rows.Any(r => r.CanDelete);
        DeleteAllButton.IsEnabled = _rows.Count > 0;
    }

    private static DownloadRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as DownloadRow;

    private void OnRowPlay(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=play item={row.Item.ItemId:N}");
            PlayRequested?.Invoke(row.Item);
        }
    }

    private void OnRowPause(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=pause item={row.Item.ItemId:N}");
            _manager.Pause(row.Item.ItemId);
        }
    }

    private void OnRowResume(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=resume item={row.Item.ItemId:N}");
            _manager.Resume(row.Item.ItemId);
        }
    }

    private async void OnRowRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        // Cancelling an in-flight download is cheap to redo; deleting a finished file
        // isn't — only the latter gets a confirm.
        if (row.Item.Status == DownloadStatus.Completed
            && !await _confirm("Delete download?", $"“{row.Item.Title}” will be removed from this device."))
        {
            Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=remove outcome=cancel item={row.Item.ItemId:N}");
            return;
        }
        Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=remove outcome=confirm item={row.Item.ItemId:N}");
        _manager.Remove(row.Item.ItemId);
    }

    private void OnPauseAll(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("downloads-view", "event=interaction action=pause-all");
        _manager.PauseAll();
    }

    private void OnClearCompleted(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("downloads-view", "event=interaction action=clear-completed");
        _manager.ClearCompleted();
    }

    private async void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        if (await _confirm("Delete all downloads?",
                "Every download — including finished files — will be removed from this device."))
        {
            Diagnostics.AppLog.Detail("downloads-view", "event=interaction action=delete-all outcome=confirm");
            _manager.DeleteAll();
        }
        else
        {
            Diagnostics.AppLog.Detail("downloads-view", "event=interaction action=delete-all outcome=cancel");
        }
    }
}
