using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LightWeaver.Localization;
using LightWeaver.Settings;

namespace LightWeaver.Views;

/// <summary>Chooses how many seconds before the end the Up Next card appears when an episode has
/// no credits marker: the presets, a custom value, and (in the show flyout) Global, which
/// inherits the Settings value. The picker validates but never saves; its host does.</summary>
public partial class UpNextLeadPicker : UserControl
{
    private readonly List<(ListBoxItem Segment, int Seconds)> _presets = [];
    private bool _loading;
    private ErrorKind _error;
    private enum ErrorKind { None, InvalidCustom, SaveFailed }

    /// <summary>The user chose a lead: a preset, a valid custom value, or null for Global.
    /// Showing a value and invalid custom input raise nothing.</summary>
    public event Action<int?>? LeadChosen;

    public UpNextLeadPicker()
    {
        InitializeComponent();
        LiveText.Watch(this);
        foreach (var seconds in UpNextPreferences.LeadPresets)
        {
            var segment = new ListBoxItem();
            AutomationProperties.SetAutomationId(segment, "UpNextLead" + seconds.ToString(CultureInfo.InvariantCulture));
            LiveText.Bind(segment, "Content", () => L.Format("UpNextLeadPicker.SecondsShort", seconds));
            Segments.Items.Insert(Segments.Items.IndexOf(CustomSegment), segment);
            _presets.Add((segment, seconds));
        }
        LiveText.Bind(GlobalSegment, "Content", () => L.Text("UpNextLeadPicker.Global"));
        LiveText.Bind(CustomSegment, "Content", () => L.Text("UpNextLeadPicker.Custom"));
        LiveText.Bind(ErrorText, "Text", () => _error switch
        {
            ErrorKind.InvalidCustom => L.Format("UpNextLeadPicker.InvalidCustom",
                UpNextPreferences.MinimumLeadSeconds, UpNextPreferences.MaximumLeadSeconds),
            ErrorKind.SaveFailed => L.Text("UpNextLeadPicker.SaveFailed"),
            _ => "",
        });
        // Wired here, not in XAML: the runtime XAML EventConverter for LwSegmentedControl needs
        // an exact handler signature (see LibraryView's FilterBox).
        Segments.SelectionChanged += OnSegmentChanged;
    }

    /// <summary>Whether the Global (inherit) segment is offered. The Settings row has none: it IS
    /// the global value.</summary>
    public bool IncludeGlobal
    {
        get => Segments.Items.Contains(GlobalSegment);
        set
        {
            if (value == IncludeGlobal) return;
            var loading = _loading;
            _loading = true;
            try
            {
                if (value) Segments.Items.Insert(0, GlobalSegment);
                else Segments.Items.Remove(GlobalSegment);
            }
            finally { _loading = loading; }
        }
    }

    /// <summary>Selects <paramref name="seconds"/> (null = Global) without raising
    /// <see cref="LeadChosen"/>. <paramref name="customDefault"/> fills the custom field when the
    /// value is not itself custom, so choosing Custom starts from the value in effect.</summary>
    public void Show(int? seconds, int customDefault)
    {
        _loading = true;
        try
        {
            var preset = _presets.FirstOrDefault(p => p.Seconds == seconds).Segment;
            Segments.SelectedItem = seconds is null && IncludeGlobal ? GlobalSegment : preset ?? CustomSegment;
            CustomBox.Text = (seconds ?? customDefault).ToString(CultureInfo.InvariantCulture);
            UpdateCustomVisibility();
            SetError(ErrorKind.None);
        }
        finally { _loading = false; }
    }

    /// <summary>The selected lead (null = Global), or false while the custom field holds a value
    /// that cannot be saved.</summary>
    public bool TryGetLead(out int? seconds)
    {
        seconds = null;
        if (ReferenceEquals(Segments.SelectedItem, GlobalSegment)) return true;
        foreach (var (segment, preset) in _presets)
        {
            if (!ReferenceEquals(segment, Segments.SelectedItem)) continue;
            seconds = preset;
            return true;
        }
        if (!ReferenceEquals(Segments.SelectedItem, CustomSegment) || !TryParseCustom(out var custom)) return false;
        seconds = custom;
        return true;
    }

    /// <summary>The host could not persist the last choice and rolled it back.</summary>
    public void ReportSaveFailed() => SetError(ErrorKind.SaveFailed);

    /// <summary>Moves keyboard focus to the selected segment, so arrows and Escape work as soon
    /// as a flyout opens.</summary>
    public void FocusSelection()
    {
        if (Segments.ItemContainerGenerator.ContainerFromItem(Segments.SelectedItem) is ListBoxItem selected)
            selected.Focus();
        else
            Segments.Focus();
    }

    private void OnSegmentChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomVisibility();
        if (_loading || Segments.SelectedItem is null) return;
        SetError(ErrorKind.None);
        if (ReferenceEquals(Segments.SelectedItem, CustomSegment))
        {
            // Custom saves nothing until the field holds a value, so a click on it never
            // replaces the current choice with whatever the field happens to show.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!ReferenceEquals(Segments.SelectedItem, CustomSegment)) return;
                CustomBox.Focus();
                CustomBox.SelectAll();
            });
            return;
        }
        TryGetLead(out var seconds);
        if (seconds is { } preset)
        {
            // The next switch to Custom starts from the value just chosen.
            _loading = true;
            try { CustomBox.Text = preset.ToString(CultureInfo.InvariantCulture); }
            finally { _loading = false; }
        }
        LeadChosen?.Invoke(seconds);
    }

    private void OnCustomChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !ReferenceEquals(Segments.SelectedItem, CustomSegment)) return;
        if (!TryParseCustom(out var seconds))
        {
            SetError(ErrorKind.InvalidCustom);
            return;
        }
        SetError(ErrorKind.None);
        LeadChosen?.Invoke(seconds);
    }

    private bool TryParseCustom(out int seconds)
        => int.TryParse(CustomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
            && UpNextPreferences.IsValidLead(seconds);

    private void UpdateCustomVisibility()
        => CustomPanel.Visibility = ReferenceEquals(Segments.SelectedItem, CustomSegment)
            ? Visibility.Visible : Visibility.Collapsed;

    private void SetError(ErrorKind error)
    {
        _error = error;
        ErrorText.Visibility = error == ErrorKind.None ? Visibility.Collapsed : Visibility.Visible;
        // Re-evaluate now as well as on live language changes.
        ErrorText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
    }
}
