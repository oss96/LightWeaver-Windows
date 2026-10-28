using LightWeaver.Localization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace LightWeaver.Views;

/// <summary>
/// Keeps the player's OSD flyouts inside the overlay, and on it when the overlay moves.
///
/// <para>WPF keeps a popup on the SCREEN, not in the window it belongs to. A Top-placed flyout
/// is left-aligned to its button, so one hanging from a button near the right edge ran past the
/// player window: measured on the guest, the info flyout overflowed a 1600x900 player by 139 px
/// and a 700x480 one by 163 (more with Media info open). <see cref="Attach"/> sets each attached
/// flyout's offsets from the overlay's layout: left-aligned to the button where that fits,
/// right-aligned to it where it does not, and clamped inside the overlay either way. Its size is
/// capped to the room the overlay has, so a long list scrolls instead of leaving the window.
/// Everything is in the overlay root's layout units, which is also the unit WPF applies the
/// offsets in (the button's), so neither the UI zoom nor the monitor DPI enters the arithmetic.</para>
///
/// <para>A popup does not follow its button when the window moves or resizes either: an open
/// flyout stayed where it was while the player shrank from 1600x900 to 700x480, entirely outside
/// it. A real change of the overlay's size or position therefore re-places every open flyout,
/// and closes one whose button is no longer shown (mini mode collapses the transport row). The
/// window's SizeChanged and LocationChanged only fire on a genuine change, so the continuous
/// owner sync, which skips writes when nothing moved, never triggers this.</para>
///
/// <para>Nor does a popup follow its button when only the button moves: the row fit folding or
/// unfolding a group, a control the view model shows or hides, a padding or zoom change. Each of
/// those moves it in a layout pass that can come after the posted re-place above. So while an
/// attached flyout is open this class watches the overlay's LayoutUpdated, and re-places one
/// whose button's origin moved since it was last placed. That includes a button the row fit
/// brings back from the overflow tray: its flyout stays open and moves onto it.
/// A pass in which nothing moved costs one TranslatePoint per open flyout and changes nothing,
/// which is also what ends the loop after a re-place's own layout pass.</para>
/// </summary>
internal sealed class OsdFlyoutPlacement
{
    /// <summary>Distance kept from the overlay's edges.</summary>
    private const double EdgeMargin = 8;

    /// <summary>Gap between a flyout anchored above an element and that element.</summary>
    private const double AboveGap = 8;

    private readonly FrameworkElement _bounds;
    private readonly Func<IEnumerable<Popup>> _flyouts;
    private readonly Action<string> _log;
    private readonly Dictionary<Popup, Anchor> _anchors = [];
    /// <summary>The open attached flyouts, each with its button's origin (left-based, in overlay
    /// units) as of its last placement. Non-empty exactly while LayoutUpdated is subscribed.</summary>
    private readonly Dictionary<Popup, Point> _open = [];
    private bool _replacePending;
    private bool _following;

    /// <summary>How an attached flyout hangs off its button. <see cref="DesignMaxHeight"/> is
    /// the child's own MaxHeight from XAML, which the room in the overlay can only lower.</summary>
    private sealed record Anchor(bool PreferRightEdge, FrameworkElement? Above, double DesignMaxHeight);

    /// <param name="window">The overlay window, whose moves re-place the open flyouts.</param>
    /// <param name="bounds">The element the flyouts are kept inside: the overlay's root, which
    /// carries the zoom, so its size is in the same units as the buttons.</param>
    /// <param name="flyouts">Every flyout to re-place on a move or resize, attached or not.</param>
    public OsdFlyoutPlacement(Window window, FrameworkElement bounds, Func<IEnumerable<Popup>> flyouts, Action<string> log)
    {
        _bounds = bounds;
        _flyouts = flyouts;
        _log = log;
        bounds.SizeChanged += (_, _) => RequestReplace("resize");
        window.LocationChanged += (_, _) => RequestReplace("move");
    }

    /// <summary>Places <paramref name="popup"/> (Placement=Top) inside the overlay whenever it
    /// opens or its content changes size. <paramref name="preferRightEdge"/> right-aligns it to
    /// its button even where left-aligned would fit. <paramref name="above"/> lifts it so its
    /// bottom edge sits above that element instead of on the button's top.</summary>
    public void Attach(Popup popup, bool preferRightEdge = false, FrameworkElement? above = null)
    {
        if (popup.Child is not FrameworkElement child)
            return;
        _anchors[popup] = new Anchor(preferRightEdge, above, child.MaxHeight);
        // The offsets below are screen left-to-right, like the pinned transport row the buttons
        // sit in. A right-to-left popup would mirror its placement and the sign of its offset, so
        // the popup keeps LeftToRight and only its content follows the UI language.
        popup.FlowDirection = FlowDirection.LeftToRight;
        UiDirection.Follow(child);
        popup.Opened += (_, _) =>
        {
            Place(popup);
            Watch(popup);
        };
        popup.Closed += (_, _) => Unwatch(popup);
        child.SizeChanged += (_, _) =>
        {
            if (popup.IsOpen)
                Place(popup);
        };
    }

    /// <summary>Re-places every open flyout once layout has caught up. Posted rather than run
    /// inline: both events arrive before the layout pass that moves the buttons, and Left and Top
    /// are written separately, so one move raises two. A move of the window leaves every button
    /// where it was relative to the overlay, so <see cref="OnLayoutUpdated"/> sees nothing to
    /// follow; and a resize changes the room a flyout is clamped to even where its button stays.
    /// A later layout pass that moves a button (the row fit folding a group after this resize)
    /// is followed by <see cref="OnLayoutUpdated"/>.</summary>
    private void RequestReplace(string reason)
    {
        if (_replacePending || !_flyouts().Any(p => p.IsOpen))
            return;
        _replacePending = true;
        _bounds.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _replacePending = false;
            foreach (var popup in _flyouts().Where(p => p.IsOpen).ToList())
            {
                if (popup.PlacementTarget is not { IsVisible: true })
                {
                    popup.IsOpen = false;
                    _log($"FlyoutClosed name={popup.Name} reason={reason}");
                    continue;
                }
                Replace(popup, reason);
            }
        });
    }

    /// <summary>Re-places <paramref name="popup"/> against its button as it is now.</summary>
    private void Replace(Popup popup, string reason)
    {
        if (_anchors.ContainsKey(popup))
            Place(popup);
        Reposition(popup);
        _log($"FlyoutReplaced name={popup.Name} reason={reason}");
    }

    /// <summary>A popup re-runs its placement when an offset changes, and only then: the values
    /// Place computes are often the same as before (an offset relative to the button survives a
    /// move), so one is stepped and put back to force the pass. Popup repositions on each offset
    /// change synchronously, so the step never reaches the screen on its own.</summary>
    private static void Reposition(Popup popup)
    {
        var offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 1;
        popup.HorizontalOffset = offset;
    }

    private static bool Moved(Point origin, Point placedAt)
        => Math.Abs(origin.X - placedAt.X) > 0.5 || Math.Abs(origin.Y - placedAt.Y) > 0.5;

    private void Watch(Popup popup)
    {
        if (!popup.IsOpen || popup.PlacementTarget is not FrameworkElement target)
            return;
        if (_open.Count == 0)
            _bounds.LayoutUpdated += OnLayoutUpdated;
        _open[popup] = Origin(target);
    }

    private void Unwatch(Popup popup)
    {
        if (_open.Remove(popup) && _open.Count == 0)
            _bounds.LayoutUpdated -= OnLayoutUpdated;
    }

    /// <summary>Re-places each open attached flyout whose button moved since it was last placed.
    /// LayoutUpdated fires after every layout pass on the thread, so the common case (nothing
    /// moved) must stay a few comparisons.
    /// <para>A button that is not shown is skipped, not closed. A pass can see a button hidden for
    /// a moment: a quality change rebuilds the row it sits in, and closing there dismissed the
    /// quality picker in the middle of applying a request (lw-playback-quality on the guest). A
    /// resize does not hide the overlay: MainWindow.SyncOverlayBounds hides it only for a
    /// minimized or hidden owner or a hidden video area. A button that stays hidden is still
    /// handled: <see cref="RequestReplace"/>
    /// closes its flyout once layout settles after a resize or move, and the row fit closes one
    /// whose button it folds.</para></summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_following)
            return;
        List<Popup>? moved = null;
        foreach (var (popup, placedAt) in _open)
        {
            if (!popup.IsOpen || popup.PlacementTarget is not FrameworkElement { IsVisible: true } target)
                continue;
            if (Moved(Origin(target), placedAt))
                (moved ??= []).Add(popup);
        }
        if (moved is null)
            return;

        _following = true;
        try
        {
            foreach (var popup in moved)
            {
                if (!popup.IsOpen || popup.PlacementTarget is not FrameworkElement target)
                    continue;
                // Recorded before placing, so even a Place that bails out (an overlay with no
                // size yet) is not retried on every following pass for the same move.
                _open[popup] = Origin(target);
                Replace(popup, "moved");
            }
        }
        finally
        {
            _following = false;
        }
    }

    private void Place(Popup popup)
    {
        if (!_anchors.TryGetValue(popup, out var anchor) || popup.Child is not FrameworkElement child
            || popup.PlacementTarget is not FrameworkElement target || !target.IsVisible
            || _bounds.ActualWidth <= 0 || _bounds.ActualHeight <= 0)
            return;

        var origin = Origin(target);
        // A content resize places from the child's SizeChanged, inside a layout pass and before
        // LayoutUpdated. If the button moved in that same pass, recording its new origin here
        // hides the move from OnLayoutUpdated, so this placement forces the reposition itself.
        var moved = false;
        if (_open.TryGetValue(popup, out var placedAt))
        {
            moved = Moved(origin, placedAt);
            _open[popup] = origin;
        }
        var bottom = origin.Y;
        if (anchor.Above is { IsVisible: true } above)
            bottom = Math.Min(bottom, above.TranslatePoint(new Point(), _bounds).Y - AboveGap);
        popup.VerticalOffset = bottom - origin.Y;
        child.MaxHeight = Math.Max(0, Math.Min(anchor.DesignMaxHeight, bottom - EdgeMargin));

        var room = Math.Max(0, _bounds.ActualWidth - 2 * EdgeMargin);
        child.MaxWidth = room;
        // ActualWidth is the child's own, before the zoom correction ZoomPopups may put on it,
        // and that is its width in the button's units too: the correction and the zoom above the
        // button cancel. Before the first arrange the measured size stands in, and the
        // SizeChanged that follows the arrange re-places it.
        var width = Math.Min(child.ActualWidth > 0 ? child.ActualWidth : child.DesiredSize.Width, room);
        var left = origin.X;
        if (anchor.PreferRightEdge || left + width > _bounds.ActualWidth - EdgeMargin)
            left = origin.X + target.ActualWidth - width;
        left = Math.Clamp(left, EdgeMargin, Math.Max(EdgeMargin, _bounds.ActualWidth - EdgeMargin - width));
        popup.HorizontalOffset = left - origin.X;
        if (moved)
            Reposition(popup);
    }

    /// <summary>The target's top-left corner in the overlay root's units, left-based.</summary>
    private Point Origin(FrameworkElement target) => LeftToRight(target.TranslatePoint(new Point(), _bounds));

    /// <summary>The overlay root mirrors for a right-to-left UI language, and a point translated
    /// into it then counts from its right edge. The target's own origin is its screen-left corner
    /// (the transport row is pinned LeftToRight), so flipping X gives the same left-based
    /// coordinate the arithmetic above uses in either direction.</summary>
    private Point LeftToRight(Point point)
        => _bounds.FlowDirection == FlowDirection.RightToLeft
            ? new Point(_bounds.ActualWidth - point.X, point.Y)
            : point;
}
