using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>The design's text field around a bare TextBox or PasswordBox: field fill, 1 px stroke
/// and bottom line; focused, a darker fill and a 2 px accentFg bottom line; in error, the bottom
/// line turns the error colour.</summary>
internal sealed class FieldFrame : Grid
{
    private readonly Border _chrome;
    private readonly Border _bottom;
    private bool _focused;
    private bool _hasError;

    public FieldFrame()
    {
        _chrome = new Border { CornerRadius = Ui.Resource<CornerRadius>("LwControlRadius"), BorderThickness = new Thickness(1), BorderBrush = Ui.Resource<Brush>("LwFieldBorderBrush") };
        _bottom = new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(0, 0, 4, 4) };
        Children.Add(_chrome);
        Children.Add(_bottom);
        GotFocus += (_, _) => { _focused = true; Update(); };
        LostFocus += (_, _) => { _focused = false; Update(); };
        Update();
    }

    public bool HasError
    {
        get => _hasError;
        set { _hasError = value; Update(); }
    }

    private void Update()
    {
        _chrome.Background = Ui.Resource<Brush>(_focused ? "LwFieldFocusBrush" : "LwFieldBrush");
        _bottom.Background = Ui.Resource<Brush>(_hasError ? "LwErrorBrush" : _focused ? "LwAccentFgBrush" : "LwFieldBottomBrush");
        _bottom.Height = _focused || _hasError ? 2 : 1;
    }
}
