using System.Windows;
using System.Windows.Controls;

namespace LightWeaver.Views;

/// <summary>
/// Builds skeleton placeholders shaped like the real cards (Phase 7 M25). Blocks use
/// the LwSkeletonBlock style (storm-2, ambient breathe); dimensions mirror the shared
/// card templates (poster 152x228, landscape 292x164) so the real cards land in place.
/// The marker TextBlock carries the AutomationId — Borders/Panels have no UIA peer.
/// </summary>
public static class SkeletonFactory
{
    /// <summary>
    /// How long a fetch may run before its indicator is revealed, for any indicator that
    /// appears inside an ALREADY-PAINTED page (a rail filling in, a load-more append, a
    /// search refinement). First paint of an empty surface does not wait - a blank screen
    /// held for a quarter second reads as broken, which is why the Home/Library/Genres
    /// skeletons stay immediate.
    ///
    /// 250 ms was set for Advanced Search first and generalized here (2026-08-02): it sits
    /// above LAN response times, so on this network the indicator never flashes, and below
    /// the ~400 ms at which a still screen starts reading as stuck. A C# const rather than a
    /// XAML duration token because every consumer is a code-behind timer - a resource key
    /// nothing binds to would be dead weight.
    /// </summary>
    public const int RevealDelayMs = 250;

    /// <summary>
    /// A horizontal rail: N cards, optionally under a placeholder title bar. Pass
    /// <paramref name="withTitle"/> = false when the real header is already on screen and
    /// only the cards below it are still loading - the marker id then rides an invisible
    /// zero-height block instead of the title.
    /// </summary>
    public static StackPanel Rail(FrameworkElement resources, int cards, bool landscape, string automationId,
                                  bool withTitle = true)
    {
        var rail = new StackPanel { Margin = new Thickness(0, withTitle ? 26 : 0, 0, 0) };
        // Real (transparent) text: an EMPTY TextBlock is pruned from the UIA control
        // view and the marker id becomes unfindable (seen live).
        var title = new TextBlock
        {
            Text = "Loading",
            Foreground = System.Windows.Media.Brushes.Transparent,
            Width = withTitle ? 170 : 0,
            Height = withTitle ? 20 : 0,
            Background = withTitle ? (System.Windows.Media.Brush)resources.FindResource("LwStorm2Brush") : null,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, withTitle ? 14 : 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(title, automationId);
        rail.Children.Add(title);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < cards; i++)
            row.Children.Add(Card(resources, landscape));
        rail.Children.Add(row);
        return rail;
    }

    /// <summary>
    /// A wrapping grid of genre-tile skeletons. Shaped to the real tile template
    /// (200x90, margin 6, LwR2 - GenresView.xaml) rather than to a poster card, because
    /// showing the wrong silhouette is the one thing a skeleton must not do.
    /// </summary>
    public static StackPanel Tiles(FrameworkElement resources, int tiles, string automationId)
    {
        var style = (Style)resources.FindResource("LwSkeletonBlock");
        var radius = (CornerRadius)resources.FindResource("LwR2");
        var host = new StackPanel { Margin = new Thickness(18, 10, 18, 10) };   // matches GenreGrid.Padding
        var marker = new TextBlock { Text = "Loading", Foreground = System.Windows.Media.Brushes.Transparent, Height = 0 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(marker, automationId);
        host.Children.Add(marker);
        var wrap = new WrapPanel();
        for (var i = 0; i < tiles; i++)
            wrap.Children.Add(new Border
            {
                Style = style,
                Width = 200,
                Height = 90,
                Margin = new Thickness(6),
                CornerRadius = radius,
            });
        host.Children.Add(wrap);
        return host;
    }

    /// <summary>
    /// N full-width row bars, for list-shaped content (the detail view's inline episode
    /// list). Episode rows are landscape rows, not cards, so the card rail would be the
    /// wrong shape here.
    /// </summary>
    public static StackPanel Rows(FrameworkElement resources, int rows, string automationId)
    {
        var style = (Style)resources.FindResource("LwSkeletonBlock");
        // LwR3, matching DetailEpisodeRow's own corner - NOT the LwR2 the card skeletons use.
        var radius = (CornerRadius)resources.FindResource("LwR3");
        var host = new StackPanel();
        var marker = new TextBlock { Text = "Loading", Foreground = System.Windows.Media.Brushes.Transparent, Height = 0 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(marker, automationId);
        host.Children.Add(marker);
        for (var i = 0; i < rows; i++)
            host.Children.Add(new Border
            {
                Style = style,
                // Derived from DetailEpisodeRow rather than guessed: thumb 74 + padding 2x9
                // + border 2x1 = 94. The first version used 72 and rendered visibly shorter
                // than the rows that replaced it (measured on a slow-load capture,
                // 2026-08-02) - which is the one thing a skeleton must not do.
                Height = 94,
                // DetailEpisodeItem: Margin 0,0,0,10 plus its 1.5 px transparent focus border.
                Margin = new Thickness(0, 0, 0, 13),
                CornerRadius = radius,
            });
        return host;
    }

    /// <summary>A wrapping grid of poster skeletons. Marker id on an invisible header.</summary>
    public static StackPanel Grid(FrameworkElement resources, int cards, string automationId)
    {
        var host = new StackPanel();
        var marker = new TextBlock { Text = "Loading", Foreground = System.Windows.Media.Brushes.Transparent, Height = 0 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(marker, automationId);
        host.Children.Add(marker);
        var wrap = new WrapPanel();
        for (var i = 0; i < cards; i++)
            wrap.Children.Add(Card(resources, landscape: false));
        host.Children.Add(wrap);
        return host;
    }

    private static StackPanel Card(FrameworkElement resources, bool landscape)
    {
        var style = (Style)resources.FindResource("LwSkeletonBlock");
        // P10 M7: all three radii come off the ladder by name (code cannot use
        // StaticResource). They used to be 8 / 3 / 3 hard-coded, which both drifted from
        // LwSkeletonBlock's own radius - the style these very Borders carry - and rendered
        // the two text-line bars near-square next to the rounded poster block above them,
        // measured at 3x on a slow-load capture. LwR1 clamps to half-height on the 13 px
        // and 10 px bars, so they come out as stadiums, which is what a text placeholder
        // should look like.
        var radius = (CornerRadius)resources.FindResource("LwR1");
        var card = new StackPanel { Width = landscape ? 292 : 152, Margin = new Thickness(0, 0, 16, 10) };
        card.Children.Add(new Border
        {
            Style = style,
            Height = landscape ? 164 : 228,
            CornerRadius = radius,
        });
        card.Children.Add(new Border
        {
            Style = style,
            Height = 13,
            Width = (landscape ? 292 : 152) * 0.72,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0),
            CornerRadius = radius,
        });
        card.Children.Add(new Border
        {
            Style = style,
            Height = 10,
            Width = (landscape ? 292 : 152) * 0.45,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 0),
            CornerRadius = radius,
        });
        return card;
    }
}
