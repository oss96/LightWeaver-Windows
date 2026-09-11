using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LightWeaver.Player;
using LightWeaver.Settings;

namespace LightWeaver.Views;

/// <summary>One rendered section of the panel.</summary>
public sealed record ShortcutGroupView(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The keyboard-shortcuts panel (Phase 10 M9). Content is rebuilt from
/// <see cref="AppShortcuts.BuildRows"/> on every <see cref="Show"/>, which is what keeps it from
/// drifting: the rebindable rows come from the same resolved map the key handler dispatches
/// against, and the chrome rows from the same table <c>MainWindow</c> registers its
/// <see cref="KeyBinding"/>s from.
/// </summary>
public partial class ShortcutsOverlay : UserControl
{
    public ShortcutsOverlay()
    {
        InitializeComponent();
        // Re-run the fold guard whenever the viewport changes under an open panel: the window can
        // be resized, maximized or restored while it is up, and the fold moves with it.
        //
        // Not while Show's run is still queued, though. The first open lays out with IsOpen
        // ALREADY true, so this fires during that pass and ran the guard a second time — same
        // decision, second `fold-guard` line in the log. A later resize has no queued run
        // pending, so it still re-runs here.
        Scroller.SizeChanged += (_, _) =>
        {
            if (IsOpen && !_foldGuardScheduled)
                ApplyFoldGuard();
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Show(AppSettings settings)
    {
        var groups = AppShortcuts.BuildRows(settings)
            .Select(g => new ShortcutGroupView(AppShortcuts.GroupTitle(g.Key), [.. g]))
            .ToList();
        Diagnostics.AppLog.Detail("shortcuts", $"event=open groups={groups.Count} rows={groups.Sum(g => g.Rows.Count)}");

        // Column assignment is fixed rather than balanced at runtime, and it is the balanced
        // pairing rather than the obvious one: Playback+Audio down the left is the longest column
        // this content can produce. The census today is Playback 15, Audio & subtitles 6,
        // General 8, Mouse 3, so Playback+Mouse against Audio+General is 18 rows left and 14
        // right — the closest of the four pairings.
        //
        // It does NOT follow that the table fits, which is what this comment claimed until
        // 2026-09-05 ("14 vs 15 ... fits at 1920x1080 without scrolling"): both numbers were
        // stale and the conclusion was written for pixels, not DIUs. The user's 1920x1080 at
        // 150% is a 1280x720 DIU window; minus the 24 card margin, the card padding, the 22 DIU
        // title and the docked Dismiss button, the scroller's viewport is about 495 DIU, and 18
        // rows plus two headings need roughly 610. So the panel scrolls, the left column's
        // second heading ("Mouse") lands just above the fold, and its first row falls below it —
        // a heading stranded with nothing under it. ApplyFoldGuard is what fixes that.
        LeftColumn.ItemsSource = Pick(groups, ShortcutGroup.Playback, ShortcutGroup.Mouse);
        RightColumn.ItemsSource = Pick(groups, ShortcutGroup.AudioSubtitles, ShortcutGroup.General);
        _foldPushes.Clear();   // the containers those two lines replaced are gone with their margins

        // Every open starts at the top of the list — a reopened ScrollViewer otherwise keeps the
        // offset the last one left behind, and the fold guard below is the offset-0 fold.
        Scroller.ScrollToTop();

        Visibility = Visibility.Visible;
        // Loaded priority: the containers do not exist until the panel has laid out once, and the
        // guard measures real positions rather than predicting them. The flag suppresses the
        // Scroller.SizeChanged run that the same layout pass would otherwise trigger.
        _foldGuardScheduled = true;
        Dispatcher.BeginInvoke(new Action(RunScheduledFoldGuard), DispatcherPriority.Loaded);
        var dur = TimeSpan.FromMilliseconds(480);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        CardRise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, dur) { EasingFunction = ease });

        Focus();
        DismissButton.Focus();
    }

    /// <summary>Gap left under the fold so a pushed heading is unmistakably below it rather than
    /// exactly on the boundary, where a rounding difference could put it back on screen.</summary>
    private const double FoldGap = 4;

    /// <summary>Every bottom margin this guard has widened, with the value to put back. Cleared
    /// in <see cref="Show"/> because rebinding the columns discards the containers.</summary>
    private readonly List<(ContentPresenter Container, Thickness Original)> _foldPushes = [];

    private bool _applyingFoldGuard;

    /// <summary>True between <see cref="Show"/> queueing the first fold-guard run and that run
    /// executing. See the <c>Scroller.SizeChanged</c> handler for what it suppresses.</summary>
    private bool _foldGuardScheduled;

    /// <summary>The run <see cref="Show"/> queues: clear the flag first, so the guard's own
    /// <c>UpdateLayout</c> re-entry is governed by <see cref="_applyingFoldGuard"/> alone and
    /// nothing can leave the flag stuck (the guard early-returns on a panel closed meanwhile).
    /// </summary>
    private void RunScheduledFoldGuard()
    {
        _foldGuardScheduled = false;
        ApplyFoldGuard();
    }

    /// <summary>
    /// Keeps a group heading with its first row across the scroll fold. A heading that is visible
    /// while the row under it is not reads as an empty section — the user's report — and the fix
    /// is to move the heading BELOW the fold so the whole group scrolls in together.
    ///
    /// <para>Only the fold at scroll offset 0 is guarded, deliberately. The panel is a reference
    /// card that opens at the top and is read from the top; a guard that chased
    /// <c>VerticalOffset</c> would have to re-run and re-lay-out on every wheel notch, and the
    /// heading it pushed would move under the pointer while the user scrolled.</para>
    ///
    /// <para>The push goes on the PREVIOUS group's container, not as top margin on the orphan:
    /// the group's own <c>StackPanel</c> is inside the container whose position is being
    /// measured, so padding it would move the heading and the fold together.</para>
    /// </summary>
    private void ApplyFoldGuard()
    {
        // UpdateLayout below re-enters through Scroller.SizeChanged when the push changes the
        // scrollbar's presence. A closed panel measures nothing worth having (the queued call
        // outlives a quick Escape).
        if (_applyingFoldGuard || !IsOpen)
            return;
        _applyingFoldGuard = true;
        try
        {
            foreach (var (container, original) in _foldPushes)
                container.Margin = original;
            _foldPushes.Clear();
            UpdateLayout();
            // Nothing is below any fold when everything fits.
            if (Scroller.ScrollableHeight == 0)
                return;
            GuardColumn(LeftColumn, "l");
            GuardColumn(RightColumn, "r");
        }
        finally
        {
            _applyingFoldGuard = false;
        }
    }

    private void GuardColumn(ItemsControl column, string tag)
    {
        var count = column.Items.Count;
        for (var i = 1; i < count; i++)
        {
            if (column.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter group
                || column.ItemContainerGenerator.ContainerFromIndex(i - 1) is not ContentPresenter previous)
                continue;
            // First TextBlock in the group template is its title; the first row is the Border the
            // row template puts inside the rows host's first container. Found by walking rather
            // than by name because both come from templates.
            var title = FirstOfType<TextBlock>(group);
            var rows = FirstOfType<ItemsControl>(group);
            var firstRow = rows?.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter row
                ? FirstOfType<Border>(row)
                : null;
            if (title is null || firstRow is null)
                continue;
            // Content coordinates: at offset 0 they are viewport coordinates, which is the only
            // scroll position this guards.
            var titleTop = title.TranslatePoint(new Point(0, 0), Columns).Y;
            var rowBottom = firstRow.TranslatePoint(new Point(0, firstRow.ActualHeight), Columns).Y;
            var viewport = Scroller.ViewportHeight;
            if (titleTop >= viewport || rowBottom <= viewport)
                continue;
            var push = viewport - titleTop + FoldGap;
            _foldPushes.Add((previous, previous.Margin));
            previous.Margin = new Thickness(previous.Margin.Left, previous.Margin.Top,
                previous.Margin.Right, previous.Margin.Bottom + push);
            Diagnostics.AppLog.Detail("shortcuts",
                $"event=fold-guard column={tag} group={title.Text} pushed={push:F0}");
            // Re-laid out before the next group is measured. With two groups per column there is
            // no next one today, but a pushed group moves everything after it and a stale
            // measurement could strand the group it created room for.
            UpdateLayout();
        }
    }

    private static T? FirstOfType<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FirstOfType<T>(child) is { } found)
                return found;
        }
        return null;
    }

    private static List<ShortcutGroupView> Pick(List<ShortcutGroupView> groups, params ShortcutGroup[] wanted)
        => [.. wanted.Select(w => groups.FirstOrDefault(g => g.Title == AppShortcuts.GroupTitle(w)))
                     .OfType<ShortcutGroupView>()];

    public void Hide()
    {
        if (IsOpen)
            Diagnostics.AppLog.Detail("shortcuts", "event=close");
        Visibility = Visibility.Collapsed;
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Hide();

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => Hide();
}
