using System.Globalization;
using System.Windows.Data;
using Application = System.Windows.Application;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace VintageStoryModManager.Converters;

public class AlternationIndexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && index % 2 == 1)
            return TryGetBrush("Brush.DataGrid.Row.Background.Alternate")
                   ?? TryGetBrush("Brush.ScrollBar.Button")
                   ?? Brushes.Transparent;

        return TryGetBrush("Brush.Panel.Secondary.Background.Solid")
               ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static Brush? TryGetBrush(object resourceKey)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush;
    }
}