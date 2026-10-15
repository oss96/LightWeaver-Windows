using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightWeaver.Theme;

/// <summary>
/// Keeps popups at the UI zoom. WPF scales a popup by the transforms above it only as it opens
/// (<c>Popup.UpdateTransform</c> runs nowhere else), so a flyout left open across a zoom step
/// keeps the old size. A ToolTip or ContextMenu gets no zoom at all: its popup is created outside
/// the visual tree, so there is nothing above it to inherit from.
///
/// <see cref="FollowProperty"/> on a Popup (every declared one, and one created in code) keeps
/// its child at the zoom while it is open.
/// <see cref="Register"/> gives every tooltip and context menu its owner's scale as it opens,
/// which is also 100% in the mini-player.
/// </summary>
public static class ZoomPopups
{
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(ZoomPopups), new PropertyMetadata(false, OnFollowChanged));

    public static bool GetFollow(DependencyObject element) => (bool)element.GetValue(FollowProperty);

    public static void SetFollow(DependencyObject element, bool value) => element.SetValue(FollowProperty, value);

    private static bool _registered;

    /// <summary>Class handlers for every ToolTip and ContextMenu. Idempotent: every MainWindow
    /// (the app's, a test harness's) calls it.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnOwnedPopupOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOwnedPopupOpened));
    }

    /// <summary>Opened runs before the popup's first layout pass, so the scale set here is the
    /// one it is drawn at. Both placement targets are set by then: ToolTipService coerces a
    /// tooltip's to its owner, and the app's menus set theirs before opening.</summary>
    private static void OnOwnedPopupOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        var (element, target) = sender switch
        {
            ToolTip tip => ((FrameworkElement)tip, tip.PlacementTarget),
            ContextMenu menu => (menu, menu.PlacementTarget),
            _ => (null, null),
        };
        if (element is not null)
            element.LayoutTransform = ScaleTransformFor(ScaleOf(target));
    }

    private static void OnFollowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup || e.NewValue is not true)
            return;
        var follower = new Follower(popup);
        popup.Opened += follower.OnOpened;
        popup.Closed += follower.OnClosed;
    }

    /// <summary>Makes up on the child the difference between the zoom the popup should be drawn
    /// at and the one WPF gave it on opening. A popup in the tree should be at its own place's
    /// zoom, and WPF gave it that zoom as it was then. A popup created in code has no place: it
    /// should be at its placement target's zoom, and WPF gave it none. Its offsets need no help,
    /// because WPF applies them in the target's coordinates.
    ///
    /// Subscribed to <see cref="UiZoom.Changed"/> only while its popup is open, so a closed
    /// popup in a discarded view is never held by the static event.</summary>
    private sealed class Follower(Popup popup)
    {
        private double _givenOnOpen = 1.0;
        private bool _subscribed;

        private bool InTree => VisualTreeHelper.GetParent(popup) is not null;

        /// <summary>Also runs for a reopen during the close fade, which WPF answers with a second
        /// Opened and no Closed: the old correction is dropped before the new baseline is taken,
        /// and the subscription is never doubled.</summary>
        public void OnOpened(object? sender, EventArgs e)
        {
            (popup.Child as FrameworkElement)?.ClearValue(FrameworkElement.LayoutTransformProperty);
            _givenOnOpen = InTree ? ScaleOf(popup) : 1.0;
            Correct();
            if (!_subscribed)
            {
                UiZoom.Changed += OnZoomChanged;
                _subscribed = true;
            }
        }

        public void OnClosed(object? sender, EventArgs e)
        {
            UiZoom.Changed -= OnZoomChanged;
            _subscribed = false;
            // The next open starts from what WPF gives it then.
            (popup.Child as FrameworkElement)?.ClearValue(FrameworkElement.LayoutTransformProperty);
        }

        /// <summary>After layout, not inside the step: until the next measure the transforms above
        /// the popup still report the previous zoom. Resizing the child also re-places the popup
        /// against its target.</summary>
        private void OnZoomChanged(double scale)
            => popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (popup.IsOpen)
                    Correct();
            });

        private void Correct()
        {
            if (popup.Child is FrameworkElement child)
                child.LayoutTransform = ScaleTransformFor(ScaleOf(InTree ? popup : popup.PlacementTarget) / _givenOnOpen);
        }
    }

    /// <summary>The scale <paramref name="visual"/> is drawn at in its window: the zoom where it
    /// sits, 1 in the mini-player or outside any window. Measured, like WPF's own popup
    /// transform, from how far a unit step along X travels to the root.</summary>
    private static double ScaleOf(Visual? visual)
    {
        if (visual is null || PresentationSource.FromVisual(visual)?.RootVisual is not { } root
            || ReferenceEquals(root, visual) || !root.IsAncestorOf(visual))
            return 1.0;
        var transform = visual.TransformToAncestor(root);
        var length = (transform.Transform(new Point(1, 0)) - transform.Transform(new Point(0, 0))).Length;
        return double.IsFinite(length) && length > 0 ? length : 1.0;
    }

    private static Transform ScaleTransformFor(double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001)
            return Transform.Identity;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        return transform;
    }
}
