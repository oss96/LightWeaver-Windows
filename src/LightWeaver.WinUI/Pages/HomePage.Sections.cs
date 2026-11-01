using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

public sealed partial class HomePage
{
    private Button _customize = null!;
    private Flyout? _sectionsFlyout;
    private readonly Dictionary<HomeSectionId, ToggleSwitch> _sectionToggles = [];

    private void InitializeSections()
    {
        _customize = new Button
        {
            Style = Ui.Resource<Style>("LwSubtleButton"),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new PathIcon { Style = Ui.Resource<Style>("LwHomeSectionsIcon") },
        };
        AutomationProperties.SetAutomationId(_customize, "HomeCustomizeButton");
        _customize.Click += (_, _) => OpenSections();
        foreach (var row in Rows)
            row.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => PlaceCustomizeButton());
    }

    private void PlaceCustomizeButton()
    {
        var first = _rows.Children.OfType<CardRow>().FirstOrDefault(r => r.Visibility == Visibility.Visible);
        foreach (var row in Rows) row.ReserveHeaderEnd(ReferenceEquals(row, first) ? 40 : 0);
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        _customize.HorizontalAlignment = rtl ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _customize.Margin = new Thickness(rtl ? 24 : 0, 18, rtl ? 0 : 24, 0);
    }

    private void OpenSections()
    {
        if (_profileKey != Shell.CacheProfileKey) return;
        if (_sectionsFlyout is { } open) { open.Hide(); return; }
        var panel = new StackPanel { MinWidth = 236, Spacing = 4, FlowDirection = FlowDirection };
        AutomationProperties.SetAutomationId(panel, "HomeCustomizeFlyout");
        AutomationProperties.SetName(panel, L.Text("Home.CustomizeSections"));
        var drag = new HomeSectionDrag(_profileKey, () => _profileKey == Shell.CacheProfileKey, () =>
        {
            ApplyVisibility();
            var ordered = HomeLayoutStore.Load(_profileKey).Select(p => p.Id).ToList();
            var lines = panel.Children.OfType<Border>().ToDictionary(b => (HomeSectionId)b.Tag);
            foreach (var id in ordered)
                if (lines.TryGetValue(id, out var line)) { panel.Children.Remove(line); panel.Children.Add(line); }
        });
        _sectionToggles.Clear();
        foreach (var pref in HomeLayoutStore.Load(_profileKey))
        {
            if (RowFor(pref.Id) is not { } row) continue;
            var toggle = new ToggleSwitch { IsOn = pref.Enabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(toggle, "HomeSectionToggle" + pref.Id);
            AutomationProperties.SetName(toggle, row.SectionTitle);
            var label = new TextBlock { Text = row.SectionTitle, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var line = new Grid { MinHeight = 40, ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var handle = drag.Handle(pref.Id, row.SectionTitle, "HomeSectionDrag");
            line.Children.Add(handle);
            Grid.SetColumn(label, 1);
            line.Children.Add(label);
            Grid.SetColumn(toggle, 2);
            line.Children.Add(toggle);
            var border = new Border { Child = line, Tag = pref.Id, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            drag.Attach(border, pref.Id);
            panel.Children.Add(border);
            _sectionToggles.Add(pref.Id, toggle);
            toggle.Toggled += (_, _) => SetSectionEnabled(pref.Id, toggle.IsOn);
        }
        var flyout = _sectionsFlyout = FastUi.Flyout(new Flyout
        {
            Placement = FlowDirection == FlowDirection.RightToLeft ? FlyoutPlacementMode.BottomEdgeAlignedLeft : FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = panel,
        });
        flyout.Opened += (_, _) => _sectionToggles.Values.FirstOrDefault()?.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            _sectionToggles.Clear();
            _sectionsFlyout = null;
            if (!IsLoaded || !_customize.IsLoaded) return;
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            _customize.Focus(FocusState.Programmatic);
        };
        flyout.ShowAt(_customize);
    }

    private void SetSectionEnabled(HomeSectionId id, bool enabled)
    {
        if (_profileKey != Shell.CacheProfileKey) return;
        var layout = HomeLayoutStore.Load(_profileKey);
        var index = layout.FindIndex(p => p.Id == id);
        if (index < 0) return;
        layout[index] = layout[index] with { Enabled = enabled };
        HomeLayoutStore.Save(_profileKey, layout);
        ApplyVisibility();
    }

    private void ApplySectionTexts()
    {
        var name = L.Text("Home.CustomizeSections");
        AutomationProperties.SetName(_customize, name);
        ToolTipService.SetToolTip(_customize, name);
        if (_sectionsFlyout?.Content is not StackPanel panel) return;
        panel.FlowDirection = FlowDirection;
        _sectionsFlyout.Placement = FlowDirection == FlowDirection.RightToLeft ? FlyoutPlacementMode.BottomEdgeAlignedLeft : FlyoutPlacementMode.BottomEdgeAlignedRight;
        AutomationProperties.SetName(panel, name);
        foreach (var border in panel.Children.OfType<Border>())
            if (border.Tag is HomeSectionId id && border.Child is Grid line && RowFor(id) is { } row)
            {
                ((TextBlock)line.Children[1]).Text = row.SectionTitle;
                AutomationProperties.SetName((Control)line.Children[0], L.Format("Home.ReorderSection", row.SectionTitle));
                ToolTipService.SetToolTip((Control)line.Children[0], L.Text("Home.ReorderHint"));
                AutomationProperties.SetName(_sectionToggles[id], row.SectionTitle);
            }
    }

    private void ApplyEmptyTexts()
    {
        var allHidden = Rows.All(_hidden.Contains);
        _emptyTitle.Text = allHidden ? L.Text("Home.AllSectionsHidden") : L.Text("Home.EmptyTitle");
        _emptyText.Text = L.Text("Home.EmptyText");
        _emptyButton.Content = L.Text("Home.EmptyBrowse");
        _emptyText.Visibility = _emptyButton.Visibility = allHidden ? Visibility.Collapsed : Visibility.Visible;
    }
}
