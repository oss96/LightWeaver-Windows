using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Player settings. Saves on every change; live-applies what mpv supports at runtime
/// (subtitle scale, hwdec, RTX filters).
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

    public SettingsView(AppSettings settings, Func<MpvPlayer?> player, PlayerViewModel playerViewModel,
        UpdateService updates)
    {
        InitializeComponent();
        _settings = settings;
        _player = player;
        _playerViewModel = playerViewModel;
        _updates = updates;
        _updates.StatusChanged += OnUpdateStatusChanged;

        HwdecBox.IsChecked = settings.HardwareDecoding;
        RtxVsrBox.IsChecked = settings.RtxVsrDefault;
        RtxHdrBox.IsChecked = settings.RtxHdrDefault;
        MaxLumaBox.Text = settings.RtxHdrMaxLumaNits.ToString();
        MaxBitrateBox.Text = settings.MaxStreamingBitrateMbps.ToString();
        VideoBufferBox.Text = settings.VideoBufferMiB.ToString(CultureInfo.InvariantCulture);
        SelectVideoBufferPreset(settings.VideoBufferMiB, updateExpansion: true);
        SubScaleSlider.Value = settings.SubtitleScale;
        SubScaleLabel.Text = $"{settings.SubtitleScale * 100:F0}%";
        PopulateLanguages(SubLangBox, settings.PreferredSubtitleLanguage);
        foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies
                     .Select(f => f.Source).OrderBy(s => s))
            SubFontBox.Items.Add(f);
        SubFontBox.Text = settings.SubtitleFontFamily;
        SubFontSizeBox.Text = settings.SubtitleFontSize.ToString();
        SubColorBox.Text = settings.SubtitleTextColor;
        SubBorderColorBox.Text = settings.SubtitleBorderColor;
        SubBorderSizeBox.Text = settings.SubtitleBorderSize.ToString(CultureInfo.InvariantCulture);
        SubShadowBox.Text = settings.SubtitleShadowOffset.ToString(CultureInfo.InvariantCulture);
        SubPositionBox.Text = settings.SubtitleBasePosition.ToString();
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
        OmdbKeyBox.Password = settings.OmdbApiKey;
        DownloadDirBox.Text = settings.DownloadDirectory;
        foreach (var n in new[] { "1", "2", "3", "4" })
            MaxParallelBox.Items.Add(n);
        MaxParallelBox.SelectedIndex = Math.Clamp(settings.MaxParallelDownloads, 1, 4) - 1;
        foreach (var r in new[] { "Original", "1080p", "720p", "480p" })
            DefaultResolutionBox.Items.Add(r);
        DefaultResolutionBox.SelectedIndex = Math.Max(0, Array.IndexOf(
            ["Original", "1080p", "720p", "480p"], settings.DefaultDownloadResolution));
        ImageCacheSizeSlider.Value = Math.Clamp(settings.ImageCacheMaxMb, 100, 2000);
        ImageCacheSizeLabel.Text = $"{(int)ImageCacheSizeSlider.Value} MB";
        FolderCacheSizeSlider.Value = Math.Clamp(settings.FolderCacheMaxItems, 500, 20000);
        FolderCacheSizeLabel.Text = $"{(int)FolderCacheSizeSlider.Value:N0}";
        RefreshCacheReadouts();
        foreach (var label in new[] { "Next in queue", "Next chapter", "Seek forward" })
            TransportNextBox.Items.Add(label);
        foreach (var label in new[] { "Previous in queue", "Previous chapter", "Seek back" })
            TransportPrevBox.Items.Add(label);
        TransportNextBox.SelectedIndex = (int)settings.MediaNextAction;
        TransportPrevBox.SelectedIndex = (int)settings.MediaPrevAction;
        TransportSeekBox.Text = settings.TransportSeekSeconds.ToString();
        UpdateTransportSeekVisibility();
        BuildShortcutRows();
        VerboseLoggingBox.IsChecked = settings.VerboseLogging;
        foreach (var label in new[] { "Manual install", "Auto-download", "Auto-download and install" })
            UpdatePolicyBox.Items.Add(label);
        UpdatePolicyBox.SelectedIndex = (int)settings.UpdatePolicy;
        UpdateUpdateUi(_updates.Status);
        LogsPath.Text = Diagnostics.AppLog.Dir;
        RefreshLogsReadout();
        _loading = false;
        Diagnostics.AppLog.Detail("settings", "event=load outcome=success");
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
            ? checkedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Never";
        UpdateVersionText.Text = $"Current version: {GitHubUpdateClient.CurrentVersion.ToString(3)} · Last checked: {lastChecked}";
        UpdateStatusText.Text = status.Phase switch
        {
            UpdatePhase.Idle => "No update check has run yet.",
            UpdatePhase.Checking => "Checking for updates…",
            UpdatePhase.UpToDate => "LightWeaver is up to date.",
            UpdatePhase.Available => status.Detail ?? $"Version {version} is available.",
            UpdatePhase.Downloading => $"Downloading version {version}…",
            UpdatePhase.ReadyToInstall when _settings.UpdatePolicy == UpdatePolicy.AutoDownloadAndInstall
                => $"Version {version} is ready — it will install when LightWeaver closes."
                    + (status.Detail is { Length: > 0 } ? $" Last check: {status.Detail}" : string.Empty),
            UpdatePhase.ReadyToInstall => $"Version {version} is verified and ready to install."
                + (status.Detail is { Length: > 0 } ? $" Last check: {status.Detail}" : string.Empty),
            UpdatePhase.Installing => $"Starting the installer…",
            UpdatePhase.Error => status.Detail ?? "Update operation failed.",
            _ => string.Empty,
        };
        UpdateDownloadProgress.Visibility = status.Phase == UpdatePhase.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = (status.Progress ?? 0) * 100;
        var hasAsset = status.Update?.SetupAssetUri is not null;
        var manual = _settings.UpdatePolicy == UpdatePolicy.ManualInstall;
        DownloadUpdateButton.Visibility = hasAsset ? Visibility.Visible : Visibility.Collapsed;
        OpenReleasePageButton.Visibility = !hasAsset && status.Update is not null ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = status.Phase is UpdatePhase.Available or UpdatePhase.Error;
        DownloadUpdateButton.Content = status.Phase == UpdatePhase.Error ? "Retry"
            : manual ? "Download and install" : "Download";
        InstallUpdateButton.IsEnabled = status.Phase == UpdatePhase.ReadyToInstall;
        InstallUpdateButton.Visibility = status.Phase == UpdatePhase.ReadyToInstall
            ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = "Install now";
        CheckForUpdatesButton.IsEnabled = status.Phase is not UpdatePhase.Checking and not UpdatePhase.Downloading and not UpdatePhase.Installing;
        CheckForUpdatesButton.Content = status.Phase == UpdatePhase.Error && !hasAsset ? "Retry" : "Check for updates";
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
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => $"{bytes} B",
    };

    private void RefreshCacheReadouts()
    {
        ImageCacheReadout.Text = FormatBytes(Imaging.ImageCache.CurrentSizeBytes());
        MetadataCacheReadout.Text = FormatBytes(Imaging.MetadataCache.CurrentSizeBytes());
        ExternalCacheReadout.Text = FormatBytes(Imaging.ExternalMetadataCache.CurrentSizeBytes());
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
        => LogsReadout.Text = FormatBytes(Diagnostics.AppLog.CurrentSizeBytes());

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
            Title = "Export LightWeaver logs",
            // Invariant timestamp: this filename ends up quoted in bug reports and must sort.
            FileName = $"lightweaver-logs-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip",
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var count = Diagnostics.AppLog.ExportTo(dialog.FileName);
            LogsReadout.Text = count == 0
                ? "nothing to export — no logs have been written"
                : $"exported {count} file{(count == 1 ? "" : "s")} to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogsReadout.Text = $"export failed: {ex.Message}";
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
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            dock.Children.Add(btn);
            dock.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = PlayerActionDispatcher.DisplayName(action),
                Style = (Style)FindResource("RowTitle"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var row = new System.Windows.Controls.Border
            {
                Style = (Style)FindResource("SettingRow"),
                Child = dock,
            };
            if (i == actions.Length - 1)
                row.BorderThickness = new Thickness(0);
            ShortcutsHost.Children.Add(row);
            _shortcutButtons[action] = btn;
        }
    }

    private void OnShortcutCaptureClick(object sender, RoutedEventArgs e)
    {
        var action = (PlayerAction)((System.Windows.Controls.Button)sender).Tag;
        Diagnostics.AppLog.Detail("settings", $"event=interaction action=shortcut-capture target={action}");
        CancelShortcutCapture();
        _capturing = action;
        _shortcutButtons[action].Content = "Press a key…";
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
        var chord = PlayerActionDispatcher.ChordOf(key, System.Windows.Input.Keyboard.Modifiers);
        var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
        // Duplicate handling: the chord moves here; the previous owner shows (unbound).
        foreach (var (other, c) in map.ToList())
            if (other != action && string.Equals(c, chord, StringComparison.OrdinalIgnoreCase))
                map[other] = "";
        map[action] = chord;
        _settings.KeyBindings = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        Diagnostics.AppLog.Detail("settings", $"event=interaction action=shortcut-bind target={action}");
        SettingsStore.Save(_settings);
        RefreshShortcutButtons();
    }

    private void RefreshShortcutButtons()
    {
        var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
        foreach (var (action, btn) in _shortcutButtons)
            btn.Content = PlayerActionDispatcher.DisplayChord(map[action]);
    }

    private void OnShortcutsReset(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("settings", "event=interaction action=shortcut-reset");
        CancelShortcutCapture();
        _settings.KeyBindings = new Dictionary<string, string>();
        SettingsStore.Save(_settings);
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
            selected = new Languages.Option(custom, $"{custom} (saved)");
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
        AudioDeviceBox.Items.Add(new AudioDeviceRow("auto", "System default"));
        foreach (var d in devices.Where(d => d.Name != "auto"))
            AudioDeviceBox.Items.Add(new AudioDeviceRow(d.Name, d.Description));
        if (saved is { Length: > 0 } && saved != "auto"
            && !AudioDeviceBox.Items.Cast<AudioDeviceRow>().Any(r => r.Name == saved))
            AudioDeviceBox.Items.Add(new AudioDeviceRow(saved, $"{saved} (saved)"));
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
        NormalizationBox.IsEnabled = PassthroughBox.IsChecked != true;
        PassthroughBox.IsEnabled = NormalizationBox.IsChecked != true;
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
        var probe = await Task.Run(() => Player.AudioPassthroughProbe.Probe(device));
        var line = Player.AudioPassthroughProbe.Describe(probe);
        // The selection may have moved on while the probe ran; the answer would then be about
        // the wrong device, and a stale capability claim is worse than none.
        if ((AudioDeviceBox.SelectedItem as AudioDeviceRow)?.Name != device)
            return;
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
            Title = "Choose the download folder",
            Multiselect = false,
        };
        var current = DownloadDirBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
            dialog.InitialDirectory = current;

        // A UserControl is not a Window, so hand the dialog our hosting window.
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            DownloadDirBox.Text = dialog.FolderName;
    }

    /// <summary>Back to the app-data default — an empty value is what `DownloadStore` reads as
    /// "use the default folder", so this is a clear rather than a path.</summary>
    private void OnClearDownloadDir(object sender, RoutedEventArgs e) => DownloadDirBox.Text = "";

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

        var safeBefore = SafeSettingsSnapshot();
        _settings.HardwareDecoding = HwdecBox.IsChecked == true;
        _settings.RtxVsrDefault = RtxVsrBox.IsChecked == true;
        _settings.RtxHdrDefault = RtxHdrBox.IsChecked == true;
        if (int.TryParse(MaxLumaBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nits)
            && nits is > 0 and <= 10_000)
            _settings.RtxHdrMaxLumaNits = nits;
        if (int.TryParse(MaxBitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps)
            && mbps is >= 0 and <= 10_000)
            _settings.MaxStreamingBitrateMbps = mbps;
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
            VideoBufferCustomError.Text = $"Enter a whole number from 32 to 1024 MiB. Last saved: {_settings.VideoBufferMiB} MiB.";
            VideoBufferCustomError.Visibility = Visibility.Visible;
        }
        _settings.SubtitleScale = Math.Round(SubScaleSlider.Value, 2);
        if (SubLangBox.SelectedItem is Languages.Option subLang)
            _settings.PreferredSubtitleLanguage = subLang.Code;
        _settings.SubtitleFontFamily = SubFontBox.Text.Trim();
        if (int.TryParse(SubFontSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontSize)
            && fontSize is > 0 and <= 300)
            _settings.SubtitleFontSize = fontSize;
        var subColor = SubColorBox.Text.Trim();
        if (subColor.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(subColor, "^#[0-9a-fA-F]{6}$"))
            _settings.SubtitleTextColor = subColor;
        var borderColor = SubBorderColorBox.Text.Trim();
        if (borderColor.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(borderColor, "^#[0-9a-fA-F]{6}$"))
            _settings.SubtitleBorderColor = borderColor;
        if (double.TryParse(SubBorderSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var borderSize)
            && borderSize is >= 0 and <= 20)
            _settings.SubtitleBorderSize = borderSize;
        if (double.TryParse(SubShadowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var shadow)
            && shadow is >= -20 and <= 20)
            _settings.SubtitleShadowOffset = shadow;
        if (int.TryParse(SubPositionBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subPos)
            && subPos is >= 0 and <= 150)
            _settings.SubtitleBasePosition = subPos;
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
        if (int.TryParse(AutoPlayCountdownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cd)
            && cd is >= 3 and <= 120)
            _settings.AutoPlayCountdownSeconds = cd;
        _settings.OmdbApiKey = CurrentOmdbKey();
        // Downloads (M20): directory validity is checked at enqueue time (the engine
        // creates it); an empty box means the app-data default.
        _settings.DownloadDirectory = DownloadDirBox.Text.Trim();
        if (MaxParallelBox.SelectedIndex >= 0)
            _settings.MaxParallelDownloads = MaxParallelBox.SelectedIndex + 1;
        if (DefaultResolutionBox.SelectedItem is string res)
            _settings.DefaultDownloadResolution = res;
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
        ImageCacheSizeLabel.Text = $"{_settings.ImageCacheMaxMb} MB";
        Imaging.ImageCache.MaxCacheBytes = _settings.ImageCacheMaxMb * 1024L * 1024;   // live-apply (M21)
        _settings.FolderCacheMaxItems = (int)FolderCacheSizeSlider.Value;
        FolderCacheSizeLabel.Text = $"{_settings.FolderCacheMaxItems:N0}";
        Jellyfin.BrowseFolderCache.MaxItems = _settings.FolderCacheMaxItems;
        LogSafeSettingsDelta(safeBefore);

        SubScaleLabel.Text = $"{_settings.SubtitleScale * 100:F0}%";
        SettingsStore.Save(_settings);
        if (previousUpdatePolicy != _settings.UpdatePolicy)
        {
            _updates.OnPolicyChanged();
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
