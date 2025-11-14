using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

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