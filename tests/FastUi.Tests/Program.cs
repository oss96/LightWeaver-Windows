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
Requires("Pages/SectionPage.xaml.cs", "QueryCache.Sections.ReadAsync", "wanted = Math.Max(wanted, cached.Items.Count)",
    "_shownAtUtc = null", "_shell.CachedDataShown(this)", "_shown.Sync(items)", "MediaCard.RefreshRealized(Items, _shown)");
Requires("Search/AdvancedSearchPage.xaml.cs", "UserControl, INavPage", "cache: QueryCache.Searches", "CachedAtUtc => _model.ShownAtUtc",
    "_model.Failed += (_, _) => _shell.CachedDataShown(this)", "!_model.HasResults && !_model.HasFailed");
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
Requires("Shell/MainWindow.Updates.cs", "notice.ActionLabel", "notice.CanRun(Updater.Status, App.Settings.UpdatePolicy)", "Updater.RunAsync(notice.Action, Quit)");
Requires("Shell/MainWindow.Updates.cs", "RunUpdateNoticeAsync(notice), \"update\", lasting: true)", "ToastHost.Remove(\"update\")");
Requires("Shell/MainWindow.Updates.cs", "!visible.CanRemain(Updater.Status, App.Settings.UpdatePolicy, place)", "_visibleUpdateNotice = failure;");
Requires("Shell/MainWindow.Updates.cs", "UpdateNotice.Failure(Updater.Status, App.Settings.UpdatePolicy)", "ToastHost.Show(ToastKind.Error, failure.Message, failure.ActionLabel", "RunUpdateNoticeAsync(failure)");
Requires("Pages/SettingsPage.cs", "UpdatePane.For(", "Updater.OnPolicyChanged();", "Updater.RunAsync(pane.Action");

Requires("Player/PlayerView.xaml", "PlayerPositionButton", "PlayerTimeEdit", "OnTimeEditKeyDown", "OnTimeEditLostFocus");
Requires("Player/PlayerView.xaml", "PlayerPreviousChapter", "PlayerNextChapter", "Icons.PreviousChapter", "Icons.NextChapter",
    "AllowFocusOnInteraction=\"False\" Click=\"OnPreviousChapter\"", "AllowFocusOnInteraction=\"False\" Click=\"OnNextChapter\"",
    "PlayerPrevious", "Icons.Previous}", "AllowFocusOnInteraction=\"False\" Click=\"OnPrevious\"");
Requires("Player/PlayerView.xaml.cs", "PreviousChapterButton.Visibility = NextChapterButton.Visibility = vm.HasChapters", "Vm?.PreviousChapter()", "Vm?.NextChapter()",
    "L.Text(\"Player.PreviousChapter\")", "L.Text(\"Player.NextChapter\")",
    "PreviousButton.Visibility = vm.CanGoPrevious", "Vm?.GoPrevious()", "L.Text(\"Player.Previous\")");
Requires("Player/PlayerView.xaml.cs", "TimeInput.TryParse(", "vm.Seek(TimeInput.Target(");
Requires("Player/PlaybackController.cs", "UpNextLead.For(", "SubtitleCarryOver");
Requires("Player/PlaybackController.cs", "Start(slice[0], choice, session, intentTaken: true)", "Start(episode, choice, session, intentTaken: true)", "if (!intentTaken) _intent++;");
Requires("Pages/SettingsPage.cs", "UpNextPreferences.MaximumCountdownSeconds", "SettingsUpNextLead", "SettingsZoomLimit", "KeyChords.IsZoomChord(");
Requires("Shell/MainWindow.Zoom.cs", "Root.PreviewKeyDown += OnZoomKey", "UiZoom.SetLimitToScreen(", "UiZoom.Wheel(");
Requires("Shell/MainWindow.Navigation.cs", "CachedBanner.IsOpen", "OfflinePlan.For(", "CachedRetryNow");
Requires("Shell/MainWindow.Downloads.cs", "DownloadAudioChoice", "DownloadSubtitleChoice", "DownloadTracks.ForTitle(", "DownloadTracks.Build(");
Requires("Shell/IAppShell.cs", "void PlayDownload(DownloadItem download, PlayOrigin? from = null)");
Requires("Controls/MediaCard.xaml.cs", "internal PlayOrigin Origin => new(Art, Picture.Source)");
Requires("Controls/CardRow.cs", "Action<MediaItem, PlayOrigin>? Open", "card.Invoked += (_, i) => open(i, card.Origin)");
Requires("Pages/HomePage.cs", "Open = (item, origin) => { if (shell.Connectivity.IsOffline) shell.Play(item, from: origin); else shell.OpenItem(item); }");
Requires("Shell/ServerUnreachableView.xaml.cs", "Open = (item, origin) => shell.Play(item, from: origin)");
Requires("Pages/DownloadsPage.cs", "_shell.PlayDownload(row.Item, row.Origin)", "public PlayOrigin Origin => new(_thumb, _image.Source)");
var downloadsPage = File.ReadAllText(Path.Combine(app, "Pages/DownloadsPage.cs"));
if (Regex.Matches(downloadsPage, @"\(\) => _page\.Play\(this\)").Count != 2)
    failures.Add("Both completed and running downloads must play from their row artwork");
var playerShell = File.ReadAllText(Path.Combine(app, "Shell/MainWindow.Player.cs"));
Requires("Shell/MainWindow.Player.cs", "_cardOrigin = from", "_cardPage = _nav.CurrentPage",
    "SetOsFullscreen(false, () => ShrinkToCard(origin))", "Animate(\"player_shrink_card\"",
    "ReferenceEquals(_cardOrigin, origin)", "ReferenceEquals(ShownPage, _cardPage)",
    "if (_leavingCard) ShowFull()", "control.Focus(FocusState.Programmatic)");
Requires("Shell/MainWindow.Files.cs", "_leavingCard = false", "_cardOrigin = null", "_cardPage = null");
Requires("Player/LayerTransition.cs", "LayerMotion.FrameAtOrigin(");
var animationStart = playerShell.IndexOf("private void Animate(", StringComparison.Ordinal);
var animationEnd = playerShell.IndexOf("private void CancelTransition(", animationStart, StringComparison.Ordinal);
var playerAnimation = playerShell[animationStart..animationEnd];
if (!playerAnimation.Contains("ResetTransform();", StringComparison.Ordinal)
    || !playerAnimation.Contains("SetLayerSize(end)", StringComparison.Ordinal)
    || !playerAnimation.Contains("SetLayerSize(moved)", StringComparison.Ordinal)
    || playerAnimation.Contains("SetLayerRect(", StringComparison.Ordinal))
    failures.Add("Player transitions must keep XAML translation at zero while the compositor positions the layer");
var cardExitStart = playerShell.IndexOf("private void ShrinkToCard(", StringComparison.Ordinal);
var cardExitEnd = playerShell.IndexOf("private void ShrinkToPreview(", cardExitStart, StringComparison.Ordinal);
var cardExit = playerShell[cardExitStart..cardExitEnd];
if (cardExit.IndexOf("Animate(", StringComparison.Ordinal) >= cardExit.LastIndexOf("_playback?.Stop()", StringComparison.Ordinal))
    failures.Add("Card playback must stop after its exit animation completes");
var downloadPlayStart = playerShell.IndexOf("public void PlayDownload(", StringComparison.Ordinal);
var downloadPlayEnd = playerShell.IndexOf("private bool ItemUnavailable(", downloadPlayStart, StringComparison.Ordinal);
var downloadPlay = playerShell[downloadPlayStart..downloadPlayEnd];
foreach (var snippet in new[] { "PlayOrigin? from = null", "_surface!.Hold(from?.Picture, Stretch.Uniform)", "OpenFull(from, App.Settings.AutoFullscreen)" })
    if (!downloadPlay.Contains(snippet, StringComparison.Ordinal))
        failures.Add($"Downloaded playback must preserve its animation origin: {snippet}");
Requires("Shell/MainWindow.RemoteControl.cs", "ItemRemovals.ForProfile(", "RemoveItems(");
// 9. The detail page offers what the player does before Play: the version drop-down and the
//    player's quality tiers with Always transcode, carried into the choice Play sends.
Requires("Pages/DetailPage.xaml", "DetailVersion", "OnVersion");
// The version drop-down keeps its slot whether or not it is shown, so late versions never re-lay out the bar.
Requires("Pages/DetailPage.xaml.cs", "VersionButton.Opacity = showVersions ? 1 : 0;");
if (Says("Pages/DetailPage.xaml.cs", "VersionButton.Visibility") || Declares("Pages/DetailPage.xaml", "VersionButton", "Collapsed"))
    failures.Add("Pages/DetailPage: the version drop-down gives up its slot; hide it with Opacity like the trailer button");
Requires("Pages/DetailPage.xaml.cs", "PlayerViewModel.QualityTierMbps", "\"DetailForceTranscode\"", "_quality, _forceTranscode)");
// 10. A Recently Added episode card shows its show's poster, in the row and in See all.
Requires("Controls/HomeRow.cs", "CardPoster = i => HomeSections.SeriesPoster(section, i);");
Requires("Pages/SectionPage.xaml.cs", "card.Poster = i => HomeSections.SeriesPoster(_section, i);");
Requires("Controls/CardRow.cs", "Poster = CardPoster,");
Requires("Controls/MediaCard.xaml.cs", "(CardKind.Poster, { } s) when Poster?.Invoke(item) is { } poster => s.GetPrimaryImageUrl(poster.Id, poster.Tag, width),");
// 11. The info panel offers every track in a drop-down per type, which holds its height before the
//     streams answer and opens its menu below itself, never under the pointer (a ComboBox there shows
//     WinUI's busy cursor); More like this and Cast and crew sit in panels under the preview and the info
//     panel, as wide as each; people are circles.
Requires("Pages/DetailPage.xaml", "x:Name=\"VideoTracks\"", "x:Name=\"AudioTracks\"", "x:Name=\"SubtitleTracks\"", "x:Name=\"SimilarPanel\"", "x:Name=\"CastPanel\"", "x:Name=\"RowsSideColumn\"");
Requires("Pages/DetailPage.xaml.cs", "DetailModel.Tracks(", "RowsSideColumn.Width = new GridLength(frame.SideWidth)", "new Rect(0, -MenuShadowAbove, anchor.ActualWidth, anchor.ActualHeight + MenuShadowAbove + MenuShadowBelow)", "ReleaseFocus");
var detailSource = File.ReadAllText(Path.Combine(app, "Pages", "DetailPage.xaml.cs"));
var detailMeasure = Regex.Match(detailSource, @"protected override Size MeasureOverride\(Size availableSize\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
if (!Regex.IsMatch(detailMeasure, @"base\.MeasureOverride\(availableSize\);.*ApplyLayout\(.*return base\.MeasureOverride\(availableSize\);", RegexOptions.Singleline))
    failures.Add("DetailPage must measure its control templates before reading the lower rows' desired height, then measure the resulting frame before returning");
if (Says("Pages/DetailPage.xaml", "<ComboBox"))
    failures.Add("Pages/DetailPage: a ComboBox opens its list under the pointer, where WinUI shows a busy cursor; use a drop-down button with a menu");
Requires("Controls/MediaCard.xaml.cs", "CardKind.Person => 1,", "private bool IsRound => _kind == CardKind.Person;");
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
Requires("Player/PlayerView.xaml.cs", "protected override AutomationPeer OnCreateAutomationPeer() => new PlayerViewPeer(this);", "protected override bool IsKeyboardFocusableCore() => true;");

// 10. In a SyncPlay group a click on the video waits out the double-click time and a second press
//     cancels it, so a double-click for full screen never pauses and resumes the whole group.
Requires("Player/PlayerView.xaml.cs", "if (vm.SyncPlay is not null)", "_groupClick.Start();", "if (_groupClick.IsRunning)");

// 11. The detail preview's compact bar idle-hides as the full player's does; only the mini player
//     pins it.
Requires("Player/PlayerView.xaml.cs", "if (!_barPinned) _idle.Start();", "if (_barPinned || _timeEditing ||");
Requires("Shell/MainWindow.Files.cs", "SetCompact(_miniPlayer, barPinned: _miniPlayer)");

// 9. A XamlRoot's ContentIslandEnvironment is null while its window tears down, and pointer events
//    still arrive then; it is read through a pattern, never dereferenced directly.
foreach (var path in files.Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
{
    var text = File.ReadAllText(path);
    foreach (Match m in Regex.Matches(text, @"\.ContentIslandEnvironment\s*\."))
        failures.Add($"{Rel(path)}:{text.AsSpan(0, m.Index).Count('\n') + 1} ContentIslandEnvironment dereferenced without a null check");
}

Requires("Shell/MainWindow.Player.cs", "PlayableItems.Select(queue, start,", "if (queue.Count == 0) return;");
Requires("Player/PlaybackController.cs", "PlayableItems.Select(queue, index)", "else if (!item.IsPlayable) return;");
Requires("Pages/DetailPage.xaml.cs", "ItemLists.SameShown(a, b)", "DownloadButton.Visibility = _item.IsVirtual ? Visibility.Collapsed : Visibility.Visible;");
Requires("Controls/MediaCard.xaml", "x:Name=\"UpcomingBadge\"", "x:Bind t:Icons.Clock", "IsHitTestVisible=\"False\"");
Requires("Controls/MediaCard.xaml.cs", "_item?.IsUpcomingEpisode(DateTimeOffset.Now)", "IsTabStop = !_isPlaceholder && !_upcoming;",
    "_item.PlaybackRelease is { Length: > 0 } date", "L.Text(\"Card.AirDateUnannounced\")", "L.Text(\"Detail.Upcoming\")",
    "!_upcoming && _hovered", "_showActions && !_upcoming", "if (_upcoming || _item is not { } item) return;",
    "if (_upcoming || !_showActions", "owner._upcoming ? AutomationControlType.Text", "owner._upcoming ? null : this",
    "FocusManager.TryMoveFocus(");
var cardSource = File.ReadAllText(Path.Combine(app, "Controls", "MediaCard.xaml.cs"));
if (cardSource.Contains("UpcomingBadge.HorizontalAlignment =", StringComparison.Ordinal)
    || cardSource.Contains("UpcomingBadge.Margin =", StringComparison.Ordinal))
    failures.Add("MediaCard: upcoming badge must inherit Grid RTL mirroring without manually swapping its alignment or margin");
var cardTree = XDocument.Load(Path.Combine(app, "Controls", "MediaCard.xaml"));
var upcomingBadge = cardTree.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "UpcomingBadge"));
if (upcomingBadge.Attribute("HorizontalAlignment")?.Value != "Left" || upcomingBadge.Attribute("Margin")?.Value != "6,6,0,0")
    failures.Add("MediaCard: upcoming badge must keep its leading-corner alignment and margin for automatic RTL mirroring");

Requires("Controls/CardRow.cs", "new ScrollView", "ScrollingContentOrientation.Horizontal",
    "ScrollAnimationStarting +=", "animation.Duration = TimeSpan.FromMilliseconds(180)",
    "ScrollingAnimationMode.Auto", "ScrollingAnimationMode.Disabled", "IsTabStop = false",
    "_paging.Destination(", "_paging.Track(correlationId, target)", "_paging.Complete(args.CorrelationId)",
    "_scroller.ExtentChanged +=", "_scroller.SizeChanged +=", "_scroller.BringingIntoView +=",
    "_scroller.PreviewKeyDown +=", "ScrollingInteractionState.Interaction",
    "Unloaded +=", "RegisterPropertyChangedCallback(FlowDirectionProperty",
    "_next.Focus(previousFocus)", "_previous.Focus(nextFocus)");
if (Says("Controls/CardRow.cs", "ChangeView(") || Says("Controls/CardRow.cs", "ScrollingAnimationMode.Enabled"))
    failures.Add("CardRow paging must use bounded compositor motion and respect reduced motion");

Requires("Controls/CardRow.cs", "IgnoredInputKinds = ScrollingInputKinds.MouseWheel",
    "PointerWheelChanged += OnPointerWheelChanged", "Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)",
    "if (e.Handled || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;",
    "_paging.WheelDestination(", "wheel.MouseWheelDelta, wheel.IsHorizontalMouseWheel, FlowDirection == FlowDirection.RightToLeft",
    "if (target is not { } destination) return;", "e.Handled = true;", "ScrollTo(destination)",
    "if (target == (_paging.Target ?? _scroller.HorizontalOffset)) return;");
if (Says("Controls/CardRow.cs", "AddHandler(PointerWheelChangedEvent")
    || Says("Controls/CardRow.cs", "_scroller.PointerWheelChanged +="))
    failures.Add("CardRow must route unhandled wheel input across the whole section, including its header");

Requires("Pages/HomePage.cs", "Children = { rows, _customize }", "ApplyVisibility();",
    "HomeSections.IsRowVisible(!_hidden.Contains(row), !row.IsEmpty, ReferenceEquals(row, _downloads) || _shown is not null, _skeletonsReady)",
    "var ready = _shown is not null", "_sectionsFlyout?.Hide()");
Requires("Pages/HomePage.Sections.cs", "HomeCustomizeButton", "HomeSectionToggle", "HomeCustomizeFlyout",
    "HomeLayoutStore.Load(_profileKey)", "HomeLayoutStore.Save(_profileKey, layout)", "ApplyVisibility();",
    "ReferenceEquals(row, first) ? 40 : 0", "FlowDirection.RightToLeft", "_customize.Focus(FocusState.Programmatic)",
    "_sectionToggles.Values.FirstOrDefault()?.Focus(FocusState.Programmatic)", "Visibility: Visibility.Collapsed");
if (Says("Pages/HomePage.Sections.cs", "new TextBlock { Text = L.Text(\"Home.CustomizeSections\")"))
    failures.Add("Home section flyout must not add a heading or helper text");
Requires("Controls/HomeSectionDrag.cs", "new Thumb", "handle.DragStarted +=", "handle.DragDelta +=",
    "handle.DragCompleted +=", "e.Canceled || !canEdit()", "HomeSectionOrder.DropTarget(", "HomeSectionOrder.Move(layout, source, target, after)",
    "HomeLayoutStore.Save(profileKey, reordered)", "if (!canEdit()) return;", "VirtualKey.Up or VirtualKey.Down",
    "handle.CancelDrag()", "handle.Unloaded +=", "_dragged = null", "ClearIndicators();");
Requires("Controls/HomeSectionDrag.cs", "parent.TransformToVisual(null)", "UIElement.PointerPressedEvent",
    "UIElement.PointerReleasedEvent", "_releasePending || _pointerId != e.Pointer.PointerId",
    "generation == _generation", "_position = e.GetCurrentPoint(null).Position", "handle.PointerCanceled +=");
Requires("Pages/HomePage.Sections.cs", "new PathIcon", "LwHomeSectionsIcon");
if (Says("Controls/HomeSectionDrag.cs", "Theme.Icons.Drag") || Says("Pages/HomePage.Sections.cs", "Ui.Icon(Theme.Icons.Tune"))
    failures.Add("Home customization must retain its approved sliders icon and use a six-dot reorder grip");
var homeStyles = XDocument.Load(Path.Combine(app, "Theme/Styles.xaml"));
var gripStyle = homeStyles.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "LwHomeSectionGrip"));
if (gripStyle?.Descendants().Count(e => e.Name.LocalName == "Ellipse") != 6)
    failures.Add("Home section reorder grip must contain six dots");
Requires("Pages/HomePage.cs", "if (visible && !ReferenceEquals(row, _downloads) && _shown is null");
Requires("Pages/HomePage.Sections.cs", "new HomeSectionDrag(", "HomeSectionDrag\"", "drag.Attach(");
Requires("Pages/SettingsPage.cs", "new HomeSectionDrag(", "SettingsHomeDrag\"", "drag.Attach(");

Requires("Controls/MediaCard.xaml.cs", "UserDataOverrides.BadgeCount(_played,", "var accepted = await change;", "revision == _playedRevision", "ReferenceEquals(_item, item)", "ReferenceEquals(Session, session)", "Item = fresh;");
Requires("Pages/DetailPage.xaml.cs", "if (_changingWatched) return;", "await LoadAsync();", "_overrides.Reconcile(readAt, children.Where(child => child.Id != _item.Id));",
    "var headerReadAt = _overrides.Mark();", "_overrides.Reconcile(headerReadAt, [_item]);", "(_played, _favorite) = _overrides.Of(_item);",
    "ChangeHeaderAsync(UserDataField.Favorite", "ChangeHeaderAsync(UserDataField.Played");

if (File.Exists(Path.Combine(root, "src", "LightWeaver", "LightWeaver.csproj")))
    failures.Add("The retired WPF project must not return");
foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
    .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
{
    var document = XDocument.Load(project);
    if (document.Descendants("UseWPF").Any(element => element.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
        || document.Descendants("ProjectReference").Any(element =>
            element.Attribute("Include")?.Value.Replace('\\', '/').EndsWith("/LightWeaver/LightWeaver.csproj", StringComparison.Ordinal) == true))
        failures.Add($"{Rel(project)} depends on the retired WPF app");
}
Requires("Controls/CardRow.cs", "_scroller.ScrollPresenter is { } presenter", "AutomationProperties.SetAutomationId(presenter, prefix + \"Items\" + key)");

foreach (var f in failures) Console.WriteLine("FAIL " + f);
Console.WriteLine($"Fast UI check: {files.Count} files, {flyouts} flyouts, {failures.Count} failures.");
return failures.Count == 0 ? 0 : 1;
