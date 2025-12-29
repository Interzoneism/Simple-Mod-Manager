using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace VintageStoryModManager.Converters;

public class ModNameAbbreviationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var letters = new StringBuilder();
        var words = name.Split([' ', '\t', '\r', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var validWordCount = 0;

        foreach (var rawWord in words)
        {
            var word = rawWord.Trim();
            if (string.IsNullOrEmpty(word)) continue;
            if (!char.IsLetter(word[0])) continue;

            letters.Append(char.ToUpperInvariant(word[0]));
            validWordCount++;

            if (validWordCount >= 3)
                break;
        }

        if (letters.Length > 0)
            return letters.ToString();

        var firstLetter = name.FirstOrDefault(char.IsLetter);
        return firstLetter == default ? string.Empty : char.ToUpperInvariant(firstLetter).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class StringNullOrWhiteSpaceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNullOrWhiteSpace = value is not string text || string.IsNullOrWhiteSpace(text);
        var invert = parameter?.ToString()?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true;
        var isVisible = invert ? !isNullOrWhiteSpace : isNullOrWhiteSpace;

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
