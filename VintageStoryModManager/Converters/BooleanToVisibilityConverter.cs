using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VintageStoryModManager.Converters;

/// <summary>
/// Converts boolean values to <see cref="Visibility"/> states with optional inversion.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Gets or sets a value indicating whether the conversion should be inverted.
    /// </summary>
    public bool IsInverted { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool boolean && boolean;

        if (IsInverted)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Visibility visibility)
        {
            return System.Windows.Data.Binding.DoNothing;
        }

        bool flag = visibility == Visibility.Visible;

        if (IsInverted)
        {
            flag = !flag;
        }

        return flag;
    }
}
