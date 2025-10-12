using System;
using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

/// <summary>
/// Selects which download metric to display based on the current auto-load mode.
/// </summary>
public sealed class DownloadMetricDisplayConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 3)
        {
            return Binding.DoNothing;
        }

        bool useRecent = values[0] is bool flag && flag;
        string? totalDisplay = values[1] as string;
        string? recentDisplay = values[2] as string;

        string? selected = useRecent ? recentDisplay : totalDisplay;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return "—";
        }

        return selected;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
