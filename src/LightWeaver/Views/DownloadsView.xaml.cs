using LightWeaver.Localization;
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
    private string _speedText = "";
    private string _playbackText = "";
    private string _playToolTip = "";
    private bool _isIndeterminate;
    private bool _canPlay;
    private bool _isStarting;

    public DownloadRow(DownloadItem item) { Item = item; Update(item); }

    public DownloadItem Item { get; private set; }

    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle ?? "";
    public bool HasSubtitle => Subtitle.Length > 0;
    public string LanguageSummary => DownloadLanguages.FormatSummary(Item);
    public string? PosterUrl => Item.PosterUrl;

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string PlaybackText { get => IsStarting ? L.Text("Downloads.StartingPlayback") : _playbackText; private set => SetProperty(ref _playbackText, value); }
    public string PlayToolTip { get => _playToolTip; private set => SetProperty(ref _playToolTip, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }

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
    public bool CanPlay { get => _canPlay && !IsStarting; private set => SetProperty(ref _canPlay, value); }
    public bool IsStarting
    {
        get => _isStarting;
        set
        {
            if (!SetProperty(ref _isStarting, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(PlaybackText));
        }
    }
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanCancel => Status != DownloadStatus.Completed;
    public bool CanDelete => Status == DownloadStatus.Completed;

    private static string DisplayResolution(string resolution) => resolution == "Original"
        ? L.Text("DownloadsView.Original.b0322603") : resolution;

    public void Update(DownloadItem item)
    {
        Item = item;
        OnPropertyChanged(nameof(LanguageSummary));
        var total = item.TotalBytes > 0 ? item.TotalBytes : item.EstimatedTotalBytes;
        var estimated = item.TotalBytes <= 0 && total > 0;
        var fraction = total > 0 ? (double)item.BytesDownloaded / total : 0;
        ProgressPercent = item.Status == DownloadStatus.Completed ? 100 : Math.Clamp(fraction * 100, 0, 99);
        Status = item.Status;
        IsIndeterminate = Status == DownloadStatus.Downloading && (total <= 0 || item.VideoCompleted);
        CanPlay = Status == DownloadStatus.Completed || item.VideoCompleted
            || Status == DownloadStatus.Downloading && item.BytesDownloaded >= DownloadManager.MinPartialStreamBytes;
        var sizeText = total > 0
            ? FormattableString.Invariant($"{(estimated ? "~" : "")}{ProgressPercent:0}% · {DownloadManager.FormatBytes(item.BytesDownloaded)} / {(estimated ? "~" : "")}{DownloadManager.FormatBytes(total)}")
            : L.Format("Downloads.UnknownTotal", DownloadManager.FormatBytes(item.BytesDownloaded));
        StatusText = item.Status switch
        {
            DownloadStatus.Downloading when item.VideoCompleted => L.Format("Downloads.FinishingSubtitles", DisplayResolution(item.Resolution)),
            DownloadStatus.Queued => L.Format("Downloads.Queued", DisplayResolution(item.Resolution)),
            DownloadStatus.Downloading => L.Format("Downloads.Downloading", DisplayResolution(item.Resolution), sizeText),
            DownloadStatus.Paused => L.Format("Downloads.Paused", DisplayResolution(item.Resolution), sizeText),
            DownloadStatus.Completed =>
                L.Format("DownloadsView.Completed.fb232f94", DisplayResolution(item.Resolution), DownloadManager.FormatBytes(item.TotalBytes)),
            DownloadStatus.Failed => L.Format("DownloadsView.Failed.c1793f06", item.ErrorMessage ?? L.Text("DownloadsView.UnknownError.11276586")),
            _ => "",
        };
        var speed = CurrentSpeed(item);
        SpeedText = Status == DownloadStatus.Downloading
            ? speed > 0 ? L.Format("Downloads.Speed", DownloadManager.FormatBytes((long)speed)) : L.Text("Downloads.WaitingData")
            : "";
        if (Status == DownloadStatus.Downloading && item.VideoCompleted)
            SpeedText = L.Text("Downloads.SavingSubtitles");
        if (speed > 0 && total > item.BytesDownloaded)
            SpeedText += L.Format("Downloads.TimeLeft", estimated ? "~" : "", FormatDuration((total - item.BytesDownloaded) / speed));
        var duration = item.RuntimeTicks is > 0 ? item.RuntimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
        var prefix = duration > 0 && total > 0 && item.BytesDownloaded > 0
            ? L.Format("Downloads.DownloadedDuration", FormatDuration(duration * Math.Clamp(fraction, 0, 1))) : "";
        PlaybackText = Status switch
        {
            _ when item.VideoCompleted && item.Status != DownloadStatus.Completed =>
                L.Text("Downloads.VideoReadySubtitlesPending"),
            DownloadStatus.Completed => duration > 0 ? L.Format("Downloads.ReadyDuration", FormatDuration(duration)) : L.Text("Downloads.ReadyOffline"),
            DownloadStatus.Downloading when _canPlay => L.Format("Downloads.PlayWhileDownloading", prefix),
            DownloadStatus.Downloading => L.Format("Downloads.PreparingPlayback", DownloadManager.FormatBytes(item.BytesDownloaded), DownloadManager.FormatBytes(DownloadManager.MinPartialStreamBytes)),
            DownloadStatus.Paused => L.Text("Downloads.ResumeToPlay"),
            DownloadStatus.Failed => L.Text("Downloads.RetryToPlay"),
            _ => L.Text("Downloads.WaitingSlot"),
        };
        PlayToolTip = Status == DownloadStatus.Completed || item.VideoCompleted ? L.Text("Downloads.PlayDownloaded")
            : _canPlay ? L.Text("Downloads.PartialPlayTooltip")
            : PlaybackText;
    }

    internal static double CurrentSpeed(DownloadItem item)
        => item.Status == DownloadStatus.Downloading && item.ProgressTimestamp > 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(item.ProgressTimestamp) < TimeSpan.FromSeconds(2)
            ? item.BytesPerSecond : 0;

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds / 2));
        if (duration.TotalHours >= 1)
            return L.Format("Downloads.DurationHours", (int)duration.TotalHours, duration.Minutes);
        return duration.TotalMinutes >= 1
            ? L.Format("Downloads.DurationMinutes", (int)duration.TotalMinutes, duration.Seconds)
            : L.Format("Downloads.DurationSeconds", Math.Max(1, duration.Seconds));
    }
}

/// <summary>Downloads management screen (Phase 7 M20).</summary>
public partial class DownloadsView : UserControl
{
    private readonly DownloadManager _manager;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly ObservableCollection<DownloadRow> _rows = [];
    private readonly DispatcherTimer _refresh;
    private int _dirty;

    /// <summary>Play a download (MainWindow resolves local-first playback: completed from the
    /// file, still running from the loopback partial stream).</summary>
    public event Func<DownloadItem, Task>? PlayRequested;

    public DownloadsView(DownloadManager manager, Func<string, string, Task<bool>> confirm)
    {
        InitializeComponent();
        LiveText.Watch(this);
        _manager = manager;
        _confirm = confirm;
        Rows.ItemsSource = _rows;

        // Progress events arrive from download threads many times a second; coalesce
        // to a 300 ms UI tick so binding churn stays negligible.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refresh.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || _rows.Any(r => r.Status == DownloadStatus.Downloading))
            {
                SyncRows();
            }
        };
        Loaded += (_, _) =>
        {
            _manager.DownloadsChanged += OnDownloadsChanged;
            SyncRows();
            _refresh.Start();
        };
        Unloaded += (_, _) =>
        {
            _refresh.Stop();
            _manager.DownloadsChanged -= OnDownloadsChanged;
        };
        SyncRows();
        LiveText.Observe(this, (_, _) => SyncRows());
        Diagnostics.AppLog.Detail("downloads-view", $"event=load outcome=success count={_rows.Count}");
    }

    private void OnDownloadsChanged() => Interlocked.Exchange(ref _dirty, 1);

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
            Empty.Show(() => L.Text("DownloadsView.NothingDownloaded.5a0d555c"), () => L.Text("DownloadsView.UseTheDownloadButtonOnAMovie.05f30ee0"));
        else
            Empty.Visibility = Visibility.Collapsed;
        PageScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var anyActive = _rows.Any(r => r.CanPause);
        PauseAllButton.IsEnabled = anyActive;
        ClearCompletedButton.IsEnabled = _rows.Any(r => r.CanDelete);
        DeleteAllButton.IsEnabled = _rows.Count > 0;
        var active = items.Count(i => i.Status == DownloadStatus.Downloading);
        var queued = items.Count(i => i.Status == DownloadStatus.Queued);
        var ready = items.Count(i => i.Status == DownloadStatus.Completed);
        Summary.Text = L.Format("Downloads.Summary", active, queued, ready);
        SummarySpeed.Text = active > 0
            ? L.Format("Downloads.TotalSpeed", DownloadManager.FormatBytes((long)items.Sum(DownloadRow.CurrentSpeed))) : "";
    }

    private static DownloadRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as DownloadRow;

    private async void OnRowPlay(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { CanPlay: true } row)
        {
            Diagnostics.AppLog.Detail("downloads-view", $"event=interaction action=play item={row.Item.ItemId:N}");
            row.IsStarting = true;
            try
            {
                if (PlayRequested is { } play) await play(row.Item);
            }
            finally { row.IsStarting = false; }
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
            && !await _confirm(L.Text("DownloadsView.DeleteDownload.a54b0aa3"), L.Format("DownloadsView.WillBeRemovedFromThisDevice.2723a2bd", row.Item.Title)))
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
        if (await _confirm(L.Text("DownloadsView.DeleteAllDownloads.800e08eb"),
                L.Text("DownloadsView.EveryDownloadIncludingFinishedFilesWillBeRemovedFromThisDevice.de0a58a6")))
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
