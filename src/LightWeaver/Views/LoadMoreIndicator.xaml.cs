using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LightWeaver.Views;

/// <summary>
/// The floating "Loading more…" pill for infinite-scroll grids (2026-08-02). Shared by
/// LibraryView, SectionView and AdvancedSearchView.
///
/// The <see cref="SkeletonFactory.RevealDelayMs"/> gate lives INSIDE the control, so a call
/// site is two lines - <c>Show()</c> before the fetch, <c>Hide()</c> in a finally - and none
/// of the three has to re-implement the timer or get the cancellation wrong.
/// </summary>
public partial class LoadMoreIndicator : UserControl
{
    private CancellationTokenSource? _reveal;

    public LoadMoreIndicator()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Arm the reveal. Nothing appears yet: if the page lands inside the delay the pill is
    /// never shown at all, which is the point - on a fast LAN a flashed spinner is worse
    /// than no spinner.
    /// </summary>
    public void Show()
    {
        _reveal?.Cancel();
        var cts = new CancellationTokenSource();
        _reveal = cts;
        var token = cts.Token;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(SkeletonFactory.RevealDelayMs, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!token.IsCancellationRequested)
                Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Cancel a pending reveal and hide. Safe to call when nothing was ever shown - that is
    /// the normal fast-LAN path. Call sites put this in a <c>finally</c> so an exception or
    /// a superseded (stale-generation) append cannot leave the pill latched on.
    /// </summary>
    public void Hide()
    {
        _reveal?.Cancel();
        _reveal = null;
        Visibility = Visibility.Collapsed;
    }
}
