using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace VintageStoryModManager.Converters;

/// <summary>
///     Converts boolean values to <see cref="Visibility" /> states with optional inversion.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     Gets or sets a value indicating whether the conversion should be inverted.
    /// </summary>
    public bool IsInverted { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether to use <see cref="Visibility.Hidden"/> instead of
    ///     <see cref="Visibility.Collapsed"/> when the value is false.
    ///     This improves performance for large controls by keeping them in the visual tree.
    /// </summary>
    public bool UseHidden { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool boolean && boolean;

        if (IsInverted) flag = !flag;

        return flag ? Visibility.Visible : (UseHidden ? Visibility.Hidden : Visibility.Collapsed);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Visibility visibility) return Binding.DoNothing;

        // When UseHidden is true, both Collapsed and Hidden are treated as false (not visible)
        var flag = visibility == Visibility.Visible;

        if (IsInverted) flag = !flag;

        return flag;
    }
}