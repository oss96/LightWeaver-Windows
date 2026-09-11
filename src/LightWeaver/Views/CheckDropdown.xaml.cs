using System.Windows;
using System.Windows.Controls;

namespace LightWeaver.Views;

/// <summary>One selectable option of a <see cref="CheckDropdown"/>: a stable key plus its
/// display label. <see cref="ToString"/> is the label so a plain ListBox shows it.</summary>
public sealed record CheckOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A woven-light multi-select dropdown (Phase 7 M4): a storm-2 trigger showing a live summary
/// ("All types" / a single label / "N selected") that opens a checkable list. Used for the
/// Advanced Search item-type and genre facets, where a plain <c>ComboBox</c> can't express
/// multi-select. The inner list is a multi-select <see cref="ListBox"/> so UIA sees each row
/// as a selectable item.
/// </summary>
public partial class CheckDropdown : UserControl
{
    private readonly List<CheckOption> _options = [];
    private bool _suppress;

    /// <summary>Raised when the selected set changes through user interaction (not while
    /// options are being (re)seeded programmatically).</summary>
    public event Action? SelectionChanged;

    public CheckDropdown()
    {
        InitializeComponent();
        UpdateSummary();
    }

    /// <summary>Placeholder shown when nothing is selected (e.g. "All types", "Any genre").</summary>
    public string Placeholder { get; set; } = "Any";

    /// <summary>Whether the option set has been populated (async facet sources load late).</summary>
    public bool HasOptions => _options.Count > 0;

    /// <summary>Replace the option set, preserving any still-valid current selection.</summary>
    public void SetOptions(IEnumerable<CheckOption> options)
    {
        var keep = new HashSet<string>(SelectedKeys);
        _suppress = true;
        _options.Clear();
        _options.AddRange(options);
        OptionsList.ItemsSource = null;
        OptionsList.ItemsSource = _options;
        foreach (var opt in _options)
            if (keep.Contains(opt.Key))
                OptionsList.SelectedItems.Add(opt);
        _suppress = false;
        UpdateSummary();
    }

    /// <summary>Currently selected option keys, in option order.</summary>
    public IReadOnlyList<string> SelectedKeys =>
        _options.Where(o => OptionsList.SelectedItems.Contains(o)).Select(o => o.Key).ToList();

    /// <summary>Set the selection programmatically (no <see cref="SelectionChanged"/>).</summary>
    public void SetSelectedKeys(IEnumerable<string> keys)
    {
        var want = new HashSet<string>(keys);
        _suppress = true;
        OptionsList.SelectedItems.Clear();
        foreach (var opt in _options)
            if (want.Contains(opt.Key))
                OptionsList.SelectedItems.Add(opt);
        _suppress = false;
        UpdateSummary();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSummary();
        if (!_suppress)
            SelectionChanged?.Invoke();
    }

    /// <summary>ASS redesign P9 footer: clear the whole selection (fires
    /// <see cref="SelectionChanged"/> once via the list's own event).</summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        OptionsList.SelectedItems.Clear();
        DropPopup.IsOpen = false;
    }

    private void UpdateSummary()
    {
        var selected = _options.Where(o => OptionsList.SelectedItems.Contains(o)).ToList();
        Toggle.ApplyTemplate();
        if (Toggle.Template.FindName("SummaryText", Toggle) is not TextBlock summary)
            return;
        // ASS redesign P9: joined labels (up to 2) + a count badge from 2 selections on,
        // so multi-select reads as multi-select ("Action, Sci-Fi [2]").
        summary.Text = selected.Count switch
        {
            0 => Placeholder,
            1 => selected[0].Label,
            _ => string.Join(", ", selected.Take(2).Select(o => o.Label)),
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty,
            selected.Count == 0 ? "LwText3Brush" : "LwText1Brush");
        if (Toggle.Template.FindName("CountBadge", Toggle) is Border badge &&
            Toggle.Template.FindName("CountText", Toggle) is TextBlock count)
        {
            badge.Visibility = selected.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
