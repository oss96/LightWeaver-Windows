using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Shell;

/// <summary>Downloads for every page (design 08): the engine shared with the WPF app, the bar's
/// badge, the quality preset and queueing a title, a season or a show.</summary>
public sealed partial class MainWindow
{
    private DownloadManager? _downloads;
    private AppSettings _downloadSettings = new();

    public DownloadManager Downloads => _downloads!;

    public Connectivity Connectivity => _connectivity;

    /// <summary>Starts the engine; what the last session left running resumes once signed in.</summary>
    private void InitializeDownloads()
    {
        _downloadSettings = App.Settings;
        _downloads = DownloadManager.Instance ?? new DownloadManager(
            () => Math.Max(1, _downloadSettings.MaxParallelDownloads),
            d => Model.FindSessionByServer(d.ServerUrl)?.GetDownloadUrl(d.ItemId, d.MediaSourceId, d.MaxWidth, d.VideoBitRate, d.Container, d.AudioStreamIndex),
            d => Model.FindSessionByServer(d.ServerUrl)?.AuthorizationHeader,
            (d, subtitle) => Model.FindSessionByServer(d.ServerUrl)?.GetDownloadSubtitleUrl(d.ItemId, d.MediaSourceId, subtitle.StreamIndex, subtitle.Codec));
        _downloads.DownloadsChanged += () => DispatcherQueue.TryEnqueue(UpdateDownloadsBadge);
        _downloads.Completed += d => DispatcherQueue.TryEnqueue(() => ToastHost.Show(ToastKind.Success, L.Format("Downloads.Finished", d.Title)));
        _connectivity.Changed += (_, _) =>
        {
            if (_connectivity.IsOffline) return;
            foreach (var failed in _downloads.Snapshot().Where(i => i.Status == DownloadStatus.Failed)) _downloads.Resume(failed.ItemId);
        };
        UpdateDownloadsBadge();
    }

    private bool _downloadsResumed;

    /// <summary>Restart recovery, once, after a session is signed in: what the last run left
    /// running re-queues and continues from its partial file.</summary>
    private void ResumeInterruptedDownloads()
    {
        if (_downloadsResumed || _downloads is null || !Model.Jellyfin.IsConnected) return;
        _downloadsResumed = true;
        _downloads.ResumeInterrupted();
    }

    private void UpdateDownloadsBadge()
    {
        if (_downloads is null) return;
        var count = DownloadsModel.BadgeCount(_downloads.Snapshot());
        Bar.SetDownloadsBadge(count > 0 ? count.ToString(CultureInfo.CurrentUICulture) : "");
    }

    private bool _downloadPreparing;

    /// <summary>Queues a title or batch with compatible quality and named track selections.</summary>
    public async Task DownloadAsync(MediaItem item)
    {
        if (_downloadPreparing) return;
        _downloadPreparing = true;
        try
        {
            var jellyfin = Model.Jellyfin;
            var profileKey = Model.ActiveSessionKey;
            if (!jellyfin.IsConnected || profileKey is null) return;
            IReadOnlyList<MediaItem> titles = item.Type switch
            {
                BaseItemDto_Type.Season when (item.SeriesId ?? item.ParentId) is { } seriesId => DownloadsModel.EpisodesToDownload(await jellyfin.GetEpisodesAsync(seriesId, item.Id), Downloads.IsTracked),
                BaseItemDto_Type.Series => DownloadsModel.EpisodesToDownload(await jellyfin.GetSeriesEpisodesAsync(item.Id), Downloads.IsTracked),
                _ => item.IsPlayable && !Downloads.IsTracked(item.Id) ? [item] : [],
            };
            if (profileKey != Model.ActiveSessionKey) return;
            if (titles.Count == 0)
            {
                ToastHost.Show(ToastKind.Info, L.Text("Downloads.NothingNew"));
                return;
            }
            var firstOptions = await jellyfin.GetDownloadOptionsAsync(titles[0].Id, CancellationToken.None);
            if (profileKey != Model.ActiveSessionKey) return;
            if (firstOptions.FirstOrDefault(o => o.IsOriginal) is not { } firstSource)
            {
                ToastHost.Show(ToastKind.Error, L.Text("Downloads.QueueFailed"));
                return;
            }
            var preset = DownloadQuality.ById(DownloadQualityStore.Load());
            var quality = preset ?? DownloadQuality.ById(DownloadQualityStore.DefaultId)!;
            var firstQuality = DownloadsModel.Resolve(quality, firstSource, firstSource.SourceHeight, titles[0].RuntimeTicks);
            var picks = DownloadTracks.Allow(firstSource, firstQuality,
                DownloadTracks.DefaultAudio(firstSource, _downloadSettings.PreferredAudioLanguage),
                DownloadTracks.DefaultSubtitle(firstSource, _downloadSettings.PreferredSubtitleLanguage), true,
                _downloadSettings.PreferredAudioLanguage);
            var selected = DownloadTracks.Select(firstSource, firstQuality, picks, _downloadSettings.PreferredAudioLanguage);
            if (preset is null || DownloadTracks.HasChoice(firstSource))
            {
                var choice = await AskDownloadSelectionAsync(titles.Count == 1 ? titles[0].Name : item.Name,
                    firstSource, titles[0].RuntimeTicks, quality);
                if (choice is null || profileKey != Model.ActiveSessionKey) return;
                (quality, selected) = choice.Value;
            }
            var directory = _downloadSettings.DownloadDirectory is { Length: > 0 } custom ? custom : DownloadStore.DefaultDirectory;
            var queued = 0;
            var skipped = 0;
            foreach (var title in titles)
            {
                if (profileKey != Model.ActiveSessionKey) return;
                if (Downloads.IsTracked(title.Id)) continue;
                var options = title.Id == titles[0].Id ? firstOptions : await jellyfin.GetDownloadOptionsAsync(title.Id, CancellationToken.None);
                if (profileKey != Model.ActiveSessionKey) return;
                if (options.FirstOrDefault(o => o.IsOriginal) is not { } original) { skipped++; continue; }
                var option = DownloadsModel.Resolve(quality, original, original.SourceHeight, title.RuntimeTicks);
                var resolved = title.Id == titles[0].Id ? selected : DownloadTracks.ForTitle(selected, option);
                if (resolved is null || !DownloadTracks.Fits(resolved)) { skipped++; continue; }
                Downloads.Enqueue(DownloadTracks.Build(title, resolved, jellyfin.ServerUrl!, directory, DateTimeOffset.UtcNow));
                queued++;
            }
            var message = queued == 1 && titles.Count == 1 ? L.Format("Downloads.Started", titles[0].Name) : L.Format("Downloads.QueuedMany", queued);
            if (skipped > 0) message += L.Format("Downloads.SkippedDownloads", skipped);
            ToastHost.Show(queued > 0 ? ToastKind.Download : ToastKind.Info, message);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("downloads", $"event=queue outcome=failure error={ex.GetType().Name}");
            if (Connectivity.IsUnreachable(ex)) ReportFailure(ex);
            else ToastHost.Show(ToastKind.Error, L.Text("Downloads.QueueFailed"));
        }
        finally { _downloadPreparing = false; }
    }

    private async Task<(DownloadQuality Quality, DownloadSelection Selection)?> AskDownloadSelectionAsync(
        string title, DownloadOption original, long? runtime, DownloadQuality initialQuality)
    {
        var panel = new StackPanel { Spacing = 12 };
        var quality = new ComboBox { Header = L.Text("Downloads.QualityHeading"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(quality, "DownloadQualityChoice");
        AutomationProperties.SetName(quality, L.Text("Downloads.QualityHeading"));
        foreach (var preset in DownloadQuality.All)
            quality.Items.Add(new ComboBoxItem { Content = $"{preset.Label} \u00b7 {preset.Note}", Tag = preset });
        quality.SelectedIndex = DownloadQuality.All.ToList().IndexOf(initialQuality);
        panel.Children.Add(quality);
        var audio = new ComboBox { Header = L.Text("Downloads.Audio"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = original.AudioTracks.Count > 0 };
        var subtitles = new ComboBox { Header = L.Text("Downloads.Subtitles"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = original.SubtitleTracks.Count > 0 };
        AutomationProperties.SetAutomationId(audio, "DownloadAudioChoice");
        AutomationProperties.SetName(audio, L.Text("Downloads.AudioLanguage"));
        AutomationProperties.SetAutomationId(subtitles, "DownloadSubtitleChoice");
        AutomationProperties.SetName(subtitles, L.Text("Downloads.SubtitleLanguage"));
        foreach (var track in DownloadTracks.AudioChoices(original))
            audio.Items.Add(new ComboBoxItem { Content = original.AudioTracks.Count == 0 ? L.Text("Downloads.NoAudioTracks") : track.Label(audio: true) });
        foreach (var track in DownloadTracks.SubtitleChoices(original))
            subtitles.Items.Add(new ComboBoxItem { Content = track.Label(audio: false) });
        audio.SelectedIndex = DownloadTracks.DefaultAudio(original, _downloadSettings.PreferredAudioLanguage);
        subtitles.SelectedIndex = DownloadTracks.DefaultSubtitle(original, _downloadSettings.PreferredSubtitleLanguage);
        panel.Children.Add(audio);
        panel.Children.Add(subtitles);
        var summary = new TextBlock { FontSize = 12, Foreground = Controls.Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("LwText2Brush"), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(summary);
        var dialog = new Controls.LwDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = L.Format("Downloads.AskTitle", title),
            Content = new ScrollViewer { Content = panel, MaxHeight = 400, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = L.Text("Downloads.Download"),
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "DownloadQualityDialog");
        var updating = false;
        var defaultAudio = true;
        DownloadSelection? selection = null;
        void Update()
        {
            if (updating) return;
            updating = true;
            try
            {
                var preset = (DownloadQuality)((ComboBoxItem)quality.SelectedItem).Tag;
                var option = DownloadsModel.Resolve(preset, original, original.SourceHeight, runtime);
                var picks = DownloadTracks.Allow(original, option, audio.SelectedIndex, subtitles.SelectedIndex,
                    defaultAudio, _downloadSettings.PreferredAudioLanguage);
                defaultAudio = picks.DefaultAudio;
                for (var i = 0; i < audio.Items.Count; i++) ((ComboBoxItem)audio.Items[i]).IsEnabled = picks.AudioAllowed[i];
                for (var i = 0; i < subtitles.Items.Count; i++) ((ComboBoxItem)subtitles.Items[i]).IsEnabled = picks.SubtitleAllowed[i];
                audio.SelectedIndex = picks.Audio;
                subtitles.SelectedIndex = picks.Subtitle;
                selection = DownloadTracks.Select(original, option, picks, _downloadSettings.PreferredAudioLanguage);
                dialog.IsPrimaryButtonEnabled = DownloadTracks.Fits(selection);
                summary.Text = dialog.IsPrimaryButtonEnabled
                    ? DownloadLanguages.FormatSummary(DownloadTracks.Build(new MediaItem { Id = Guid.Empty, Name = title }, selection, "", DownloadStore.DefaultDirectory, DateTimeOffset.UtcNow))
                    : L.Text("Downloads.OriginalRequiredTracks");
            }
            finally { updating = false; }
        }
        quality.SelectionChanged += (_, _) => Update();
        audio.SelectionChanged += (_, _) => { if (!updating) { defaultAudio = false; Update(); } };
        subtitles.SelectionChanged += (_, _) => Update();
        Update();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selection is null) return null;
        return ((DownloadQuality)((ComboBoxItem)quality.SelectedItem).Tag, selection);
    }

    /// <summary>Deletes a download's file at once, from the item menu.</summary>
    public void DeleteDownload(MediaItem item)
    {
        Downloads.Remove(item.Id);
        ToastHost.Show(ToastKind.Success, L.Format("Downloads.Deleted", item.Name));
    }
}
