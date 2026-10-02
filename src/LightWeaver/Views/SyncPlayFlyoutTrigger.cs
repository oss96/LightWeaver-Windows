using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LightWeaver.Views;

// Popup dismisses an outside press before ToggleButton sees it. Keep that press's
// original state so clicking its own trigger closes rather than immediately reopens.
internal sealed class SyncPlayFlyoutTrigger(Popup popup, ToggleButton trigger) : IDisposable
{
    private bool _dismissCurrentPress;

    public void Attach() => InputManager.Current.PreProcessInput += OnInput;

    public bool ConsumeDismissal()
    {
        var dismiss = _dismissCurrentPress;
        _dismissCurrentPress = false;
        return dismiss;
    }

    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not MouseButtonEventArgs { ChangedButton: MouseButton.Left } mouse)
            return;
        if (mouse.RoutedEvent == Mouse.PreviewMouseUpEvent
            || mouse.RoutedEvent == Mouse.PreviewMouseUpOutsideCapturedElementEvent)
            _dismissCurrentPress = false;
        else if (!_dismissCurrentPress && (mouse.RoutedEvent == Mouse.PreviewMouseDownEvent
            || mouse.RoutedEvent == Mouse.PreviewMouseDownOutsideCapturedElementEvent))
            _dismissCurrentPress = popup.IsOpen && trigger.IsVisible
                && new Rect(trigger.RenderSize).Contains(mouse.GetPosition(trigger));
    }

    public void Dispose()
    {
        InputManager.Current.PreProcessInput -= OnInput;
        _dismissCurrentPress = false;
    }
}
