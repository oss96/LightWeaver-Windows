using LightWeaver.Localization;
using System.Windows;
using System.Windows.Controls;
using LightWeaver.Player;

namespace LightWeaver.Views;

public partial class OpenStreamView : UserControl
{
    public event Action<Uri>? PlayRequested;

    public OpenStreamView()
    {
        InitializeComponent();
        LiveText.Watch(this);
        Loaded += (_, _) => StreamUrlBox.Focus();
        // Navigation history holds views in memory; do not keep copied signed links there.
        Unloaded += (_, _) => StreamUrlBox.Clear();
    }

    public void FocusAddress() => StreamUrlBox.Focus();

    private void OnAddressKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        OnPlay(sender, e);
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        if (StreamUrlError is not null)
            StreamUrlError.Visibility = Visibility.Collapsed;
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (!StreamAddress.TryParse(StreamUrlBox.Text, out var address))
        {
            LiveText.Bind(StreamUrlError, "Text", () => L.Text("OpenStreamView.EnterAFullHttpOrHttpsUrl.aa9a2cb3"));
            StreamUrlError.Visibility = Visibility.Visible;
            return;
        }
        StreamUrlBox.Clear();
        PlayRequested?.Invoke(address!);
    }
}
