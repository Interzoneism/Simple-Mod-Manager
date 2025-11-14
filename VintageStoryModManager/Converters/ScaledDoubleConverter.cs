using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

public class ScaledDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double scale) return Binding.DoNothing;

        var baseValue = ResolveDouble(parameter, culture);
        return baseValue * scale;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static double ResolveDouble(object parameter, CultureInfo culture)
    {
        if (parameter is double doubleValue) return doubleValue;

        if (parameter is string doubleString &&
            double.TryParse(doubleString, NumberStyles.Float, culture, out var parsedValue)) return parsedValue;

        return 0d;
    }
}