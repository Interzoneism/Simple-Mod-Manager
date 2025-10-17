using System;
using System.Globalization;
using System.Windows.Data;

namespace VintageStoryModManager.Converters;

public class DoubleOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double baseValue = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            null => 0d,
            _ => System.Convert.ToDouble(value, culture)
        };

        double offset = parameter switch
        {
            double d => d,
            float f => f,
            int i => i,
            null => 0d,
            _ => System.Convert.ToDouble(parameter, culture)
        };

        double result = baseValue + offset;

        if (targetType == typeof(string))
        {
            return result.ToString(culture);
        }

        if (targetType == typeof(int))
        {
            return (int)Math.Round(result);
        }

        if (targetType == typeof(float))
        {
            return (float)result;
        }

        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
