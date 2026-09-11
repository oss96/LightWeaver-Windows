using System.Windows;
using System.Windows.Input;
using LightWeaver.Player;
using LightWeaver.Views;

namespace LightWeaver;

public partial class MainWindow
{
    private void HandleStreamShortcut(KeyEventArgs e)
    {
        if (e.Handled || _windowClosing || _app.State == ViewModels.AppState.Playing || IsShortcutsOpen)
            return;
        var modifiers = Keyboard.Modifiers;
        // Bare letters and Shift+letters must remain available to address/search editing.
        if (IsTextEntryFocused() && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!PlayerActionDispatcher.TryResolve(key, modifiers, _settings.KeyBindings, out var action)
            || action != PlayerAction.OpenStream)
            return;
        e.Handled = true;
        if (!e.IsRepeat)
        {
            Diagnostics.AppLog.Detail("shortcut", "event=invoke scope=browse action=OpenStream");
            PlayerActionDispatcher.Dispatch(action, _playerViewModel);
        }
    }

    private void OnStreamToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        var chord = PlayerActionDispatcher.Resolve(_settings.KeyBindings)[PlayerAction.OpenStream];
        NavStream.ToolTip = string.IsNullOrWhiteSpace(chord)
            ? "Stream from URL"
            : $"Stream from URL ({PlayerActionDispatcher.DisplayChord(chord)})";
    }

    private void OnOpenStream(object sender, RoutedEventArgs e)
    {
        // Like Settings, this destination belongs to the browse shell. Do not stop a
        // running video merely to edit an address that might never be submitted.
        if (_windowClosing || _app.State == ViewModels.AppState.Playing)
            return;
        if (_nav.Current?.View is OpenStreamView current)
        {
            current.FocusAddress();
            return;
        }
        var view = new OpenStreamView();
        view.PlayRequested += address =>
        {
            if (!_windowClosing && ReferenceEquals(_nav.Current?.View, view))
                PlayUrl(address.AbsoluteUri, null);
        };
        _nav.Navigate(new ViewModels.NavFrame(view, "Stream from URL", NavStream));
        ShowCurrentFrame();
    }
}
