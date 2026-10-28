using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>The app's ContentDialog: its default button fills with <c>LwAccentBrush</c> and white
/// text. The stock <c>AccentButtonStyle</c> it gives that button takes its brushes from WinUI's own
/// theme dictionaries ahead of the app's merged ones, so the dialog carries the overrides in its
/// own theme dictionaries, where the button looks first.</summary>
internal sealed partial class LwDialog : ContentDialog
{
    public LwDialog()
    {
        Resources.ThemeDictionaries["Default"] = AccentButtonBrushes();
        Resources.ThemeDictionaries["Dark"] = AccentButtonBrushes();
    }

    private static ResourceDictionary AccentButtonBrushes()
    {
        var fill = Ui.Resource<Brush>("LwAccentBrush");
        var text = Ui.Resource<Brush>("LwTextBrush");
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return new ResourceDictionary
        {
            ["AccentButtonBackground"] = fill,
            ["AccentButtonBackgroundPointerOver"] = Ui.Resource<Brush>("LwAccentHoverBrush"),
            ["AccentButtonBackgroundPressed"] = fill,
            ["AccentButtonForeground"] = text,
            ["AccentButtonForegroundPointerOver"] = text,
            ["AccentButtonForegroundPressed"] = Ui.Resource<Brush>("LwText2Brush"),
            ["AccentButtonBorderBrush"] = none,
            ["AccentButtonBorderBrushPointerOver"] = none,
            ["AccentButtonBorderBrushPressed"] = none,
        };
    }
}
