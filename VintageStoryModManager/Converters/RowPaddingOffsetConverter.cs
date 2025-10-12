using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VintageStoryModManager.Converters;

public class RowPaddingOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: 2 })
        {
            return DependencyProperty.UnsetValue;
        }

        var baseThickness = values[0] switch
        {
            Thickness thickness => thickness,
            string thicknessString when TryParseThickness(thicknessString, culture, out var parsedThickness) => parsedThickness,
            _ => default(Thickness)
        };

        double offset = values[1] switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            string offsetString when double.TryParse(offsetString, NumberStyles.Float, culture, out var parsedOffset) => parsedOffset,
            _ => 0d
        };

        double halfOffset = offset / 2d;

        return new Thickness(
            baseThickness.Left,
            baseThickness.Top + halfOffset,
            baseThickness.Right,
            baseThickness.Bottom + halfOffset);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static bool TryParseThickness(string value, CultureInfo culture, out Thickness thickness)
    {
        thickness = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        double[] numbers = new double[4];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, culture, out numbers[i]))
            {
                return false;
            }
        }

        thickness = parts.Length switch
        {
            1 => new Thickness(numbers[0]),
            2 => new Thickness(numbers[0], numbers[1], numbers[0], numbers[1]),
            4 => new Thickness(numbers[0], numbers[1], numbers[2], numbers[3]),
            _ => default
        };

        return true;
    }
}
