using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LightWeaver.Localization;
using LightWeaver.Settings;

namespace LightWeaver.Views;

/// <summary>When the Up Next card appears for one whole show: the flyout behind the series and
/// season pill and the episode page's Up Next button. It owns the popup, the picker, the status
/// line and the save; the host owns the trigger and its label, which it refreshes on
/// <see cref="LeadChanged"/>. Settings are read on every open, so a change made on one page shows
/// on the other the next time it is opened.</summary>
public partial class UpNextShowFlyout : UserControl
{
    private const double FlyoutWidth = 440;
    private const double Gap = 8;
    private AppSettings? _settings;
    /// <summary>The show, scoped to the server and Jellyfin user; null until a host attaches one
    /// the session can form a key for.</summary>
    private string? _showKey;
    private FrameworkElement? _trigger;

    /// <summary>The show's lead was saved, or a refused save rolled it back. The host relabels its
    /// trigger.</summary>
    public event Action? LeadChanged;

    public UpNextShowFlyout()
    {
        InitializeComponent();
        LiveText.Watch(this);
        LiveText.Bind(Status, "Text", StatusText);
        Picker.LeadChosen += OnLeadChosen;
    }

    /// <summary>Binds the flyout to a show and anchors it to <paramref name="trigger"/>. Returns
    /// whether the session forms a show key; without one the host hides its trigger. Safe to call
    /// again when the host re-renders: a different show closes an open flyout.</summary>
    public bool Attach(AppSettings settings, string? serverUrl, Guid userId, Guid? seriesId, FrameworkElement trigger)
    {
        var key = UpNextPreferences.TryGetShowKey(serverUrl, userId, seriesId, out var showKey) ? showKey : null;
        if (key != _showKey && FlyoutPopup.IsOpen)
            FlyoutPopup.IsOpen = false;
        _settings = settings;
        _showKey = key;
        _trigger = trigger;
        FlyoutPopup.PlacementTarget = trigger;
        Refresh();
        return key is not null;
    }

    public bool IsOpen => FlyoutPopup.IsOpen;

    /// <summary>The show's own lead, or null when it inherits the global one (or nothing is
    /// attached).</summary>
    public int? ShowLead => _settings is not null && _showKey is not null
        ? UpNextPreferences.GetShowLead(_settings, _showKey) : null;

    public int GlobalLead => _settings?.UpNextLeadSeconds ?? UpNextPreferences.DefaultLeadSeconds;

    /// <summary>Reads the settings into the picker and the status line, places the flyout inside the
    /// window and moves focus to the selected segment.</summary>
    public void Open()
    {
        if (_settings is null || _showKey is null) return;
        Picker.Show(UpNextPreferences.GetShowLead(_settings, _showKey), _settings.UpNextLeadSeconds);
        Refresh();
        Place();
        FlyoutPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FlyoutPopup.IsOpen) Picker.FocusSelection();
        });
    }

    public void Close() => FlyoutPopup.IsOpen = false;

    /// <summary>Re-reads the status line; the picker is re-read on every open.</summary>
    public void Refresh() => Status.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();

    /// <summary>Placement lines the flyout's left edge up with the trigger's. Near the right edge
    /// (the series pill at the 700x480 minimum) a 440-wide flyout would cross the window, so it is
    /// narrowed to the window and shifted left by whatever would overflow. It opens below the
    /// trigger, or above it when only that side has room (an action row low on the episode page).</summary>
    private void Place()
    {
        const double margin = 12;
        if (_trigger is null || Window.GetWindow(_trigger)?.Content is not FrameworkElement root || root.ActualWidth <= 0)
            return;
        // The popup inherits the trigger's UI zoom, so its width and offsets are in the trigger's
        // layout units while the window root is not zoomed. Measure the window in those units.
        var zoom = _trigger.TransformToAncestor(root) is System.Windows.Media.Transform toRoot ? toRoot.Value.M11 : 1.0;
        if (zoom <= 0) zoom = 1.0;
        var rootWidth = root.ActualWidth / zoom;
        var rootHeight = root.ActualHeight / zoom;
        FlyoutPanel.Width = Math.Min(FlyoutWidth, Math.Max(0, rootWidth - 2 * margin));
        var rootOrigin = _trigger.TranslatePoint(new Point(0, 0), root);
        var origin = new Point(rootOrigin.X / zoom, rootOrigin.Y / zoom);
        var overflow = origin.X + FlyoutPanel.Width - (rootWidth - margin);
        FlyoutPopup.HorizontalOffset = overflow > 0 ? -Math.Min(overflow, Math.Max(0, origin.X - margin)) : 0;

        FlyoutPanel.Measure(new Size(FlyoutPanel.Width, double.PositiveInfinity));
        var height = FlyoutPanel.DesiredSize.Height;
        var below = rootHeight - margin - (origin.Y + _trigger.ActualHeight + Gap);
        var above = origin.Y - Gap - margin;
        var up = height > below && above > below;
        FlyoutPopup.Placement = up ? PlacementMode.Top : PlacementMode.Bottom;
        FlyoutPopup.VerticalOffset = up ? -Gap : Gap;
    }

    // While the flyout is open an outside press closes it; if that press lands on the trigger, its
    // Click must not reopen it at once. The trigger takes no hits until the press has been handled.
    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (_trigger is not null) _trigger.IsHitTestVisible = false;
        // Its width cap and offsets are in zoomed units, so a zoom step while it is open re-places it.
        Theme.UiZoom.Changed += OnZoomChanged;
    }

    /// <summary>After layout, not inside the step: until the next measure the trigger's transform
    /// and position still report the previous zoom.</summary>
    private void OnZoomChanged(double scale)
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (FlyoutPopup.IsOpen) Place(); });

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        Theme.UiZoom.Changed -= OnZoomChanged;
        var trigger = _trigger;
        if (trigger is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => trigger.IsHitTestVisible = true);
    }

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        FlyoutPopup.IsOpen = false;
        _trigger?.Focus();
    }

    private void OnLeadChosen(int? seconds)
    {
        if (_settings is null || _showKey is null) return;
        var hadPrevious = _settings.ShowUpNextLeadSeconds.TryGetValue(_showKey, out var previous);
        var before = UpNextPreferences.GetShowLead(_settings, _showKey);
        UpNextPreferences.SetShowLead(_settings, _showKey, seconds);
        if (SettingsStore.Save(_settings))
        {
            Diagnostics.AppLog.Detail("settings", FormattableString.Invariant(
                $"event=change scope=show delta=upNextLead:{before?.ToString() ?? "global"}->{seconds?.ToString() ?? "global"}"));
        }
        else
        {
            // SettingsStore logged why. Keep memory equal to the file, and say so.
            if (hadPrevious) _settings.ShowUpNextLeadSeconds[_showKey] = previous;
            else _settings.ShowUpNextLeadSeconds.Remove(_showKey);
            // Put the selection back on the saved value too, so the picker agrees with the trigger.
            Picker.Show(UpNextPreferences.GetShowLead(_settings, _showKey), _settings.UpNextLeadSeconds);
            Picker.ReportSaveFailed();
        }
        Refresh();
        LeadChanged?.Invoke();
    }

    private object StatusText()
    {
        if (_settings is null || _showKey is null) return "";
        var global = _settings.UpNextLeadSeconds;
        return UpNextPreferences.GetShowLead(_settings, _showKey) is { } seconds
            ? L.Format("UpNextShowFlyout.StatusShow", seconds, global)
            : L.Format("UpNextShowFlyout.StatusGlobal", global);
    }
}
