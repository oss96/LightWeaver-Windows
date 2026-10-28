using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Pages;

/// <summary>Stand-in for a destination whose v2 screen is not built yet; shows its name.</summary>
public sealed partial class PlaceholderPage : Grid
{
    public PlaceholderPage(string name)
    {
        var title = new TextBlock
        {
            Text = name,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Margin = new Thickness(24, 16, 24, 0),
        };
        AutomationProperties.SetAutomationId(title, "PlaceholderPage");
        Children.Add(title);
    }
}
