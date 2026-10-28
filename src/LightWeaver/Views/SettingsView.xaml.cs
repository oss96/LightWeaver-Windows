using LightWeaver.Localization;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Player settings. Saves on every change (slider changes once their gesture settles); live-applies
/// what mpv supports at runtime (subtitle scale, hwdec, RTX filters). A save that writes nothing
/// shows an inline banner saying why.
///
/// Phase 9 M6: an in-app navigation destination, not a floating window. One instance is
/// cached by <see cref="MainWindow"/> and reached through the normal navigation stack, so
/// back/forward work on it for free. Settings are global (`settings.json`), not per-profile,
/// so a single instance shared across profile shells is correct.
///
/// It can no longer be opened *during playback* - the browse layer is collapsed while Playing
/// and showing it would have to stop the video. Ctrl+, is a no-op there by decision; the
/// live-apply paths below are untouched and still run whenever a player exists.
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly Func<MpvPlayer?> _player;
    private readonly PlayerViewModel _playerViewModel;
    private readonly UpdateService _updates;
    private bool _loading = true;
    private bool _syncingPeek;
    private bool _syncingVideoBufferPreset;
    private string _libraryServer = "";
    private string _libraryLanguage = "";
    private IReadOnlyList<Jellyfin.MediaItem> _libraries = [];
    private readonly List<(Guid Id, TextBox Field)> _libraryNameFields = [];
    private sealed record ResolutionRow(string Value, string Label);
    private sealed record BitrateRow(int? Value, string Label);
    private sealed record FontRow(string Value, string Label);
    private sealed record ColorRow(string? Value, string Label);
    private sealed record NumberRow(double? Value, string Label);
    private readonly DispatcherTimer _availabilityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DisplayBrightness.DisplayInfo? _displayInfo;
    private bool _checkingDisplay;
    private PassthroughProbe? _passthroughProbe;
    private int _passthroughRequest;
    /// <summary>Coalesces slider saves. A slider raises ValueChanged on every tick of a drag or a
    /// held arrow key, and each one used to rewrite settings.json; the label and live-applied
    /// limits still follow every tick, only the file write waits for the gesture to settle.</summary>
    private readonly DispatcherTimer _sliderSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private SettingsSaveFailure _shownSaveFailure;
    private bool _sliderSavePending;
    private bool _sliderDragging;

    public SettingsView(AppSettings settings, Func<MpvPlayer?> player, PlayerViewModel playerViewModel,
        UpdateService updates)
    {
        InitializeComponent();
        LiveText.Watch(this);
        _settings = settings;
        LibraryNames.Configure(settings);
        settings.UiLanguage ??= "";
        var uiLanguages = new[] { new UiLanguage("", L.Text("SettingsView.SystemLanguage.2d8bcd37")) }
            .Concat(L.Languages).ToList();
        if (!uiLanguages.Any(language => language.Code.Equals(settings.UiLanguage, StringComparison.OrdinalIgnoreCase)))
            uiLanguages.Add(new UiLanguage(settings.UiLanguage, L.Format("SettingsView.Saved.22a48118", settings.UiLanguage)));
        UiLanguageBox.ItemsSource = uiLanguages;
        UiLanguageBox.SelectedItem = UiLanguageBox.Items.Cast<UiLanguage>()
            .FirstOrDefault(language => language.Code.Equals(settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
            ?? UiLanguageBox.Items[0];
        _player = player;
        _playerViewModel = playerViewModel;
        _updates = updates;
        _updates.StatusChanged += OnUpdateStatusChanged;

        HwdecBox.IsChecked = settings.HardwareDecoding;
        RtxVsrBox.IsChecked = settings.RtxVsrDefault;
        RtxHdrBox.IsChecked = settings.RtxHdrDefault;
        MaxLumaBox.Text = settings.RtxHdrMaxLumaNits.ToString();
        PopulateBitrateChoices();
        MaxBitrateCustomBox.Text = settings.MaxStreamingBitrateMbps.ToString(CultureInfo.InvariantCulture);
        MaxBitrateBox.SelectedItem = MaxBitrateBox.Items.Cast<BitrateRow>()
            .FirstOrDefault(row => row.Value == settings.MaxStreamingBitrateMbps)
            ?? MaxBitrateBox.Items[MaxBitrateBox.Items.Count - 1];
        UpdateCustomBitrateVisibility();
        VideoBufferBox.Text = settings.VideoBufferMiB.ToString(CultureInfo.InvariantCulture);
        SelectVideoBufferPreset(settings.VideoBufferMiB, updateExpansion: true);
        SubScaleSlider.Value = settings.SubtitleScale;
        SubScaleLabel.Text = $"{settings.SubtitleScale * 100:F0}%";
        PopulateLanguages(SubLangBox, settings.PreferredSubtitleLanguage);
        SubFontBox.Items.Add(new FontRow("", L.Text("SettingsView.DefaultSubtitleFont")));
        foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies
                     .Select(f => f.Source).OrderBy(s => s))
            SubFontBox.Items.Add(new FontRow(f, f));
        if (!SubFontBox.Items.Cast<FontRow>().Any(row => row.Value == settings.SubtitleFontFamily))
            SubFontBox.Items.Add(new FontRow(settings.SubtitleFontFamily, settings.SubtitleFontFamily));
        SubFontBox.SelectedItem = SubFontBox.Items.Cast<FontRow>()
            .First(row => row.Value == settings.SubtitleFontFamily);
        SubFontSizeBox.Text = settings.SubtitleFontSize.ToString();
        SubColorBox.Text = EffectiveColor(settings.SubtitleTextColor, "#FFFFFF");
        SubBorderColorBox.Text = EffectiveColor(settings.SubtitleBorderColor, "#000000");
        SubBorderSizeBox.Text = settings.SubtitleBorderSize.ToString(CultureInfo.InvariantCulture);
        SubShadowBox.Text = settings.SubtitleShadowOffset.ToString(CultureInfo.InvariantCulture);
        SubPositionBox.Text = settings.SubtitleBasePosition.ToString(CultureInfo.InvariantCulture);
        PopulateSubtitlePresets();
        SelectColor(SubColorPreset, SubColorBox.Text);
        SelectColor(SubBorderColorPreset, SubBorderColorBox.Text);
        SelectNumber(SubBorderSizePreset, settings.SubtitleBorderSize);
        SelectNumber(SubShadowPreset, settings.SubtitleShadowOffset);
        SelectNumber(SubPositionPreset, settings.SubtitleBasePosition);
        UpdateSubtitleFields();
        PopulateLanguages(AudioLangBox, settings.PreferredAudioLanguage);
        PopulateAudioDevices(settings.AudioDevice);
        PassthroughBox.IsChecked = settings.AudioPassthrough;
        NormalizationBox.IsChecked = settings.VolumeNormalization;
        UpdateAudioExclusivity();
        UpdatePassthroughCapability();
        AutoSkipIntroBox.IsChecked = settings.AutoSkipIntro;
        AutoSkipCreditsBox.IsChecked = settings.AutoSkipCredits;
        SkipForwardBox.Text = settings.SkipForwardSeconds.ToString();
        SkipBackwardBox.Text = settings.SkipBackwardSeconds.ToString();
        LargeSkipBox.Text = settings.LargeSkipSeconds.ToString();
        AutoPlayNextBox.IsChecked = settings.AutoPlayNextEpisode;
        AutoPlayCountdownBox.Text = settings.AutoPlayCountdownSeconds.ToString();
        UpNextLeadChoice.Show(settings.UpNextLeadSeconds, settings.UpNextLeadSeconds);
        UpNextLeadChoice.LeadChosen += _ => OnChanged(UpNextLeadChoice, new RoutedEventArgs());
        OmdbKeyBox.Password = settings.OmdbApiKey;
        DownloadDirBox.Text = settings.DownloadDirectory is { Length: > 0 } directory
            ? directory : Downloads.DownloadStore.DefaultDirectory;
        foreach (var n in new[] { "1", "2", "3", "4" })
            MaxParallelBox.Items.Add(n);
        MaxParallelBox.SelectedIndex = Math.Clamp(settings.MaxParallelDownloads, 1, 4) - 1;
        foreach (var r in new[] { "Original", "1080p", "720p", "480p" })
            DefaultResolutionBox.Items.Add(new ResolutionRow(r,
                r == "Original" ? L.Text("SettingsView.Original.a8d63619") : r));
        DefaultResolutionBox.DisplayMemberPath = nameof(ResolutionRow.Label);
        DefaultResolutionBox.SelectedIndex = Math.Max(0, Array.IndexOf(
            ["Original", "1080p", "720p", "480p"], settings.DefaultDownloadResolution));
        ImageCacheSizeSlider.Value = Math.Clamp(settings.ImageCacheMaxMb, 100, 2000);
        LiveText.Bind(ImageCacheSizeLabel, "Text", () => L.Format("SettingsView.Mb.67a3de26", ((int)ImageCacheSizeSlider.Value)));
        FolderCacheSizeSlider.Value = Math.Clamp(settings.FolderCacheMaxItems, 500, 20000);
        FolderCacheSizeLabel.Text = $"{(int)FolderCacheSizeSlider.Value:N0}";
        RefreshCacheReadouts();
        foreach (var label in new[] { L.Text("SettingsView.NextInQueue.a5abcc0f"), L.Text("SettingsView.NextChapter.7feee55d"), L.Text("SettingsView.SeekForward.86fd3e75") })
            TransportNextBox.Items.Add(label);
        foreach (var label in new[] { L.Text("SettingsView.PreviousInQueue.bd53ab82"), L.Text("SettingsView.PreviousChapter.1f1ecf33"), L.Text("SettingsView.SeekBack.58ae379b") })
            TransportPrevBox.Items.Add(label);
        TransportNextBox.SelectedIndex = (int)settings.MediaNextAction;
        TransportPrevBox.SelectedIndex = (int)settings.MediaPrevAction;
        TransportSeekBox.Text = settings.TransportSeekSeconds.ToString();
        UpdateTransportSeekVisibility();
        BuildShortcutRows();
        VerboseLoggingBox.IsChecked = settings.VerboseLogging;
        UpdateVerboseLoggingHint();
        ZoomLimitBox.IsChecked = settings.LimitUiZoomToScreen;
        foreach (var label in new[] { L.Text("SettingsView.ManualInstall.137212a0"), L.Text("SettingsView.AutoDownload.b44f785a"), L.Text("SettingsView.AutoDownloadAndInstall.0f40ada9") })
            UpdatePolicyBox.Items.Add(label);
        UpdatePolicyBox.SelectedIndex = (int)settings.UpdatePolicy;
        UpdateUpdateUi(_updates.Status);
        LogsPath.Text = Diagnostics.AppLog.Dir;
        RefreshLogsReadout();
        _loading = false;
        LiveText.Observe(this, OnLanguageChanged);
        _availabilityTimer.Tick += (_, _) => RefreshDisplayAvailability();
        Loaded += (_, _) => { RefreshDisplayAvailability(); _availabilityTimer.Start(); };
        Unloaded += (_, _) => { _availabilityTimer.Stop(); FlushPendingSave(); };
        // The view is cached and shown again on every visit, so anything that changes while it is
        // away (caches filling during browsing, logs, a save that failed elsewhere) is re-read on
        // return; leaving flushes a slider save that has not settled yet.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                RefreshCacheReadouts();
                RefreshLogsReadout();
                // Announce a failure still present on arrival: one recorded while the page was
                // hidden, or before it was first shown (an unreadable file), reached no reader.
                _shownSaveFailure = SettingsSaveFailure.None;
                UpdateSaveFailureBanner();
            }
            else
                FlushPendingSave();
        };
        _sliderSaveTimer.Tick += (_, _) =>
        {
            _sliderSaveTimer.Stop();
            // Mid-drag pause: the drag's own completion flushes, so the file is written once.
            if (!_sliderDragging) FlushPendingSave();
        };
        foreach (var slider in new[] { SubScaleSlider, ImageCacheSizeSlider, FolderCacheSizeSlider })
        {
            // handledEventsToo: Slider's own class handlers sit on the same thumb events.
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _sliderDragging = true), true);
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            {
                _sliderDragging = false;
                FlushPendingSave();
            }), true);
        }
        UpdateSaveFailureBanner();
        UpdateAvailability();
        Diagnostics.AppLog.Detail("settings", "event=load outcome=success");
    }

    /// <summary>Asks before the zoom limit is turned off (title, body, confirm label; true to go
    /// ahead). Set by the window hosting the page; without it the change applies unasked.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    private bool _syncingZoomLimit;

    /// <summary>Turning the limit off asks first: past it the layout no longer fits the screen,
    /// and some controls can end up cut off or out of reach. Toggles while the question is open
    /// are ignored, and the box is set to the answer afterwards, so it always shows what applies.</summary>
    private async void OnZoomLimitChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncingZoomLimit)
            return;
        var limit = ZoomLimitBox.IsChecked == true;
        if (!limit && ConfirmAsync is { } confirm)
        {
            _syncingZoomLimit = true;
            try
            {
                limit = !await confirm(L.Text("SettingsView.RemoveTheZoomLimit.71c3e0d4"),
                    L.Text("SettingsView.ZoomLimitWarning.a6f8125b"), L.Text("SettingsView.RemoveLimit.0b9d4e67"));
                ZoomLimitBox.IsChecked = limit;
            }
            finally
            {
                _syncingZoomLimit = false;
            }
            if (limit)
            {
                Diagnostics.AppLog.Detail("settings", "event=change setting=LimitUiZoomToScreen outcome=declined");
                return;
            }
        }
        _settings.LimitUiZoomToScreen = limit;
        SettingsStore.Save(_settings);
        Theme.UiZoom.SetLimitToScreen(limit);
        Diagnostics.AppLog.Detail("settings", $"event=change delta=LimitUiZoomToScreen:{!limit}->{limit}");
    }

    private void OnUiLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UiLanguageBox.SelectedItem is not UiLanguage language) return;
        SaveLibraryNames();
        _settings.UiLanguage = language.Code;
        Save();
        L.Initialize(language.Code);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var loading = _loading;
        _loading = true;
        try
        {
            var languages = new[] { new UiLanguage("", L.Text("SettingsView.SystemLanguage.2d8bcd37")) }
                .Concat(L.Languages).ToList();
            if (!languages.Any(language => language.Code.Equals(_settings.UiLanguage, StringComparison.OrdinalIgnoreCase)))
                languages.Add(new UiLanguage(_settings.UiLanguage, L.Format("SettingsView.Saved.22a48118", _settings.UiLanguage)));
            UiLanguageBox.ItemsSource = languages;
            UiLanguageBox.SelectedItem = languages.First(language => language.Code.Equals(_settings.UiLanguage, StringComparison.OrdinalIgnoreCase));
            PopulateLanguages(SubLangBox, _settings.PreferredSubtitleLanguage);
            PopulateLanguages(AudioLangBox, _settings.PreferredAudioLanguage);
            PopulateAudioDevices(_settings.AudioDevice);
            ReplaceLabels(DefaultResolutionBox, new[] { "Original", "1080p", "720p", "480p" }
                .Select(value => new ResolutionRow(value, value == "Original" ? L.Text("SettingsView.Original.a8d63619") : value)));
            var bitrateIndex = MaxBitrateBox.SelectedIndex;
            PopulateBitrateChoices();
            MaxBitrateBox.SelectedIndex = bitrateIndex;
            var fonts = SubFontBox.Items.Cast<FontRow>()
                .Select(row => row.Value.Length == 0 ? row with { Label = L.Text("SettingsView.DefaultSubtitleFont") } : row).ToArray();
            ReplaceLabels(SubFontBox, fonts);
            var subtitleIndexes = SubtitlePresetBoxes().Select(box => box.SelectedIndex).ToArray();
            PopulateSubtitlePresets();
            foreach (var (box, index) in SubtitlePresetBoxes().Zip(subtitleIndexes)) box.SelectedIndex = index;
            UpdateSubtitleFields();
            ReplaceLabels(TransportNextBox, new[] { L.Text("SettingsView.NextInQueue.a5abcc0f"), L.Text("SettingsView.NextChapter.7feee55d"), L.Text("SettingsView.SeekForward.86fd3e75") });
            ReplaceLabels(TransportPrevBox, new[] { L.Text("SettingsView.PreviousInQueue.bd53ab82"), L.Text("SettingsView.PreviousChapter.1f1ecf33"), L.Text("SettingsView.SeekBack.58ae379b") });
            ReplaceLabels(UpdatePolicyBox, new[] { L.Text("SettingsView.ManualInstall.137212a0"), L.Text("SettingsView.AutoDownload.b44f785a"), L.Text("SettingsView.AutoDownloadAndInstall.0f40ada9") });
            var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
            foreach (var (action, button) in _shortcutButtons)
                if (_capturing != action) button.Content = PlayerActionDispatcher.DisplayChord(map[action]);
            RefreshCacheReadouts();
            UpdateUpdateUi(_updates.Status);
            UpdatePassthroughCapability();
            UpdateAvailability();
            if (_libraryLanguage != L.Language) BuildLibraryNameRows();
        }
        finally { _loading = loading; }
    }

    public void SetLibraries(string server, IReadOnlyList<Jellyfin.MediaItem> libraries)
    {
        // Commit pending names to their previous server/language before a profile switch.
        if (SaveLibraryNames()) L.Source.Refresh();
        _libraryServer = server;
        _libraries = libraries;
        BuildLibraryNameRows();
    }

    private void BuildLibraryNameRows()
    {
        _libraryLanguage = L.Language;
        LibraryNamesExpander.Visibility = _libraries.Count > 0 && _libraryServer.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Bind(LibraryNamesHint, "Text", () => L.Format("SettingsView.LibraryNamesHint",
            CultureInfo.GetCultureInfo(_libraryLanguage).NativeName));
        LibraryNameRows.Children.Clear();
        _libraryNameFields.Clear();
        foreach (var library in _libraries)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.Children.Add(new TextBlock
            {
                Text = library.Name, Style = (Style)FindResource("RowTitle"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 14, 0),
            });
            var field = new TextBox
            {
                Text = LibraryNames.Override(_libraryServer, library.Id, _libraryLanguage) ?? "",
                Style = (Style)FindResource("LwTextField"), MinHeight = 34,
                MaxLength = 200,
            };
            field.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
                "LibraryName_" + library.Id.ToString("N"));
            LiveText.Bind(field, System.Windows.Automation.AutomationProperties.NameProperty,
                () => L.Format("SettingsView.LibraryNameFor", library.Name));
            LiveText.Bind(field, "ToolTip", () => L.Format("SettingsView.LibraryNameDefault", LibraryNames.Default(library.Name)));
            field.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                SaveLibraryNames();
                L.Source.Refresh();
                e.Handled = true;
            };
            Grid.SetColumn(field, 1);
            row.Children.Add(field);
            LibraryNameRows.Children.Add(row);
            _libraryNameFields.Add((library.Id, field));
        }
    }

    private bool SaveLibraryNames()
    {
        if (_libraryServer.Length == 0 || _libraryNameFields.Count == 0) return false;
        var changed = false;
        foreach (var (id, field) in _libraryNameFields)
        {
            var text = string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim();
            if (LibraryNames.Override(_libraryServer, id, _libraryLanguage) == text) continue;
            LibraryNames.Set(_libraryServer, id, _libraryLanguage, field.Text);
            changed = true;
        }
        if (changed) Save();
        return changed;
    }

    private void OnSaveLibraryNames(object sender, RoutedEventArgs e)
    {
        SaveLibraryNames();
        L.Source.Refresh();
    }

    private static void ReplaceLabels(ComboBox box, IEnumerable<object> labels)
    {
        var index = box.SelectedIndex;
        box.Items.Clear();
        foreach (var label in labels) box.Items.Add(label);
        box.SelectedIndex = index;
    }

    // ---- Updates ---------------------------------------------------------------------

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnUpdateStatusChanged(status));
            return;
        }
        UpdateUpdateUi(status);
    }

    private void UpdateUpdateUi(UpdateStatus status)
    {
        var version = status.Update?.DisplayVersion;
        var lastChecked = status.LastCheckedUtc is { } checkedUtc
            ? checkedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : L.Text("SettingsView.Never.b8c49021");
        LiveText.Bind(UpdateVersionText, "Text", () => L.Format("SettingsView.CurrentVersionLastChecked.deff5f18", (GitHubUpdateClient.CurrentVersion.ToString(3)), (lastChecked)));
        LiveText.Bind(UpdateStatusText, "Text", () => status.Phase switch
        {
            UpdatePhase.Idle => L.Text("SettingsView.NoUpdateCheckHasRunYet.b888684f"),
            UpdatePhase.Checking => L.Text("SettingsView.CheckingForUpdates.716abad2"),
            UpdatePhase.UpToDate => L.Text("SettingsView.LightweaverIsUpToDate.522503ca"),
            UpdatePhase.Available => status.Detail ?? L.Format("SettingsView.VersionIsAvailable.863cd3d7", (version)),
            UpdatePhase.Downloading => L.Format("SettingsView.DownloadingVersion.8a4557dc", (version)),
            UpdatePhase.ReadyToInstall when _settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall
                => L.Format("SettingsView.VersionIsReadyItWillInstallWhen.15e2a08e", (version))
                    + (status.Detail is { Length: > 0 } ? L.Format("SettingsView.LastCheck.f89df2b0", (status.Detail)) : string.Empty),
            UpdatePhase.ReadyToInstall => L.Format("SettingsView.VersionIsVerifiedAndReadyToInstall.a3763580", (version))
                + (status.Detail is { Length: > 0 } ? L.Format("SettingsView.LastCheck.f89df2b0", (status.Detail)) : string.Empty),
            UpdatePhase.Installing => L.Text("SettingsView.StartingTheInstaller.0f1f95e3"),
            UpdatePhase.Error => status.Detail ?? L.Text("SettingsView.UpdateOperationFailed.6a3b525a"),
            _ => string.Empty,
        });
        UpdateDownloadProgress.Visibility = status.Phase == UpdatePhase.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = (status.Progress ?? 0) * 100;
        var hasAsset = status.Update?.SetupAssetUri is not null;
        var manual = _settings.UpdatePolicy == UpdatePolicy.ManualInstall;
        DownloadUpdateButton.Visibility = hasAsset ? Visibility.Visible : Visibility.Collapsed;
        OpenReleasePageButton.Visibility = !hasAsset && status.Update is not null ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = status.Phase is UpdatePhase.Available or UpdatePhase.Error;
        LiveText.Bind(DownloadUpdateButton, "Content", () => status.Phase == UpdatePhase.Error ? L.Text("SettingsView.Retry.8a625e56")
            : manual ? L.Text("SettingsView.DownloadAndInstall.35531833") : L.Text("SettingsView.Download.9ccefefe"));
        InstallUpdateButton.IsEnabled = status.Phase == UpdatePhase.ReadyToInstall;
        InstallUpdateButton.Visibility = status.Phase == UpdatePhase.ReadyToInstall
            ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Bind(InstallUpdateButton, "Content", () => L.Text("SettingsView.InstallNow.e4cf6a26"));
        CheckForUpdatesButton.IsEnabled = status.Phase is not UpdatePhase.Checking and not UpdatePhase.Downloading and not UpdatePhase.Installing;
        LiveText.Bind(CheckForUpdatesButton, "Content", () => status.Phase == UpdatePhase.Error && !hasAsset ? L.Text("SettingsView.Retry.8a625e56") : L.Text("SettingsView.CheckForUpdates.b964dc04"));
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=check-updates");
        await _updates.CheckNowAsync();
    }

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=download-update");
        if (_settings.UpdatePolicy == UpdatePolicy.ManualInstall)
        {
            if (await _updates.DownloadAndInstallInteractiveAsync())
                Window.GetWindow(this)?.Close();
        }
        else
            await _updates.DownloadAsync();
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=install-update");
        if (await _updates.InstallNowAsync())
            Window.GetWindow(this)?.Close();
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e) => _updates.OpenReleasePage();

    // ---- Storage (Phase 7 M21) ------------------------------------------------------

    // Invariant decimals: the UI is English and locale commas break log/test parsing.
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + L.Text("SettingsView.Gb.b4766b60"),
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + L.Text("SettingsView.Mb.6bb5abe0"),
        >= 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + L.Text("SettingsView.Kb.ac2a41e1"),
        _ => L.Format("SettingsView.B.22d53ad5", (bytes)),
    };

    private void RefreshCacheReadouts()
    {
        LiveText.Bind(ImageCacheReadout, "Text", () => FormatBytes(Imaging.ImageCache.CurrentSizeBytes()));
        LiveText.Bind(MetadataCacheReadout, "Text", () => FormatBytes(Imaging.MetadataCache.CurrentSizeBytes()));
        LiveText.Bind(ExternalCacheReadout, "Text", () => FormatBytes(Imaging.ExternalMetadataCache.CurrentSizeBytes()));
    }

    private void OnClearImageCache(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=clear-cache target=image");
        Imaging.ImageCache.Clear();
        RefreshCacheReadouts();
    }

    private void OnClearMetadataCache(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=clear-cache target=metadata");
        Imaging.MetadataCache.Clear();
        RefreshCacheReadouts();
    }

    private void OnClearExternalCache(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=clear-cache target=external");
        Imaging.ExternalMetadataCache.Clear();
        RefreshCacheReadouts();
    }

    private void OnClearAllCaches(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=clear-cache target=all");
        Imaging.ImageCache.Clear();
        Imaging.MetadataCache.Clear();
        Imaging.ExternalMetadataCache.Clear();
        RefreshCacheReadouts();
    }

    // ---- Diagnostics ---------------------------------------------------------------

    private void RefreshLogsReadout()
        => LiveText.Bind(LogsReadout, "Text", () => FormatBytes(Diagnostics.AppLog.CurrentSizeBytes()));

    /// <summary>Opens the log folder in Explorer, creating it first — the normal state of a
    /// healthy install is that nothing has ever been logged, and a button that reports "path
    /// not found" reads as a broken feature rather than as good news.</summary>
    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=open-logs-folder");
        var dir = Diagnostics.AppLog.EnsureDir();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diagnostics.AppLog.Error("settings",
                $"could not open the log folder error={ex.GetType().Name}");
        }
        RefreshLogsReadout();
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=clear-logs");
        Diagnostics.AppLog.Clear();
        RefreshLogsReadout();
    }

    /// <summary>Saves every log file as one zip, for sending on. The readout doubles as the
    /// result line: an export that found nothing has to say so, because the healthy state of this
    /// app is that no log has ever been written, and a silent "success" there is indistinguishable
    /// from a broken button.</summary>
    private void OnExportLogs(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=export-logs");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            // Invariant timestamp: this filename ends up quoted in bug reports and must sort.
            FileName = $"lightweaver-logs-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip",
            DefaultExt = ".zip",
            Filter = L.Text("SettingsView.ZipArchive.15f41101").Replace('|', ' ') + " (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true,
            Title = L.Text("SettingsView.ExportLightweaverLogs.f46ec341")
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var count = Diagnostics.AppLog.ExportTo(dialog.FileName);
            LiveText.Bind(LogsReadout, "Text", () => count == 0
                ? L.Text("SettingsView.NothingToExportNoLogsHaveBeen.5570c384")
                : count == 1 ? L.Format("SettingsView.Exported1FileTo.a005f55e", Path.GetFileName(dialog.FileName)) : L.Format("SettingsView.ExportedFilesTo.0ab1ae50", count, Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LiveText.Bind(LogsReadout, "Text", () => L.Format("SettingsView.ExportFailed.d8568efd", (ex.Message)));
        }
    }

    /// <summary>The seek-seconds row matters only while a transport direction is
    /// mapped to Seek.</summary>
    private void UpdateTransportSeekVisibility()
        => TransportSeekRow.Visibility =
            TransportNextBox.SelectedIndex == (int)TransportAction.Seek
            || TransportPrevBox.SelectedIndex == (int)TransportAction.Seek
                ? Visibility.Visible : Visibility.Collapsed;

    private void OnVideoBufferPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingVideoBufferPreset
            || VideoBufferPresetBox.SelectedItem is not ComboBoxItem { Tag: string value })
            return;

        if (value == "custom")
        {
            VideoBufferAdvancedExpander.IsExpanded = true;
            return;
        }

        VideoBufferAdvancedExpander.IsExpanded = false;
        VideoBufferBox.Text = value;
    }

    private void SelectVideoBufferPreset(int value, bool updateExpansion = false)
    {
        var selected = VideoBufferPresetBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag
                && (tag == value.ToString(CultureInfo.InvariantCulture) || tag == "custom"));
        if (selected is null || ReferenceEquals(VideoBufferPresetBox.SelectedItem, selected))
            return;

        _syncingVideoBufferPreset = true;
        try
        {
            VideoBufferPresetBox.SelectedItem = selected;
            // Numeric edits may pass through a preset (32 while typing 320).
            // Keep the editor open until the user explicitly chooses a preset.
            if (updateExpansion)
                VideoBufferAdvancedExpander.IsExpanded = selected.Tag as string == "custom";
        }
        finally
        {
            _syncingVideoBufferPreset = false;
        }
    }

    // ---- Shortcuts (Phase 7 M16): one generated row per PlayerAction --------------

    private readonly Dictionary<PlayerAction, System.Windows.Controls.Button> _shortcutButtons = new();
    private readonly Dictionary<PlayerAction, System.Windows.Controls.TextBlock> _shortcutNotes = new();
    private PlayerAction? _capturing;

    private void BuildShortcutRows()
    {
        var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
        var actions = Enum.GetValues<PlayerAction>();
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var btn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("LwButtonSecondary"),
                MinWidth = 120,
                Content = PlayerActionDispatcher.DisplayChord(map[action]),
                Tag = action,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, $"Shortcut{action}");
            System.Windows.Controls.DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
            btn.Click += OnShortcutCaptureClick;
            // Says why a key was refused, or that a saved one collides with an app shortcut.
            var note = new System.Windows.Controls.TextBlock
            {
                Style = (Style)FindResource("RowSub"),
                Foreground = (System.Windows.Media.Brush)FindResource("LwGemAmberTextBrush"),
                Visibility = Visibility.Collapsed,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(note, $"ShortcutNote{action}");
            var labels = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(LiveText.Bind(new System.Windows.Controls.TextBlock
            {
                Style = (Style)FindResource("RowTitle"),
            }, "Text", () => PlayerActionDispatcher.DisplayName(action)));
            labels.Children.Add(note);
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            dock.Children.Add(btn);
            dock.Children.Add(labels);
            var row = new System.Windows.Controls.Border
            {
                Style = (Style)FindResource("SettingRow"),
                Child = dock,
            };
            if (i == actions.Length - 1)
                row.BorderThickness = new Thickness(0);
            ShortcutsHost.Children.Add(row);
            _shortcutButtons[action] = btn;
            _shortcutNotes[action] = note;
        }
        RefreshShortcutButtons();
    }

    private void OnShortcutCaptureClick(object sender, RoutedEventArgs e)
    {
        var action = (PlayerAction)((System.Windows.Controls.Button)sender).Tag;
        Diagnostics.AppLog.Detail("settings", $"event=interaction action=shortcut-capture target={action}");
        CancelShortcutCapture();
        _capturing = action;
        LiveText.Bind(_shortcutButtons[action], "Content", () => L.Text("SettingsView.PressAKey.dab8168a"));
        PreviewKeyDown += OnShortcutKeyCapture;
    }

    private void CancelShortcutCapture()
    {
        if (_capturing is null)
            return;
        PreviewKeyDown -= OnShortcutKeyCapture;
        _capturing = null;
        RefreshShortcutButtons();
    }

    private void OnShortcutKeyCapture(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturing is not { } action)
            return;
        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
            return;   // modifier alone — keep waiting for the real key
        PreviewKeyDown -= OnShortcutKeyCapture;
        _capturing = null;
        if (key == System.Windows.Input.Key.Escape)
        {
            Diagnostics.AppLog.Detail("settings", $"event=interaction action=shortcut-capture outcome=cancel target={action}");
            RefreshShortcutButtons();
            return;
        }
        BindShortcut(action, PlayerActionDispatcher.ChordOf(key, System.Windows.Input.Keyboard.Modifiers));
    }

    /// <summary>Binds a captured chord, unless an app shortcut owns it: that binding would do
    /// nothing in the main window, so the row keeps its key and says which shortcut has it.</summary>
    private void BindShortcut(PlayerAction action, string chord)
    {
        if (AppShortcuts.OwnerOf(chord, action) is { } owner)
        {
            Diagnostics.AppLog.Detail("settings",
                $"event=interaction action=shortcut-bind outcome=refused reason=app_shortcut target={action} owner={owner.Id}");
            RefreshShortcutButtons();
            ShowShortcutNote(action, chord, owner, refused: true);
            return;
        }
        // Duplicate handling: the chord moves here; the previous owner shows (unbound).
        var map = PlayerActionDispatcher.Rebind(_settings.KeyBindings, action, chord);
        _settings.KeyBindings = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        Diagnostics.AppLog.Detail("settings", $"event=interaction action=shortcut-bind target={action}");
        Save();
        RefreshShortcutButtons();
    }

    private void RefreshShortcutButtons()
    {
        var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
        foreach (var (action, btn) in _shortcutButtons)
        {
            btn.Content = PlayerActionDispatcher.DisplayChord(map[action]);
            ShowShortcutNote(action, map[action], AppShortcuts.OwnerOf(map[action], action), refused: false);
        }
    }

    /// <summary>The row's note: hidden when no app shortcut owns <paramref name="chord"/>;
    /// otherwise why it was not taken, or, for a saved binding, that it may do nothing.</summary>
    private void ShowShortcutNote(PlayerAction action, string? chord, ChromeShortcut? owner, bool refused)
    {
        var note = _shortcutNotes[action];
        if (owner is null)
        {
            System.Windows.Data.BindingOperations.ClearBinding(note, System.Windows.Controls.TextBlock.TextProperty);
            note.Visibility = Visibility.Collapsed;
            return;
        }
        // Spelled again on every read, so the key names and the shortcut's follow a language switch.
        string Keys() => PlayerActionDispatcher.DisplayChord(chord);
        string Owner() => AppShortcuts.Chrome.First(c => c.Id == owner.Id).Function;
        LiveText.Bind(note, "Text", refused
            ? () => L.Format("SettingsView.ShortcutTakenByApp.5c0e93a7", Keys(), Owner())
            : () => L.Format("SettingsView.ShortcutSharedWithApp.b17d4e2f", Keys(), Owner()));
        note.Visibility = Visibility.Visible;
    }

    private void OnShortcutsReset(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=shortcut-reset");
        CancelShortcutCapture();
        _settings.KeyBindings = new Dictionary<string, string>();
        Save();
        RefreshShortcutButtons();
    }

    /// <summary>Curated language dropdown ("None" = no preference). A legacy
    /// hand-typed code selects its equivalent list entry (deu ↔ ger); a code the
    /// list doesn't know is kept as an extra row so the setting never changes
    /// behind the user's back.</summary>
    private static void PopulateLanguages(System.Windows.Controls.ComboBox box, string saved)
    {
        foreach (var option in Languages.Choices)
            box.Items.Add(option);
        var selected = box.Items.Cast<Languages.Option>().FirstOrDefault(o =>
            o.Code.Equals(saved.Trim(), StringComparison.OrdinalIgnoreCase)
            || Languages.Matches(saved, o.Code));
        if (selected is null && saved.Trim() is { Length: > 0 } custom)
        {
            selected = new Languages.Option(custom, L.Format("SettingsView.Saved.22a48118", (custom)));
            box.Items.Add(selected);
        }
        box.SelectedItem = selected ?? box.Items[0];
    }

    /// <summary>One row per mpv audio output. mpv exists only once playback has
    /// started — before that the box holds just "auto" plus the saved value.</summary>
    private void PopulateAudioDevices(string saved)
    {
        var devices = _player()?.GetAudioDevices() ?? [];
        AudioDeviceBox.Items.Clear();
        AudioDeviceBox.Items.Add(new AudioDeviceRow("auto", L.Text("SettingsView.SystemDefault.d56d22d4")));
        foreach (var d in devices.Where(d => d.Name != "auto"))
            AudioDeviceBox.Items.Add(new AudioDeviceRow(d.Name, d.Description));
        if (saved is { Length: > 0 } && saved != "auto"
            && !AudioDeviceBox.Items.Cast<AudioDeviceRow>().Any(r => r.Name == saved))
            AudioDeviceBox.Items.Add(new AudioDeviceRow(saved, L.Format("SettingsView.Saved.22a48118", (saved))));
        AudioDeviceBox.SelectedItem = AudioDeviceBox.Items.Cast<AudioDeviceRow>()
            .FirstOrDefault(r => r.Name == saved) ?? AudioDeviceBox.Items[0];
        AudioDeviceHint.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record AudioDeviceRow(string Name, string Description)
    {
        public override string ToString() => Description;
    }

    /// <summary>Passthrough bypasses audio filters — the two options exclude each other.</summary>
    private void UpdateAudioExclusivity()
    {
        var unsupported = _passthroughProbe?.Verdict is PassthroughVerdict.NoneSupported or PassthroughVerdict.NoExclusiveMode;
        NormalizationBox.IsEnabled = PassthroughBox.IsChecked != true;
        // A saved, enabled preference can still be switched off on an unsupported endpoint.
        PassthroughBox.IsEnabled = NormalizationBox.IsChecked != true && (!unsupported || PassthroughBox.IsChecked == true);
        NormalizationBox.ToolTip = PassthroughBox.IsChecked == true
            ? L.Text("SettingsView.DynaudnormMutuallyExclusiveWithPassthrough.7d926f57") : null;
        PassthroughBox.ToolTip = NormalizationBox.IsChecked == true
            ? L.Text("SettingsView.DynaudnormMutuallyExclusiveWithPassthrough.7d926f57")
            : unsupported ? Player.AudioPassthroughProbe.Describe(_passthroughProbe!) : null;
    }

    /// <summary>Asks the SELECTED output which bitstream formats it will actually take, and says
    /// so under the toggle. Bitstreaming is all-or-nothing per device: before this the toggle
    /// read as active on a device that could never honour it, and the only symptom was playback
    /// that did not start. Re-run whenever the device selection changes, since the answer is a
    /// property of the device and not of the setting.
    ///
    /// The probe is a pure WASAPI query that neither opens nor takes the endpoint, so this is
    /// safe to run while something is playing — but it is still a handful of COM round trips,
    /// so it runs off the UI thread and the row stays collapsed until an answer arrives.</summary>
    private async void UpdatePassthroughCapability()
    {
        var device = (AudioDeviceBox.SelectedItem as AudioDeviceRow)?.Name;
        var request = ++_passthroughRequest;
        _passthroughProbe = null;
        UpdateAudioExclusivity();
        var probe = await Task.Run(() => Player.AudioPassthroughProbe.Probe(device));
        var line = Player.AudioPassthroughProbe.Describe(probe);
        // The selection may have moved on while the probe ran; the answer would then be about
        // the wrong device, and a stale capability claim is worse than none.
        if (request != _passthroughRequest || (AudioDeviceBox.SelectedItem as AudioDeviceRow)?.Name != device)
            return;
        _passthroughProbe = probe;
        UpdateAudioExclusivity();
        PassthroughCapabilityText.Text = line ?? string.Empty;
        PassthroughCapabilityText.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        // Amber when the answer is negative — the app's warning colour. A device that accepts
        // nothing means the toggle beside it is on and cannot do anything, which is a different
        // kind of statement from listing what the device supports, and it read as just another
        // subtitle in the quiet text colour.
        PassthroughCapabilityText.Foreground = probe.Verdict is Player.PassthroughVerdict.SomeSupported
            ? (System.Windows.Media.Brush)FindResource("LwText3Brush")
            : (System.Windows.Media.Brush)FindResource("LwGemAmberTextBrush");
    }

    /// <summary>
    /// Picks the download folder with the native folder browser (Phase 9 M5) — typing a path by
    /// hand was the only way before, so a typo silently became the download target. Seeds the
    /// dialog with the current value when it still exists, so re-picking starts where the user
    /// left off. `Microsoft.Win32.OpenFolderDialog` is WPF-native since .NET 8: no WinForms
    /// reference, and consistent with the decision that OS file dialogs stay native.
    /// Writing to the box raises TextChanged → OnChanged, which is the existing save-on-change
    /// path, so nothing else has to persist it.
    /// </summary>
    private void OnBrowseDownloadDir(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Multiselect = false,
            Title = L.Text("SettingsView.ChooseTheDownloadFolder.37147ee3")
        };
        var current = DownloadDirBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
            dialog.InitialDirectory = current;

        // A UserControl is not a Window, so hand the dialog our hosting window.
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            DownloadDirBox.Text = dialog.FolderName;
    }

    /// <summary>Shows the app-data default; OnChanged keeps its saved value implicit.</summary>
    private void OnClearDownloadDir(object sender, RoutedEventArgs e)
        => DownloadDirBox.Text = Downloads.DownloadStore.DefaultDirectory;

    private ComboBox[] SubtitlePresetBoxes() => [SubColorPreset, SubBorderColorPreset, SubBorderSizePreset, SubShadowPreset, SubPositionPreset];

    private static bool IsColor(string color) => System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");
    private static string EffectiveColor(string color, string fallback) => IsColor(color) ? color.ToUpperInvariant() : fallback;
    private static void SelectColor(ComboBox box, string color) => box.SelectedItem = box.Items.Cast<ColorRow>()
        .FirstOrDefault(row => row.Value == color) ?? box.Items[box.Items.Count - 1];
    private static void SelectNumber(ComboBox box, double value) => box.SelectedItem = box.Items.Cast<NumberRow>()
        .FirstOrDefault(row => row.Value == value) ?? box.Items[box.Items.Count - 1];

    private void PopulateSubtitlePresets()
    {
        foreach (var box in new[] { SubColorPreset, SubBorderColorPreset })
        {
            box.Items.Clear();
            box.Items.Add(new ColorRow("#FFFFFF", L.Text("SettingsView.ColorWhite")));
            box.Items.Add(new ColorRow("#000000", L.Text("SettingsView.ColorBlack")));
            box.Items.Add(new ColorRow("#FFFF00", L.Text("SettingsView.ColorYellow")));
            box.Items.Add(new ColorRow("#00FFFF", L.Text("SettingsView.ColorCyan")));
            box.Items.Add(new ColorRow("#00FF00", L.Text("SettingsView.ColorGreen")));
            box.Items.Add(new ColorRow("#FF0000", L.Text("SettingsView.ColorRed")));
            box.Items.Add(new ColorRow(null, L.Text("SettingsView.Custom.cf0bc965")));
        }
        SubBorderSizePreset.Items.Clear();
        SubBorderSizePreset.Items.Add(new NumberRow(0, L.Text("SettingsView.NoEffect")));
        SubBorderSizePreset.Items.Add(new NumberRow(1, L.Text("SettingsView.BorderThin")));
        SubBorderSizePreset.Items.Add(new NumberRow(3, L.Text("SettingsView.BorderMedium")));
        SubBorderSizePreset.Items.Add(new NumberRow(5, L.Text("SettingsView.BorderThick")));
        SubShadowPreset.Items.Clear();
        SubShadowPreset.Items.Add(new NumberRow(0, L.Text("SettingsView.NoEffect")));
        SubShadowPreset.Items.Add(new NumberRow(1, L.Text("SettingsView.ShadowSubtle")));
        SubShadowPreset.Items.Add(new NumberRow(2, L.Text("SettingsView.ShadowSoft")));
        SubShadowPreset.Items.Add(new NumberRow(4, L.Text("SettingsView.ShadowStrong")));
        SubPositionPreset.Items.Clear();
        SubPositionPreset.Items.Add(new NumberRow(100, L.Text("SettingsView.PositionBottom")));
        SubPositionPreset.Items.Add(new NumberRow(90, L.Text("SettingsView.PositionRaised")));
        SubPositionPreset.Items.Add(new NumberRow(50, L.Text("SettingsView.PositionMiddle")));
        SubPositionPreset.Items.Add(new NumberRow(0, L.Text("SettingsView.PositionTop")));
        foreach (var box in new[] { SubBorderSizePreset, SubShadowPreset, SubPositionPreset })
            box.Items.Add(new NumberRow(null, L.Text("SettingsView.Custom.cf0bc965")));
    }

    private void OnSubtitlePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            foreach (var (box, field) in new[] { (SubColorPreset, SubColorBox), (SubBorderColorPreset, SubBorderColorBox) })
                if (ReferenceEquals(sender, box) && box.SelectedItem is ColorRow { Value: { } color }) field.Text = color;
            foreach (var (box, field) in new[] { (SubBorderSizePreset, SubBorderSizeBox), (SubShadowPreset, SubShadowBox), (SubPositionPreset, SubPositionBox) })
                if (ReferenceEquals(sender, box) && box.SelectedItem is NumberRow { Value: { } value }) field.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        finally { _loading = false; }
        OnChanged(sender, e);
    }

    private void UpdateSubtitleFields()
    {
        foreach (var (box, field) in new[] { (SubColorPreset, SubColorBox), (SubBorderColorPreset, SubBorderColorBox) })
            field.Visibility = box.SelectedItem is ColorRow { Value: null } ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (box, field) in new[] { (SubBorderSizePreset, SubBorderSizeBox), (SubShadowPreset, SubShadowBox), (SubPositionPreset, SubPositionBox) })
            field.Visibility = box.SelectedItem is NumberRow { Value: null } ? Visibility.Visible : Visibility.Collapsed;
        UpdateColor(SubColorPreset, SubColorBox, SubColorPreview, SubColorError, "#FFFFFF", _settings.SubtitleTextColor);
        UpdateColor(SubBorderColorPreset, SubBorderColorBox, SubBorderColorPreview, SubBorderColorError, "#000000", _settings.SubtitleBorderColor);
        ReadNumber(SubBorderSizePreset, SubBorderSizeBox, SubBorderSizeError, 0, 20, false, L.Text("SettingsView.CustomBorderSizeError"));
        ReadNumber(SubShadowPreset, SubShadowBox, SubShadowError, -20, 20, false, L.Text("SettingsView.CustomShadowError"));
        ReadNumber(SubPositionPreset, SubPositionBox, SubPositionError, 0, 150, true, L.Text("SettingsView.CustomPositionError"));
    }

    private static string? UpdateColor(ComboBox box, TextBox field, Border preview, TextBlock error, string fallback, string saved)
    {
        var color = (box.SelectedItem as ColorRow)?.Value ?? field.Text.Trim();
        var valid = IsColor(color);
        error.Text = L.Text("SettingsView.CustomColorError");
        error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        preview.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            valid ? color : EffectiveColor(saved, fallback)));
        preview.ToolTip = valid ? color.ToUpperInvariant() : EffectiveColor(saved, fallback);
        return valid ? (color.Equals(fallback, StringComparison.OrdinalIgnoreCase) ? "" : color.ToUpperInvariant()) : null;
    }

    private void SaveSubtitleChoices()
    {
        UpdateSubtitleFields();
        var text = UpdateColor(SubColorPreset, SubColorBox, SubColorPreview, SubColorError, "#FFFFFF", _settings.SubtitleTextColor);
        if (text is not null) _settings.SubtitleTextColor = text;
        var border = UpdateColor(SubBorderColorPreset, SubBorderColorBox, SubBorderColorPreview, SubBorderColorError, "#000000", _settings.SubtitleBorderColor);
        if (border is not null) _settings.SubtitleBorderColor = border;
        if (ReadNumber(SubBorderSizePreset, SubBorderSizeBox, SubBorderSizeError, 0, 20, false, L.Text("SettingsView.CustomBorderSizeError")) is { } borderSize)
            _settings.SubtitleBorderSize = borderSize;
        if (ReadNumber(SubShadowPreset, SubShadowBox, SubShadowError, -20, 20, false, L.Text("SettingsView.CustomShadowError")) is { } shadow)
            _settings.SubtitleShadowOffset = shadow;
        if (ReadNumber(SubPositionPreset, SubPositionBox, SubPositionError, 0, 150, true, L.Text("SettingsView.CustomPositionError")) is { } position)
            _settings.SubtitleBasePosition = (int)position;
    }

    private static double? ReadNumber(ComboBox box, TextBox field, TextBlock error, double minimum, double maximum, bool whole, string message)
    {
        var value = (box.SelectedItem as NumberRow)?.Value;
        if (value is null && double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var custom)) value = custom;
        var valid = value is { } number && double.IsFinite(number) && number >= minimum && number <= maximum && (!whole || number == Math.Truncate(number));
        error.Text = message;
        error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        return valid ? value : null;
    }

    private void PopulateBitrateChoices()
    {
        MaxBitrateBox.Items.Clear();
        foreach (var value in new[] { 0, 1, 4, 10, 20, 40, 80, 120 })
            MaxBitrateBox.Items.Add(new BitrateRow(value, value == 0
                ? L.Text("SettingsView.Unlimited") : L.Format("SettingsView.BitrateMbps", value)));
        MaxBitrateBox.Items.Add(new BitrateRow(null, L.Text("SettingsView.Custom.cf0bc965")));
    }

    private void UpdateCustomBitrateVisibility()
        => MaxBitrateCustomBox.Visibility = MaxBitrateBox.SelectedItem is BitrateRow { Value: null }
            ? Visibility.Visible : Visibility.Collapsed;

    private void OnBitratePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateCustomBitrateVisibility();
        if (MaxBitrateBox.SelectedItem is BitrateRow { Value: null })
        {
            _loading = true;
            try { MaxBitrateCustomBox.Text = Math.Max(1, _settings.MaxStreamingBitrateMbps).ToString(CultureInfo.InvariantCulture); }
            finally { _loading = false; }
        }
        OnChanged(sender, e);
    }

    private void UpdateVerboseLoggingHint()
        => LiveText.Bind(VerboseLoggingHint, "Text", () => _settings.VerboseLogging
            ? L.Text("SettingsView.VerboseLoggingEnabled") : L.Text("SettingsView.VerboseLoggingDisabled"));

    private void OnUseDisplayBrightness(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        var nits = window is null ? null : DisplayBrightness.PeakNitsForWindow(new WindowInteropHelper(window).Handle);
        DisplayBrightnessStatus.Visibility = Visibility.Visible;
        if (nits is { } value)
        {
            MaxLumaBox.Text = value.ToString(CultureInfo.InvariantCulture);
            LiveText.Bind(DisplayBrightnessStatus, "Text", () => L.Format("SettingsView.DisplayBrightnessReported", value));
        }
        else
            LiveText.Bind(DisplayBrightnessStatus, "Text", () => L.Text("SettingsView.DisplayBrightnessUnavailable"));
    }

    private async void RefreshDisplayAvailability()
    {
        if (_checkingDisplay || !IsVisible || Window.GetWindow(this) is not { } window) return;
        _checkingDisplay = true;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var info = await Task.Run(() => DisplayBrightness.ReadForWindow(handle));
            if (!IsVisible || Window.GetWindow(this) != window) return;
            if (_displayInfo?.Monitor != info?.Monitor || _displayInfo?.HdrEnabled != info?.HdrEnabled)
                DisplayBrightnessStatus.Visibility = Visibility.Collapsed;
            _displayInfo = info;
            UpdateAvailability();
        }
        finally { _checkingDisplay = false; }
    }

    private void UpdateAvailability()
    {
        var gpuReason = GpuCapabilities.RtxUnavailableReason;
        var hdrReason = gpuReason ?? (_displayInfo is { HdrEnabled: false }
            ? L.Text("SettingsView.HdrDisplayRequired") : null);
        SetRowAvailability(RtxVsrRow, gpuReason);
        SetRowAvailability(RtxHdrRow, hdrReason);
        SetRowAvailability(RtxBrightnessRow, hdrReason);
        UseDisplayBrightnessButton.IsEnabled = _displayInfo?.PeakNits is not null;
        UseDisplayBrightnessButton.ToolTip = hdrReason ?? (_displayInfo?.PeakNits is null
            ? L.Text("SettingsView.DisplayBrightnessUnavailable") : null);
        SetRowAvailability(AutoPlayCountdownRow, AutoPlayNextBox.IsChecked == true
            ? null : L.Text("SettingsView.AutoPlayCountdownUnavailable"));
    }

    private static void SetRowAvailability(Border row, string? reason)
    {
        row.IsEnabled = reason is null;
        row.ToolTip = reason;
    }

    /// <summary>Structural settings diagnostics. The whitelist intentionally excludes every
    /// free-form or identifying value: languages, font names, colours, paths, API keys and raw
    /// audio endpoint ids never enter the snapshot. Audio output is only default/custom.</summary>
    private Dictionary<string, string> SafeSettingsSnapshot() => new(StringComparer.Ordinal)
    {
        ["hardwareDecoding"] = Bool(_settings.HardwareDecoding),
        ["rtxVsr"] = Bool(_settings.RtxVsrDefault),
        ["rtxHdr"] = Bool(_settings.RtxHdrDefault),
        ["rtxHdrMaxLuma"] = _settings.RtxHdrMaxLumaNits.ToString(CultureInfo.InvariantCulture),
        ["maxBitrate"] = _settings.MaxStreamingBitrateMbps.ToString(CultureInfo.InvariantCulture),
        ["videoBufferMiB"] = _settings.VideoBufferMiB.ToString(CultureInfo.InvariantCulture),
        ["subtitleScale"] = _settings.SubtitleScale.ToString(CultureInfo.InvariantCulture),
        ["subtitleFontSize"] = _settings.SubtitleFontSize.ToString(CultureInfo.InvariantCulture),
        ["subtitleBorderSize"] = _settings.SubtitleBorderSize.ToString(CultureInfo.InvariantCulture),
        ["subtitleShadowOffset"] = _settings.SubtitleShadowOffset.ToString(CultureInfo.InvariantCulture),
        ["subtitleBasePosition"] = _settings.SubtitleBasePosition.ToString(CultureInfo.InvariantCulture),
        ["audioDevice"] = string.IsNullOrEmpty(_settings.AudioDevice) || _settings.AudioDevice == "auto"
            ? "default" : "custom",
        ["audioPassthrough"] = Bool(_settings.AudioPassthrough),
        ["volumeNormalization"] = Bool(_settings.VolumeNormalization),
        ["autoSkipIntro"] = Bool(_settings.AutoSkipIntro),
        ["autoSkipCredits"] = Bool(_settings.AutoSkipCredits),
        ["skipForward"] = _settings.SkipForwardSeconds.ToString(CultureInfo.InvariantCulture),
        ["skipBackward"] = _settings.SkipBackwardSeconds.ToString(CultureInfo.InvariantCulture),
        ["largeSkip"] = _settings.LargeSkipSeconds.ToString(CultureInfo.InvariantCulture),
        ["autoPlayNext"] = Bool(_settings.AutoPlayNextEpisode),
        ["autoPlayCountdown"] = _settings.AutoPlayCountdownSeconds.ToString(CultureInfo.InvariantCulture),
        ["upNextLead"] = _settings.UpNextLeadSeconds.ToString(CultureInfo.InvariantCulture),
        ["maxParallelDownloads"] = _settings.MaxParallelDownloads.ToString(CultureInfo.InvariantCulture),
        ["defaultDownloadResolution"] = SafeDownloadResolution(_settings.DefaultDownloadResolution),
        ["mediaNextAction"] = _settings.MediaNextAction.ToString(),
        ["mediaPrevAction"] = _settings.MediaPrevAction.ToString(),
        ["transportSeek"] = _settings.TransportSeekSeconds.ToString(CultureInfo.InvariantCulture),
        ["verboseLogging"] = Bool(_settings.VerboseLogging),
        ["updatePolicy"] = _settings.UpdatePolicy.ToString(),
        ["imageCacheMaxMb"] = _settings.ImageCacheMaxMb.ToString(CultureInfo.InvariantCulture),
        ["folderCacheMaxItems"] = _settings.FolderCacheMaxItems.ToString(CultureInfo.InvariantCulture),
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static string SafeDownloadResolution(string? value) => value switch
    {
        "Original" => "original",
        "1080p" => "1080p",
        "720p" => "720p",
        "480p" => "480p",
        null or "" => "unset",
        _ => "custom",
    };

    private void LogSafeSettingsDelta(IReadOnlyDictionary<string, string> before)
    {
        var after = SafeSettingsSnapshot();
        var changes = after.Where(pair => before[pair.Key] != pair.Value)
            .Select(pair => $"{pair.Key}:{before[pair.Key]}->{pair.Value}");
        var delta = string.Join(',', changes);
        if (delta.Length > 0)
            Diagnostics.AppLog.Detail("settings", $"event=change delta={delta}");
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncingPeek)
            return;

        if (ReferenceEquals(sender, MaxLumaBox))
            DisplayBrightnessStatus.Visibility = Visibility.Collapsed;
        var validCountdown = int.TryParse(AutoPlayCountdownBox.Text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var countdown)
            && countdown >= UpNextPreferences.MinimumCountdownSeconds
            && countdown <= UpNextPreferences.MaximumCountdownSeconds;
        AutoPlayCountdownError.Visibility = validCountdown ? Visibility.Collapsed : Visibility.Visible;
        if (!validCountdown)
        {
            LiveText.Bind(AutoPlayCountdownError, "Text", () => L.Format("SettingsView.AutoPlayCountdownInvalid",
                UpNextPreferences.MinimumCountdownSeconds, UpNextPreferences.MaximumCountdownSeconds,
                _settings.AutoPlayCountdownSeconds));
            if (ReferenceEquals(sender, AutoPlayCountdownBox)) return;
        }

        var safeBefore = SafeSettingsSnapshot();
        _settings.HardwareDecoding = HwdecBox.IsChecked == true;
        _settings.RtxVsrDefault = RtxVsrBox.IsChecked == true;
        _settings.RtxHdrDefault = RtxHdrBox.IsChecked == true;
        if (int.TryParse(MaxLumaBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nits)
            && nits is > 0 and <= 10_000)
            _settings.RtxHdrMaxLumaNits = nits;
        var bitrate = (MaxBitrateBox.SelectedItem as BitrateRow)?.Value;
        var validCustomBitrate = int.TryParse(MaxBitrateCustomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps)
            && mbps is > 0 and <= 10_000;
        if (bitrate is { } preset) _settings.MaxStreamingBitrateMbps = preset;
        else if (validCustomBitrate) _settings.MaxStreamingBitrateMbps = mbps;
        MaxBitrateCustomError.Visibility = bitrate is null && !validCustomBitrate
            ? Visibility.Visible : Visibility.Collapsed;
        if (int.TryParse(VideoBufferBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buffer)
            && buffer is >= 32 and <= 1024)
        {
            _settings.VideoBufferMiB = buffer;
            SelectVideoBufferPreset(buffer);
            VideoBufferCustomError.Text = "";
            VideoBufferCustomError.Visibility = Visibility.Collapsed;
        }
        else
        {
            LiveText.Bind(VideoBufferCustomError, "Text", () => L.Format("SettingsView.EnterAWholeNumberFrom32To.b6003a6e", (_settings.VideoBufferMiB)));
            VideoBufferCustomError.Visibility = Visibility.Visible;
        }
        _settings.SubtitleScale = Math.Round(SubScaleSlider.Value, 2);
        if (SubLangBox.SelectedItem is Languages.Option subLang)
            _settings.PreferredSubtitleLanguage = subLang.Code;
        if (SubFontBox.SelectedItem is FontRow font)
            _settings.SubtitleFontFamily = font.Value;
        if (int.TryParse(SubFontSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontSize)
            && fontSize is > 0 and <= 300)
            _settings.SubtitleFontSize = fontSize;
        SaveSubtitleChoices();
        if (AudioLangBox.SelectedItem is Languages.Option audioLang)
            _settings.PreferredAudioLanguage = audioLang.Code;
        if (AudioDeviceBox.SelectedItem is AudioDeviceRow deviceRow)
        {
            // Capability is a property of the DEVICE, so re-probe when the selection moves —
            // otherwise the line under the toggle would keep describing the previous output.
            if (_settings.AudioDevice != deviceRow.Name)
            {
                _settings.AudioDevice = deviceRow.Name;
                UpdatePassthroughCapability();
            }
        }
        _settings.AudioPassthrough = PassthroughBox.IsChecked == true;
        _settings.VolumeNormalization = NormalizationBox.IsChecked == true;
        UpdateAudioExclusivity();
        _settings.AutoSkipIntro = AutoSkipIntroBox.IsChecked == true;
        _settings.AutoSkipCredits = AutoSkipCreditsBox.IsChecked == true;
        if (int.TryParse(SkipForwardBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fwd)
            && fwd is > 0 and <= 600)
            _settings.SkipForwardSeconds = fwd;
        if (int.TryParse(SkipBackwardBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var back)
            && back is > 0 and <= 600)
            _settings.SkipBackwardSeconds = back;
        if (int.TryParse(LargeSkipBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var large)
            && large is > 0 and <= 600)
            _settings.LargeSkipSeconds = large;
        _settings.AutoPlayNextEpisode = AutoPlayNextBox.IsChecked == true;
        UpdateAvailability();
        if (validCountdown)
            _settings.AutoPlayCountdownSeconds = countdown;
        // An invalid custom draft keeps the saved value; the picker shows its own error.
        if (UpNextLeadChoice.TryGetLead(out var lead) && lead is { } leadSeconds)
            _settings.UpNextLeadSeconds = leadSeconds;
        _settings.OmdbApiKey = CurrentOmdbKey();
        // Display the effective directory without pinning the app-data default to this
        // build's path. Empty remains the saved sentinel for the default.
        var downloadDirectory = DownloadDirBox.Text.Trim();
        _settings.DownloadDirectory = downloadDirectory.Equals(Downloads.DownloadStore.DefaultDirectory, StringComparison.OrdinalIgnoreCase)
            ? "" : downloadDirectory;
        if (MaxParallelBox.SelectedIndex >= 0)
            _settings.MaxParallelDownloads = MaxParallelBox.SelectedIndex + 1;
        if (DefaultResolutionBox.SelectedItem is ResolutionRow res)
            _settings.DefaultDownloadResolution = res.Value;
        if (TransportNextBox.SelectedIndex >= 0)
            _settings.MediaNextAction = (TransportAction)TransportNextBox.SelectedIndex;
        if (TransportPrevBox.SelectedIndex >= 0)
            _settings.MediaPrevAction = (TransportAction)TransportPrevBox.SelectedIndex;
        if (int.TryParse(TransportSeekBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tseek)
            && tseek is > 0 and <= 600)
            _settings.TransportSeekSeconds = tseek;
        UpdateTransportSeekVisibility();
        _playerViewModel.NotifyTransportConfigChanged();
        // Verbose logging is live-applied to the facility as well as saved: the mpv client log
        // level is read at Create() (so it takes effect at the next playback), but app.log starts
        // taking informational entries immediately, which is what makes the toggle testable
        // without a restart.
        _settings.VerboseLogging = VerboseLoggingBox.IsChecked == true;
        UpdateVerboseLoggingHint();
        var previousUpdatePolicy = _settings.UpdatePolicy;
        if (UpdatePolicyBox.SelectedIndex >= 0)
            _settings.UpdatePolicy = (UpdatePolicy)UpdatePolicyBox.SelectedIndex;
        if (Diagnostics.AppLog.Verbose != _settings.VerboseLogging)
        {
            // Set the flag BEFORE recording the change so turning it on records its own start;
            // Milestone writes in either direction, so turning it off leaves a line saying why
            // the file stops rather than just stopping.
            Diagnostics.AppLog.Verbose = _settings.VerboseLogging;
            Diagnostics.AppLog.Milestone("settings",
                $"verbose logging turned {(_settings.VerboseLogging ? "on" : "off")} "
                + "(mpv detail applies from the next playback)");
        }
        // Outside the if: the folder grows while this page sits open (a failed playback, an error),
        // and a readout that only refreshes when the toggle moves is stale exactly when the user
        // is looking at it to decide whether to press Clear.
        RefreshLogsReadout();
        _settings.ImageCacheMaxMb = (int)ImageCacheSizeSlider.Value;
        LiveText.Bind(ImageCacheSizeLabel, "Text", () => L.Format("SettingsView.Mb.67a3de26", (_settings.ImageCacheMaxMb)));
        Imaging.ImageCache.MaxCacheBytes = _settings.ImageCacheMaxMb * 1024L * 1024;   // live-apply (M21)
        _settings.FolderCacheMaxItems = (int)FolderCacheSizeSlider.Value;
        FolderCacheSizeLabel.Text = $"{_settings.FolderCacheMaxItems:N0}";
        Jellyfin.BrowseFolderCache.MaxItems = _settings.FolderCacheMaxItems;
        LogSafeSettingsDelta(safeBefore);

        SubScaleLabel.Text = $"{_settings.SubtitleScale * 100:F0}%";
        if (sender is Slider)
        {
            _sliderSavePending = true;
            _sliderSaveTimer.Stop();
            _sliderSaveTimer.Start();
        }
        else
            Save();
        if (previousUpdatePolicy != _settings.UpdatePolicy)
        {
            _updates.OnPolicyChanged();
            RefreshCacheReadouts();
            UpdateUpdateUi(_updates.Status);
        }

        // Live-apply what mpv supports at runtime.
        if (_player() is { } player)
        {
            player.SetSubtitleScale(_settings.SubtitleScale);
            player.SetHardwareDecoding(_settings.HardwareDecoding);
            player.ApplySubtitleStyle(_settings);
            player.ApplyAudioOptions(_settings);
            player.ApplyVideoBuffer(_settings);
        }
        // Base position changed: ask the overlay to recompose. This deliberately does NOT call
        // SetSubtitlePosition here - OverlayWindow.UpdateSubtitlePosition is the single writer of
        // sub-pos, and a direct write was simply lost at the next bar show/hide (Phase 10 M4).
        // Outside playback there is no overlay listening, which is fine: the overlay recomputes
        // on IsVisibleChanged when playback next starts.
        _playerViewModel.NotifySubtitleBasePositionChanged();
        _playerViewModel.ApplyRtxDefaults(_settings);
    }

    /// <summary>Every save this page makes goes through here, so the banner always describes the
    /// latest attempt. Writes the whole settings object, so it also covers a pending slider save.
    /// </summary>
    private void Save()
    {
        _sliderSaveTimer.Stop();
        _sliderSavePending = false;
        SettingsStore.Save(_settings);
        UpdateSaveFailureBanner();
    }

    /// <summary>Writes a slider change that is still waiting for its gesture to settle. Called
    /// when a drag ends, when the page is left, and by the window as it closes.</summary>
    public void FlushPendingSave()
    {
        if (_sliderSavePending) Save();
    }

    /// <summary>Shows why the latest save wrote nothing. Two different situations: an existing
    /// file the app may not read is deliberately left alone (saving would overwrite settings the
    /// user still has), while a failed write is an IO problem that the next save may get past.
    /// Read from the store rather than from this page's own saves, so a failure in a save made
    /// elsewhere (audio device in the player, a show's Up Next lead) shows here on return too.
    /// </summary>
    private void UpdateSaveFailureBanner()
    {
        var failure = SettingsStore.LastSaveFailure;
        var changed = failure != _shownSaveFailure;
        _shownSaveFailure = failure;
        SaveFailureBanner.Visibility = failure == SettingsSaveFailure.None ? Visibility.Collapsed : Visibility.Visible;
        LiveText.Bind(SaveFailureText, "Text", () => failure switch
        {
            SettingsSaveFailure.ReadDenied => L.Text("SettingsView.SaveRefusedUnreadable"),
            SettingsSaveFailure.WriteFailed => L.Text("SettingsView.SaveWriteFailed"),
            _ => string.Empty,
        });
        // WPF does not announce a live region on its own. Once per new failure, not per save,
        // so a run of failed saves does not repeat the same sentence to a screen reader.
        if (changed && failure != SettingsSaveFailure.None)
            (UIElementAutomationPeer.FromElement(SaveFailureText) ?? UIElementAutomationPeer.CreatePeerForElement(SaveFailureText))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>The OMDB key from whichever twin is showing (masked box or reveal field).</summary>
    private string CurrentOmdbKey() =>
        (OmdbKeyReveal.Visibility == Visibility.Visible ? OmdbKeyReveal.Text : OmdbKeyBox.Password).Trim();

    /// <summary>Peek toggle: swaps the masked PasswordBox and the plain reveal TextBox,
    /// copying the current value across. Guarded so the sync doesn't re-trigger a save.</summary>
    private void OnToggleOmdbPeek(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=toggle-api-key-visibility");
        _syncingPeek = true;
        var reveal = OmdbKeyReveal.Visibility != Visibility.Visible;   // about to show plain text
        if (reveal)
        {
            OmdbKeyReveal.Text = OmdbKeyBox.Password;
            OmdbKeyReveal.Visibility = Visibility.Visible;
            OmdbKeyBox.Visibility = Visibility.Collapsed;
            OmdbKeyPeek.Content = (string)FindResource("IconHidden");
        }
        else
        {
            OmdbKeyBox.Password = OmdbKeyReveal.Text;
            OmdbKeyBox.Visibility = Visibility.Visible;
            OmdbKeyReveal.Visibility = Visibility.Collapsed;
            OmdbKeyPeek.Content = (string)FindResource("IconVisible");
        }
        _syncingPeek = false;
    }
}
