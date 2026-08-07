using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

/// <summary>
///     Scales a double value by a multiplier from the parameter.
/// </summary>
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

/// <summary>
///     Scales a Thickness value by a multiplier from the parameter.
/// </summary>
public class ScaledThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double scale) return Binding.DoNothing;

        var baseThickness = ResolveThickness(parameter, culture);

        return new Thickness(
            baseThickness.Left * scale,
            baseThickness.Top * scale,
            baseThickness.Right * scale,
            baseThickness.Bottom * scale);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static Thickness ResolveThickness(object parameter, CultureInfo culture)
    {
        if (parameter is Thickness thickness) return thickness;

        if (parameter is string thicknessString)
        {
            var converter = new ThicknessConverter();
            if (converter.ConvertFromString(null, culture, thicknessString) is Thickness parsedThickness)
                return parsedThickness;
        }

        return new Thickness();
    }
}
