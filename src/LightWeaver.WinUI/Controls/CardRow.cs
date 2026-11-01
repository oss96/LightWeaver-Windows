using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>A titled row of cards (Home and detail sketches): "Title →" opens the full list,
/// ‹ › page through the row, and the row also scrolls sideways. AutomationIds are the
/// <c>key</c> after {prefix}Open, {prefix}Previous, {prefix}Next and {prefix}Items.</summary>
internal partial class CardRow : StackPanel
{
    private const double Gap = 14;
    private const double HeaderGap = 12;
    private const double TitleSize = 17;
    private const double ArrowSize = 15;
    private readonly IAppShell _shell;
    private readonly UserDataOverrides _overrides;
    private readonly ItemMenuOptions? _menu;
    private readonly Func<string> _titleText;
    private readonly double _cardWidth;
    private readonly CardKind _kind;
    private readonly TextBlock _title;
    private readonly FontIcon _arrow;
    private readonly Button _openButton;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Grid _header;
    private readonly ScrollView _scroller;
    private readonly CardRowPaging _paging = new();
    private readonly StackPanel _cards;
    private readonly TextBlock _empty;
    private readonly List<MediaItem> _items = [];
    private Func<MediaItem, int?>? _count;
    private CardScale _scale = CardScale.None;
    private bool _holding;

    public CardRow(IAppShell shell, string prefix, string key, Func<string> title, Action? open, CardKind kind, double cardWidth,
        UserDataOverrides overrides, ItemMenuOptions? menu = null)
    {
        _shell = shell;
        _overrides = overrides;
        _menu = menu;
        _titleText = title;
        _kind = kind;
        _cardWidth = cardWidth;
        Spacing = HeaderGap;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerWheelChanged += OnPointerWheelChanged;

        _title = new TextBlock { FontSize = TitleSize, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var titleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleContent.Children.Add(_title);
        titleContent.Children.Add(_arrow = Ui.DirectionalIcon(Theme.Icons.ArrowForward, ArrowSize));
        var openButton = _openButton = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Padding = new Thickness(6, 3, 8, 3), Margin = new Thickness(-6, 0, 0, 0), Content = titleContent };
        AutomationProperties.SetAutomationId(openButton, prefix + "Open" + key);
        if (open is null) openButton.IsEnabled = false;
        else openButton.Click += (_, _) => open();

        _previous = PageButton(Theme.Icons.ChevronLeft, prefix + "Previous" + key, -1);
        _next = PageButton(Theme.Icons.ChevronRight, prefix + "Next" + key, 1);
        var header = _header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(openButton);
        openButton.HorizontalAlignment = HorizontalAlignment.Left;
        var paging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        paging.Children.Add(_previous);
        paging.Children.Add(_next);
        Grid.SetColumn(paging, 1);
        header.Children.Add(paging);
        Children.Add(header);

        _cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
        _scroller = new ScrollView
        {
            ContentOrientation = ScrollingContentOrientation.Horizontal,
            HorizontalScrollMode = ScrollingScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollingScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
            ZoomMode = ScrollingZoomMode.Disabled,
            IgnoredInputKinds = ScrollingInputKinds.MouseWheel,
            IsTabStop = false,
            Content = _cards,
        };
        _scroller.Loaded += (_, _) =>
        {
            if (_scroller.ScrollPresenter is { } presenter)
                AutomationProperties.SetAutomationId(presenter, prefix + "Items" + key);
        };
        _scroller.ViewChanged += (_, _) => UpdatePaging();
        _scroller.SizeChanged += (_, _) => CancelPaging();
        _scroller.ExtentChanged += (_, _) => CancelPaging();
        _scroller.ScrollAnimationStarting += (_, args) =>
        {
            if (args.Animation is Vector3KeyFrameAnimation animation)
                animation.Duration = TimeSpan.FromMilliseconds(180);
        };
        _scroller.ScrollCompleted += (_, args) =>
        {
            _paging.Complete(args.CorrelationId);
            UpdatePaging();
        };
        _scroller.StateChanged += (_, _) =>
        {
            if (_scroller.State == ScrollingInteractionState.Interaction) CancelPaging(stop: false);
        };
        _scroller.PreviewKeyDown += (_, _) => CancelPaging();
        _scroller.BringingIntoView += (_, _) => CancelPaging();
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => CancelPaging());
        Unloaded += (_, _) => CancelPaging();
        _empty = new TextBlock { FontSize = EmptySize, Foreground = Ui.Resource<Brush>("LwText2Brush"), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        Children.Add(new Grid { Children = { _scroller, _empty } });
        ApplyTexts();
    }

    public bool IsEmpty => _items.Count == 0;

    public string SectionTitle => _titleText();

    public void ReserveHeaderEnd(double width)
        => _header.Margin = FlowDirection == FlowDirection.RightToLeft
            ? new Thickness(width, 0, 0, 0) : new Thickness(0, 0, width, 0);

    /// <summary>The quiet line an empty row shows in the place it holds; set, the row holds its
    /// place (<see cref="HoldPlace"/>) instead of collapsing when it has nothing.</summary>
    public Func<string>? EmptyText { get; init; }

    private const double EmptySize = 13;

    /// <summary>How long a held place stays blank before its placeholder cards show as skeletons.</summary>
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Takes the row's final height before its items arrive: placeholder cards of the real
    /// cards' size, invisible for 150 ms and skeletons after that, so arriving items fill the place
    /// without moving anything.</summary>
    public void HoldPlace()
    {
        CancelPaging();
        _holding = true;
        _cards.Children.Clear();
        for (var i = 0; i < SkeletonCards; i++) _cards.Children.Add(Placeholder(visible: false));
        Visibility = Visibility.Visible;
        UpdateOpener();
        UpdatePaging();
        var reveal = DispatcherQueue.CreateTimer();
        reveal.Interval = SkeletonDelay;
        reveal.IsRepeating = false;
        reveal.Tick += (_, _) =>
        {
            if (!_holding) return;
            foreach (var placeholder in _cards.Children.OfType<MediaCard>().Where(c => c.Item is null)) placeholder.Opacity = 1;
        };
        reveal.Start();
    }

    private const int SkeletonCards = 8;

    private MediaCard Placeholder(bool visible)
    {
        var card = MediaCard.Placeholder(_kind);
        card.Width = CardWidth;
        card.ApplyScale(_scale);
        card.Opacity = visible ? 1 : 0;
        return card;
    }

    private double CardWidth => _scale.Width(_cardWidth);

    private double CardGap => _scale.Space(Gap);

    /// <summary>The card scale for a page <paramref name="width"/> DIP wide, from the theme's tokens.</summary>
    public static CardScale ScaleFor(double width)
        => CardScale.For(width, Ui.Resource<double>("LwCardScaleFrom"), MaxScale, Ui.Resource<double>("LwCardTextShare"));

    /// <summary>The largest card scale (<c>LwCardScaleMax</c>).</summary>
    public static double MaxScale => Ui.Resource<double>("LwCardScaleMax");

    /// <summary>Sizes the cards, their gaps, captions and the row heading for <paramref name="scale"/>,
    /// keeping the cards and their artwork.</summary>
    public void ApplyScale(CardScale scale)
    {
        if (scale == _scale) return;
        CancelPaging();
        _scale = scale;
        Spacing = scale.Space(HeaderGap);
        _title.FontSize = scale.Font(TitleSize);
        _empty.FontSize = scale.Font(EmptySize);
        _arrow.FontSize = scale.Font(ArrowSize);
        _cards.Spacing = CardGap;
        foreach (var child in _cards.Children)
            switch (child)
            {
                case MediaCard card:
                    card.Width = CardWidth;
                    card.ApplyScale(scale);
                    break;
                case FrameworkElement skeleton:
                    skeleton.Width = CardWidth;
                    skeleton.Height = SkeletonHeight;
                    break;
            }
    }

    /// <summary>The card's title line; the item's name unless set.</summary>
    public Func<MediaItem, string>? CardTitle { get; init; }

    /// <summary>The card's second line.</summary>
    public Func<MediaItem, string>? CardSubtitle { get; init; }

    /// <summary>The poster a card shows instead of its item's own; the item's unless set.</summary>
    public Func<MediaItem, (Guid Id, string Tag)?>? CardPoster { get; init; }

    /// <summary>Whether cards offer hover actions (watched, favourite); off where the server is away.</summary>
    public bool ShowCardActions { get; init; } = true;

    /// <summary>Opens a card's item; the item page unless set (people open their page).</summary>
    public Action<MediaItem>? Open { get; init; }

    /// <summary>Shows <paramref name="items"/>, keeping the card of every item already shown (moved
    /// into place, its content refreshed without reloading unchanged artwork) and creating cards
    /// only for new items, so a refresh with the same answer changes nothing on screen.</summary>
    public void SetItems(IReadOnlyList<MediaItem> items, Func<MediaItem, int?>? count = null)
    {
        if (!_items.Select(item => item.Id).SequenceEqual(items.Select(item => item.Id))) CancelPaging();
        _count = count;
        var existing = new Dictionary<Guid, Queue<MediaCard>>();
        foreach (var card in _cards.Children.OfType<MediaCard>())
        {
            if (card.Item is not { } shown) continue;
            if (!existing.TryGetValue(shown.Id, out var queue)) existing[shown.Id] = queue = new Queue<MediaCard>();
            queue.Enqueue(card);
        }
        var target = new List<MediaCard>(items.Count);
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Id, out var queue) && queue.TryDequeue(out var card))
            {
                card.Count = count;
                card.Item = item;
            }
            else
            {
                card = Card(item);
            }
            target.Add(card);
        }
        for (var i = 0; i < target.Count; i++)
        {
            if (i < _cards.Children.Count && ReferenceEquals(_cards.Children[i], target[i])) continue;
            var at = _cards.Children.IndexOf(target[i]);
            if (at > i) _cards.Children.Move((uint)at, (uint)i);
            else _cards.Children.Insert(i, target[i]);
        }
        while (_cards.Children.Count > target.Count) _cards.Children.RemoveAt(_cards.Children.Count - 1);
        _items.Clear();
        _items.AddRange(items);
        _holding = false;
        var keepPlace = EmptyText is not null;
        if (keepPlace && items.Count == 0) _cards.Children.Add(Placeholder(visible: false));
        _empty.Visibility = keepPlace && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = items.Count > 0 || keepPlace ? Visibility.Visible : Visibility.Collapsed;
        UpdateOpener();
        UpdatePaging();
    }

    /// <summary>A row that holds its place opens its list only once it has items: until then its
    /// heading takes no clicks or focus and is left out of UI Automation, and an empty row's heading
    /// drops its arrow, so it reads as a plain heading over the empty line.</summary>
    private void UpdateOpener()
    {
        if (EmptyText is null) return;
        var live = _items.Count > 0;
        AutomationProperties.SetAccessibilityView(_openButton, live ? AccessibilityView.Content : AccessibilityView.Raw);
        _openButton.IsHitTestVisible = _openButton.IsTabStop = live;
        _arrow.Opacity = live || _holding ? 1 : 0;
    }

    /// <summary>Grey placeholder cards, shown when loading takes longer than 150 ms.</summary>
    public void ShowSkeleton()
    {
        CancelPaging();
        _cards.Children.Clear();
        for (var i = 0; i < 8; i++)
            _cards.Children.Add(new Border { Width = CardWidth, Height = SkeletonHeight, CornerRadius = Ui.Resource<CornerRadius>("LwCardRadius"), Background = Ui.Resource<Brush>("LwPlaceholderBrush") });
        Visibility = Visibility.Visible;
    }

    private double SkeletonHeight => _kind switch { CardKind.Wide => CardWidth * 9 / 16, CardKind.Person or CardKind.Square => CardWidth, _ => CardWidth * 1.5 };

    /// <summary>Takes an item out of the row and returns where it was, for Undo.</summary>
    public int Remove(MediaItem item)
    {
        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0) return -1;
        CancelPaging();
        _items.RemoveAt(index);
        _cards.Children.RemoveAt(index);
        Visibility = _items.Count > 0 || EmptyText is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateOpener();
        UpdatePaging();
        return index;
    }

    public void Insert(int index, MediaItem item)
    {
        CancelPaging();
        index = Math.Clamp(index, 0, _items.Count);
        _items.Insert(index, item);
        _cards.Children.Insert(index, Card(item));
        Visibility = Visibility.Visible;
        UpdateOpener();
        UpdatePaging();
    }

    public void ApplyTexts()
    {
        _title.Text = _titleText();
        _empty.Text = EmptyText?.Invoke() ?? "";
        var previous = L.Text("Home.Previous");
        var next = L.Text("Home.Next");
        ToolTipService.SetToolTip(_previous, previous);
        AutomationProperties.SetName(_previous, previous);
        ToolTipService.SetToolTip(_next, next);
        AutomationProperties.SetName(_next, next);
        foreach (var card in _cards.Children.OfType<MediaCard>()) card.Refresh();
    }

    private MediaCard Card(MediaItem item)
    {
        var card = new MediaCard
        {
            Width = CardWidth,
            Kind = _kind,
            Shell = _shell,
            Session = _shell.Model.Jellyfin,
            Overrides = _overrides,
            MenuOptions = _menu,
            Title = CardTitle,
            Subtitle = CardSubtitle,
            Poster = CardPoster,
            Count = _count,
            ShowActions = ShowCardActions && _kind != CardKind.Person,
            FollowsCardScale = true,
        };
        card.ApplyScale(_scale);
        if (Open is { } open) card.Invoked += (_, i) => open(i);
        card.Item = item;
        return card;
    }

    private Button PageButton(string glyph, string automationId, int direction)
    {
        var button = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 28, Height = 28, Foreground = Ui.Resource<Brush>("LwText2Brush"), Content = Ui.DirectionalIcon(glyph, 14), IsEnabled = false };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => Page(direction);
        return button;
    }

    /// <summary>Moves by the whole cards that fit, so the next page starts on a card edge.</summary>
    private void Page(int direction)
    {
        var target = _paging.Destination(_scroller.HorizontalOffset, _scroller.ViewportWidth,
            _scroller.ScrollableWidth, CardWidth, CardGap, direction);
        ScrollTo(target);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        var wheel = e.GetCurrentPoint(this).Properties;
        var target = _paging.WheelDestination(_scroller.HorizontalOffset, _scroller.ScrollableWidth,
            wheel.MouseWheelDelta, wheel.IsHorizontalMouseWheel, FlowDirection == FlowDirection.RightToLeft);
        if (target is not { } destination) return;
        e.Handled = true;
        ScrollTo(destination);
    }

    private void ScrollTo(double target)
    {
        if (target == (_paging.Target ?? _scroller.HorizontalOffset)) return;
        var correlationId = _scroller.ScrollTo(target, 0,
            new ScrollingScrollOptions(ScrollingAnimationMode.Auto, ScrollingSnapPointsMode.Ignore));
        _paging.Track(correlationId, target);
        UpdatePaging();
    }

    private void CancelPaging(bool stop = true)
    {
        var pending = _paging.Target.HasValue;
        _paging.Reset();
        if (pending && stop)
            _scroller.ScrollTo(_scroller.HorizontalOffset, 0,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        UpdatePaging();
    }

    private void UpdatePaging()
    {
        var offset = _paging.Target ?? _scroller.HorizontalOffset;
        var previous = _items.Count > 0 && offset > 1;
        var next = _items.Count > 0 && offset < _scroller.ScrollableWidth - 1;
        var previousFocus = _previous.FocusState;
        var nextFocus = _next.FocusState;
        _previous.IsEnabled = previous;
        _next.IsEnabled = next;
        if (!previous && next && previousFocus != FocusState.Unfocused) _next.Focus(previousFocus);
        if (!next && previous && nextFocus != FocusState.Unfocused) _previous.Focus(nextFocus);
    }
}
