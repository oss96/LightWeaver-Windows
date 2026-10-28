using LightWeaver.Localization;
using LightWeaver.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>Edits the current show's Up Next lead.</summary>
internal static class UpNextLeadFlyout
{
    /// <summary>Shows the action when the session identifies a show.</summary>
    public static void Attach(Button button, string? serverUrl, Guid userId, Guid? seriesId)
    {
        if (!UpNextPreferences.TryGetShowKey(serverUrl, userId, seriesId, out var key))
        {
            button.Visibility = Visibility.Collapsed;
            return;
        }
        button.Visibility = Visibility.Visible;
        NameButton(button, key);
        button.Loaded += (_, _) => NameButton(button, key);
        LiveText.Observe(button, (_, _) => button.DispatcherQueue.TryEnqueue(() => NameButton(button, key)));
        button.Click += (_, _) => Show(button, key);
    }

    private static string Seconds(int seconds) => L.Format("UpNextLeadPicker.SecondsShort", seconds);

    private static void NameButton(Button button, string key)
    {
        var settings = App.Settings;
        var name = UpNextPreferences.GetShowLead(settings, key) is { } seconds
            ? L.Format("ItemDetailView.UpNextTooltip", Seconds(seconds))
            : L.Format("ItemDetailView.UpNextTooltipGlobal", Seconds(settings.UpNextLeadSeconds));
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private static void Show(Button button, string key)
    {
        var settings = App.Settings;
        var shown = UpNextPreferences.GetShowLead(settings, key);
        var panel = new StackPanel { Spacing = 10, MinWidth = 300, MaxWidth = 380 };
        AutomationProperties.SetAutomationId(panel, "UpNextLeadFlyout");
        panel.Children.Add(new TextBlock { Text = L.Text("LibraryView.UpNextTooltip"), Style = Ui.Resource<Style>("LwFilterLabel"), TextWrapping = TextWrapping.WrapWholeWords });

        var segments = new WrapPanel { HorizontalSpacing = 0, VerticalSpacing = 2 };
        AutomationProperties.SetName(segments, L.Text("UpNextLeadPicker.Name"));
        panel.Children.Add(new Border { Style = Ui.Resource<Style>("LwSegmentTrack"), HorizontalAlignment = HorizontalAlignment.Left, Child = segments });

        var custom = new NumberBox
        {
            Value = shown ?? settings.UpNextLeadSeconds,
            Minimum = UpNextPreferences.MinimumLeadSeconds,
            Maximum = UpNextPreferences.MaximumLeadSeconds,
            SmallChange = 5,
            LargeChange = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 120,
        };
        AutomationProperties.SetName(custom, L.Text("UpNextLeadPicker.CustomField"));
        AutomationProperties.SetAutomationId(custom, "UpNextLeadCustom");
        panel.Children.Add(custom);

        var status = new TextBlock { FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.WrapWholeWords };
        AutomationProperties.SetAutomationId(status, "UpNextLeadStatus");
        panel.Children.Add(status);

        void Refresh()
        {
            status.Text = UpNextPreferences.GetShowLead(settings, key) is { } seconds
                ? L.Format("UpNextShowFlyout.StatusShow", seconds, settings.UpNextLeadSeconds)
                : L.Format("UpNextShowFlyout.StatusGlobal", settings.UpNextLeadSeconds);
            NameButton(button, key);
        }

        void Save(int? seconds)
        {
            UpNextPreferences.SetShowLead(settings, key, seconds);
            App.SaveSettings();
            Refresh();
        }

        var isCustom = shown is { } value && !UpNextPreferences.LeadPresets.Contains(value);
        var seeding = false;
        RadioButton? selected = null;
        (int? Seconds, string Label, string Id)[] options =
        [
            (null, L.Text("UpNextLeadPicker.Global"), "UpNextLeadGlobal"),
            .. UpNextPreferences.LeadPresets.Select(s => ((int?)s, Seconds(s), "UpNextLead" + s)),
            (-1, L.Text("UpNextLeadPicker.Custom"), "UpNextLeadCustomSegment"),
        ];
        foreach (var (seconds, label, id) in options)
        {
            var customOption = seconds == -1;
            var on = customOption ? isCustom : !isCustom && seconds == shown;
            var segment = new RadioButton { Style = Ui.Resource<Style>("LwSegment"), GroupName = "UpNextLead" + key, Content = label, Padding = new Thickness(10, 0, 10, 0), IsChecked = on };
            AutomationProperties.SetAutomationId(segment, id);
            if (on) selected = segment;
            segment.Checked += (_, _) =>
            {
                custom.Visibility = customOption ? Visibility.Visible : Visibility.Collapsed;
                if (customOption)
                {
                    seeding = true;
                    custom.Value = UpNextPreferences.GetShowLead(settings, key) ?? settings.UpNextLeadSeconds;
                    seeding = false;
                    custom.Focus(FocusState.Programmatic);
                }
                else Save(seconds);
            };
            segments.Children.Add(segment);
        }
        custom.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        custom.ValueChanged += (_, e) => { if (!seeding && custom.Visibility == Visibility.Visible && double.IsFinite(e.NewValue)) Save((int)Math.Round(e.NewValue)); };
        Refresh();

        var flyout = FastUi.Flyout(new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, Content = panel });
        flyout.Opened += (_, _) => selected?.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) => { if (button.IsLoaded) button.Focus(FocusState.Programmatic); };
        flyout.ShowAt(button);
    }
}
