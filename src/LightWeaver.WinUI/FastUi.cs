using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LightWeaver.WinUI;

/// <summary>Strips WinUI's default motion, which the W1 spike measured at 3 to 5 times slower
/// than the WPF app (spikes/W1-winui3-composition/RESULTS.md, criterion 8). The owner's rule for
/// v2: every screen runs with these levers; tests/FastUi.Tests enforces the XAML side.
///
/// Levers and the defaults they replace:
/// - Flyout and MenuFlyout open/close slide-and-fade: FlyoutBase.AreOpenCloseAnimationsEnabled =
///   false, set in XAML on every flyout; <see cref="Flyout"/> sets it on code-created ones.
/// - Hover and press colours fading over 83 ms (ContentPresenter/Border/Panel
///   BackgroundTransition in the stock templates): cleared by <see cref="Walk"/>. The app's own
///   templates in Theme/Styles.xaml declare none.
/// - Panel ChildrenTransitions and ItemsControl ItemContainerTransitions (entrance, reorder,
///   add/delete theme transitions): cleared by <see cref="Walk"/>.
/// - Connected animations (about 300 ms): 120 ms.</summary>
internal static class FastUi
{
    public static readonly TimeSpan ConnectedAnimationDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PopupPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>Hooks a window: walks its content once loaded, and each popup when it appears
    /// (suggestion lists, tooltips, flyout presenters), since their templates expand lazily.</summary>
    public static void Attach(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        var open = new HashSet<Popup>();
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = PopupPoll;
        timer.Tick += (_, _) =>
        {
            if (root.XamlRoot is null) return;
            var now = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot);
            foreach (var popup in now)
                if (open.Add(popup) && popup.Child is { } child) Walk(child);
            open.IntersectWith(now);
        };
        root.Loaded += (_, _) =>
        {
            Walk(root);
            try { ConnectedAnimationService.GetForCurrentView().DefaultDuration = ConnectedAnimationDuration; }
            catch (Exception) { /* no view on some activation paths; nothing animates then either */ }
            timer.Start();
        };
        window.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Applies the levers to a flyout: no open/close animation, and its content walked
    /// the moment it opens rather than on the next poll.</summary>
    public static T Flyout<T>(T flyout) where T : FlyoutBase
    {
        flyout.AreOpenCloseAnimationsEnabled = false;
        flyout.Opened += (_, _) =>
        {
            if (flyout.XamlRoot is null) return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(flyout.XamlRoot))
                if (popup.Child is { } child) Walk(child);
        };
        return flyout;
    }

    /// <summary>Clears brush and children transitions below <paramref name="node"/>. Call again
    /// for content that loads later (a page, a templated item). Media cards are skipped: they are
    /// most of a page's elements and are built only from the app's own templates, which declare no
    /// transitions, so walking them made every page switch slower for nothing.</summary>
    public static void Walk(DependencyObject node)
    {
        if (node is Controls.MediaCard) return;
        switch (node)
        {
            case ContentPresenter cp:
                cp.BackgroundTransition = null;
                break;
            case Border b:
                b.BackgroundTransition = null;
                break;
            case Panel p:
                p.BackgroundTransition = null;
                p.ChildrenTransitions?.Clear();
                break;
        }
        if (node is ItemsControl ic)
            ic.ItemContainerTransitions?.Clear();
        var n = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < n; i++)
            Walk(VisualTreeHelper.GetChild(node, i));
    }
}
