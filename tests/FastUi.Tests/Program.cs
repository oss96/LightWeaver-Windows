using System.Text.RegularExpressions;
using System.Xml.Linq;

// The owner's rule for LightWeaver v2 (2026-10-17): WinUI's default motion stays off. The W1 spike
// measured stock WinUI at 3 to 5 times slower than the WPF app (spikes/W1-winui3-composition,
// criterion 8); FastUi.cs holds the runtime levers, this test holds the source-level ones.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var app = Path.Combine(root, "src", "LightWeaver.WinUI");
var failures = new List<string>();
var files = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
    .ToList();
string Rel(string p) => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/');

const string FlyoutTypes = "TextCommandBarFlyout|CommandBarFlyout|MenuFlyout|Flyout";
var flyouts = 0;
foreach (var path in files.Where(p => p.EndsWith(".xaml", StringComparison.Ordinal)))
{
    var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
    foreach (var e in doc.Descendants())
    {
        var name = e.Name.LocalName;
        var line = ((System.Xml.IXmlLineInfo)e).LineNumber;
        // 1. Every flyout opens and closes without the slide-and-fade animation.
        if (Regex.IsMatch(name, $"^({FlyoutTypes})$"))
        {
            flyouts++;
            if (e.Attribute("AreOpenCloseAnimationsEnabled")?.Value != "False")
                failures.Add($"{Rel(path)}:{line} {name} without AreOpenCloseAnimationsEnabled=\"False\"");
        }
        // 2. No theme transitions or brush/scalar fades. An empty <TransitionCollection/> is the
        //    XAML way to switch a control's transitions off and is allowed.
        if (name.EndsWith("ThemeTransition", StringComparison.Ordinal) || name is "BrushTransition" or "ScalarTransition" or "Vector3Transition")
            failures.Add($"{Rel(path)}:{line} motion element <{name}>");
        // 3. No animated visual-state changes in the app's own templates.
        if (name is "VisualTransition" || (name is "Storyboard" && e.Ancestors().Any(a => a.Name.LocalName == "VisualState")))
            failures.Add($"{Rel(path)}:{line} animated visual state <{name}>");
    }
}

// 4. Flyouts created in code are wrapped in FastUi.Flyout(...) in the same statement. Matching is
//    on the type, across line breaks, for `new T(...)`, `new T { ... }` and target-typed `T x = new()`.
var explicitNew = new Regex($@"\bnew\s+({FlyoutTypes})\b", RegexOptions.Multiline);
var targetTyped = new Regex($@"\b({FlyoutTypes})\??\s+\w+\s*=\s*new\s*\(", RegexOptions.Multiline);
foreach (var path in files.Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
{
    var text = File.ReadAllText(path);
    int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
    foreach (Match m in explicitNew.Matches(text))
    {
        flyouts++;
        var statementStart = text.LastIndexOfAny([';', '{', '}'], Math.Max(0, m.Index - 1)) + 1;
        if (!text[statementStart..m.Index].Contains("FastUi.Flyout(", StringComparison.Ordinal))
            failures.Add($"{Rel(path)}:{LineOf(m.Index)} {m.Groups[1].Value} created without FastUi.Flyout(...)");
    }
    foreach (Match m in targetTyped.Matches(text))
    {
        flyouts++;
        failures.Add($"{Rel(path)}:{LineOf(m.Index)} target-typed {m.Groups[1].Value}: write FastUi.Flyout(new {m.Groups[1].Value}())");
    }
}

// 5. Every window attaches the runtime levers.
foreach (var path in files.Where(p => p.EndsWith(".xaml.cs", StringComparison.Ordinal)))
{
    var text = File.ReadAllText(path);
    if (Regex.IsMatch(text, @":\s*Window\b") && !text.Contains("FastUi.Attach(this)", StringComparison.Ordinal))
        failures.Add($"{Rel(path)} window without FastUi.Attach(this)");
}

// 6. Home stays in the tree, collapsed in its own host, while another page shows, so XAML keeps its
//    decoded artwork; See more shows the library grid's skeleton while its first load runs.
bool Declares(string xaml, string name, string visibility) => XDocument.Load(Path.Combine(app, xaml)).Descendants()
    .Any(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name) && e.Attribute("Visibility")?.Value == visibility);
bool Says(string code, string text) => File.ReadAllText(Path.Combine(app, code)).Contains(text, StringComparison.Ordinal);
if (!Declares("Shell/MainWindow.xaml", "HomeHost", "Collapsed") || !Says("Shell/MainWindow.xaml.cs", "if (home) HomeHost.Content = page;"))
    failures.Add("Shell/MainWindow: Home is not kept in its own collapsed HomeHost");
if (!Declares("Pages/SectionPage.xaml", "SkeletonScroller", "Collapsed") || !Says("Pages/SectionPage.xaml.cs", "SkeletonScroller.Visibility = Visibility.Visible"))
    failures.Add("Pages/SectionPage: no skeleton while the first load runs");

// 7. Play a link and Open file stay reachable: profile menu items with their shortcuts, the
//    accelerators, Settings › Keys, the dialog's empty and clipboard states, and the link failure.
void Requires(string file, params string[] snippets)
{
    var text = File.ReadAllText(Path.Combine(app, file));
    foreach (var snippet in snippets.Where(s => !text.Contains(s, StringComparison.Ordinal)))
        failures.Add($"src/LightWeaver.WinUI/{file} lacks {snippet}");
}
Requires("Shell/MainWindow.Profile.cs", "\"ProfilePlayLink\"", "L.Text(\"Profile.PlayLinkShortcut\")", "\"ProfileOpenFile\"", "L.Text(\"Profile.OpenFileShortcut\")",
    "link.Click += (_, _) => _ = OpenStreamAsync()", "file.Click += (_, _) => _ = OpenFileAsync()");
Requires("Shell/MainWindow.Files.cs", "VirtualKey.L, VirtualKeyModifiers.Control, () => _ = OpenStreamAsync()", "VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenFileAsync()",
    "IsPrimaryButtonEnabled = false", "await ClipboardLinkAsync()");
Requires("Pages/SettingsPage.cs", "\"Ctrl+L\"", "\"Ctrl+O\"");
Requires("Player/PlayerView.xaml.cs", "FailureCause.Link => L.Text(\"Player.FailedLink\")");

// 8. The updater runs as in the WPF app: started with the app, Settings › Updates acting through it
//    and passing a policy change on, and the installer handed off when the window closes.
Requires("App.xaml.cs", "Services.Updater.StartAsync(");
Requires("Services/Updater.cs", "new UpdateService(settings)", "service.Start();", "service.TryLaunchInstallAfterExit();");
Requires("Shell/MainWindow.xaml.cs", "InitializeUpdates();");
Requires("Shell/MainWindow.Updates.cs", "Updater.OnExit();", "_updateNotices.Next(");
Requires("Pages/SettingsPage.cs", "UpdatePane.For(", "Updater.OnPolicyChanged();", "Updater.RunAsync(pane.Action");

Requires("Player/PlayerView.xaml", "PlayerPositionButton", "PlayerTimeEdit", "OnTimeEditKeyDown", "OnTimeEditLostFocus");
Requires("Player/PlayerView.xaml.cs", "TimeInput.TryParse(", "vm.Seek(TimeInput.Target(");
Requires("Player/PlaybackController.cs", "UpNextLead.For(", "SubtitleCarryOver");
Requires("Pages/SettingsPage.cs", "UpNextPreferences.MaximumCountdownSeconds", "SettingsUpNextLead", "SettingsZoomLimit", "KeyChords.IsZoomChord(");
Requires("Shell/MainWindow.Zoom.cs", "Root.PreviewKeyDown += OnZoomKey", "UiZoom.SetLimitToScreen(", "UiZoom.Wheel(");
Requires("Shell/MainWindow.Navigation.cs", "CachedBanner.IsOpen", "OfflinePlan.For(", "CachedRetryNow");
Requires("Shell/MainWindow.Downloads.cs", "DownloadAudioChoice", "DownloadSubtitleChoice", "DownloadTracks.ForTitle(", "DownloadTracks.Build(");
Requires("Shell/MainWindow.RemoteControl.cs", "ItemRemovals.ForProfile(", "RemoveItems(");
// 9. The detail page offers what the player does before Play: the version drop-down and the
//    player's quality tiers with Always transcode, carried into the choice Play sends.
Requires("Pages/DetailPage.xaml", "DetailVersion", "OnVersion");
// The version drop-down keeps its slot whether or not it is shown, so late versions never re-lay out the bar.
Requires("Pages/DetailPage.xaml.cs", "VersionButton.Opacity = showVersions ? 1 : 0;");
if (Says("Pages/DetailPage.xaml.cs", "VersionButton.Visibility") || Declares("Pages/DetailPage.xaml", "VersionButton", "Collapsed"))
    failures.Add("Pages/DetailPage: the version drop-down gives up its slot; hide it with Opacity like the trailer button");
Requires("Pages/DetailPage.xaml.cs", "PlayerViewModel.QualityTierMbps", "\"DetailForceTranscode\"", "_quality, _forceTranscode)");
var shellTree = XDocument.Load(Path.Combine(app, "Shell/MainWindow.xaml"));
var zoomNames = shellTree.Descendants().Where(e => e.Name.LocalName == "ZoomBox")
    .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName == "Name").Select(a => a.Value)).ToHashSet();
if (!zoomNames.IsSupersetOf(["BarZoom", "LayerZoom", "ToastZoom"])) failures.Add("Shell/MainWindow: app chrome lacks zoom hosts");
if (shellTree.Descendants().Any(e => e.Name.LocalName == "SwapChainPanel" && e.Ancestors().Any(a => a.Name.LocalName == "ZoomBox")))
    failures.Add("Shell/MainWindow: video must stay outside app zoom");

// The rules must catch what they claim to; a broken pattern would pass everything.
string[] mustFail =
[
    "var m = new MenuFlyout();",
    "MenuFlyout m = new();",
    "var f = new\n  Flyout { Placement = x };",
    "var t = new TextCommandBarFlyout();",
];
foreach (var sample in mustFail)
    if (!explicitNew.IsMatch(sample) && !targetTyped.IsMatch(sample))
        failures.Add("self-check: pattern misses " + sample.Replace("\n", " "));
if (explicitNew.IsMatch("var x = FastUi.Flyout(new MenuFlyout());") is false)
    failures.Add("self-check: wrapped flyout not recognised");

Requires("Player/PlaybackController.cs", "await player.AddExternalSubtitleAsync(", "ViewModel.CompleteExternalSubtitle(");
var playbackSource = File.ReadAllText(Path.Combine(app, "Player", "PlaybackController.cs"));
var attachmentStart = playbackSource.IndexOf("private async Task AttachExternalSubtitlesAsync(", StringComparison.Ordinal);
var attachmentEnd = playbackSource.IndexOf("private void OnTracksChanged(", attachmentStart, StringComparison.Ordinal);
var attachment = playbackSource[attachmentStart..attachmentEnd];
if (attachment.Contains("ApplyTracksAsync(", StringComparison.Ordinal) || !attachment.Contains("ApplyLateSubtitleChoice(", StringComparison.Ordinal))
    failures.Add("a delayed subtitle reply must reconcile subtitles without reapplying initial audio choices");
var lateChoiceStart = playbackSource.IndexOf("private void ApplyLateSubtitleChoice(", StringComparison.Ordinal);
var lateChoiceEnd = playbackSource.IndexOf("private bool ApplyDownloadSubtitle(", lateChoiceStart, StringComparison.Ordinal);
if (playbackSource[lateChoiceStart..lateChoiceEnd].Contains("Audio", StringComparison.Ordinal))
    failures.Add("late subtitle reconciliation must preserve audio choices made while loading");
Requires("Player/PlayerView.xaml.cs", "row.IsEnabled = false;", "PlayerSubtitleUnavailable", "vm.UnavailableSubtitles.CollectionChanged += OnRowsChanged;", "vm.UnavailableSubtitles.CollectionChanged -= OnRowsChanged;", "row.Focus(focusState)");

// 10. In a SyncPlay group a click on the video waits out the double-click time and a second press
//     cancels it, so a double-click for full screen never pauses and resumes the whole group.
Requires("Player/PlayerView.xaml.cs", "if (vm.SyncPlay is not null)", "_groupClick.Start();", "if (_groupClick.IsRunning)");

// 9. A XamlRoot's ContentIslandEnvironment is null while its window tears down, and pointer events
//    still arrive then; it is read through a pattern, never dereferenced directly.
foreach (var path in files.Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
{
    var text = File.ReadAllText(path);
    foreach (Match m in Regex.Matches(text, @"\.ContentIslandEnvironment\s*\."))
        failures.Add($"{Rel(path)}:{text.AsSpan(0, m.Index).Count('\n') + 1} ContentIslandEnvironment dereferenced without a null check");
}

foreach (var f in failures) Console.WriteLine("FAIL " + f);
Console.WriteLine($"Fast UI check: {files.Count} files, {flyouts} flyouts, {failures.Count} failures.");
return failures.Count == 0 ? 0 : 1;
