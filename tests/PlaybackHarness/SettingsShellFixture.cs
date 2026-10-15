using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Diagnostics;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.Settings;
using LightWeaver.Updates;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// App shell, settings & login: the password reveal, the save-failure banner, alias-aware shortcut
// rebinding, cache readouts on return, coalesced slider saves, and fit at 700x480. Every mode
// brings its own temporary app-data root and no credential. ContractMode opens no window; the UI
// modes open production views and belong on the guest.
internal static class SettingsShellFixture
{
    public const string ContractMode = "--settings-shell-contract";
    public const string UiMode = "--settings-shell-ui";
    public const string ReadDeniedUiMode = "--settings-shell-ui-read-denied";
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
    private static int _assertions;
    private static int _failures;

    public static bool Handles(string[] args)
        => args.Length > 0 && args[0] is ContractMode or UiMode or ReadDeniedUiMode;

    public static int Run(string[] args)
    {
        // Production async void handlers (the passthrough probe in Settings) resume through the
        // synchronization context; without the dispatcher's they resume on a pool thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = Path.Combine(Path.GetTempPath(), "lw-settings-shell-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", root);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        Directory.CreateDirectory(root);
        try
        {
            Require(AppPaths.Root == root && AppPaths.IsDebugEnvironment
                && !File.Exists(Path.Combine(root, "credentials.dat")), "fixture uses isolated Debug app data without a credential");
            switch (args[0])
            {
                case ContractMode: RunContracts(root); break;
                case UiMode:
                    RunUi(root);
                    // The refusal is sticky for a process, so it gets one of its own.
                    RunChild(ReadDeniedUiMode, "isolated read-denied process UI checks pass");
                    break;
                default: RunReadDeniedUi(root); break;
            }
        }
        catch (Exception ex)
        {
            Check(false, "fixture exception: " + ex);
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_assertions} settings shell assertions, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- Windowless contracts --------------------------------------------------------

    private static void RunContracts(string root)
    {
        var close = PlayerActionDispatcher.ChordOf(Key.OemCloseBrackets, ModifierKeys.None);
        var saved = new Dictionary<string, string>
        {
            [nameof(PlayerAction.NextChapter)] = "Oem6",
            [nameof(PlayerAction.SpeedUp)] = "Shift+Ctrl+X",
        };
        var before = PlayerActionDispatcher.Resolve(saved);
        var map = PlayerActionDispatcher.Rebind(saved, PlayerAction.SeekForward, close);
        Check(map[PlayerAction.NextChapter] == "" && map[PlayerAction.SeekForward] == "OemCloseBrackets",
            "capturing ] unbinds an action saved in the alias spelling Oem6");
        Check(map.All(kv => kv.Key is PlayerAction.SeekForward or PlayerAction.NextChapter || kv.Value == before[kv.Key]),
            "rebinding leaves every unrelated binding unchanged");
        var rebound = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        Check(PlayerActionDispatcher.TryResolve(Key.OemCloseBrackets, ModifierKeys.None, rebound, out var owner)
            && owner == PlayerAction.SeekForward, "after rebinding, ] dispatches only to its new owner");
        map = PlayerActionDispatcher.Rebind(saved, PlayerAction.ToggleMute,
            PlayerActionDispatcher.ChordOf(Key.X, ModifierKeys.Control | ModifierKeys.Shift));
        Check(map[PlayerAction.SpeedUp] == "" && map[PlayerAction.ToggleMute] == "Ctrl+Shift+X",
            "a saved modifier order does not hide a duplicate");
        map = PlayerActionDispatcher.Rebind(null, PlayerAction.ToggleMute, PlayerActionDispatcher.ChordOf(Key.Add, ModifierKeys.None));
        Check(map[PlayerAction.SpeedUp] == "" && map[PlayerAction.ToggleMute] == "OemPlus",
            "numpad plus takes the key from Speed up, as it dispatches");
        map = PlayerActionDispatcher.Rebind(null, PlayerAction.ToggleMute, "M");
        Check(map.All(kv => kv.Value == PlayerActionDispatcher.Defaults[kv.Key]),
            "capturing an action's own key changes nothing");

        var path = Path.Combine(root, "settings.json");
        Check(SettingsStore.LastSaveFailure == SettingsSaveFailure.None, "a fresh store reports no save failure");
        Check(SettingsStore.Save(new AppSettings { SettingsVersion = 1, SkipForwardSeconds = 11 })
            && SettingsStore.LastSaveFailure == SettingsSaveFailure.None, "a successful save reports no failure");
        File.Delete(path);
        Directory.CreateDirectory(path);
        Check(!SettingsStore.Save(new AppSettings { SettingsVersion = 1, SkipForwardSeconds = 12 })
            && SettingsStore.LastSaveFailure == SettingsSaveFailure.WriteFailed, "a failed write returns false and reports WriteFailed");
        Directory.Delete(path, recursive: true);
        Check(SettingsStore.Save(new AppSettings { SettingsVersion = 1, SkipForwardSeconds = 13 })
            && SettingsStore.LastSaveFailure == SettingsSaveFailure.None, "the next successful write clears the failure");

        // Last on purpose: a read refusal disables saving for the rest of the process.
        var bytes = File.ReadAllBytes(path);
        using (DenyRead(path))
        {
            var loaded = SettingsStore.Load();
            Check(SettingsStore.LastSaveFailure == SettingsSaveFailure.ReadDenied && loaded.SkipForwardSeconds != 13,
                "an existing file that cannot be read reports ReadDenied as soon as it loads");
            Check(!SettingsStore.Save(loaded) && SettingsStore.LastSaveFailure == SettingsSaveFailure.ReadDenied,
                "a save over an unreadable file is refused and keeps reporting ReadDenied");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "the unreadable file is left byte for byte");
    }

    // ---- Windowed checks (guest) -----------------------------------------------------

    private static void RunUi(string root)
    {
        // Verbose logging on in the saved file, so the store's save lines can be counted: Settings
        // re-applies the saved flag to the log on every change.
        Require(SettingsStore.Save(new AppSettings
        {
            SettingsVersion = 1,
            VerboseLogging = true,
            KeyBindings = new() { [nameof(PlayerAction.NextChapter)] = "Oem6" },
        }), "fixture seeds settings");
        var app = UiFixtureApplication.Create();
        var main = new MainWindow { Width = 700, Height = 480, Left = 20, Top = 20 };
        var closed = false;
        try
        {
            main.Show();
            main.Activate();
            Pump();
            VerifyLogin(main);
            closed = VerifySettings(main, root);
        }
        finally
        {
            if (!closed) main.Close();
            app.Shutdown();
        }
    }

    private static void VerifyLogin(MainWindow main)
    {
        var login = Descendants(main).OfType<LoginView>().Single();
        var box = Element<PasswordBox>(login, "PasswordBox");
        var reveal = Element<TextBox>(login, "PasswordReveal");
        var peek = Element<Button>(login, "PasswordPeek");
        var scroll = Descendants(login).OfType<ScrollViewer>().Single(s => s.Content is StackPanel);
        Check(main.ActualWidth <= 701 && main.ActualHeight <= 481, "login runs in a 700x480 window");
        peek.BringIntoView();
        Pump();
        Check(IsInsideViewport(box, scroll) && IsInsideViewport(peek, scroll),
            "700x480 login keeps the password field and its reveal button in view");
        Check(AutomationProperties.GetName(peek) == L.Text("LoginView.ShowPassword")
            && peek.ToolTip as string == L.Text("LoginView.ShowPassword"), "masked password offers Show password");
        Check(AutomationProperties.GetName(box) == L.Text("LoginView.Password.6c4492e4")
            && AutomationProperties.GetName(reveal) == L.Text("LoginView.Password.6c4492e4"),
            "both password fields carry the localized Password name for screen readers");
        box.Password = "synthetic-pass-1";
        Capture(main, "login-masked-narrow");

        Invoke(peek);
        Check(reveal.IsVisible && !box.IsVisible && reveal.Text == "synthetic-pass-1",
            "reveal shows the typed password in a plain-text field");
        Check(reveal.IsFocused && reveal.CaretIndex == reveal.Text.Length, "reveal moves focus to the visible field, caret at the end");
        Check(AutomationProperties.GetName(peek) == L.Text("LoginView.HidePassword")
            && peek.ToolTip as string == L.Text("LoginView.HidePassword"), "revealed password offers Hide password");
        Check(IsInsideViewport(reveal, scroll), "700x480 login keeps the revealed field in view");
        Capture(main, "login-revealed-narrow");

        reveal.Text = "synthetic-pass-2";
        var current = (string)typeof(LoginView).GetProperty("CurrentPassword", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(login)!;
        Check(current == "synthetic-pass-2", "sign-in reads the revealed field while it is showing");
        reveal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reveal)!, 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
        var error = Element<TextBlock>(login, "ErrorText");
        Check(error.IsVisible && error.Text == L.Text("LoginView.ServerAndUsernameAreRequired.b128c904"),
            "Enter in the revealed field submits the form");
        error.Visibility = Visibility.Collapsed;

        Invoke(peek);
        Check(box.IsVisible && !reveal.IsVisible && box.Password == "synthetic-pass-2" && reveal.Text.Length == 0,
            "hiding carries the edit back to the masked field and empties the plain-text twin");
        Check(box.IsFocused, "hiding moves focus back to the masked field");

        Invoke(peek);
        login.Visibility = Visibility.Collapsed;
        Pump();
        Check(reveal.Visibility == Visibility.Collapsed && reveal.Text.Length == 0 && box.Password == "synthetic-pass-2",
            "leaving the sign-in screen masks the password again");
        login.Visibility = Visibility.Visible;
        Pump();
        Check(AutomationProperties.GetName(peek) == L.Text("LoginView.ShowPassword"), "a return to sign-in starts masked");
    }

    /// <returns>true once the window has been closed by the close-flush check.</returns>
    private static bool VerifySettings(MainWindow main, string root)
    {
        var settings = Field<AppSettings>(main, "_settings");
        var host = Element<ContentControl>(main, "ShellHost");
        var view = new SettingsView(settings, () => null, Field<PlayerViewModel>(main, "_playerViewModel"),
            Field<UpdateService>(main, "_updateService"));
        typeof(MainWindow).GetField("_settingsView", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, view);
        host.Content = view;
        Pump();
        var away = new Grid();
        var banner = Element<Border>(view, "SaveFailureBanner");
        var bannerText = Element<TextBlock>(view, "SaveFailureText");
        var scroll = Descendants(view).OfType<ScrollViewer>().First(s => s.Content is StackPanel);
        var path = Path.Combine(root, "settings.json");
        Check(banner.Visibility == Visibility.Collapsed, "a healthy store shows no save banner");

        VerifyFit(view, scroll);

        // Shortcut aliases, through the production capture path.
        var seek = Element<Button>(view, "ShortcutSeekForward");
        var chapter = Element<Button>(view, "ShortcutNextChapter");
        Check(settings.KeyBindings[nameof(PlayerAction.NextChapter)] == "Oem6"
            && chapter.Content as string == PlayerActionDispatcher.DisplayChord("Oem6"),
            "precondition: Next chapter is saved as Oem6");
        Invoke(seek);
        view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.OemCloseBrackets)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump();
        Check(seek.Content as string == PlayerActionDispatcher.DisplayChord("OemCloseBrackets")
            && chapter.Content as string == PlayerActionDispatcher.DisplayChord(""),
            "capturing ] for Seek forward unbinds Next chapter saved as Oem6");
        var file = ReadSaved(path);
        Check(file.KeyBindings[nameof(PlayerAction.NextChapter)] == ""
            && file.KeyBindings[nameof(PlayerAction.SeekForward)] == "OemCloseBrackets", "the saved bindings agree");

        // Slider saves: label and limit live, file once.
        var image = Element<Slider>(view, "ImageCacheSizeSlider");
        var imageLabel = Element<TextBlock>(view, "ImageCacheSizeLabel");
        var start = Saves();
        bool labels = true, limits = true;
        foreach (var mb in new[] { 600, 700, 800, 900, 1000 })
        {
            image.Value = mb;
            Pump();
            labels &= imageLabel.Text == L.Format("SettingsView.Mb.67a3de26", mb);
            limits &= LightWeaver.Imaging.ImageCache.MaxCacheBytes == mb * 1024L * 1024;
        }
        Check(labels, "the image cache label follows every slider step");
        Check(limits, "the image cache limit is live-applied on every step");
        Check(Saves() == start, "five quick slider steps write nothing yet");
        Pump(800);
        Check(Saves() == start + 1 && ReadSaved(path).ImageCacheMaxMb == 1000,
            "the settled slider gesture is written once, with its final value");

        var thumb = ((Track)image.Template.FindName("PART_Track", image)).Thumb;
        start = Saves();
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
        foreach (var mb in new[] { 1100, 1200, 1300 })
        {
            image.Value = mb;
            Pump();
        }
        Pump(800);
        Check(Saves() == start, "a drag held still past the settle delay still writes nothing");
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        Check(Saves() == start + 1 && ReadSaved(path).ImageCacheMaxMb == 1300, "releasing the drag writes the final value at once");

        var folder = Element<Slider>(view, "FolderCacheSizeSlider");
        start = Saves();
        folder.Value = 6000;
        Pump();
        Check(Element<TextBlock>(view, "FolderCacheSizeLabel").Text == $"{6000:N0}"
            && LightWeaver.Jellyfin.BrowseFolderCache.MaxItems == 6000, "the folder cache label and limit follow the slider live");
        host.Content = away;
        Pump();
        Check(Saves() == start + 1 && ReadSaved(path).FolderCacheMaxItems == 6000,
            "leaving Settings writes a slider change that had not settled");

        host.Content = view;
        Pump();
        var subScale = Element<Slider>(view, "SubScaleSlider");
        var skipIntro = Element<CheckBox>(view, "AutoSkipIntroBox");
        start = Saves();
        subScale.Value = 1.2;
        Pump();
        Check(Element<TextBlock>(view, "SubScaleLabel").Text == "120%", "the subtitle size label follows the slider live");
        skipIntro.IsChecked = skipIntro.IsChecked != true;
        Pump(800);
        file = ReadSaved(path);
        Check(Saves() == start + 1 && file.SubtitleScale == 1.2 && file.AutoSkipIntro == (skipIntro.IsChecked == true),
            "a toggle saves at once, carries the pending slider value, and nothing is written twice");

        // Cache readouts on return.
        var readout = Element<TextBlock>(view, "ImageCacheReadout");
        var emptyReadout = readout.Text;
        host.Content = away;
        Pump();
        var imageDir = Path.Combine(root, "imagecache");
        Directory.CreateDirectory(imageDir);
        File.WriteAllBytes(Path.Combine(imageDir, "fixture.img"), new byte[3 * 1024 * 1024]);
        host.Content = view;
        Pump();
        Check(readout.Text != emptyReadout && readout.Text == (3.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            + L.Text("SettingsView.Mb.6bb5abe0"), "returning to Settings re-reads a cache that grew while it was away");
        // Hidden and shown again without leaving the tree raises no Loaded, so LiveText's own
        // reload cannot be what refreshes the readout here.
        view.Visibility = Visibility.Collapsed;
        Pump();
        File.WriteAllBytes(Path.Combine(imageDir, "fixture-2.img"), new byte[2 * 1024 * 1024]);
        view.Visibility = Visibility.Visible;
        Pump();
        Check(readout.Text == (5.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            + L.Text("SettingsView.Mb.6bb5abe0"), "showing Settings again without a reload re-reads the cache size");

        // Save-failure banner.
        File.Delete(path);
        Directory.CreateDirectory(path);
        var skipCredits = Element<CheckBox>(view, "AutoSkipCreditsBox");
        skipCredits.IsChecked = skipCredits.IsChecked != true;
        Pump();
        Check(banner.IsVisible && bannerText.Text == L.Text("SettingsView.SaveWriteFailed"),
            "a failed write shows the write-failure banner");
        Check(AutomationProperties.GetLiveSetting(bannerText) == AutomationLiveSetting.Assertive
            && new TextBlockAutomationPeer(bannerText).GetName() == bannerText.Text,
            "the banner text is an assertive live region that names the failure");
        CheckBannerFit(view, banner, scroll, "write-failure");
        Capture(main, "settings-write-failed-narrow");
        Directory.Delete(path, recursive: true);
        skipCredits.IsChecked = skipCredits.IsChecked != true;
        Pump();
        Check(banner.Visibility == Visibility.Collapsed && ReadSaved(path).AutoSkipCredits == (skipCredits.IsChecked == true),
            "the next successful save hides the banner");

        host.Content = away;
        Pump();
        File.Delete(path);
        Directory.CreateDirectory(path);
        Require(!SettingsStore.Save(settings), "a save made elsewhere fails while the page is away");
        host.Content = view;
        Pump();
        Check(banner.IsVisible && bannerText.Text == L.Text("SettingsView.SaveWriteFailed"),
            "a failed save made elsewhere shows on return to Settings");
        Directory.Delete(path, recursive: true);
        host.Content = away;
        Pump();
        Require(SettingsStore.Save(settings), "a save made elsewhere succeeds again");
        host.Content = view;
        Pump();
        Check(banner.Visibility == Visibility.Collapsed, "a later successful save made elsewhere clears it on return");

        // Close flush.
        start = Saves();
        image.Value = 400;
        Pump();
        main.Close();
        Check(Saves() == start + 1 && ReadSaved(path).ImageCacheMaxMb == 400,
            "closing the window writes a slider change that had not settled");
        return true;
    }

    /// <summary>Nothing on the page may be cut off at 700x480: the controls this work touched must
    /// come fully into view, and every visible control must fit the viewport's width.</summary>
    private static void VerifyFit(SettingsView view, ScrollViewer scroll)
    {
        var touched = new[] { "ImageCacheSizeSlider", "ImageCacheSizeLabel", "FolderCacheSizeSlider", "FolderCacheSizeLabel",
            "SubScaleSlider", "SubScaleLabel", "OmdbKeyPeek", "ShortcutSeekForward", "ShortcutNextChapter" };
        foreach (var name in touched)
        {
            var control = Element<FrameworkElement>(view, name);
            control.BringIntoView();
            Pump();
            Check(IsInsideViewport(control, scroll), "700x480 Settings brings " + name + " fully into view");
        }
        var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var clipped = Descendants((DependencyObject)scroll.Content).OfType<Control>()
            .Where(c => c.IsVisible && c is ButtonBase or ComboBox or Slider or TextBox or PasswordBox)
            .Where(c =>
            {
                var b = c.TransformToAncestor(viewport).TransformBounds(new Rect(c.RenderSize));
                return b.Left < -1 || b.Right > viewport.ActualWidth + 1;
            })
            .Select(c => string.IsNullOrEmpty(c.Name) ? AutomationProperties.GetAutomationId(c) : c.Name).ToArray();
        Check(clipped.Length == 0, "700x480 Settings fits every visible control to the page width"
            + (clipped.Length == 0 ? "" : " (clipped: " + string.Join(", ", clipped) + ")"));
        scroll.ScrollToTop();
        Pump();
        Capture(Window.GetWindow(view)!, "settings-top-narrow");
    }

    private static void RunReadDeniedUi(string root)
    {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"SettingsVersion\":1,\"SkipForwardSeconds\":17}");
        var bytes = File.ReadAllBytes(path);
        using (DenyRead(path))
        {
            var app = UiFixtureApplication.Create();
            var main = new MainWindow { Width = 700, Height = 480, Left = 20, Top = 20 };
            try
            {
                main.Show();
                Pump();
                var view = new SettingsView(Field<AppSettings>(main, "_settings"), () => null,
                    Field<PlayerViewModel>(main, "_playerViewModel"), Field<UpdateService>(main, "_updateService"));
                Element<ContentControl>(main, "ShellHost").Content = view;
                Pump();
                var banner = Element<Border>(view, "SaveFailureBanner");
                var text = Element<TextBlock>(view, "SaveFailureText");
                Check(banner.IsVisible && text.Text == L.Text("SettingsView.SaveRefusedUnreadable"),
                    "an unreadable existing file shows the refusal banner before any change");
                Check(text.Text != L.Text("SettingsView.SaveWriteFailed"), "the refusal is worded differently from a failed write");
                CheckBannerFit(view, banner, Descendants(view).OfType<ScrollViewer>().First(s => s.Content is StackPanel), "refusal");
                var skipIntro = Element<CheckBox>(view, "AutoSkipIntroBox");
                skipIntro.IsChecked = skipIntro.IsChecked != true;
                Pump();
                Check(banner.IsVisible && text.Text == L.Text("SettingsView.SaveRefusedUnreadable"),
                    "a change made while refused keeps the refusal banner");
                Capture(main, "settings-read-denied-narrow");
            }
            finally
            {
                main.Close();
                app.Shutdown();
            }
        }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "the unreadable settings file is untouched by the session");
    }

    private static void CheckBannerFit(SettingsView view, Border banner, ScrollViewer scroll, string kind)
    {
        var bounds = banner.TransformToAncestor(view).TransformBounds(new Rect(banner.RenderSize));
        Check(bounds.Left >= 0 && bounds.Right <= view.ActualWidth + 1 && bounds.Top >= 0 && scroll.ActualHeight >= 200,
            "700x480 keeps the " + kind + " banner inside the page and the settings scroller usable");
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static IDisposable DenyRead(string path)
    {
        var info = new FileInfo(path);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Restore(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>Successful settings writes so far, from the store's own log line.</summary>
    private static int Saves()
    {
        AppLog.Flush();
        var log = Path.Combine(AppLog.Dir, "app.log");
        if (!File.Exists(log)) return 0;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
            if (line.Contains("event=save outcome=success", StringComparison.Ordinal)) count++;
        return count;
    }

    private static AppSettings ReadSaved(string path)
        => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;

    private static void Invoke(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        Pump();
    }

    private static void RunChild(string mode, string description)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child process was not started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        Console.Write(stdout.GetAwaiter().GetResult());
        Console.Write(stderr.GetAwaiter().GetResult());
        Check(process.HasExited && process.ExitCode == 0, description);
    }

    private static T Field<T>(object owner, string name) where T : class
        => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    /// <summary>A named XAML control, or a generated one (the shortcut rows) by its AutomationId.</summary>
    private static T Element<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T
            ?? Descendants(root).OfType<T>().FirstOrDefault(c => c is DependencyObject d && AutomationProperties.GetAutomationId(d) == name)
            ?? throw new InvalidOperationException("Missing control " + name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static bool IsInsideViewport(FrameworkElement control, ScrollViewer scroll)
    {
        var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        return control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0
            && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }

    private static void Capture(FrameworkElement visual, string name)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(visual.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Output);
        var path = Path.Combine(Output, "settings-shell-" + name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("CAPTURE: " + path);
    }

    private static void Pump(int milliseconds = 150)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {message}");
    }
}
