using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LightWeaver;

/// <summary>Visible when the bound numeric value is &gt; 0, collapsed otherwise.</summary>
public sealed class PositiveToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture) > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
