using LightWeaver.Localization;
using LightWeaver.Navigation;
using LightWeaver.Search;
using LightWeaver.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace LightWeaver.WinUI.Shell;

/// <summary>Which top-bar item shows as selected.</summary>
public enum TopBarDestination { None, Home, Libraries, Playlists, Downloads, SyncPlay, Settings }

/// <summary>Which list the Home | Libraries | Playlists group morphs into.</summary>
public enum MorphKind { Libraries, Playlists }

/// <summary>One item of the morphed group: a library, a playlist, or All playlists.</summary>
public sealed record MorphItem(string Key, string Label, string Glyph);

public sealed partial class TopBar : UserControl
{
    /// <summary>The design's one animation over 200 ms: the group's width (240 ms, ease-out).</summary>
    private static readonly TimeSpan MorphDuration = NavMorphMotion.Duration;
    private const double ItemWidth = 64;
    private const double GroupChrome = 6;
    private const double SearchMinimum = 160;
    private bool _advancedOpen;
    private TopBarDestination _selected = TopBarDestination.Home;
    private MorphKind? _morph;
    private IReadOnlyList<string> _recent = [];
    private int _recentIndex = -1;
    private NavMorphTransition? _morphAnimation;
    private IDisposable? _morphHold;
    private Storyboard? _slideAnimation;
    private Services.FrameLog.Recording? _morphFrames;
    private Services.FrameLog.Recording? _slideFrames;
    private BarItem? _slideSlot;
    private double _closedWidth = 3 * ItemWidth + GroupChrome;
    private MorphItem? _library;
    private MorphItem? _playlist;

    public TopBar()
    {
        InitializeComponent();
        ApplyChoice();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(ApplyChoice));
        foreach (var e in new FrameworkElement[] { HistoryGroup, NavGroup, SearchField, RightGroup, ProfileButton })
            e.SizeChanged += (_, _) => RegionsChanged?.Invoke(this, EventArgs.Empty);
        SizeChanged += (_, _) => { RegionsChanged?.Invoke(this, EventArgs.Empty); if (_morph is not null) ClampMorph(); };
    }

    /// <summary>An interactive group moved or resized; the window re-marks the pass-through areas.</summary>
    public event EventHandler? RegionsChanged;

    /// <summary>Text typed in the search field. Advanced search combines it with its filters.</summary>
    public string SearchText
    {
        get => SearchBox.Text;
        set { if (SearchBox.Text != value) SearchBox.Text = value; }
    }

    /// <summary>Whether the advanced-search panel is open. The tune button stays highlighted
    /// (accentSoft background, accentFg glyph) while it is.</summary>
    public bool IsAdvancedOpen
    {
        get => _advancedOpen;
        set
        {
            _advancedOpen = value;
            TuneButton.Background = value ? (Brush)Application.Current.Resources["LwAccentSoftBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TuneButton.Foreground = (Brush)Application.Current.Resources[value ? "LwAccentFgBrush" : "LwText2Brush"];
        }
    }

    public bool CanGoBack { get => BackButton.IsEnabled; set => BackButton.IsEnabled = value; }
    public bool CanGoForward { get => ForwardButton.IsEnabled; set => ForwardButton.IsEnabled = value; }

    /// <summary>The signed-in user, shown as the avatar's initials and colour.</summary>
    public void SetUser(string? name)
    {
        AvatarText.Text = Avatar.Initials(name);
        ProfileButton.Background = Avatar.BrushFor(name);
    }

    /// <summary>Login's minimal chrome (design 12): no bar groups, only the caption buttons.</summary>
    public bool IsMinimal
    {
        get => HistoryGroup.Visibility == Visibility.Collapsed;
        set
        {
            var visibility = value ? Visibility.Collapsed : Visibility.Visible;
            foreach (var e in new FrameworkElement[] { HistoryGroup, NavGroup, SearchField, RightGroup, ProfileButton }) e.Visibility = visibility;
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Opens a menu under the avatar; the avatar shows a focus ring while it is open.</summary>
    public void ShowProfileMenu(FlyoutBase menu)
    {
        menu.Opened += (_, _) => ProfileButton.BorderThickness = new Thickness(2);
        menu.Closed += (_, _) => ProfileButton.BorderThickness = new Thickness(0);
        menu.ShowAt(ProfileButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    public TopBarDestination SelectedDestination
    {
        get => _selected;
        set
        {
            _selected = value;
            NavHome.IsSelected = value == TopBarDestination.Home;
            NavLibraries.IsSelected = value == TopBarDestination.Libraries;
            NavPlaylists.IsSelected = value == TopBarDestination.Playlists;
            NavDownloads.IsSelected = value == TopBarDestination.Downloads;
            SyncPlayButton.IsSelected = value == TopBarDestination.SyncPlay;
            NavSettings.IsSelected = value == TopBarDestination.Settings;
        }
    }

    /// <summary>The library or playlist open now takes the place of Libraries or Playlists, with its
    /// name and icon (design 01); null restores the item.</summary>
    public void ShowChoice(MorphItem? library, MorphItem? playlist)
    {
        _library = library;
        _playlist = playlist;
        ApplyChoice();
    }

    private void ApplyChoice()
    {
        ApplyChoice(NavLibraries, _library, Theme.Icons.VideoLibrary, L.Text("TopBar.Libraries"));
        ApplyChoice(NavPlaylists, _playlist, Theme.Icons.PlaylistPlay, L.Text("TopBar.Playlists"));
    }

    private static void ApplyChoice(BarItem slot, MorphItem? choice, string glyph, string label)
    {
        slot.Icon = choice?.Glyph ?? glyph;
        slot.Label = choice?.Label ?? label;
        AutomationProperties.SetName(slot, choice?.Label ?? label);
        AutomationProperties.SetHelpText(slot, choice is null ? "" : label);
        ToolTipService.SetToolTip(slot, choice?.Label);
    }

    /// <summary>Closes the bar's own open layer (the nav morph). False when none is open.</summary>
    public bool HandleEscape()
    {
        if (RecentPopup.IsOpen)
        {
            RecentPopup.IsOpen = false;
            return true;
        }
        if (_morph is null) return false;
        CloseMorph(returnFocus: true);
        return true;
    }

    public bool IsMorphOpen => _morph is not null;

    /// <summary>Libraries or Playlists was clicked; the window answers with <see cref="OpenMorph"/>.</summary>
    public event EventHandler<MorphKind>? MorphRequested;

    /// <summary>An item of the morphed group was chosen (its key).</summary>
    public event EventHandler<string>? MorphItemInvoked;

    /// <summary>Whether a window point (in DIPs) is inside the nav group, for click-outside.</summary>
    public bool IsInNavGroup(Point windowPoint)
    {
        var bounds = NavGroup.TransformToVisual(null).TransformBounds(new Rect(0, 0, NavGroup.ActualWidth, NavGroup.ActualHeight));
        return bounds.Contains(windowPoint);
    }

    /// <summary>Widens the group to fit the items (design 01, 1b to 1e): the items swap at once and
    /// the growing clip reveals them. It grows toward the search field until search is at its
    /// 200 px minimum, then scrolls inside its own bounds with a chevron on a clipped edge.</summary>
    public void OpenMorph(MorphKind kind, IReadOnlyList<MorphItem> items, string? currentKey)
    {
        _morph = kind;
        EndSlide();
        MorphItems.Children.Clear();
        BarItem? current = null;
        foreach (var item in items)
        {
            var bar = new BarItem { Icon = item.Glyph, Label = item.Label, IsSelected = item.Key == currentKey };
            AutomationProperties.SetAutomationId(bar, "NavMorphItem");
            AutomationProperties.SetName(bar, item.Label);
            ToolTipService.SetToolTip(bar, item.Label);
            bar.Click += (_, _) =>
            {
                var focus = bar.FocusState;
                var from = bar.TransformToVisual(Morph).TransformPoint(default).X;
                CloseMorph(returnFocus: false);
                MorphItemInvoked?.Invoke(this, item.Key);
                var slot = kind == MorphKind.Libraries ? NavLibraries : NavPlaylists;
                SlideToSlot(slot, from - NavItems.Children.TakeWhile(c => c != slot).Sum(c => c.DesiredSize.Width));
                if (focus != FocusState.Unfocused) slot.Focus(focus);
            };
            if (item.Key == currentKey) current = bar;
            MorphItems.Children.Add(bar);
        }
        if (NavItems.Visibility == Visibility.Visible) _closedWidth = CollapsedWidth();
        NavItems.Visibility = Visibility.Collapsed;
        Morph.Visibility = Visibility.Visible;
        MorphScroller.ChangeView(0, null, null, disableAnimation: true);
        AnimateGroupWidth(MorphWidth(), "nav_morph_open");
        var focus = current ?? MorphItems.Children.OfType<BarItem>().FirstOrDefault();
        DispatcherQueue.TryEnqueue(() =>
        {
            focus?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            focus?.Focus(FocusState.Programmatic);
            UpdateMorphEdges();
        });
    }

    /// <summary>Contracts back to Home | Libraries | Playlists.</summary>
    public void CloseMorph(bool returnFocus)
    {
        if (_morph is not { } kind) return;
        _morph = null;
        Morph.Visibility = Visibility.Collapsed;
        NavItems.Visibility = Visibility.Visible;
        AnimateGroupWidth(CollapsedWidth(), "nav_morph_close");
        if (returnFocus) (kind == MorphKind.Libraries ? NavLibraries : NavPlaylists).Focus(FocusState.Keyboard);
    }

    /// <summary>Home | Libraries | Playlists at their labels' widths.</summary>
    private double CollapsedWidth()
    {
        NavItems.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return NavItems.DesiredSize.Width + GroupChrome;
    }

    /// <summary>The open group's width, never narrower than the group was closed, as measured before
    /// the morph hid its items.</summary>
    private double MorphWidth()
    {
        var room = NavGroup.ActualWidth + Math.Max(0, SearchField.ActualWidth - SearchMinimum);
        return NavMorphWidth.Open(MorphItems.Children.Count, ItemWidth, GroupChrome, room, _closedWidth);
    }

    private void ClampMorph()
    {
        if (_morphAnimation is not null) return;
        NavGroup.Width = MorphWidth();
        UpdateMorphEdges();
    }

    /// <summary>Changes the group's width over 240 ms on the compositor (<see cref="NavMorphTransition"/>):
    /// one layout at the wider width, then no layout until it lands. A morph already under way
    /// continues from the width on screen. Opening holds the artwork queue; closing does not, since
    /// it comes with the page switch of a chosen item, whose artwork should land at once.</summary>
    private void AnimateGroupWidth(double target, string name)
    {
        var from = _morphAnimation?.Width ?? (NavGroup.ActualWidth > 0 ? NavGroup.ActualWidth : NavGroup.Width);
        var laidOut = NavGroup.ActualWidth;
        var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(SearchField);
        EndMorphAnimation();
        if (laidOut <= 0 || slot.Width <= 0 || Math.Abs(from - target) < 0.5)
        {
            NavGroup.Width = _morph is null ? double.NaN : target;
            UpdateMorphEdges();
            return;
        }
        var parts = new NavMorphParts(NavGroup, NavPill, NavContent, SearchField, SearchChrome, SearchChromeHost, SearchBottom,
            [SearchPlaceholder, SearchBox], [ClearButton, TuneButton, SearchButton]);
        var morph = new NavMorphTransition(parts, from, target, new BarSpan(slot.X, slot.Width), laidOut);
        morph.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_morphAnimation, morph)) return;
            EndMorphAnimation();
            NavGroup.Width = _morph is null ? double.NaN : target;
            if (_morph is not null) ClampMorph();
            UpdateMorphEdges();
        };
        NavGroup.Width = morph.LayoutWidth;
        _morphAnimation = morph;
        if (_morph is not null) _morphHold = Services.ImageQueue.Hold();
        _morphFrames = Services.FrameLog.Start(name);
        LayoutUpdated += OnMorphLayout;
        morph.Start();
    }

    /// <summary>A resize or a moved caption inset mid-morph moves the search field: the morph
    /// continues from where XAML put it.</summary>
    private void OnMorphLayout(object? sender, object e)
    {
        if (_morphAnimation is not { } morph) return;
        var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(SearchField);
        if (slot.Width > 0 && NavGroup.ActualWidth > 0) morph.Retarget(new BarSpan(slot.X, slot.Width), NavGroup.ActualWidth);
    }

    private void EndMorphAnimation()
    {
        LayoutUpdated -= OnMorphLayout;
        _morphAnimation?.Stop();
        _morphAnimation = null;
        _morphHold?.Dispose();
        _morphHold = null;
        _morphFrames?.Dispose();
        _morphFrames = null;
    }

    /// <summary>The chosen item travels from its place in the list to its slot while the group
    /// contracts, with the group's own 240 ms ease-out.</summary>
    private void SlideToSlot(BarItem slot, double offset)
    {
        EndSlide();
        if (Math.Abs(offset) < 1) return;
        var shift = new TranslateTransform { X = offset };
        slot.RenderTransform = shift;
        Canvas.SetZIndex(slot, 1);
        var animation = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = new Duration(MorphDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, shift);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard { Children = { animation } };
        storyboard.Completed += (_, _) => { if (ReferenceEquals(_slideAnimation, storyboard)) EndSlide(); };
        _slideAnimation = storyboard;
        _slideSlot = slot;
        _slideFrames = Services.FrameLog.Start("nav_slide");
        storyboard.Begin();
    }

    private void EndSlide()
    {
        _slideAnimation?.Stop();
        _slideAnimation = null;
        _slideFrames?.Dispose();
        _slideFrames = null;
        if (_slideSlot is { } slot)
        {
            slot.RenderTransform = null;
            Canvas.SetZIndex(slot, 0);
        }
        _slideSlot = null;
    }

    private void OnMorphScrolled(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateMorphEdges();

    /// <summary>A fade and a chevron only on a clipped edge.</summary>
    private void UpdateMorphEdges()
    {
        var left = _morph is not null && MorphScroller.HorizontalOffset > 1;
        var right = _morph is not null && MorphScroller.HorizontalOffset < MorphScroller.ScrollableWidth - 1;
        MorphLeft.Visibility = MorphFadeLeft.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        MorphRight.Visibility = MorphFadeRight.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The chevrons page by three items.</summary>
    private void OnMorphLeft(object sender, RoutedEventArgs e) => MorphScroller.ChangeView(Math.Max(0, MorphScroller.HorizontalOffset - 3 * ItemWidth), null, null, disableAnimation: true);
    private void OnMorphRight(object sender, RoutedEventArgs e) => MorphScroller.ChangeView(Math.Min(MorphScroller.ScrollableWidth, MorphScroller.HorizontalOffset + 3 * ItemWidth), null, null, disableAnimation: true);

    /// <summary>Opens the profile menu (also reached from the offline view's Switch server).</summary>
    public void OpenProfileMenu() => ProfileRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? AdvancedToggled;
    public event EventHandler<string>? SearchTextChanged;
    public event EventHandler<string>? SearchSubmitted;
    public event EventHandler? HomeRequested;
    public event EventHandler? DownloadsRequested;

    /// <summary>The SyncPlay button; the window opens its flyout under it.</summary>
    public event EventHandler<FrameworkElement>? SyncPlayRequested;

    /// <summary>The member count while in a group; empty hides the badge.</summary>
    public void SetSyncPlayBadge(string count) => SyncPlayButton.Badge = count;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ProfileRequested;

    /// <summary>Screen rectangles (in physical pixels of this window) of everything clickable,
    /// so the window can mark them as pass-through in its custom title bar.</summary>
    public IEnumerable<Rect> InteractiveRects()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        foreach (FrameworkElement e in new FrameworkElement[] { HistoryGroup, NavGroup, SearchField, RightGroup, ProfileButton })
        {
            if (e.ActualWidth <= 0 || e.Visibility == Visibility.Collapsed) continue;
            var r = e.TransformToVisual(null).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            yield return new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
        }
    }

    /// <summary>Width the system caption buttons take, so the bar ends where they begin. They stay
    /// on the physical right in a right-to-left layout, where the bar's leading edge is.</summary>
    public double CaptionInset
    {
        get => _captionInset;
        set
        {
            _captionInset = Math.Max(0, value);
            var rtl = FlowDirection == FlowDirection.RightToLeft;
            CaptionColumn.Width = new GridLength(rtl ? 0 : _captionInset);
            Root.Padding = new Thickness(rtl ? _captionInset + 8 : 8, 0, 0, 0);
        }
    }

    private double _captionInset = 138;

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    /// <summary>Running and queued downloads; empty hides the badge.</summary>
    public void SetDownloadsBadge(string count) => NavDownloads.Badge = count;

    public bool IsSearchFocused => SearchBox.FocusState != FocusState.Unfocused;

    /// <summary>The second Esc: the field gives up the keyboard.</summary>
    public void BlurSearch() => Focus(FocusState.Programmatic);

    /// <summary>The profile whose recent searches the field lists.</summary>
    public Func<string>? RecentProfile { get; set; }

    public void FocusTune() => TuneButton.Focus(FocusState.Programmatic);

    private void OnBack(object sender, RoutedEventArgs e) { CloseMorph(returnFocus: false); BackRequested?.Invoke(this, EventArgs.Empty); }
    private void OnForward(object sender, RoutedEventArgs e) { CloseMorph(returnFocus: false); ForwardRequested?.Invoke(this, EventArgs.Empty); }
    private void OnTune(object sender, RoutedEventArgs e) => AdvancedToggled?.Invoke(this, EventArgs.Empty);
    private void OnSearch(object sender, RoutedEventArgs e) => SearchSubmitted?.Invoke(this, SearchBox.Text);
    private void OnNavHome(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
    private void OnNavLibraries(object sender, RoutedEventArgs e) => MorphRequested?.Invoke(this, MorphKind.Libraries);
    private void OnNavPlaylists(object sender, RoutedEventArgs e) => MorphRequested?.Invoke(this, MorphKind.Playlists);
    private void OnNavDownloads(object sender, RoutedEventArgs e) => DownloadsRequested?.Invoke(this, EventArgs.Empty);
    private void OnSyncPlay(object sender, RoutedEventArgs e) => SyncPlayRequested?.Invoke(this, SyncPlayButton);
    private void OnNavSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnProfile(object sender, RoutedEventArgs e) => ProfileRequested?.Invoke(this, EventArgs.Empty);

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateRecent();
        SearchTextChanged?.Invoke(this, SearchBox.Text);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RecentPopup.IsOpen && _recent.Count > 0)
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    SelectRecent(Math.Min(_recent.Count - 1, _recentIndex + 1));
                    e.Handled = true;
                    return;
                case VirtualKey.Up:
                    SelectRecent(Math.Max(0, _recentIndex - 1));
                    e.Handled = true;
                    return;
                case VirtualKey.Delete when _recentIndex >= 0:
                    RemoveRecent(_recent[_recentIndex]);
                    e.Handled = true;
                    return;
                case VirtualKey.Enter when _recentIndex >= 0:
                    ChooseRecent(_recent[_recentIndex]);
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key == VirtualKey.Enter)
        {
            if (SearchBox.Text.Trim().Length > 0) RecentSearches.Add(RecentProfile?.Invoke() ?? "", SearchBox.Text);
            SearchSubmitted?.Invoke(this, SearchBox.Text);
            e.Handled = true;
        }
    }

    /// <summary>Shows the recent searches while the field is focused and empty.</summary>
    private void UpdateRecent()
    {
        var show = IsSearchFocused && SearchBox.Text.Length == 0 && RecentProfile is not null && !IsMinimal;
        _recent = show ? RecentSearches.Load(RecentProfile!()) : [];
        if (_recent.Count == 0)
        {
            RecentPopup.IsOpen = false;
            return;
        }
        RecentHeading.Text = L.Text("Search.Recent");
        RecentClearAll.Content = L.Text("Search.ClearAll");
        RecentHint.Text = L.Text("Search.RecentHint");
        RecentPanel.Width = Math.Max(380, SearchField.ActualWidth);
        RecentList.Children.Clear();
        _recentIndex = -1;
        foreach (var term in _recent) RecentList.Children.Add(RecentRow(term));
        RecentPopup.IsOpen = true;
    }

    private Grid RecentRow(string term)
    {
        var row = new Grid { Height = 36, CornerRadius = new CornerRadius(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = Theme.Icons.Clock, FontSize = 16, Foreground = Ui.Resource<Brush>("LwText2Brush") });
        content.Children.Add(new TextBlock { Text = term, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var choose = new Button
        {
            Style = Ui.Resource<Style>("LwSubtleButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(10, 0, 4, 0),
            Content = content,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(choose, "SearchRecent");
        AutomationProperties.SetName(choose, term);
        choose.Click += (_, _) => ChooseRecent(term);
        var remove = new Button
        {
            Style = Ui.Resource<Style>("LwSubtleButton"),
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0,
            Foreground = Ui.Resource<Brush>("LwText2Brush"),
            Content = new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = Theme.Icons.Close, FontSize = 14 },
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(remove, "SearchRecentRemove");
        AutomationProperties.SetName(remove, L.Format("Search.RemoveRecent", term));
        ToolTipService.SetToolTip(remove, L.Format("Search.RemoveRecent", term));
        remove.Click += (_, _) => RemoveRecent(term);
        Grid.SetColumn(remove, 1);
        row.Children.Add(choose);
        row.Children.Add(remove);
        row.PointerEntered += (_, _) => remove.Opacity = 1;
        row.PointerExited += (_, _) => remove.Opacity = RecentList.Children.IndexOf(row) == _recentIndex ? 1 : 0;
        return row;
    }

    private void SelectRecent(int index)
    {
        _recentIndex = index;
        for (var i = 0; i < RecentList.Children.Count; i++)
        {
            var row = (Grid)RecentList.Children[i];
            row.Background = i == index ? Ui.Resource<Brush>("LwHoverBrush") : null;
            row.Children[1].Opacity = i == index ? 1 : 0;
        }
    }

    private void ChooseRecent(string term)
    {
        RecentPopup.IsOpen = false;
        RecentSearches.Add(RecentProfile?.Invoke() ?? "", term);
        SearchBox.Text = term;
        SearchBox.SelectionStart = term.Length;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void RemoveRecent(string term)
    {
        RecentSearches.Remove(RecentProfile?.Invoke() ?? "", term);
        var index = _recentIndex;
        UpdateRecent();
        if (_recent.Count > 0 && index >= 0) SelectRecent(Math.Min(index, _recent.Count - 1));
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        RecentSearches.Clear(RecentProfile?.Invoke() ?? "");
        RecentPopup.IsOpen = false;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private bool IsInRecent(DependencyObject? node)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, RecentPanel)) return true;
        return false;
    }

    // Focused: darker field and a 2 px accent bottom line (design: sBg #1c1c1c, sBottom accentFg).
    private void OnSearchFocus(object sender, RoutedEventArgs e)
    {
        var focused = SearchBox.FocusState != FocusState.Unfocused;
        var res = Application.Current.Resources;
        var fill = (Brush)res[focused ? "LwFieldFocusBrush" : "LwFieldBrush"];
        if (_morphAnimation is { } morph) morph.SetSearchFill(fill);
        else SearchChrome.Background = fill;
        SearchBottom.Background = (Brush)res[focused ? "LwAccentFgBrush" : "LwFieldBottomBrush"];
        SearchBottom.Height = focused ? 2 : 1;
        if (focused) UpdateRecent();
        else if (!IsInRecent(XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null)) RecentPopup.IsOpen = false;
    }
}
