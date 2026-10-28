using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Controls;

/// <summary>An item in one of the top bar's glass groups (Home | Libraries | Playlists, and
/// Downloads | SyncPlay | Settings): an icon over a short label, a selected state with the filled
/// icon and the accent bar, and an optional count badge. Template in Theme/Styles.xaml.</summary>
public sealed partial class BarItem : Button
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(BarItem), new PropertyMetadata(""));
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(BarItem), new PropertyMetadata(""));
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(BarItem), new PropertyMetadata(false, (d, _) => ((BarItem)d).UpdateStates()));
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(BarItem), new PropertyMetadata("", (d, _) => ((BarItem)d).UpdateBadge()));
    public static readonly DependencyProperty BadgeVisibilityProperty =
        DependencyProperty.Register(nameof(BadgeVisibility), typeof(Visibility), typeof(BarItem), new PropertyMetadata(Visibility.Collapsed));

    public BarItem() => DefaultStyleKey = typeof(BarItem);

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public Visibility BadgeVisibility { get => (Visibility)GetValue(BadgeVisibilityProperty); private set => SetValue(BadgeVisibilityProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates();
    }

    private void UpdateStates() => VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", false);

    private void UpdateBadge() => BadgeVisibility = string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
}
