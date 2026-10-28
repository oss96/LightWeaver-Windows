using System.Diagnostics;
using System.Globalization;
using LightWeaver.Diagnostics;
using LightWeaver.Downloads;
using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Login;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Pages;

/// <summary>Settings (design 10, Windows): a page in the main window with the sections on the left
/// (280 px) and cards on the right. Every change applies at once; there is no Save. The search box
/// filters rows across all sections.</summary>
public sealed partial class SettingsPage : Grid, INavPage
{
    private readonly IAppShell _shell;
    private readonly AppSettings _settings = App.Settings;
    private readonly ListView _sections = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _search = new();
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _body = new() { Spacing = 22, Padding = new Thickness(32, 4, 32, 32) };
    private readonly List<(string Key, string Glyph, Func<string> Label, Func<IEnumerable<Group>> Build)> _sectionList = [];
    private string _section = "server";

    private sealed record Row(string Label, string? Description, string? Glyph, FrameworkElement Control, string? AutomationId = null, TextBlock? LiveDescription = null);
    private sealed record Group(string? Title, IReadOnlyList<Row> Rows, IReadOnlyList<Button>? Foot = null, FrameworkElement? Extra = null);

    public SettingsPage(IAppShell shell, string? section = null)
    {
        _shell = shell;
        _section = section ?? "server";
        AutomationProperties.SetAutomationId(this, "SettingsPage");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _sectionList.AddRange(
        [
            ("server", Theme.Icons.Server, () => L.Text("Settings.SectionServer"), ServerSection),
            ("home", Theme.Icons.Home, () => L.Text("Settings.SectionHome"), HomeSection),
            ("playback", Theme.Icons.Play, () => L.Text("Settings.SectionPlayback"), PlaybackSection),
            ("subtitles", Theme.Icons.Subtitles, () => L.Text("Settings.SectionSubtitles"), SubtitlesSection),
            ("audio", Theme.Icons.Volume, () => L.Text("Settings.SectionAudio"), AudioSection),
            ("video", Theme.Icons.Videocam, () => L.Text("Settings.SectionVideo"), VideoSection),
            ("downloads", Theme.Icons.Download, () => L.Text("Settings.SectionDownloads"), DownloadsSection),
            ("storage", Theme.Icons.Storage, () => L.Text("Settings.SectionStorage"), StorageSection),
            ("language", Theme.Icons.Globe, () => L.Text("Settings.SectionLanguage"), LanguageSection),
            ("keys", Theme.Icons.Settings, () => L.Text("Settings.SectionKeys"), KeysSection),
            ("diag", Theme.Icons.Info, () => L.Text("Settings.SectionDiagnostics"), DiagnosticsSection),
            ("updates", Theme.Icons.Refresh, () => L.Text("Settings.SectionUpdates"), UpdatesSection),
        ]);

        var side = new Grid { Padding = new Thickness(12, 12, 8, 12), RowSpacing = 10 };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetAutomationId(_search, "SettingsSearch");
        _search.TextChanged += (_, _) => Show();
        side.Children.Add(_search);
        AutomationProperties.SetAutomationId(_sections, "SettingsSections");
        _sections.SelectionChanged += (_, _) =>
        {
            if (_sections.SelectedItem is ListViewItem { Tag: string key } && key != _section)
            {
                _section = key;
                _search.Text = "";
                Show();
            }
        };
        SetRow(_sections, 1);
        side.Children.Add(_sections);
        Children.Add(side);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _title.Margin = new Thickness(32, 20, 32, 12);
        AutomationProperties.SetAutomationId(_title, "SettingsTitle");
        AutomationProperties.SetHeadingLevel(_title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        right.Children.Add(_title);
        var scroller = new ScrollViewer { Content = _body, HorizontalScrollMode = ScrollMode.Disabled };
        AutomationProperties.SetAutomationId(scroller, "SettingsScroller");
        SetRow(scroller, 1);
        right.Children.Add(scroller);
        SetColumn(right, 1);
        Children.Add(right);

        BuildSectionList();
        Show();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(() => { BuildSectionList(); Show(); }));
    }

    public void OnShown() => Show();

    /// <summary>Settings › Updates is on screen, not a search.</summary>
    public bool ShowsUpdates => _section == "updates" && _search.Text.Trim().Length == 0;

    /// <summary>Shows one section with the search cleared, also when it is already selected.</summary>
    public void ShowSection(string key)
    {
        _section = key;
        _search.Text = "";
        _sections.SelectedItem = _sections.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is string tag && tag == key);
        Show();
    }

    private void BuildSectionList()
    {
        _search.PlaceholderText = L.Text("Settings.Search");
        AutomationProperties.SetName(_search, L.Text("Settings.Search"));
        _sections.Items.Clear();
        foreach (var (key, glyph, label, _) in _sectionList)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(Ui.Icon(glyph, 16));
            content.Children.Add(new TextBlock { Text = label(), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListViewItem { Content = content, Tag = key, MinHeight = 36 };
            AutomationProperties.SetAutomationId(item, "SettingsSection" + char.ToUpperInvariant(key[0]) + key[1..]);
            AutomationProperties.SetName(item, label());
            _sections.Items.Add(item);
            if (key == _section) _sections.SelectedItem = item;
        }
    }

    /// <summary>One section, or every matching row when the search box has text. The control that
    /// had focus takes it again once its row is rebuilt, so focus never falls out of the page.</summary>
    private void Show()
    {
        var focused = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control control && IsInside(control, _body)
            && AutomationProperties.GetAutomationId(control) is { Length: > 0 } id ? (Id: id, control.FocusState) : default;
        Build();
        if (focused.Id is not null && FindById(_body, focused.Id) is Control again) FocusWhenLoaded(again, focused.FocusState);
    }

    private static void FocusWhenLoaded(Control control, FocusState state)
    {
        if (control.IsLoaded)
        {
            control.Focus(state);
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            control.Loaded -= OnLoaded;
            control.Focus(state);
        }
        control.Loaded += OnLoaded;
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    private static FrameworkElement? FindById(DependencyObject node, string id)
    {
        if (node is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindById(VisualTreeHelper.GetChild(node, i), id) is { } found) return found;
        return null;
    }

    private void Build()
    {
        _body.Children.Clear();
        var query = _search.Text.Trim();
        if (query.Length > 0)
        {
            _title.Text = L.Format("Settings.ResultsFor", query);
            var any = false;
            foreach (var (_, _, label, build) in _sectionList)
            {
                var rows = build().SelectMany(g => g.Rows).Where(r => Matches(r, query)).ToList();
                if (rows.Count == 0) continue;
                any = true;
                _body.Children.Add(GroupView(new Group(label(), rows)));
            }
            if (!any) _body.Children.Add(new TextBlock { Text = L.Text("Settings.NoMatch"), Foreground = Ui.Resource<Brush>("LwText2Brush") });
            return;
        }
        var section = _sectionList.First(s => s.Key == _section);
        _title.Text = section.Label();
        foreach (var group in section.Build()) _body.Children.Add(GroupView(group));
    }

    private static bool Matches(Row row, string query)
        => row.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) || (row.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static StackPanel GroupView(Group group)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (group.Title is { Length: > 0 } title)
            panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) });
        if (group.Extra is { } extra) panel.Children.Add(extra);
        foreach (var row in group.Rows) panel.Children.Add(Card(row));
        if (group.Foot is { Count: > 0 } foot)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var button in foot) buttons.Children.Add(button);
            panel.Children.Add(buttons);
        }
        return panel;
    }

    private const double RowIconSize = 18;

    /// <summary>A SettingsCard row: icon, label and description on the left, the control on the right.
    /// The icon slot has a fixed width, so every label starts at the same place.</summary>
    private static Border Card(Row row)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 64, Padding = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (row.Glyph is { } glyph)
        {
            var icon = Ui.Icon(glyph, RowIconSize);
            icon.Width = RowIconSize;
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
        }
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Label, FontSize = 14, TextWrapping = TextWrapping.WrapWholeWords });
        if ((row.LiveDescription ?? (row.Description is { Length: > 0 } description ? new TextBlock { Text = description } : null)) is { } line)
        {
            line.FontSize = 12;
            line.Foreground = Ui.Resource<Brush>("LwText2Brush");
            line.TextWrapping = TextWrapping.WrapWholeWords;
            text.Children.Add(line);
        }
        SetColumn(text, 1);
        grid.Children.Add(text);
        row.Control.VerticalAlignment = VerticalAlignment.Center;
        if (row.Control is not TextBlock) AutomationProperties.SetName(row.Control, row.Label);
        if (row.AutomationId is { } id) AutomationProperties.SetAutomationId(row.Control, id);
        SetColumn(row.Control, 2);
        grid.Children.Add(row.Control);
        return new Border
        {
            Background = Ui.Resource<Brush>("LwBoxBrush"),
            BorderBrush = Ui.Resource<Brush>("LwBoxBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid,
        };
    }

    // ---- Controls --------------------------------------------------------------------------------

    private static void Save() => App.SaveSettings();

    private static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => { set(toggle.IsOn); Save(); };
        return toggle;
    }

    private static ComboBox Choices<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set, double minWidth = 180)
    {
        var box = new ComboBox { MinWidth = minWidth };
        var list = options.ToList();
        foreach (var (_, label) in list) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(list[box.SelectedIndex].Value); Save(); } };
        return box;
    }

    private static StackPanel Segments(string group, IEnumerable<(int Value, string Label)> options, int current, Action<int> set)
    {
        var track = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label) in options)
        {
            var segment = new RadioButton { Style = Ui.Resource<Style>("LwSegment"), GroupName = group, Content = label, Padding = new Thickness(12, 0, 12, 0), IsChecked = value == current };
            AutomationProperties.SetAutomationId(segment, group + "Option");
            segment.Checked += (_, _) => { set(value); Save(); };
            track.Children.Add(segment);
        }
        return track;
    }

    private static NumberBox Stepper(int value, int min, int max, Action<int> set)
    {
        var box = new NumberBox { Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1, MinWidth = 120 };
        box.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { set((int)Math.Round(e.NewValue)); Save(); } };
        return box;
    }

    private static Slider SliderFor(double value, double min, double max, double step, Action<double> set, Func<double, string>? describe = null)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = 220 };
        if (describe is not null) slider.ThumbToolTipValueConverter = new Converter(describe);
        slider.ValueChanged += (_, e) => { set(e.NewValue); Save(); };
        return slider;
    }

    private sealed partial class Converter(Func<double, string> describe) : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => describe(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

        public object ConvertBack(object value, Type targetType, object parameter, string language) => value;
    }

    private static Button ButtonFor(string label, Action click, bool accent = false, string? automationId = null)
    {
        var button = new Button { Style = Ui.Resource<Style>(accent ? "LwAccentButton" : "LwButton"), Content = label };
        if (automationId is not null) AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Value(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Resource<Brush>("LwText2Brush") };

    // ---- Sections --------------------------------------------------------------------------------

    private IEnumerable<Group> ServerSection()
    {
        var jellyfin = _shell.Model.Jellyfin;
        var saved = SavedServers.From(CredentialStore.LoadProfiles()).Count;
        var status = Value(_shell.Connectivity.IsOffline ? L.Text("Login.Offline") : L.Text("Login.Online"));
        AutomationProperties.SetAutomationId(status, "SettingsServerStatus");
        yield return new Group(L.Text("Settings.Server"),
        [
            new Row(_shell.ServerName, ServerAddress.Display(jellyfin.ServerUrl ?? ""), Theme.Icons.Server, status),
            new Row(L.Text("Settings.SavedServers"), saved == 1 ? L.Text("Settings.OneServer") : L.Format("Settings.Servers", saved), Theme.Icons.List,
                ButtonFor(L.Text("Settings.Manage"), () => _ = ManageServersAsync(), automationId: "SettingsManageServers")),
        ]);
        yield return new Group(L.Text("Settings.Profile"),
        [
            new Row(L.Text("Settings.SignedInAs"), null, Theme.Icons.Person, Value(jellyfin.UserName ?? "")),
            new Row(L.Text("Settings.SwitchUser"), L.Text("Settings.SwitchUserHint"), Theme.Icons.SwitchUser, ButtonFor(L.Text("Settings.Switch"), _shell.OpenProfileMenu, automationId: "SettingsSwitchUser")),
            new Row(L.Text("Settings.QuickConnect"), L.Text("Settings.QuickConnectHint"), Theme.Icons.Lock, ButtonFor(L.Text("Settings.Authorize"), () => _ = AuthorizeAsync(), automationId: "SettingsQuickConnect")),
        ], [ButtonFor(L.Text("Profile.LogOut"), _shell.LogOut, automationId: "SettingsLogOut")]);
        var omdb = new PasswordBox { Password = _settings.OmdbApiKey, PasswordRevealMode = PasswordRevealMode.Peek, MinWidth = 240, PlaceholderText = L.Text("Settings.OmdbPlaceholder") };
        omdb.LostFocus += (_, _) =>
        {
            var key = omdb.Password.Trim();
            if (key == _settings.OmdbApiKey) return;
            _settings.OmdbApiKey = key;
            Save();
        };
        yield return new Group(L.Text("Settings.Ratings"),
        [
            new Row(L.Text("Settings.OmdbKey"), L.Text("Settings.OmdbKeyHint"), Theme.Icons.Star, omdb, "SettingsOmdbKey"),
        ]);
    }

    private async Task ManageServersAsync()
    {
        var list = new StackPanel { Spacing = 6, MinWidth = 360 };
        var dialog = new LwDialog { XamlRoot = XamlRoot, Title = L.Text("Settings.SavedServers"), Content = list, CloseButtonText = L.Text("Settings.Done") };
        void Fill()
        {
            list.Children.Clear();
            foreach (var server in SavedServers.From(CredentialStore.LoadProfiles()))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = server.Name, FontSize = 14 });
                text.Children.Add(new TextBlock { Text = ServerAddress.Display(server.Url), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush") });
                row.Children.Add(text);
                var remove = ButtonFor(L.Text("Settings.Remove"), () =>
                {
                    CredentialStore.RemoveWhere(p => string.Equals(p.ServerUrl.TrimEnd('/'), server.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                    Fill();
                }, automationId: "SettingsRemoveServer");
                AutomationProperties.SetName(remove, L.Format("Settings.RemoveServer", server.Name));
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Children.Add(row);
            }
        }
        Fill();
        AutomationProperties.SetAutomationId(dialog, "SettingsServersDialog");
        await dialog.ShowAsync();
        Show();
    }

    /// <summary>Approves the code another device shows for Quick Connect.</summary>
    private async Task AuthorizeAsync()
    {
        var box = new TextBox { PlaceholderText = "000000", MaxLength = 6, InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } } };
        AutomationProperties.SetAutomationId(box, "SettingsQuickConnectCode");
        AutomationProperties.SetName(box, L.Text("Settings.QuickConnectCode"));
        var dialog = new LwDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Text("Settings.QuickConnect"),
            Content = box,
            PrimaryButtonText = L.Text("Settings.Authorize"),
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
        var ok = await _shell.Model.Jellyfin.AuthorizeQuickConnectAsync(box.Text.Trim());
        ToastHost.Show(ok ? ToastKind.Success : ToastKind.Error, ok ? L.Text("Settings.Authorized") : L.Text("Settings.AuthorizeFailed"));
    }

    private IEnumerable<Group> HomeSection()
    {
        var jellyfin = _shell.Model.Jellyfin;
        var key = HomeLayoutStore.ProfileKey(jellyfin.UserId, jellyfin.ServerUrl);
        var layout = HomeLayoutStore.Load(key).Where(p => p.Id != HomeSectionId.Libraries).ToList();
        var rows = new List<Row>();
        for (var i = 0; i < layout.Count; i++)
        {
            var index = i;
            var pref = layout[i];
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var up = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 32, Height = 32, Content = Ui.Icon(Theme.Icons.ArrowUpward, 14), IsEnabled = i > 0 };
            AutomationProperties.SetAutomationId(up, "SettingsHomeUp");
            AutomationProperties.SetName(up, L.Format("Settings.MoveUp", RowName(pref.Id)));
            up.Click += (_, _) => { MoveHomeRow(key, index, index - 1); };
            var down = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 32, Height = 32, Content = Ui.Icon(Theme.Icons.ArrowDownward, 14), IsEnabled = i < layout.Count - 1 };
            AutomationProperties.SetAutomationId(down, "SettingsHomeDown");
            AutomationProperties.SetName(down, L.Format("Settings.MoveDown", RowName(pref.Id)));
            down.Click += (_, _) => { MoveHomeRow(key, index, index + 1); };
            var toggle = new ToggleSwitch { IsOn = pref.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            AutomationProperties.SetAutomationId(toggle, "SettingsHomeToggle");
            AutomationProperties.SetName(toggle, RowName(pref.Id));
            toggle.Toggled += (_, _) =>
            {
                var all = HomeLayoutStore.Load(key);
                var at = all.FindIndex(p => p.Id == pref.Id);
                if (at >= 0) all[at] = all[at] with { Enabled = toggle.IsOn };
                HomeLayoutStore.Save(key, all);
            };
            controls.Children.Add(up);
            controls.Children.Add(down);
            controls.Children.Add(toggle);
            rows.Add(new Row(RowName(pref.Id), null, Theme.Icons.Drag, controls));
        }
        yield return new Group(L.Text("Settings.HomeRows"), rows,
            [ButtonFor(L.Text("Settings.HomeReset"), () => { HomeLayoutStore.Save(key, [.. Enum.GetValues<HomeSectionId>().Select(id => new HomeSectionPref(id, true))]); ShowLater(); }, automationId: "SettingsHomeReset")],
            new TextBlock { Text = L.Text("Settings.HomeRowsHint"), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), Margin = new Thickness(2, -4, 0, 6) });
    }

    private void MoveHomeRow(string key, int from, int to)
    {
        var visible = HomeLayoutStore.Load(key).Where(p => p.Id != HomeSectionId.Libraries).ToList();
        if (to < 0 || to >= visible.Count) return;
        var moved = visible[from];
        visible.RemoveAt(from);
        visible.Insert(to, moved);
        var libraries = HomeLayoutStore.Load(key).Where(p => p.Id == HomeSectionId.Libraries);
        HomeLayoutStore.Save(key, [.. visible, .. libraries]);
        ShowLater();
    }

    private static string RowName(HomeSectionId id) => id switch
    {
        HomeSectionId.Resume => L.Text("Home.ContinueWatching"),
        HomeSectionId.NextUp => L.Text("Home.NextUp"),
        HomeSectionId.Latest => L.Text("Home.RecentlyAdded"),
        HomeSectionId.Favorites => L.Text("Home.Favorites"),
        _ => id.ToString(),
    };

    private IEnumerable<Group> PlaybackSection()
    {
        var languages = Languages.Choices.Select(c => (c.Code, c.DisplayName)).ToList();
        yield return new Group(L.Text("Settings.Languages"),
        [
            new Row(L.Text("Settings.AudioLanguage"), null, Theme.Icons.Volume, Choices(languages, _settings.PreferredAudioLanguage, v => _settings.PreferredAudioLanguage = v), "SettingsAudioLanguage"),
            new Row(L.Text("Settings.SubtitleLanguage"), L.Text("Settings.SubtitleLanguageHint"), Theme.Icons.Subtitles, Choices(languages, _settings.PreferredSubtitleLanguage, v => _settings.PreferredSubtitleLanguage = v), "SettingsSubtitleLanguage"),
        ]);
        yield return new Group(L.Text("Settings.Skipping"),
        [
            new Row(L.Text("Settings.SkipIntros"), L.Text("Settings.SkipIntrosHint"), Theme.Icons.SkipForward30, Toggle(_settings.AutoSkipIntro, v => _settings.AutoSkipIntro = v), "SettingsSkipIntros"),
            new Row(L.Text("Settings.SkipCredits"), null, Theme.Icons.Next, Toggle(_settings.AutoSkipCredits, v => _settings.AutoSkipCredits = v), "SettingsSkipCredits"),
            new Row(L.Text("Settings.SkipBack"), null, Theme.Icons.SkipBack10, Segments("SettingsSkipBack", [.. new[] { 5, 10, 15, 30 }.Select(s => (s, L.Format("Settings.Seconds", s)))], _settings.SkipBackwardSeconds, v => _settings.SkipBackwardSeconds = v)),
            new Row(L.Text("Settings.SkipForward"), null, Theme.Icons.SkipForward30, Segments("SettingsSkipForward", [.. new[] { 10, 15, 30, 60 }.Select(s => (s, L.Format("Settings.Seconds", s)))], _settings.LargeSkipSeconds, v => _settings.LargeSkipSeconds = v)),
        ]);
        yield return new Group(L.Text("Settings.UpNext"),
        [
            new Row(L.Text("Settings.PlayNext"), null, Theme.Icons.Next, Toggle(_settings.AutoPlayNextEpisode, v => _settings.AutoPlayNextEpisode = v), "SettingsPlayNext"),
            new Row(L.Text("Settings.Countdown"), L.Text("Settings.CountdownHint"), Theme.Icons.Timer, Stepper(_settings.AutoPlayCountdownSeconds, UpNextPreferences.MinimumCountdownSeconds, UpNextPreferences.MaximumCountdownSeconds, v => _settings.AutoPlayCountdownSeconds = v), "SettingsCountdown"),
            new Row(L.Text("SettingsView.UpNextLeadTitle"), L.Text("Settings.UpNextLeadHint"), Theme.Icons.Clock, Stepper(_settings.UpNextLeadSeconds, UpNextPreferences.MinimumLeadSeconds, UpNextPreferences.MaximumLeadSeconds, v => _settings.UpNextLeadSeconds = v), "SettingsUpNextLead"),
        ]);
        (TransportAction, string)[] transport =
        [
            (TransportAction.QueueStep, L.Text("Settings.TransportQueue")),
            (TransportAction.ChapterStep, L.Text("Settings.TransportChapter")),
            (TransportAction.Seek, L.Text("Settings.TransportSeek")),
        ];
        yield return new Group(L.Text("Settings.MediaKeys"),
        [
            new Row(L.Text("Settings.MediaNext"), L.Text("Settings.MediaNextHint"), Theme.Icons.Next, Choices(transport, _settings.MediaNextAction, v => _settings.MediaNextAction = v), "SettingsMediaNext"),
            new Row(L.Text("Settings.MediaPrevious"), L.Text("Settings.MediaPreviousHint"), Theme.Icons.Previous, Choices(transport, _settings.MediaPrevAction, v => _settings.MediaPrevAction = v), "SettingsMediaPrevious"),
            new Row(L.Text("Settings.MediaSeek"), L.Text("Settings.MediaSeekHint"), Theme.Icons.Clock, Stepper(_settings.TransportSeekSeconds, 5, 300, v => _settings.TransportSeekSeconds = v), "SettingsMediaSeek"),
        ]);
    }

    private static readonly (string Value, Func<string> Label)[] SubtitleColors =
    [
        ("#FFFFFF", () => L.Text("Settings.ColorWhite")),
        ("#FFE34D", () => L.Text("Settings.ColorYellow")),
        ("#7DE3F0", () => L.Text("Settings.ColorCyan")),
        ("#B6F07D", () => L.Text("Settings.ColorGreen")),
        ("#FF9DB0", () => L.Text("Settings.ColorPink")),
    ];

    private IEnumerable<Group> SubtitlesSection()
    {
        var preview = new TextBlock { Text = L.Text("Settings.SubtitlePreview"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 26), TextWrapping = TextWrapping.WrapWholeWords, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetAutomationId(preview, "SettingsSubtitlePreview");
        var frame = new Grid { Height = 150, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1C, 0x22)), Children = { preview } };
        void Redraw()
        {
            preview.FontFamily = new FontFamily(_settings.SubtitleFontFamily is { Length: > 0 } font ? font : "Segoe UI");
            preview.FontSize = Math.Clamp(_settings.SubtitleFontSize * 0.45, 12, 40);
            preview.Foreground = new SolidColorBrush(ParseColor(_settings.SubtitleTextColor));
        }
        Redraw();
        App.SettingsChanged += (_, _) => DispatcherQueue.TryEnqueue(Redraw);
        yield return new Group(null, [], null, frame);
        string[] fonts = ["Segoe UI", "Segoe UI Variable Display", "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Calibri", "Georgia"];
        var fontList = fonts.Select(f => (f, f)).ToList();
        if (!fonts.Contains(_settings.SubtitleFontFamily) && _settings.SubtitleFontFamily is { Length: > 0 } custom) fontList.Insert(0, (custom, custom));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled };
        var currentColor = ParseColor(_settings.SubtitleTextColor);
        foreach (var (color, name) in SubtitleColors)
        {
            var swatch = new RadioButton { Style = Ui.Resource<Style>("LwSwatch"), GroupName = "SettingsSubtitleColor", IsChecked = ParseColor(color) == currentColor, Background = new SolidColorBrush(ParseColor(color)) };
            AutomationProperties.SetAutomationId(swatch, "SettingsSubtitleColorOption");
            AutomationProperties.SetName(swatch, name());
            swatch.Checked += (_, _) => { _settings.SubtitleTextColor = color; Save(); };
            swatches.Children.Add(swatch);
        }
        yield return new Group(L.Text("Settings.Text"),
        [
            new Row(L.Text("Settings.Font"), null, Theme.Icons.Subtitles, Choices(fontList, _settings.SubtitleFontFamily, v => _settings.SubtitleFontFamily = v, 200), "SettingsSubtitleFont"),
            new Row(L.Text("Settings.Size"), null, Theme.Icons.Subtitles, SliderFor(_settings.SubtitleFontSize, 30, 90, 1, v => _settings.SubtitleFontSize = (int)v), "SettingsSubtitleSize"),
            new Row(L.Text("Settings.Color"), null, Theme.Icons.Picture, swatches),
        ]);
        yield return new Group(L.Text("Settings.OutlineShadow"),
        [
            new Row(L.Text("Settings.Border"), null, null, Segments("SettingsSubtitleBorder", [(0, L.Text("Settings.BorderNone")), (2, L.Text("Settings.BorderThin")), (4, L.Text("Settings.BorderThick"))], (int)Math.Round(_settings.SubtitleBorderSize) switch { <= 0 => 0, <= 2 => 2, _ => 4 }, v => _settings.SubtitleBorderSize = v)),
            new Row(L.Text("Settings.Shadow"), null, null, SliderFor(_settings.SubtitleShadowOffset, 0, 5, 0.5, v => _settings.SubtitleShadowOffset = v), "SettingsSubtitleShadow"),
        ]);
        yield return new Group(L.Text("Settings.Position"),
        [
            new Row(L.Text("Settings.VerticalPosition"), L.Text("Settings.VerticalPositionHint"), null, SliderFor(_settings.SubtitleBasePosition, 50, 100, 1, v => _settings.SubtitleBasePosition = (int)v), "SettingsSubtitlePosition"),
        ]);
    }

    private static Windows.UI.Color ParseColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Microsoft.UI.Colors.White;
    }

    private IEnumerable<Group> AudioSection()
    {
        var devices = new List<(string, string)> { ("auto", L.Text("Settings.SystemDefault")) };
        if (_shell.AudioDevices() is { } list) devices.AddRange(list.Where(d => d.Name != "auto").Select(d => (d.Name, d.Description)));
        if (!devices.Any(d => d.Item1 == _settings.AudioDevice) && _settings.AudioDevice is { Length: > 0 } saved) devices.Add((saved, saved));
        yield return new Group(L.Text("Settings.Output"),
        [
            new Row(L.Text("Settings.OutputDevice"), null, Theme.Icons.Volume, Choices(devices, _settings.AudioDevice is { Length: > 0 } d ? d : "auto", v => _settings.AudioDevice = v, 260), "SettingsAudioDevice"),
            new Row(L.Text("Settings.Passthrough"), L.Text("Settings.PassthroughHint"), Theme.Icons.Volume, Toggle(_settings.AudioPassthrough, v => _settings.AudioPassthrough = v), "SettingsPassthrough"),
        ]);
        yield return new Group(L.Text("Settings.Loudness"),
        [
            new Row(L.Text("Settings.Normalize"), L.Text("Settings.NormalizeHint"), Theme.Icons.Volume, Toggle(_settings.VolumeNormalization, v => _settings.VolumeNormalization = v), "SettingsNormalize"),
        ]);
    }

    private IEnumerable<Group> VideoSection()
    {
        yield return new Group(L.Text("Settings.Decoding"),
        [
            new Row(L.Text("Settings.HardwareDecoding"), L.Text("Settings.HardwareDecodingHint"), Theme.Icons.Videocam, Toggle(_settings.HardwareDecoding, v => _settings.HardwareDecoding = v), "SettingsHardwareDecoding"),
        ]);
        yield return new Group(L.Text("Settings.Rtx"),
        [
            new Row(L.Text("Settings.RtxVsr"), L.Text("Settings.RtxVsrHint"), Theme.Icons.Videocam, Toggle(_settings.RtxVsrDefault, v => _settings.RtxVsrDefault = v), "SettingsRtxVsr"),
            new Row(L.Text("Settings.RtxHdr"), L.Text("Settings.RtxHdrHint"), Theme.Icons.Videocam, Toggle(_settings.RtxHdrDefault, v => _settings.RtxHdrDefault = v), "SettingsRtxHdr"),
            new Row(L.Text("Settings.RtxPeak"), L.Text("Settings.RtxPeakHint"), Theme.Icons.Videocam, SliderFor(_settings.RtxHdrMaxLumaNits, 400, 2000, 50, v => _settings.RtxHdrMaxLumaNits = (int)v, v => L.Format("Settings.Nits", (int)v)), "SettingsRtxPeak"),
        ]);
        int[] bitrates = [0, 60, 40, 20, 10, 8, 4, 2];
        yield return new Group(L.Text("Settings.Streaming"),
        [
            new Row(L.Text("Settings.MaxBitrate"), L.Text("Settings.MaxBitrateHint"), Theme.Icons.Speed, Choices(bitrates.Select(b => (b, b == 0 ? L.Text("Settings.Unlimited") : L.Format("Detail.QualityMbps", b))), _settings.MaxStreamingBitrateMbps, v => _settings.MaxStreamingBitrateMbps = v), "SettingsMaxBitrate"),
            new Row(L.Text("Settings.Buffer"), L.Text("Settings.BufferHint"), Theme.Icons.Storage, Choices(new[] { 75, 150, 300, 600, 1200 }.Select(m => (m, L.Format("Settings.MegaBytes", m))), _settings.VideoBufferMiB, v => _settings.VideoBufferMiB = v), "SettingsBuffer"),
        ]);
    }

    private IEnumerable<Group> DownloadsSection()
    {
        var directory = _settings.DownloadDirectory is { Length: > 0 } custom ? custom : DownloadStore.DefaultDirectory;
        string? free = null;
        try { free = L.Format("Downloads.Free", DownloadManager.FormatBytes(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        var qualities = DownloadQuality.All.Select(q => (q.Id, q.IsOriginal ? q.Label : $"{q.Label} · {q.Note}")).Append((DownloadQuality.AskId, L.Text("Downloads.AskEachTime")));
        yield return new Group(L.Text("Settings.Location"),
        [
            new Row(directory, free, Theme.Icons.Folder, ButtonFor(L.Text("Settings.Change"), () => _ = PickFolderAsync(), automationId: "SettingsDownloadFolder")),
        ]);
        yield return new Group(L.Text("Settings.Behaviour"),
        [
            new Row(L.Text("Settings.DefaultQuality"), null, Theme.Icons.Download, Choices(qualities, DownloadQualityStore.Load(), DownloadQualityStore.Save), "SettingsDownloadQuality"),
            new Row(L.Text("Settings.Simultaneous"), null, Theme.Icons.Download, Stepper(_settings.MaxParallelDownloads, 1, 4, v => _settings.MaxParallelDownloads = v), "SettingsSimultaneous"),
        ]);
    }

    private async Task PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _shell.WindowHandle);
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        _settings.DownloadDirectory = folder.Path;
        Save();
        Show();
    }

    private IEnumerable<Group> StorageSection()
    {
        yield return new Group(L.Text("Settings.Caches"),
        [
            new Row(L.Text("Settings.ImageCache"), DownloadManager.FormatBytes(ImageDiskCache.CurrentSizeBytes()), Theme.Icons.Storage,
                ButtonFor(L.Text("Settings.Clear"), () => { Services.ImageLoader.Clear(); ShowLater(); }, automationId: "SettingsClearImages")),
            new Row(L.Text("Settings.MetadataCache"), DownloadManager.FormatBytes(MetadataCache.CurrentSizeBytes()), Theme.Icons.Storage,
                ButtonFor(L.Text("Settings.Clear"), () => { MetadataCache.Clear(); ShowLater(); }, automationId: "SettingsClearMetadata")),
            new Row(L.Text("Settings.RatingsCache"), DownloadManager.FormatBytes(ExternalMetadataCache.CurrentSizeBytes()), Theme.Icons.Storage,
                ButtonFor(L.Text("Settings.Clear"), () => { ExternalMetadataCache.Clear(); ShowLater(); }, automationId: "SettingsClearRatings")),
            new Row(L.Text("Settings.BrowseCache"), L.Text("Settings.BrowseCacheHint"), Theme.Icons.Storage,
                ButtonFor(L.Text("Settings.Clear"), () => _ = ClearBrowseAsync(), automationId: "SettingsClearBrowse")),
        ], [ButtonFor(L.Text("Settings.ClearAll"), () => { Services.ImageLoader.Clear(); MetadataCache.Clear(); ExternalMetadataCache.Clear(); _ = ClearBrowseAsync(); }, automationId: "SettingsClearAll")]);
    }

    private async Task ClearBrowseAsync()
    {
        await BrowseFolderCache.InvalidateProfileAsync(_shell.Model.ActiveSessionKey ?? "");
        ToastHost.Show(ToastKind.Success, L.Text("Settings.Cleared"));
        Show();
    }

    private IEnumerable<Group> LanguageSection()
    {
        var languages = new List<(string, string)> { ("", L.Text("Settings.SystemLanguage")) };
        languages.AddRange(L.Languages.Select(l => (l.Code, l.Name)));
        var box = Choices(languages, _settings.UiLanguage ?? "", v =>
        {
            _settings.UiLanguage = v;
            DispatcherQueue.TryEnqueue(() => L.Initialize(v));
        }, 220);
        yield return new Group(null,
        [
            new Row(L.Text("Settings.AppLanguage"), L.Text("Settings.AppLanguageHint"), Theme.Icons.Globe, box, "SettingsAppLanguage"),
        ]);
    }

    private PlayerAction? _recording;
    internal bool IsRecordingShortcut => _recording is not null;
    private Button? _recordingButton;
    private (PlayerAction Action, string Chord, PlayerAction Owner)? _conflict;

    /// <summary>Design 10c: click a shortcut to record a new one. Esc cancels, Backspace clears, and a
    /// chord another action already uses is named inline with Replace. The map is the one the WPF app
    /// saves, so a rebind carries over.</summary>
    private IEnumerable<Group> KeysSection()
    {
        var map = PlayerActionDispatcher.Resolve(_settings.KeyBindings);
        var rows = Enum.GetValues<PlayerAction>().Where(a => a != PlayerAction.OpenStream)
            .Select(a => new Row(PlayerActionDispatcher.DisplayName(a), null, null, ChordButton(a, map[a]), "SettingsKey" + a)).ToList();
        yield return new Group(L.Text("Settings.KeysPlayback"), rows,
            [ButtonFor(L.Text("Settings.KeysReset"), ResetKeys, automationId: "SettingsKeysReset")], KeysHeader());
        (string Action, string Keys)[] fixedKeys =
        [
            (L.Text("Settings.KeySearch"), "Ctrl+F"), (L.Text("Settings.KeyBack"), "Alt+←"), (L.Text("Settings.KeySettings"), "Ctrl+,"),
            (L.Text("Settings.KeyEscape"), "Esc"), (L.Text("Settings.KeyFullscreen"), "F"), (L.Text("Settings.KeyInfo"), "Ctrl+I"),
            (L.Text("Settings.KeyPlayLink"), "Ctrl+L"), (L.Text("Settings.KeyOpenFile"), "Ctrl+O"),
            (L.Text("AppShortcuts.ZoomIn.4c1e7b2a"), "Ctrl+Plus"),
            (L.Text("AppShortcuts.ZoomOut.9d3f60e5"), "Ctrl+Minus"),
            (L.Text("AppShortcuts.ResetZoom.e27a5c81"), "Ctrl+0"),
            (L.Text("AppShortcuts.ZoomTheInterface.b85d3f17"), L.Text("AppShortcuts.CtrlWheel.6a0e9c42")),
        ];
        yield return new Group(L.Text("Settings.KeysNavigation"), [.. fixedKeys.Select(k => new Row(k.Action, null, null, KeyCap(k.Keys)))]);
        yield return new Group(L.Text("SettingsView.Zoom.3c7f2a91"),
        [
            new Row(L.Text("SettingsView.LimitZoomToTheScreenSize.e4a19c06"), L.Text("SettingsView.ZoomLimitHint.8d52b3f0"),
                Theme.Icons.Settings, ZoomLimitToggle(), "SettingsZoomLimit"),
        ]);
    }

    private ToggleSwitch ZoomLimitToggle()
    {
        var toggle = new ToggleSwitch { IsOn = _settings.LimitUiZoomToScreen, OnContent = "", OffContent = "", MinWidth = 0 };
        var changing = false;
        toggle.Toggled += async (_, _) =>
        {
            if (changing) return;
            changing = true;
            try
            {
                if (!toggle.IsOn)
                {
                    var dialog = new LwDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = L.Text("SettingsView.RemoveTheZoomLimit.71c3e0d4"),
                        Content = new TextBlock { Text = L.Text("SettingsView.ZoomLimitWarning.a6f8125b"), TextWrapping = TextWrapping.WrapWholeWords },
                        PrimaryButtonText = L.Text("SettingsView.RemoveLimit.0b9d4e67"),
                        CloseButtonText = L.Text("Playlist.Cancel"),
                        DefaultButton = ContentDialogButton.Close,
                    };
                    AutomationProperties.SetAutomationId(dialog, "SettingsZoomLimitDialog");
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) toggle.IsOn = true;
                }
                _settings.LimitUiZoomToScreen = toggle.IsOn;
                LightWeaver.Theme.UiZoom.SetLimitToScreen(toggle.IsOn);
                Save();
            }
            finally { changing = false; }
        };
        return toggle;
    }

    private StackPanel KeysHeader()
    {
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = L.Text("Settings.KeysHint"), FontSize = 12, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.WrapWholeWords });
        if (_conflict is { } conflict)
        {
            var bar = new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Message = L.Format("Settings.KeyConflict", PlayerActionDispatcher.DisplayChord(conflict.Chord), PlayerActionDispatcher.DisplayName(conflict.Owner)),
            };
            AutomationProperties.SetAutomationId(bar, "SettingsKeyConflict");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(ButtonFor(L.Text("Settings.KeyReplace"), () =>
            {
                Bind(conflict.Owner, "");
                Bind(conflict.Action, conflict.Chord);
                _conflict = null;
                ShowLater();
            }, accent: true, automationId: "SettingsKeyReplace"));
            actions.Children.Add(ButtonFor(L.Text("Settings.KeyKeep"), () => { _conflict = null; ShowLater(); }, automationId: "SettingsKeyKeep"));
            bar.Content = actions;
            header.Children.Add(bar);
        }
        return header;
    }

    private Button ChordButton(PlayerAction action, string chord)
    {
        var button = new Button { MinWidth = 120 };
        Paint(button, action, recording: false);
        button.Click += (_, _) =>
        {
            if (_recordingButton is { } previous && _recording is { } other && !ReferenceEquals(previous, button)) Paint(previous, other, recording: false);
            _recording = action;
            _recordingButton = button;
            // After the click: restyling a button inside its own Click swaps its template mid-invoke.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_recording != action) return;
                Paint(button, action, recording: true);
                button.Focus(FocusState.Programmatic);
            });
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (_recording != action) return;
            e.Handled = true;
            if (e.Key == VirtualKey.Escape) { StopRecording(button, action); return; }
            if (e.Key == VirtualKey.Back) { Bind(action, ""); StopRecording(button, action); return; }
            static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (Player.KeyChords.Of(e.Key, Down(VirtualKey.Control), Down(VirtualKey.Menu), Down(VirtualKey.Shift)) is not { } pressed) return;
            if (Player.KeyChords.IsZoomChord(pressed))
            {
                StopRecording(button, action);
                ToastHost.Show(ToastKind.Info, L.Text("Settings.ZoomShortcutReserved"));
                return;
            }
            var owner = PlayerActionDispatcher.Resolve(_settings.KeyBindings)
                .FirstOrDefault(b => b.Key != action && PlayerActionDispatcher.SameChord(b.Value, pressed));
            StopRecording(button, action);
            if (owner.Value is not null)
            {
                _conflict = (action, pressed, owner.Key);
                ShowLater();
                return;
            }
            Bind(action, pressed);
            Paint(button, action, recording: false);
        };
        button.LostFocus += (_, _) => { if (_recording == action) StopRecording(button, action); };
        return button;
    }

    /// <summary>The chord, or "Press a key…" while recording; changed in place, so a click
    /// elsewhere is never lost to a rebuilt page.</summary>
    private void Paint(Button button, PlayerAction action, bool recording)
    {
        button.Style = Ui.Resource<Style>(recording ? "LwAccentButton" : "LwButton");
        var text = new TextBlock { FontSize = 12.5 };
        if (recording) text.Text = L.Text("Settings.KeyPress");
        else
        {
            text.Text = PlayerActionDispatcher.DisplayChord(PlayerActionDispatcher.Resolve(_settings.KeyBindings)[action]);
            text.FontFamily = Ui.Resource<FontFamily>("LwMonoFont");
        }
        button.Content = text;
    }

    private void StopRecording(Button button, PlayerAction action)
    {
        _recording = null;
        _recordingButton = null;
        Paint(button, action, recording: false);
    }

    /// <summary>Rebuilds the page after the click that asked for it has finished.</summary>
    private void ShowLater() => DispatcherQueue.TryEnqueue(Show);

    private void Bind(PlayerAction action, string chord)
    {
        _settings.KeyBindings[action.ToString()] = chord;
        Save();
    }

    private void ResetKeys()
    {
        _settings.KeyBindings.Clear();
        _conflict = null;
        _recording = null;
        _recordingButton = null;
        Save();
        ShowLater();
    }

    private static Border KeyCap(string keys) => new()
    {
        Padding = new Thickness(8, 3, 8, 4),
        CornerRadius = new CornerRadius(4),
        Background = Ui.Resource<Brush>("LwButtonBrush"),
        BorderBrush = Ui.Resource<Brush>("LwButtonBorderBrush"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = keys, FontFamily = Ui.Resource<FontFamily>("LwMonoFont"), FontSize = 12.5 },
    };

    private IEnumerable<Group> DiagnosticsSection()
    {
        var log = new TextBlock { FontFamily = Ui.Resource<FontFamily>("LwMonoFont"), FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = true, Text = LogTail() };
        AutomationProperties.SetAutomationId(log, "SettingsLogTail");
        var logFrame = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x14, 0x14, 0x16)), Child = new ScrollViewer { Content = log, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220 } };
        yield return new Group(L.Text("Settings.Logging"),
        [
            new Row(L.Text("Settings.VerboseLogging"), L.Text("Settings.VerboseLoggingHint"), Theme.Icons.Info, Toggle(_settings.VerboseLogging, v => { _settings.VerboseLogging = v; AppLog.Verbose = v; }), "SettingsVerboseLogging"),
        ], [ButtonFor(L.Text("Settings.OpenLogFolder"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Dir}\"") { UseShellExecute = true }), automationId: "SettingsOpenLogs"),
            ButtonFor(L.Text("Settings.ExportDiagnostics"), () => _ = ExportAsync(), accent: true, automationId: "SettingsExport")], logFrame);
    }

    private static string LogTail()
    {
        var path = Path.Combine(AppLog.Dir, "app.log");
        try
        {
            if (!File.Exists(path)) return L.Text("Settings.NoLog");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return string.Join("\n", reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(12));
        }
        catch (IOException)
        {
            return L.Text("Settings.NoLog");
        }
    }

    /// <summary>A zip of the app and mpv logs and system information; tokens and passwords are removed.</summary>
    private async Task ExportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"lightweaver-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add("Zip", [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _shell.WindowHandle);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        var files = await Task.Run(() => AppLog.ExportTo(file.Path));
        ToastHost.Show(ToastKind.Success, L.Format("Settings.Exported", files));
    }

    /// <summary>The drawn Updates pane: the version with the last check and the updater's state, the
    /// policy, and in the foot the one action the state offers beside Check for updates. All of it
    /// updates in place, so an open policy list stays open.</summary>
    private IEnumerable<Group> UpdatesSection()
    {
        var lastChecked = new TextBlock();
        var status = Value("");
        status.MaxWidth = 320;
        status.TextWrapping = TextWrapping.WrapWholeWords;
        status.TextAlignment = TextAlignment.End;
        status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(status, "SettingsUpdateStatus");
        var done = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var track = new Grid { Width = 160, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = Ui.Resource<Brush>("LwTrackBrush") };
        track.ColumnDefinitions.Add(done);
        track.ColumnDefinitions.Add(rest);
        track.Children.Add(new Border { Background = Ui.Resource<Brush>("LwAccentFgBrush") });
        AutomationProperties.SetAutomationId(track, "SettingsUpdateProgress");
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        state.Children.Add(track);
        state.Children.Add(status);

        var pane = UpdatePane.For(Updater.Status, _settings.UpdatePolicy);
        var action = ButtonFor("", () => _ = Updater.RunAsync(pane.Action, _shell.Quit), accent: true, automationId: "SettingsUpdateAction");
        var check = ButtonFor("", () => _ = Updater.RunAsync(UpdateAction.Check, _shell.Quit), automationId: "SettingsCheckUpdates");
        void Paint(UpdateStatus current)
        {
            pane = UpdatePane.For(current, _settings.UpdatePolicy);
            lastChecked.Text = current.LastCheckedUtc is { } at ? L.Format("Settings.LastChecked", at.ToLocalTime()) : "";
            lastChecked.Visibility = current.LastCheckedUtc is null ? Visibility.Collapsed : Visibility.Visible;
            status.Text = pane.Status;
            status.Foreground = Ui.Resource<Brush>(pane.IsError ? "LwErrorBrush" : "LwText2Brush");
            track.Visibility = pane.Progress is null ? Visibility.Collapsed : Visibility.Visible;
            done.Width = new GridLength(pane.Progress ?? 0, GridUnitType.Star);
            rest.Width = new GridLength(1 - (pane.Progress ?? 0), GridUnitType.Star);
            var separate = pane.Action != UpdateAction.Check;
            action.Visibility = separate ? Visibility.Visible : Visibility.Collapsed;
            action.Content = pane.ActionLabel;
            action.IsEnabled = pane.ActionEnabled;
            check.Style = Ui.Resource<Style>(separate ? "LwButton" : "LwAccentButton");
            check.Content = separate ? L.Text("Settings.CheckUpdates") : pane.ActionLabel;
            check.IsEnabled = pane.CanCheck;
        }
        Paint(Updater.Status);
        check.Loaded += (_, _) => { Updater.Changed -= Paint; Updater.Changed += Paint; Paint(Updater.Status); };
        check.Unloaded += (_, _) => Updater.Changed -= Paint;

        var policies = new[] { (UpdatePolicy.ManualInstall, L.Text("Settings.UpdateManual")), (UpdatePolicy.AutoDownload, L.Text("Settings.UpdateDownload")), (UpdatePolicy.AutoDownloadAndInstall, L.Text("Settings.UpdateInstall")) };
        yield return new Group(null,
        [
            new Row(L.Format("Settings.Version", GitHubUpdateClient.CurrentVersion.ToString(3)), null, Theme.Icons.InfoSolid, state, LiveDescription: lastChecked),
            new Row(L.Text("Settings.UpdatePolicy"), null, Theme.Icons.Refresh, Choices(policies, _settings.UpdatePolicy, v => { _settings.UpdatePolicy = v; Updater.OnPolicyChanged(); }, 220), "SettingsUpdatePolicy"),
        ], [action, check]);
        if (UpdateRevert.PreviousVersion(AppContext.BaseDirectory, GitHubUpdateClient.CurrentVersion) is { } previous)
            yield return RevertGroup(previous);
    }

    private static bool s_reverting;
    private static string s_revertStatus = "";
    private static event Action? RevertChanged;

    /// <summary>The one revert's state, shared by every build of the page while it runs.</summary>
    private static void SetRevert(bool running, string status)
    {
        s_reverting = running;
        s_revertStatus = status;
        RevertChanged?.Invoke();
    }

    /// <summary>Back to the version installed before this update, when the installer recorded one.</summary>
    private Group RevertGroup(Version previous)
    {
        var version = previous.ToString(3);
        var revertStatus = Value("");
        revertStatus.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(revertStatus, "SettingsRevertStatus");
        var button = ButtonFor(L.Format("Settings.RevertTo", version), () => _ = RevertAsync(previous), automationId: "SettingsRevert");
        void Show()
        {
            button.IsEnabled = !s_reverting;
            revertStatus.Text = s_revertStatus;
            revertStatus.Visibility = s_revertStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Show();
        button.Loaded += (_, _) => { RevertChanged -= Show; RevertChanged += Show; Show(); };
        button.Unloaded += (_, _) => RevertChanged -= Show;
        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        control.Children.Add(revertStatus);
        control.Children.Add(button);
        return new Group(L.Text("Settings.PreviousVersion"),
        [
            new Row(L.Format("Settings.RevertTo", version), L.Text("Settings.RevertHint"), Theme.Icons.Undo, control, "SettingsRevertRow"),
        ]);
    }

    /// <summary>Asks first, then downloads and verifies the older setup, starts it and closes the app.
    /// Only one runs at a time, and the button comes back with the reason whenever it stops.</summary>
    private async Task RevertAsync(Version previous)
    {
        if (s_reverting) return;
        var version = previous.ToString(3);
        var dialog = new LwDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Format("Settings.RevertConfirmTitle", version),
            Content = new TextBlock { Text = L.Format("Settings.RevertConfirmText", version), TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = L.Text("Settings.Revert"),
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "SettingsRevertDialog");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || s_reverting) return;

        SetRevert(true, L.Text("Settings.RevertLooking"));
        var outcome = L.Text("Settings.RevertDownloadFailed");
        var quitting = false;
        try
        {
            var revert = new UpdateRevert();
            var progress = new Progress<double>(done => SetRevert(true, done >= 1 ? L.Text("Settings.RevertChecking") : L.Format("Settings.RevertDownloading", (int)(done * 100))));
            using var staged = await revert.DownloadAsync(previous, progress, CancellationToken.None);
            SetRevert(true, L.Text("Settings.RevertStarting"));
            await revert.InstallAsync(staged, _settings, App.SaveSettings, CancellationToken.None);
            quitting = true;
            _shell.Quit();
        }
        catch (UpdateRevertException ex)
        {
            outcome = ex.Reason switch
            {
                RevertFailure.NoRelease => L.Format("Settings.RevertNoRelease", version),
                RevertFailure.NoInstaller => L.Format("Settings.RevertNoInstaller", version),
                RevertFailure.Verification => L.Format("Settings.RevertNotVerified", version),
                RevertFailure.SettingsNotSaved => L.Text("Settings.RevertSettingsNotSaved"),
                RevertFailure.Launch => L.Text("Settings.RevertLaunchFailed"),
                RevertFailure.Busy => L.Text("Settings.RevertBusy"),
                _ => L.Text("Settings.RevertDownloadFailed"),
            };
        }
        catch (Exception ex)
        {
            outcome = L.Text("Settings.RevertFailed");
            AppLog.Error("updates", $"event=revert outcome=failure reason=unexpected error={ex.GetType().Name}");
        }
        finally
        {
            if (!quitting) SetRevert(false, outcome);
        }
    }
}
