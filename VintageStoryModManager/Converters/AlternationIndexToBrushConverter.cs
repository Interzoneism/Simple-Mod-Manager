using System;
using System.Globalization;

namespace VintageStoryModManager.Converters;

public class AlternationIndexToBrushConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && index % 2 == 1)
        {
            return TryGetBrush("Brush.DataGrid.Row.Background.Alternate")
                   ?? TryGetBrush("Brush.ScrollBar.Button")
                   ?? System.Windows.Media.Brushes.Transparent;
        }

        return TryGetBrush("Brush.Panel.Secondary.Background.Solid")
               ?? System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private static System.Windows.Media.Brush? TryGetBrush(object resourceKey)
    {
        return System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush;
    }
}
