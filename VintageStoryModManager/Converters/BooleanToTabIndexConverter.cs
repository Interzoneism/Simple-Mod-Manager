using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

/// <summary>
///     Converts between a boolean value and a tab index (0 or 1).
/// </summary>
public sealed class BooleanToTabIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool flag) return flag ? 1 : 0;

        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index) return index == 1;

        if (value is string text && int.TryParse(text, NumberStyles.Integer, culture, out var parsed))
            return parsed == 1;

        return Binding.DoNothing;
    }
}