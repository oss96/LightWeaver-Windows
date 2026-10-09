using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Runs only within the existing guest localization fixture and its temporary profile.
internal static class UpNextUiFixture
{
    /// <param name="episodePage">The episode page runs in its own child process and time budget;
    /// the series/season pages, Settings and the player run in the other.</param>
    public static void Verify(MainWindow main, string language, bool episodePage, Action<bool, string> check,
        Action<FrameworkElement, string> capture, Action pump)
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Up Next UI checks require the lw-localization guest route.");
        var host = (ContentControl)main.FindName("ShellHost");
        // Countdown 25 is not the default, so finding it on disk after a show save proves the save
        // kept the rest of the settings.
        var settings = new AppSettings { SettingsVersion = 1, UiLanguage = language, AutoPlayCountdownSeconds = 25 };
        const string server = "https://show-preference-fixture.invalid/base";
        var user = Guid.NewGuid();
        var show = Guid.NewGuid();
        UpNextPreferences.TryGetShowKey(server, user, show, out var key);
        main.Width = 1280;
        main.Height = 800;
        pump();

        if (episodePage)
            VerifyEpisode(main, host, settings, server, user, language, check, capture, pump);
        else
        {
            VerifyLibrary(main, host, settings, server, user, show, key, language, check, capture, pump);
            VerifySettings(main, host, settings, language, check, capture, pump);
            VerifyPlayer(language, check);
        }
        host.Content = null;
    }

    private static void VerifyLibrary(MainWindow main, ContentControl host, AppSettings settings,
        string server, Guid user, Guid show, string key, string language, Action<bool, string> check,
        Action<FrameworkElement, string> capture, Action pump)
    {
        var english = language == "en";
        var model = new AppViewModel();
        try
        {
            typeof(JellyfinService).GetProperty(nameof(JellyfinService.ServerUrl))!.SetValue(model.Jellyfin, server);
            typeof(JellyfinService).GetProperty(nameof(JellyfinService.UserId))!.SetValue(model.Jellyfin, user);
            foreach (var type in new[] { BaseItemDto_Type.Series, BaseItemDto_Type.Season, BaseItemDto_Type.CollectionFolder })
            {
                var source = new MediaItem { Id = type == BaseItemDto_Type.Series ? show : Guid.NewGuid(),
                    SeriesId = show, Name = "Synthetic show", Type = type };
                var view = new LibraryView(model, source, null, settings);
                // Pre-populate the grid so Loaded cannot perform its normal Jellyfin query.
                Field<ObservableCollection<MediaItem>>(view, "_items").Add(new MediaItem
                    { Id = Guid.NewGuid(), Name = "Synthetic episode", Type = BaseItemDto_Type.Episode });
                if (type == BaseItemDto_Type.CollectionFolder)
                    ((ComboBox)view.FindName("GenreBox")).Items.Add("Synthetic genre");
                host.Content = view;
                pump();
                var pill = (Button)view.FindName("UpNextButton");
                var pillText = (TextBlock)view.FindName("UpNextButtonText");
                // The flyout is the shared UpNextShowFlyout the episode page uses too.
                var popup = Part<Popup>(view, "FlyoutPopup");
                var flyout = Part<Border>(view, "FlyoutPanel");
                var picker = Part<UpNextLeadPicker>(view, "Picker");
                var status = Part<TextBlock>(view, "Status");
                if (type == BaseItemDto_Type.CollectionFolder)
                {
                    check(pill.Visibility == Visibility.Collapsed && !pill.IsVisible,
                        language + " a library folder page has no Up Next pill");
                    continue;
                }
                // Layout rounding can move a 34 DIP height by a fraction at non-integer DPI scales.
                check(pill.IsVisible && Math.Abs(pill.ActualHeight - 34) < 1
                    && Math.Abs(pill.ActualHeight - ((Button)view.FindName("ShuffleButton")).ActualHeight) < 0.01,
                    language + " " + type + " page shows the Up Next pill at the queue buttons' height");
                var pillIcon = Find<TextBlock>(pill, "UpNextLeadButtonIcon");
                check(pillIcon.IsVisible && pillIcon.ActualWidth > 0
                    && pillIcon.Text == (string)Application.Current.FindResource("IconUpNextLead")
                    && pillIcon.FontFamily == (FontFamily)Application.Current.FindResource("LwFontIcon"),
                    language + " " + type + " page pill shows the episode page's Up Next icon");
                if (type == BaseItemDto_Type.Season)
                {
                    check(pillText.Text == Pill(Seconds(60)) && AutomationProperties.GetName(pill) == pillText.Text,
                        language + " season page pill shows and names the show's 60 s");
                    Open();
                    check(Selected() == "UpNextLead60" && status.Text == L.Format("UpNextShowFlyout.StatusShow", 60, 30),
                        language + " season page resolves the same show-wide lead as the series page");
                    Escape();
                    continue;
                }

                // Series page: the full flow.
                check(pillText.Text == Pill(L.Text("UpNextLeadPicker.Global"))
                    && pillText.Text == (english ? "Up Next · Global" : "DE Up Next · DE Global")
                    && AutomationProperties.GetName(pill) == pillText.Text,
                    language + " an inheriting show's pill reads Global and carries it as its accessible name");
                check(popup.StaysOpen == false && !popup.IsOpen, language + " the flyout starts closed and is configured to close on an outside click");
                Open();
                var segments = Find<LwSegmentedControl>(picker, "UpNextLeadSegments");
                var customBox = Find<TextBox>(picker, "UpNextLeadCustomBox");
                var error = Find<TextBlock>(picker, "UpNextLeadError");
                check(popup.IsOpen && Labels().SequenceEqual(new[] { L.Text("UpNextLeadPicker.Global"),
                    Seconds(15), Seconds(30), Seconds(60), L.Text("UpNextLeadPicker.Custom") }),
                    language + " the flyout offers Global, 15, 30, 60 and Custom in order");
                if (english)
                    check(Labels().SequenceEqual(new[] { "Global", "15 s", "30 s", "60 s", "Custom" }),
                        "English segments use the approved labels");
                check(Selected() == "UpNextLeadGlobal" && !customBox.IsVisible && !error.IsVisible,
                    language + " an inheriting show selects Global with no custom field");
                check(status.Text == L.Format("UpNextShowFlyout.StatusGlobal", 30)
                    && (!english || status.Text == "Appears at the credits marker. Episodes without one: 30 s before the end (global setting)."),
                    language + " the status line explains the inherited lead");
                capture(flyout, language + "-up-next-flyout-inherit");

                Choose("UpNextLead60");
                check(UpNextPreferences.GetShowLead(settings, key) == 60
                    && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 60
                    && settings.UpNextLeadSeconds == 30,
                    language + " choosing 60 saves the show lead in memory and on disk, not the global one");
                check(pillText.Text == Pill(Seconds(60)) && (!english || pillText.Text == "Up Next · 60 s"),
                    language + " the pill shows the show's 60 s");
                check(status.Text == L.Format("UpNextShowFlyout.StatusShow", 60, 30)
                    && (!english || status.Text == "Appears at the credits marker. Episodes without one: 60 s before the end. Global is 30 s."),
                    language + " the status line names the show lead and the global one");
                capture(flyout, language + "-up-next-flyout-60");

                Choose("UpNextLeadCustom");
                check(customBox.IsVisible && customBox.Text == "60" && UpNextPreferences.GetShowLead(settings, key) == 60,
                    language + " Custom reveals the field at the current value and saves nothing by itself");
                var invalidMessage = L.Format("UpNextLeadPicker.InvalidCustom", 5, 300);
                foreach (var invalid in new[] { "4", "301", "x" })
                {
                    customBox.Text = invalid;
                    pump();
                    check(error.IsVisible && error.Text == invalidMessage
                        && UpNextPreferences.GetShowLead(settings, key) == 60
                        && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 60
                        && pillText.Text == Pill(Seconds(60)),
                        language + " invalid custom lead " + invalid + " reports the range and keeps the saved 60 s");
                }
                if (english)
                    check(error.Text == "Enter a whole number from 5 to 300. Your saved setting is unchanged.",
                        "English invalid custom lead uses the approved message");
                capture(flyout, language + "-up-next-flyout-invalid");

                // The user's path: switch language with the flyout open and an invalid draft in it.
                var before = (Pill: pillText.Text, Global: Labels()[0], Error: error.Text);
                L.Initialize(english ? "de" : "en");
                pump();
                check(pillText.Text == Pill(Seconds(60)) && pillText.Text != before.Pill
                    && Labels()[0] == L.Text("UpNextLeadPicker.Global") && Labels()[0] != before.Global
                    && error.IsVisible && error.Text == L.Format("UpNextLeadPicker.InvalidCustom", 5, 300) && error.Text != before.Error
                    && Selected() == "UpNextLeadCustom" && customBox.Text == "x",
                    language + " live language switching relabels the pill, segments and error and keeps the draft");
                L.Initialize(language);
                pump();

                customBox.Text = "45";
                pump();
                check(!error.IsVisible && UpNextPreferences.GetShowLead(settings, key) == 45
                    && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 45
                    && pillText.Text == Pill(Seconds(45)) && status.Text == L.Format("UpNextShowFlyout.StatusShow", 45, 30),
                    language + " a valid custom 45 saves and relabels the pill");
                foreach (var (text, seconds) in new[] { ("5", 5), ("300", 300), ("45", 45) })
                {
                    customBox.Text = text;
                    pump();
                    check(!error.IsVisible && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == seconds,
                        language + " custom lead accepts " + text);
                }

                using (var settingsLock = File.Open(Path.Combine(AppPaths.Root, "settings.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Choose("UpNextLead15");
                    check(error.IsVisible && error.Text == L.Text("UpNextLeadPicker.SaveFailed")
                        && UpNextPreferences.GetShowLead(settings, key) == 45
                        && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 45
                        && pillText.Text == Pill(Seconds(45))
                        && Selected() == "UpNextLeadCustom" && customBox.Text == "45",
                        language + " a refused save rolls the show lead and the picker back and reports it");
                }
                Choose("UpNextLead30");
                check(!error.IsVisible && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 30
                    && pillText.Text == Pill(Seconds(30)),
                    language + " a later save clears the failure and persists");

                Choose("UpNextLeadGlobal");
                var reloaded = SettingsStore.Load();
                check(!settings.ShowUpNextLeadSeconds.ContainsKey(key) && !reloaded.ShowUpNextLeadSeconds.ContainsKey(key)
                    && reloaded.AutoPlayCountdownSeconds == 25
                    && pillText.Text == Pill(L.Text("UpNextLeadPicker.Global"))
                    && status.Text == L.Format("UpNextShowFlyout.StatusGlobal", 30),
                    language + " Global removes the override in memory and on disk and keeps the other settings");

                Choose("UpNextLead60");
                Escape();
                check(!popup.IsOpen, language + " Escape closes the flyout");

                main.Width = 700;
                main.Height = 480;
                pump();
                Open();
                var window = ScreenRect((FrameworkElement)main.Content);
                var shown = ScreenRect(flyout);
                check(popup.IsOpen && shown.Width > 0 && shown.Left >= window.Left - 2 && shown.Right <= window.Right + 2
                    && shown.Top >= window.Top - 2 && shown.Bottom <= window.Bottom + 2,
                    language + " at 700x480 the open flyout stays inside the window");
                var pillRect = ScreenRect(pill);
                check(pillRect.Right <= window.Right + 2 && pill.IsVisible,
                    language + " at 700x480 the Up Next pill stays inside the window");
                capture(flyout, language + "-up-next-flyout-narrow");
                capture(main, language + "-up-next-series-page-narrow");
                Escape();
                main.Width = 1280;
                main.Height = 800;
                pump();
                capture(main, language + "-up-next-series-page");

                List<string> Labels() => segments.Items.Cast<ListBoxItem>().Select(item => (string)item.Content).ToList();
                void Choose(string id)
                {
                    segments.SelectedItem = segments.Items.Cast<ListBoxItem>()
                        .Single(item => AutomationProperties.GetAutomationId(item) == id);
                    pump();
                }
            }

            string? Selected() => SelectedLead((FrameworkElement)host.Content);
            void Open() => OpenFlyout(main, (FrameworkElement)host.Content, language + " clicking the pill opens the flyout", check, pump);
            void Escape() => PressEscape((FrameworkElement)host.Content, pump);
        }
        finally { host.Content = null; model.Jellyfin.Dispose(); }
    }

    /// <summary>The episode page's Up Next button opens the same flyout as the series pill and writes
    /// the same key. ItemDetailView is the production class, built offline: its item carries no
    /// season, poster or backdrop, so nothing asks the unreachable server for images, and its
    /// item, stream, version, similar and download loads go through the unconnected SDK client and
    /// fail in-process, as on the offline LibraryView pages.</summary>
    private static void VerifyEpisode(MainWindow main, ContentControl host, AppSettings settings,
        string server, Guid user, string language, Action<bool, string> check,
        Action<FrameworkElement, string> capture, Action pump)
    {
        var english = language == "en";
        var model = new AppViewModel();
        // A show of its own: VerifyLibrary left its show at 60 s, and choosing 60 here must be what
        // the series page then reads, not what it read already.
        var show = Guid.NewGuid();
        UpNextPreferences.TryGetShowKey(server, user, show, out var key);
        try
        {
            typeof(JellyfinService).GetProperty(nameof(JellyfinService.ServerUrl))!.SetValue(model.Jellyfin, server);
            typeof(JellyfinService).GetProperty(nameof(JellyfinService.UserId))!.SetValue(model.Jellyfin, user);
            foreach (var (item, label) in new[]
            {
                (new MediaItem { Id = Guid.NewGuid(), Name = "Synthetic movie", Type = BaseItemDto_Type.Movie, SeriesId = show }, "a movie"),
                (new MediaItem { Id = Guid.NewGuid(), Name = "Synthetic orphan episode", Type = BaseItemDto_Type.Episode }, "an episode without a series"),
            })
            {
                var page = new ItemDetailView(model, settings, item);
                host.Content = page;
                pump();
                var hidden = (Button)page.FindName("UpNextButton");
                check(hidden.Visibility == Visibility.Collapsed && !hidden.IsVisible
                    && ((Button)page.FindName("FavoriteButton")).IsVisible,
                    language + " " + label + " page renders its actions without an Up Next button");
            }

            var detail = new ItemDetailView(model, settings, new MediaItem
            {
                Id = Guid.NewGuid(), Name = "Synthetic episode", Type = BaseItemDto_Type.Episode,
                SeriesId = show, SeriesName = "Synthetic show", IndexNumber = 3,
            });
            host.Content = detail;
            pump();
            var button = (Button)detail.FindName("UpNextButton");
            var favorite = (Button)detail.FindName("FavoriteButton");
            var popup = Part<Popup>(detail, "FlyoutPopup");
            var flyout = Part<Border>(detail, "FlyoutPanel");
            var status = Part<TextBlock>(detail, "Status");
            check(button.IsVisible && Math.Abs(button.ActualWidth - favorite.ActualWidth) < 0.01
                && Math.Abs(button.ActualHeight - favorite.ActualHeight) < 0.01,
                language + " an episode with a series shows the Up Next button at the round action buttons' size");
            check(Tip() == L.Format("ItemDetailView.UpNextTooltipGlobal", Seconds(30))
                && (!english || Tip() == "Up Next for this show: Global (30 s)")
                && AutomationProperties.GetName(button) == Tip(),
                language + " the Up Next button's tooltip and name give the inherited lead");

            Open();
            check(LeadLabels(detail).SequenceEqual(new[] { L.Text("UpNextLeadPicker.Global"),
                Seconds(15), Seconds(30), Seconds(60), L.Text("UpNextLeadPicker.Custom") })
                && SelectedLead(detail) == "UpNextLeadGlobal"
                && status.Text == L.Format("UpNextShowFlyout.StatusGlobal", 30),
                language + " the episode flyout offers the series page's segments at the inherited lead");
            ChooseLead(detail, "UpNextLead60", pump);
            check(UpNextPreferences.GetShowLead(settings, key) == 60
                && UpNextPreferences.GetShowLead(SettingsStore.Load(), key) == 60 && settings.UpNextLeadSeconds == 30
                && status.Text == L.Format("UpNextShowFlyout.StatusShow", 60, 30),
                language + " choosing 60 on the episode page saves the show lead under the series page's key");
            check(Tip() == L.Format("ItemDetailView.UpNextTooltip", Seconds(60))
                && (!english || Tip() == "Up Next for this show: 60 s")
                && AutomationProperties.GetName(button) == Tip(),
                language + " the Up Next button's tooltip and name follow the change");
            PressEscape(detail, pump);
            check(!popup.IsOpen && ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(button)), button),
                language + " Escape closes the episode flyout and returns focus to the Up Next button");

            var before = Tip();
            L.Initialize(english ? "de" : "en");
            pump();
            check(Tip() == L.Format("ItemDetailView.UpNextTooltip", Seconds(60)) && Tip() != before,
                language + " live language switching relabels the Up Next button's tooltip");
            L.Initialize(language);
            pump();

            // Both directions: the series page reads the episode page's choice, and the episode page
            // reads the series page's when it is shown again.
            var series = new LibraryView(model, new MediaItem { Id = show, Name = "Synthetic show", Type = BaseItemDto_Type.Series },
                null, settings);
            Field<ObservableCollection<MediaItem>>(series, "_items").Add(new MediaItem
                { Id = Guid.NewGuid(), Name = "Synthetic season", Type = BaseItemDto_Type.Season, SeriesId = show });
            host.Content = series;
            pump();
            check(((TextBlock)series.FindName("UpNextButtonText")).Text == Pill(Seconds(60)),
                language + " the series page pill reads the 60 s chosen on its episode page");
            OpenFlyout(main, series, language + " clicking the series pill opens the flyout", check, pump);
            check(SelectedLead(series) == "UpNextLead60", language + " the series flyout selects the episode page's 60 s");
            ChooseLead(series, "UpNextLead15", pump);
            PressEscape(series, pump);
            host.Content = detail;
            pump();
            check(Tip() == L.Format("ItemDetailView.UpNextTooltip", Seconds(15)),
                language + " the episode page reads the 15 s chosen on its series page when shown again");

            foreach (var (width, height, size) in new[] { (700, 480, "narrow"), (1280, 800, "standard") })
            {
                main.Width = width;
                main.Height = height;
                pump();
                button.BringIntoView();
                pump();
                var window = ScreenRect((FrameworkElement)main.Content);
                var row = Descendants(detail).OfType<WrapPanel>()
                    .Single(panel => AutomationProperties.GetAutomationId(panel) == "DetailActionRow");
                check(row.Children.OfType<FrameworkElement>().Where(child => child.IsVisible).All(child =>
                        ScreenRect(child) is var rect && rect.Left >= window.Left - 2 && rect.Right <= window.Right + 2),
                    language + " " + size + " the episode action row keeps every button, the Up Next button included, inside the window");
                Open();
                var shown = ScreenRect(flyout);
                check(popup.IsOpen && shown.Width > 0 && shown.Left >= window.Left - 2 && shown.Right <= window.Right + 2
                    && shown.Top >= window.Top - 2 && shown.Bottom <= window.Bottom + 2,
                    language + " " + size + " the episode page's open flyout stays inside the window");
                // RenderTargetBitmap cannot see the popup's own window, so the page and the flyout
                // are captured separately while it is open.
                capture(main, language + "-up-next-episode-page-" + size);
                capture(flyout, language + "-up-next-episode-flyout-" + size);
                PressEscape(detail, pump);
            }

            string Tip() => button.ToolTip as string ?? "";
            void Open() => OpenFlyout(main, detail, language + " clicking the Up Next button opens the flyout", check, pump);
        }
        finally { host.Content = null; model.Jellyfin.Dispose(); }
    }

    private static T Part<T>(FrameworkElement page, string name) where T : class
        => (T)((UpNextShowFlyout)page.FindName("ShowLeadFlyout")).FindName(name);

    private static string? SelectedLead(FrameworkElement page)
        => Part<UpNextLeadPicker>(page, "Picker").FindName("Segments") is Selector { SelectedItem: ListBoxItem item }
            ? AutomationProperties.GetAutomationId(item) : null;

    private static List<string> LeadLabels(FrameworkElement page)
        => Find<LwSegmentedControl>(Part<UpNextLeadPicker>(page, "Picker"), "UpNextLeadSegments")
            .Items.Cast<ListBoxItem>().Select(item => (string)item.Content).ToList();

    private static void ChooseLead(FrameworkElement page, string id, Action pump)
    {
        var segments = Find<LwSegmentedControl>(Part<UpNextLeadPicker>(page, "Picker"), "UpNextLeadSegments");
        segments.SelectedItem = segments.Items.Cast<ListBoxItem>()
            .Single(item => AutomationProperties.GetAutomationId(item) == id);
        pump();
    }

    /// <summary>Both pages name their trigger UpNextButton.</summary>
    private static void OpenFlyout(MainWindow main, FrameworkElement page, string message, Action<bool, string> check, Action pump)
    {
        main.Activate();
        ((Button)page.FindName("UpNextButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        pump();
        check(Part<Popup>(page, "FlyoutPopup").IsOpen, message);
    }

    private static void PressEscape(FrameworkElement page, Action pump)
    {
        var flyout = Part<Border>(page, "FlyoutPanel");
        flyout.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(flyout), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        pump();
    }

    private static void VerifySettings(MainWindow main, ContentControl host, AppSettings settings,
        string language, Action<bool, string> check, Action<FrameworkElement, string> capture, Action pump)
    {
        var english = language == "en";
        var vm = new PlayerViewModel();
        var view = new SettingsView(settings, () => null, vm, Field<UpdateService>(main, "_updateService"));
        host.Content = view;
        pump();
        var picker = Descendants(view).OfType<UpNextLeadPicker>().Single();
        var segments = Find<LwSegmentedControl>(picker, "UpNextLeadSegments");
        var customBox = Find<TextBox>(picker, "UpNextLeadCustomBox");
        var error = Find<TextBlock>(picker, "UpNextLeadError");
        var labels = segments.Items.Cast<ListBoxItem>().Select(item => (string)item.Content).ToList();
        check(labels.SequenceEqual(new[] { Seconds(15), Seconds(30), Seconds(60), L.Text("UpNextLeadPicker.Custom") })
            && segments.Items.Cast<ListBoxItem>().All(item => AutomationProperties.GetAutomationId(item) != "UpNextLeadGlobal"),
            language + " the Settings lead row has no Global segment");
        check(Selected() == "UpNextLead30" && !customBox.IsVisible, language + " the Settings lead row starts at the 30 s default");
        var row = (Panel)VisualTreeHelper.GetParent(picker);
        check(row.Children.OfType<TextBlock>().Any(text => text.Text == L.Text("SettingsView.UpNextLeadHint")
                && (!english || text.Text == "Used when an episode has no credits marker.")),
            language + " the Settings lead row explains the credits marker fallback");
        var countdownBox = Find<TextBox>(view, "AutoPlayCountdownBox");
        var countdownTexts = ((DockPanel)VisualTreeHelper.GetParent(countdownBox)).Children.OfType<StackPanel>().Single()
            .Children.OfType<TextBlock>().ToList();
        check(countdownTexts.Count == 2 && AutomationProperties.GetAutomationId(countdownTexts[1]) == "AutoPlayCountdownError",
            language + " the countdown row has a title and its error line, no hint subtext");

        Choose("UpNextLead60");
        check(settings.UpNextLeadSeconds == 60 && SettingsStore.Load().UpNextLeadSeconds == 60,
            language + " choosing 60 in Settings persists the global lead");
        Choose("UpNextLeadCustom");
        check(customBox.IsVisible && customBox.Text == "60", language + " Settings Custom starts at the current value");
        foreach (var invalid in new[] { "4", "301", "x" })
        {
            customBox.Text = invalid;
            pump();
            check(error.IsVisible && error.Text == L.Format("UpNextLeadPicker.InvalidCustom", 5, 300)
                && settings.UpNextLeadSeconds == 60 && SettingsStore.Load().UpNextLeadSeconds == 60,
                language + " invalid Settings lead " + invalid + " shows the error and keeps 60 s");
        }
        customBox.Text = "90";
        pump();
        check(!error.IsVisible && settings.UpNextLeadSeconds == 90 && SettingsStore.Load().UpNextLeadSeconds == 90,
            language + " a valid custom Settings lead saves");
        foreach (var (width, height, size) in new[] { (1280, 800, "standard"), (700, 480, "narrow") })
        {
            main.Width = width;
            main.Height = height;
            pump();
            countdownBox.BringIntoView();
            picker.BringIntoView();
            pump();
            var bounds = segments.TransformToAncestor(view).TransformBounds(new Rect(segments.RenderSize));
            check(segments.IsVisible && bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1,
                language + " " + size + " Settings lead segments stay inside the page");
            capture(main, language + "-settings-up-next-" + size);
        }
        main.Width = 1280;
        main.Height = 800;
        pump();

        // The global countdown is unchanged by the rework: valid saves, invalid keeps the value.
        var countdownError = Find<TextBlock>(view, "AutoPlayCountdownError");
        countdownBox.Text = "41";
        pump();
        check(settings.AutoPlayCountdownSeconds == 41 && SettingsStore.Load().AutoPlayCountdownSeconds == 41,
            language + " global countdown saves a valid input");
        countdownBox.Text = "121";
        pump();
        check(countdownError.IsVisible && settings.AutoPlayCountdownSeconds == 41
            && SettingsStore.Load().AutoPlayCountdownSeconds == 41 && SettingsStore.Load().UpNextLeadSeconds == 90,
            language + " invalid global countdown displays an error and preserves the saved values");
        countdownBox.Text = "3";
        pump();
        check(!countdownError.IsVisible && settings.AutoPlayCountdownSeconds == 3,
            language + " correcting the global countdown clears validation");
        host.Content = null;

        string? Selected() => segments.SelectedItem is ListBoxItem item ? AutomationProperties.GetAutomationId(item) : null;
        void Choose(string id)
        {
            segments.SelectedItem = segments.Items.Cast<ListBoxItem>()
                .Single(item => AutomationProperties.GetAutomationId(item) == id);
            pump();
        }
    }

    private static void VerifyPlayer(string language, Action<bool, string> check)
    {
        var vm = new PlayerViewModel();
        var next = new MediaItem { Id = Guid.NewGuid(), Name = "Next synthetic episode", Type = BaseItemDto_Type.Episode };
        var advances = 0;
        vm.UpNextPlayRequested += item => { if (item.Id == next.Id) advances++; };
        Set(vm, "_durationSeconds", 200d);
        Set(vm, "_isPaused", false);
        Set(vm, "_isBuffering", false);
        try
        {
            vm.SetUpNext(next, 8, true);
            Invoke(vm, "UpdateUpNext", 169d);
            check(!vm.UpNextVisible, language + " countdown card stays hidden before the default 30-second lead");
            Arm(170);
            check(vm.UpNextVisible && vm.UpNextHasCountdown && Remaining() == 8,
                language + " card begins with the selected eight-second countdown at the trigger");
            Invoke(vm, "TickUpNextCountdown");
            check(Remaining() == 7, language + " countdown advances by one second per tick");
            Set(vm, "_isPaused", true);
            Invoke(vm, "TickUpNextCountdown");
            check(Remaining() == 7, language + " paused playback holds the countdown");
            Set(vm, "_isPaused", false);
            Set(vm, "_isBuffering", true);
            Invoke(vm, "TickUpNextCountdown");
            check(Remaining() == 7, language + " buffering playback holds the countdown");
            Set(vm, "_isBuffering", false);
            Invoke(vm, "UpdateUpNext", 100d);
            check(!vm.UpNextVisible && !vm.UpNextHasCountdown, language + " seeking before credits disarms the card");
            Arm(170);
            check(Remaining() == 8, language + " seeking back into credits re-arms the full selected countdown");
            for (var i = 0; i < 8; i++) Invoke(vm, "TickUpNextCountdown");
            check(advances == 1 && !vm.UpNextVisible && !vm.UpNextHasCountdown,
                language + " completing the selected countdown requests exactly one next episode");

            vm.SetUpNext(next, 60, true);
            Arm(170);
            Invoke(vm, "TickUpNextCountdown");
            var remainingBeforeEof = Remaining();
            Invoke(vm, "HandleUpNextEndReached");
            StopTimer();
            check(advances == 1 && vm.UpNextVisible && vm.UpNextHasCountdown && Remaining() == remainingBeforeEof,
                language + " EOF preserves a long countdown instead of advancing early");
            Set(vm, "_isPaused", true);
            Set(vm, "_isBuffering", true);
            Invoke(vm, "TickUpNextCountdown");
            check(Remaining() == remainingBeforeEof - 1,
                language + " EOF countdown continues despite terminal mpv pause and buffering state");
            Set(vm, "_positionSeconds", 199.96d);
            Invoke(vm, "UpdateUpNext", 199.96d);
            Invoke(vm, "TickUpNextCountdown");
            check(Remaining() == remainingBeforeEof - 2,
                language + " a last-frame position after EOF does not stall the countdown");
            Invoke(vm, "HandleUpNextEndCleared");
            Invoke(vm, "TickUpNextCountdown");
            check(vm.UpNextVisible && Remaining() == remainingBeforeEof - 2,
                language + " leaving EOF inside the credits while paused holds the countdown again");
            Invoke(vm, "HandleUpNextEndReached");
            StopTimer();
            Set(vm, "_positionSeconds", 100d);
            Invoke(vm, "UpdateUpNext", 100d);
            check(vm.UpNextVisible, language + " a position post before eof-reached clears does not disarm at EOF");
            Invoke(vm, "HandleUpNextEndCleared");
            check(!vm.UpNextVisible && !vm.UpNextHasCountdown,
                language + " leaving EOF re-judges a seek out of the credits and disarms");

            Set(vm, "_durationSeconds", 4d);
            vm.SetUpNext(next, 10, true);
            Set(vm, "_positionSeconds", 4d);
            Invoke(vm, "HandleUpNextEndReached");
            StopTimer();
            Invoke(vm, "UpdateUpNext", 3.96d);
            check(vm.UpNextVisible && vm.UpNextHasCountdown && Remaining() == 10,
                language + " a file shorter than the trigger keeps its EOF countdown past a last-frame position");
            Set(vm, "_durationSeconds", 200d);
            Set(vm, "_positionSeconds", 0d);
            Set(vm, "_isPaused", false);
            Set(vm, "_isBuffering", false);
            vm.DismissUpNext();
            Invoke(vm, "HandleUpNextEndReached");
            check(advances == 1 && !vm.UpNextVisible && !vm.UpNextHasCountdown,
                language + " dismissing the card prevents automatic advance at EOF");

            vm.SetUpNext(next, 40, false);
            Arm(170);
            Invoke(vm, "HandleUpNextEndReached");
            check(vm.UpNextVisible && !vm.UpNextHasCountdown && vm.UpNextCountdownText == "" && advances == 1,
                language + " global auto-play off retains the manual card and never advances at EOF");
            vm.PlayUpNextNow();
            check(advances == 2, language + " global auto-play off still permits explicit Play now");

            // The lead time moves the trigger when there is no credits marker: 200 - 60 = 140.
            Set(vm, "_isPaused", false);
            vm.SetUpNext(next, 10, true, 60);
            Arm(139);
            check(!vm.UpNextVisible, language + " a 60-second lead keeps the card hidden at 139 s of 200");
            Arm(140);
            check(vm.UpNextVisible && Remaining() == 10, language + " a 60-second lead shows the card at 140 s of 200");

            vm.SetSkipSegments([new SkipSegment(SkipSegmentKind.Credits, 100, 200, 100, 200)], false, false);
            vm.SetUpNext(next, 13, true, 15);
            Arm(99);
            check(!vm.UpNextVisible, language + " credits metadata replaces the lead-time trigger");
            Arm(100);
            check(vm.UpNextVisible && Remaining() == 13,
                language + " the credits marker wins over a 15-second lead and keeps the countdown");
        }
        finally { vm.SetUpNext(null); }
        int Remaining() => (int)typeof(PlayerViewModel).GetField("_upNextRemaining", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        void StopTimer() => Field<DispatcherTimer?>(vm, "_upNextTimer")?.Stop();
        void Arm(double position) { Invoke(vm, "UpdateUpNext", position); StopTimer(); }
    }

    private static string Seconds(int seconds) => L.Format("UpNextLeadPicker.SecondsShort", seconds);
    private static string Pill(string value) => L.Format("LibraryView.UpNextPill", value);

    private static Rect ScreenRect(FrameworkElement element)
        => new(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    private static void Set(object owner, string name, object value)
        => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static void Invoke(object owner, string name, params object[] arguments)
        => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
        => Descendants(root).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
