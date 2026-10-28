using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace LightWeaver.WinUI.Controls;

/// <summary>A round user avatar: the user's picture when there is one, else initials on a colour
/// picked from the name, so a user keeps the same colour everywhere.</summary>
internal sealed class Avatar : Grid
{
    private static readonly Color[] Solids = [Color.FromArgb(255, 0x3D, 0x55, 0x68), Color.FromArgb(255, 0x5A, 0x5A, 0x3C), Color.FromArgb(255, 0x4D, 0x3D, 0x68), Color.FromArgb(255, 0x3C, 0x5A, 0x50), Color.FromArgb(255, 0x68, 0x4D, 0x3D)];
    private readonly TextBlock _initials;

    public Avatar(string? name, double size)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size / 2);
        Background = BrushFor(name);
        _initials = new TextBlock
        {
            Text = Initials(name),
            FontSize = Math.Round(size * 0.33),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_initials);
    }

    public void SetPicture(ImageSource picture)
    {
        Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill };
        _initials.Visibility = Visibility.Collapsed;
    }

    /// <summary>The design's avatar gradient (linear-gradient 140deg #6b4d57 to #3c3036) or one of
    /// the muted solids, chosen by a stable hash of the name.</summary>
    public static Brush BrushFor(string? name)
    {
        var hash = 0;
        foreach (var c in (name ?? "").ToUpperInvariant()) hash = unchecked(hash * 31 + c);
        var index = (int)((uint)hash % (uint)(Solids.Length + 1));
        if (index == 0)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0.047, -0.04),
                EndPoint = new Point(0.953, 1.04),
                GradientStops = { new GradientStop { Color = Color.FromArgb(255, 0x6B, 0x4D, 0x57), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 0x3C, 0x30, 0x36), Offset = 1 } },
            };
        }
        return new SolidColorBrush(Solids[index - 1]);
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}"
            : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
}
