using System.Globalization;
using System.Windows.Data;
using VintageStoryModManager.ViewModels;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

/// <summary>
///     Selects which download metric to display based on the current auto-load mode.
/// </summary>
public sealed class DownloadMetricDisplayConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 5) return Binding.DoNothing;

        var useRecent = values[0] is bool flag && flag;
        var totalDisplay = values[1] as string;
        var thirtyDayDisplay = values[2] as string;
        var tenDayDisplay = values[3] as string;
        var mode = values[4] as ModDatabaseAutoLoadMode?;

        string? selected;
        if (useRecent && mode.HasValue)
            selected = mode.Value == ModDatabaseAutoLoadMode.DownloadsLastTenDays
                ? tenDayDisplay
                : thirtyDayDisplay;
        else
            selected = totalDisplay;

        if (string.IsNullOrWhiteSpace(selected) || selected == "—") return "—";

        if (useRecent && mode.HasValue)
            switch (mode.Value)
            {
                case ModDatabaseAutoLoadMode.DownloadsLastTenDays:
                    return FormatRecentMetric(selected, " (10 days)");
                case ModDatabaseAutoLoadMode.DownloadsLastThirtyDays:
                    return FormatRecentMetric(selected, " (30 days)");
            }

        return selected;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string FormatRecentMetric(string value, string suffix)
    {
        var trimmed = value.Trim();

        var approxValue = trimmed.StartsWith("≈", StringComparison.Ordinal)
            ? trimmed
            : $"≈{trimmed}";

        if (approxValue.EndsWith(suffix, StringComparison.Ordinal)) return approxValue;

        return approxValue + suffix;
    }
}