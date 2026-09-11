using System.Windows;
using System.Windows.Controls;

namespace LightWeaver.Views;

/// <summary>Shared woven-light empty state (Phase 7 M24). Call <see cref="Show"/> when a
/// region comes up empty; collapse it via <see cref="Visibility"/> like the old labels.</summary>
public partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public void Show(string headline, string subline)
    {
        HeadlineText.Text = headline;
        SublineText.Text = subline;
        Visibility = Visibility.Visible;
    }
}
