using LightWeaver.Localization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Player;

namespace LightWeaver.Views;

/// <summary>The download's quality and named tracks are chosen together, before queueing.</summary>
internal sealed class DownloadOptionsPicker : StackPanel
{
    private readonly IReadOnlyList<DownloadOption> _options;
    internal ComboBox AudioChoice { get; } = new();
    internal ComboBox SubtitleChoice { get; } = new();
    private ListBox? _quality;
    private ModalHost? _modal;
    private bool _updating;
    private bool _useDefaultAudio = true;
    private readonly string? _preferredAudio;

    private sealed record Choice(string Label, DownloadTrack? Track = null)
    {
        public override string ToString() => Label;
    }

    private static void AddChoice(ComboBox combo, Func<Choice> choice)
        => combo.Items.Add(LiveText.Bind(new ComboBoxItem(), "Content", () => choice()));

    private static Choice? SelectedChoice(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Content as Choice;

    internal DownloadOptionsPicker(IReadOnlyList<DownloadOption> options,
        string? preferredAudio = null, string? preferredSubtitle = null)
    {
        _options = options;
        _preferredAudio = preferredAudio;
        Margin = new Thickness(0, 12, 0, 0);
        var source = options[0];
        AddChoice(AudioChoice, () => new Choice(source.AudioTracks.Count == 0 ? L.Text("Downloads.NoAudioTracks") : L.Text("Downloads.AllAudioLanguages")));
        foreach (var track in source.AudioTracks)
            AddChoice(AudioChoice, () => new Choice(track.Display + (track.IsDefault ? L.Text("Downloads.DefaultTrack") : ""), track));
        var audio = source.AudioTracks.ToList().IndexOf(DownloadLanguages.DefaultAudio(source, preferredAudio)!);
        AudioChoice.SelectedIndex = audio + 1;
        AddChoice(SubtitleChoice, () => new Choice(L.Text("Downloads.None")));
        AddChoice(SubtitleChoice, () => new Choice(L.Text("Downloads.AllSubtitles")));
        foreach (var track in source.SubtitleTracks)
            AddChoice(SubtitleChoice, () => new Choice(track.Display, track));
        var subtitle = source.SubtitleTracks.ToList().FindIndex(t => Languages.Matches(t.Language, preferredSubtitle));
        SubtitleChoice.SelectedIndex = subtitle >= 0 ? subtitle + 2 : 0;
        if (source.SubtitleTracks.Count == 0) SubtitleChoice.SelectedIndex = 0;
        AudioChoice.IsEnabled = source.AudioTracks.Count > 0;
        SubtitleChoice.IsEnabled = source.SubtitleTracks.Count > 0;
        AutomationProperties.SetAutomationId(AudioChoice, "DownloadAudioChoice");
        LiveText.Bind(AudioChoice, AutomationProperties.NameProperty, () => L.Text("Downloads.AudioLanguage"));
        AutomationProperties.SetAutomationId(SubtitleChoice, "DownloadSubtitleChoice");
        LiveText.Bind(SubtitleChoice, AutomationProperties.NameProperty, () => L.Text("Downloads.SubtitleLanguage"));
        AddField(() => L.Text("Downloads.Audio"), AudioChoice);
        AddField(() => L.Text("Downloads.Subtitles"), SubtitleChoice);
        AudioChoice.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _useDefaultAudio = false;
            UpdateSelection();
        };
        SubtitleChoice.SelectionChanged += (_, _) => UpdateSelection();
    }

    private TextBlock Label(Func<string> text, double size = 13) => LiveText.Bind(new TextBlock()
    {
        FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("LwText2Brush"),
    }, "Text", () => text());

    private void AddField(Func<string> title, ComboBox combo)
    {
        var label = Label(title);
        label.Margin = new Thickness(0, 8, 0, 5);
        combo.MinHeight = 34;
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        Children.Add(label);
        Children.Add(combo);
    }

    internal DownloadSelection Selection => new(
        _options[Math.Max(0, _quality?.SelectedIndex ?? 0)],
        SelectedChoice(AudioChoice)?.Track,
        AllSubtitles: SubtitleChoice.SelectedIndex == 1,
        Subtitle: SelectedChoice(SubtitleChoice)?.Track)
        { UseDefaultAudio = _useDefaultAudio, PreferredAudioLanguage = _preferredAudio };

    private void UpdateSelection()
    {
        if (_quality is null || _updating || _quality.SelectedIndex < 0) return;
        _updating = true;
        try
        {
            var quality = _options[_quality.SelectedIndex];
            foreach (ComboBoxItem item in AudioChoice.Items)
            {
                var choice = (Choice)item.Content;
                SetAvailable(item, (choice.Track is not null || quality.IsOriginal)
                    && IsCompatible(new DownloadSelection(quality, choice.Track)));
            }
            if (AudioChoice.IsEnabled && AudioChoice.SelectedItem is ComboBoxItem { IsEnabled: false })
            {
                var defaultAudio = DownloadLanguages.DefaultAudio(quality, _preferredAudio);
                AudioChoice.SelectedIndex = quality.AudioTracks.ToList().IndexOf(defaultAudio!) + 1;
                _useDefaultAudio = true;
            }
            for (var i = 0; i < SubtitleChoice.Items.Count; i++)
            {
                var item = (ComboBoxItem)SubtitleChoice.Items[i];
                var choice = (Choice)item.Content;
                SetAvailable(item, IsCompatible(new DownloadSelection(quality,
                    SelectedChoice(AudioChoice)?.Track, AllSubtitles: i == 1, Subtitle: choice.Track)));
            }
            if (SubtitleChoice.SelectedItem is ComboBoxItem { IsEnabled: false })
                SubtitleChoice.SelectedIndex = 0;
            _modal?.SetConfirmationEnabled(IsCompatible(Selection));
        }
        finally { _updating = false; }
    }

    private static bool IsCompatible(DownloadSelection selection)
    {
        try { return DownloadLanguages.EffectiveQuality(selection) == selection.Quality; }
        catch (InvalidOperationException) { return false; }
    }

    private static void SetAvailable(ComboBoxItem item, bool available)
    {
        item.IsEnabled = available;
        item.Opacity = available ? 1 : 0.4;
    }

    internal async Task<DownloadSelection?> ChooseAsync(ModalHost modal, string title, int preselect)
    {
        _modal = modal;
        SelectionChangedEventHandler changed = (_, _) => UpdateSelection();
        try
        {
            var picked = await modal.ChooseAsync(L.Text("Downloads.Download"), title, _options.Select(o => o.Label).ToList(),
                L.Text("Downloads.Download"), preselect, this, list =>
                {
                    LiveText.Bind((TextBlock)modal.FindName("TitleText"), "Text", () => L.Text("Downloads.Download"));
                    LiveText.Bind((Button)modal.FindName("ConfirmButton"), "Content", () => L.Text("Downloads.Download"));
                    _quality = list;
                    VirtualizingPanel.SetIsVirtualizing(list, false);
                    list.UpdateLayout();
                    for (var index = 0; index < _options.Count; index++)
                    {
                        var option = _options[index];
                        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row)
                            LiveText.Bind(row, "Content", () => option.DisplayLabel);
                    }
                    list.MaxHeight = 140;
                    list.SelectionChanged += changed;
                    UpdateSelection();
                });
            if (picked is null) return null;
            var selection = Selection with { Quality = _options[picked.Value] };
            return IsCompatible(selection) ? selection : null;
        }
        finally
        {
            if (_quality is not null)
            {
                _quality.SelectionChanged -= changed;
                _quality.MaxHeight = 260;
            }
            _quality = null;
            _modal = null;
        }
    }
}
