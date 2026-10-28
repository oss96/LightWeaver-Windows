using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Controls;

/// <summary>The artwork shape of a card (design Card.dc.html <c>kind</c>).</summary>
public enum CardKind { Poster, Wide, Person, Square }

public sealed partial class MediaCard : UserControl
{
    private MediaItem? _item;
    private CardKind _kind;
    private bool _played;
    private bool _favorite;
    private bool _hovered;
    private bool _focused;
    private bool _menuOpen;
    private bool _showActions = true;
    private int _imageRequest;
    private int _decodedWidth;
    private bool _loadWhenVisible;
    private bool _inView;
    private bool _picturePending;
    private CancellationTokenSource? _pictureLoad;
    private readonly double _titleSize;
    private readonly double _subtitleSize;
    private readonly double _captionGap;

    public MediaCard()
    {
        InitializeComponent();
        (_titleSize, _subtitleSize, _captionGap) = (TitleText.FontSize, SubText.FontSize, Body.Spacing);
        AutomationProperties.SetAutomationId(this, "MediaCard");
        Art.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            if (_picturePending)
            {
                LoadPendingPicture();
                return;
            }
            // Grids stretch cards after the first layout; decode again once the card is noticeably
            // wider than the image was decoded for.
            var shown = e.NewSize.Width * (XamlRoot?.RasterizationScale ?? 1);
            if (FollowsCardScale ? CardScale.NeedsSharperArt(_decodedWidth, shown) : _decodedWidth > 0 && shown > _decodedWidth + 16) _ = LoadPictureAsync();
        };
        Loaded += (_, _) =>
        {
            LoadPendingPicture();
            WatchDownloads(true);
            ShowOfflineBadge();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            SuspendPicture();
            WatchDownloads(false);
        };
        GotFocus += (_, _) => { _focused = true; UpdateState(); };
        LostFocus += (_, _) =>
        {
            _focused = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject f && IsInside(f);
            UpdateState();
        };
        Tapped += OnTapped;
        KeyDown += OnKeyDown;
        ContextRequested += OnContextRequested;
        var info = new KeyboardAccelerator { Key = VirtualKey.I, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = this };
        info.Invoked += (_, e) => { if (_item is { } item) Shell?.ShowInfo(item); e.Handled = true; };
        KeyboardAccelerators.Add(info);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>A card with no item that holds a row's place at a real card's size: placeholder
    /// artwork and blank captions one line each, not focusable, clickable or seen by UI Automation.</summary>
    internal static MediaCard Placeholder(CardKind kind)
    {
        var card = new MediaCard { Kind = kind, IsHitTestVisible = false, IsTabStop = false, _isPlaceholder = true };
        AutomationProperties.SetAutomationId(card, "MediaCardPlaceholder");
        AutomationProperties.SetAccessibilityView(card, AccessibilityView.Raw);
        card.Refresh();
        return card;
    }

    private bool _isPlaceholder;

    /// <summary>A blank caption line that still measures one line tall.</summary>
    private const string BlankLine = " ";

    /// <summary>The window's navigation, for opening the item and the item menu's actions.</summary>
    internal IAppShell? Shell { get; set; }

    /// <summary>The session that loads artwork and changes watched and favourite state.</summary>
    internal JellyfinService? Session { get; set; }

    /// <summary>The title line; the item's name unless the page says otherwise (a show for an episode).</summary>
    internal Func<MediaItem, string>? Title { get; set; }

    /// <summary>Second line under the title; the page decides what it says.</summary>
    internal Func<MediaItem, string>? Subtitle { get; set; }

    /// <summary>A count shown in the corner badge (Next Up: episodes left after this one).</summary>
    internal Func<MediaItem, int?>? Count { get; set; }

    /// <summary>Shared watched/favourite state for the page's cards.</summary>
    internal UserDataOverrides? Overrides { get; set; }

    /// <summary>Whether the item holds nothing (an empty playlist): its card shows no artwork and no
    /// watched check, whatever the server still reports for it.</summary>
    internal Func<MediaItem, bool>? IsEmpty { get; set; }

    /// <summary>Extra entries for the item menu (Continue Watching's Remove).</summary>
    internal ItemMenuOptions? MenuOptions { get; set; }

    /// <summary>Raised when the card is clicked or invoked; without a handler it opens the item.</summary>
    internal event EventHandler<MediaItem>? Invoked;

    /// <summary>Whether the title and subtitle show under the artwork (library display options).</summary>
    internal bool ShowCaption
    {
        get => Caption.Visibility == Visibility.Visible;
        set => Caption.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the card has the watched check and the item menu; people have neither.</summary>
    internal bool ShowActions
    {
        get => _showActions;
        set { _showActions = value; UpdateState(); }
    }

    /// <summary>Whether a row sizes the card by the window's <see cref="CardScale"/>; its artwork then
    /// loads again only once the card is clearly wider than it was decoded for.</summary>
    internal bool FollowsCardScale { get; set; }

    /// <summary>Sizes the caption and its gap to the artwork for <paramref name="scale"/>.</summary>
    internal void ApplyScale(CardScale scale)
    {
        TitleText.FontSize = scale.Font(_titleSize);
        SubText.FontSize = scale.Font(_subtitleSize);
        Body.Spacing = scale.Font(_captionGap);
    }

    /// <summary>Marks the item on show in a list (the episode of an episode page).</summary>
    internal bool IsCurrent
    {
        get => CurrentRing.Visibility == Visibility.Visible;
        set => CurrentRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Loads the artwork only once the card is on screen or close to it (search results,
    /// where most cards of a superseded query are never seen).</summary>
    internal bool LoadsPictureWhenVisible
    {
        get => _loadWhenVisible;
        set
        {
            if (_loadWhenVisible == value) return;
            _loadWhenVisible = value;
            if (value) EffectiveViewportChanged += OnViewportChanged;
            else EffectiveViewportChanged -= OnViewportChanged;
        }
    }

    internal CardKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Art.Ratio = AspectRatio(value);
        }
    }

    /// <summary>The item shown. A newer copy of the same item (a refresh) keeps the artwork on
    /// screen when its address is unchanged; anything else loads it again, once the card is in the
    /// tree and laid out, so the display scale and width are known (a guess fetches it twice).</summary>
    internal MediaItem? Item
    {
        get => _item;
        set
        {
            var keepPicture = value is not null && _item is not null && value.Id == _item.Id && Picture.Source is not null
                && PictureUrl(value, 0) == PictureUrl(_item, 0);
            _item = value;
            (_played, _favorite) = value is null ? (false, false) : UserState(value);
            if (!keepPicture)
            {
                CancelPicture();
                Picture.Source = null;
                _decodedWidth = 0;
            }
            UpdateOfflineBadge();
            Refresh();
            if (keepPicture) return;
            _picturePending = value is not null;
            LoadPendingPicture();
        }
    }

    /// <summary>Loads a pending picture once the card is in the tree with a width and, for cards
    /// that wait for the viewport, near it.</summary>
    private void LoadPendingPicture()
    {
        if (!_picturePending || XamlRoot is null || _loadWhenVisible && !_inView || !HasWidth) return;
        _picturePending = false;
        _ = LoadPictureAsync();
    }

    private bool HasWidth => Art.ActualWidth > 0 || !double.IsNaN(Width);

    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs e)
    {
        const double Ahead = 200;
        var view = e.EffectiveViewport;
        _inView = view.Width > 0 && view.Height > 0
            && view.X < ActualWidth + Ahead && view.Right > -Ahead
            && view.Y < ActualHeight + Ahead && view.Bottom > -Ahead;
        LoadPendingPicture();
    }

    /// <summary>Gives the cards a repeater has realized the newer copies of the items they show
    /// (same items, same order), so a refresh neither resets the repeater nor reloads artwork.</summary>
    internal static void RefreshRealized(ItemsRepeater repeater, IReadOnlyList<MediaItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            if (repeater.TryGetElement(i) is MediaCard card) card.Item = items[i];
    }

    /// <summary>Re-reads the language-dependent text (subtitle, tooltips).</summary>
    /// <summary>Follows downloads while on screen, so the offline badge leaves when the download
    /// is deleted and appears when one finishes.</summary>
    private void WatchDownloads(bool watch)
    {
        if (Downloads.DownloadManager.Instance is not { } downloads) return;
        downloads.AvailabilityChanged -= OnAvailabilityChanged;
        if (watch) downloads.AvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged() => DispatcherQueue.TryEnqueue(ShowOfflineBadge);

    private void ShowOfflineBadge()
    {
        if (UpdateOfflineBadge()) Refresh();
    }

    /// <summary>True when the badge changed.</summary>
    private bool UpdateOfflineBadge()
    {
        var shown = _item is { } item && Downloads.DownloadManager.Instance?.IsDownloaded(item.Id) == true ? Visibility.Visible : Visibility.Collapsed;
        if (OfflineBadge.Visibility == shown) return false;
        OfflineBadge.Visibility = shown;
        return true;
    }

    internal void Refresh()
    {
        var blank = _isPlaceholder ? BlankLine : "";
        TitleText.Text = _item is null ? blank : Title?.Invoke(_item) ?? _item.Name;
        SubText.Text = _item is null ? blank : Subtitle?.Invoke(_item) ?? "";
        var name = SubText.Text.Length > 0 ? $"{TitleText.Text}, {SubText.Text}" : TitleText.Text;
        AutomationProperties.SetName(this, OfflineBadge.Visibility == Visibility.Visible ? $"{name}, {L.Text("Card.AvailableOffline")}" : name);
        ToolTipService.SetToolTip(OfflineBadge, L.Text("Card.AvailableOffline"));
        UpdateState();
    }

    internal void Invoke()
    {
        if (_item is not { } item) return;
        if (Invoked is { } handler) handler(this, item);
        else Shell?.OpenItem(item);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MediaCardPeer(this);

    private static double AspectRatio(CardKind kind) => kind switch
    {
        CardKind.Wide => 16.0 / 9,
        CardKind.Person => 3.0 / 4,
        CardKind.Square => 1,
        _ => 2.0 / 3,
    };

    private string? PictureUrl(MediaItem item, int width) => (_kind, Session) switch
    {
        (_, null) => null,
        _ when IsEmpty?.Invoke(item) == true => null,
        (CardKind.Wide, { } s) when item.Type == BaseItemDto_Type.Episode && item.PrimaryImageTag is { Length: > 0 } tag => s.GetPrimaryImageUrl(item.Id, tag, width),
        (CardKind.Wide, { } s) when item.BackdropImageTag is { Length: > 0 } tag => s.GetBackdropImageUrl(item.Id, tag, width),
        (CardKind.Wide, _) when item.ThumbUrl is { } thumb => thumb,
        (_, { } s) when item.PrimaryImageTag is { Length: > 0 } tag => s.GetPrimaryImageUrl(item.Id, tag, width),
        (_, { } s) when item.SeriesId is { } series && item.SeriesPrimaryImageTag is { Length: > 0 } tag => s.GetPrimaryImageUrl(series, tag, width),
        _ => item.PosterUrl ?? item.ThumbUrl,
    };

    private async Task LoadPictureAsync()
    {
        var request = ++_imageRequest;
        CancelPicture();
        if (_item is not { } item || Session is null) return;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var shown = Art.ActualWidth > 0 ? Art.ActualWidth : double.IsNaN(Width) ? 0 : Width;
        var width = (int)Math.Ceiling(Math.Max(140, shown) * scale);
        if (PictureUrl(item, width) is null) return;
        _decodedWidth = width;
        var load = _pictureLoad = new CancellationTokenSource();
        var loading = ImageLoader.LoadAsync(Session, step => PictureUrl(item, step), width, load.Token);
        var fromMemory = loading.IsCompleted;
        var image = await loading;
        if (image is null || !Wanted())
        {
            if (ReferenceEquals(_pictureLoad, load)) _pictureLoad = null;
            return;
        }
        ImageQueue.Land(fromMemory, () =>
        {
            if (ReferenceEquals(_pictureLoad, load)) _pictureLoad = null;
            if (Wanted()) Picture.Source = image;
        });

        bool Wanted() => !load.IsCancellationRequested && request == _imageRequest && item.Id == _item?.Id;
    }

    /// <summary>Drops the card's unfinished load, including a picture still queued to land; one
    /// still waiting for its turn then costs nothing.</summary>
    private void CancelPicture()
    {
        _pictureLoad?.Cancel();
        _pictureLoad = null;
    }

    /// <summary>The card left the tree (its page was navigated away): drop an unfinished load and
    /// load again when it comes back.</summary>
    private void SuspendPicture()
    {
        if (_pictureLoad is null) return;
        CancelPicture();
        _picturePending = _item is not null;
    }

    /// <summary>Whether the play button starts something: a title, the next episode of a show or
    /// season, or a playlist.</summary>
    private static bool CanPlay(MediaItem item)
        => item.IsPlayable || item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.Playlist;

    // The play button and the action pill stay in the tree (Opacity, not Visibility) so keyboard
    // focus and UI Automation can reach them; they only take pointer input while shown.
    private void UpdateState()
    {
        var active = _hovered || _focused || _menuOpen;
        HoverWash.Visibility = HoverRing.Visibility = _hovered ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = _showActions ? Visibility.Visible : Visibility.Collapsed;
        Actions.Opacity = active ? 1 : 0;
        Actions.IsHitTestVisible = active;
        PlayButton.Visibility = _showActions && _item is { } playable && CanPlay(playable) ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Opacity = active ? 1 : 0;
        PlayButton.IsHitTestVisible = active;

        var count = _item is null ? null : Count?.Invoke(_item);
        CountText.Text = count?.ToString(System.Globalization.CultureInfo.CurrentUICulture) ?? "";
        CountBadge.Visibility = count is > 0 ? Visibility.Visible : Visibility.Collapsed;
        WatchedBadge.Visibility = _played && count is not > 0 ? Visibility.Visible : Visibility.Collapsed;

        var progress = _item is { PlayedPercentage: > 0 and < 100 } && !_played ? _item.PlayedPercentage!.Value / 100 : 0;
        ProgressTrack.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressDone.Width = new GridLength(progress, GridUnitType.Star);
        ProgressLeft.Width = new GridLength(1 - progress, GridUnitType.Star);
        Actions.Margin = new Thickness(0, 0, 6, progress > 0 ? 18 : 6);

        WatchedButton.Foreground = _played ? Ui.Resource<Brush>("LwAccentFgBrush") : new SolidColorBrush(Microsoft.UI.Colors.White);
        var watchedLabel = _played ? L.Text("Card.MarkUnwatched") : L.Text("Card.MarkWatched");
        ToolTipService.SetToolTip(WatchedButton, watchedLabel);
        AutomationProperties.SetName(WatchedButton, watchedLabel);
        var moreLabel = L.Text("Card.More");
        ToolTipService.SetToolTip(MoreButton, moreLabel);
        AutomationProperties.SetName(MoreButton, moreLabel);
        var playLabel = _item is { IsPlayable: true, ResumePositionTicks: > 0 } && !_played ? L.Text("Card.Resume") : L.Text("Card.Play");
        ToolTipService.SetToolTip(PlayButton, playLabel);
        AutomationProperties.SetName(PlayButton, playLabel);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) { _hovered = true; UpdateState(); }
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { _hovered = false; UpdateState(); }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && (IsInside(source, Actions) || IsInside(source, PlayButton))) return;
        e.Handled = true;
        Invoke();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space) || !ReferenceEquals(e.OriginalSource, this)) return;
        e.Handled = true;
        Invoke();
    }

    private async void OnToggleWatched(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item || Session is not { } session) return;
        var target = !_played;
        await ChangeAsync(item, UserDataField.Played, target, () => session.SetPlayedAsync(item.Id, target));
    }

    /// <summary>Shows the user's change at once and again once the server has answered (the
    /// overrides decide whether a refusal is undone).</summary>
    private async Task ChangeAsync(MediaItem item, UserDataField field, bool value, Func<Task<bool>> send)
    {
        var overrides = Overrides ??= new UserDataOverrides();
        var change = overrides.ChangeAsync(item, field, value, send);
        ShowUserData(item);
        await change;
        ShowUserData(item);
    }

    private void OnMore(object sender, RoutedEventArgs e) => ShowMenu(MoreButton, null);

    private static int s_playRequests;

    /// <summary>Plays or resumes a title; a show or season plays its next episode, a playlist its items.
    /// A lookup that a later card's Play, a play, a stop or a session switch overtakes plays nothing.</summary>
    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item || Session is not { } session || Shell is not { } shell) return;
        var request = ++s_playRequests;
        var origin = Origin;
        if (item.IsPlayable)
        {
            shell.Play(item, item.ResumePositionTicks > 0 && !_played ? TimeSpan.FromTicks(item.ResumePositionTicks) : TimeSpan.Zero, origin);
            return;
        }
        var intent = shell.PlaybackIntent;
        var active = shell.Model.Jellyfin;
        bool Current() => request == s_playRequests && shell.PlaybackIntent == intent && ReferenceEquals(shell.Model.Jellyfin, active);
        try
        {
            switch (item.Type)
            {
                case BaseItemDto_Type.Playlist:
                    if (await session.GetPlaylistItemsAsync(item.Id) is { Count: > 0 } queue && Current()) shell.PlayQueue(queue, 0, origin);
                    break;
                case BaseItemDto_Type.Series:
                    if (DetailModel.NextEpisode(await session.GetSeriesEpisodesAsync(item.Id)) is { } next && Current()) shell.Play(next, from: origin);
                    break;
                case BaseItemDto_Type.Season when (item.SeriesId ?? item.ParentId) is { } seriesId:
                    if (DetailModel.NextEpisode(await session.GetEpisodesAsync(seriesId, item.Id)) is { } episode && Current()) shell.Play(episode, from: origin);
                    break;
            }
        }
        catch (Exception ex)
        {
            Shell.ReportFailure(ex);
        }
    }

    /// <summary>This card's artwork, which a play started here grows out of.</summary>
    private PlayOrigin Origin => new(Art, Picture.Source);

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ShowMenu(this, e.TryGetPosition(this, out var point) ? point : null);
    }

    private void ShowMenu(FrameworkElement target, Windows.Foundation.Point? at)
    {
        if (!_showActions || _item is not { } item || Session is null || Shell is null) return;
        var menu = ItemMenu.Build(Shell, Session, item, Overrides?.Of(item) ?? (_played, _favorite),
            (field, value, send) => ChangeAsync(item, field, value, send), MenuOptions, Origin);
        // The menu takes focus; keep the pill shown under it.
        menu.Opened += (_, _) => { _menuOpen = true; UpdateState(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateState(); };
        menu.ShowAt(target, at is { } p ? new FlyoutShowOptions { Position = p } : new FlyoutShowOptions());
    }

    /// <summary>Shows the item's current watched and favourite state, only if this card still holds
    /// it (a recycled card may show another title by the time a request returns).</summary>
    private void ShowUserData(MediaItem item)
    {
        if (_item is not { } shown || shown.Id != item.Id || Overrides is null) return;
        (_played, _favorite) = UserState(shown);
        UpdateState();
    }

    /// <summary>Watched and favourite as shown: the overrides' state, else the server's; an empty
    /// item is never watched.</summary>
    private (bool Played, bool Favorite) UserState(MediaItem item)
    {
        var (played, favorite) = Overrides?.Of(item) ?? (item.Played, item.IsFavorite);
        return (played && IsEmpty?.Invoke(item) != true, favorite);
    }

    private bool IsInside(DependencyObject node) => IsInside(node, this);

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>Lets UI Automation (and the guest suites) open a card like a button.</summary>
    private sealed partial class MediaCardPeer(MediaCard owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(MediaCard);

        /// <summary>"artwork" once the picture is on screen, for tests that check artwork loads.</summary>
        protected override string GetItemStatusCore() => owner.Picture.Source is null ? "" : "artwork";

        protected override object? GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke() => owner.Invoke();
    }
}
