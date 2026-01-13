using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace VintageStoryModManager.Converters;

/// <summary>
/// Inverts a boolean value.
/// </summary>
public class InvertBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }
}

/// <summary>
/// Converts a null value to Visibility.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter?.ToString()?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true;
        var isVisible = value != null;

        if (invert)
            isVisible = !isVisible;

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}


/// <summary>
/// Converts a selected state to a border brush.
/// </summary>
public class SelectedToBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isSelected = value is bool boolValue && boolValue;
        return isSelected
            ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) // Blue
            : new SolidColorBrush(Color.FromArgb(13, 161, 161, 170)); // Transparent-ish
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Compares a value to a parameter for equality.
/// </summary>
public class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked)
        {
            return parameter;
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts collection count to visibility (visible if count > 0).
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter?.ToString()?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true;
        var count = value switch
        {
            int i => i,
            ICollection c => c.Count,
            _ => 0
        };

        var isVisible = count > 0;
        if (invert)
            isVisible = !isVisible;

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Checks if an item is contained in a collection.
/// Used with MultiBinding: values[0] = item, values[1] = collection.
/// </summary>
public class ContainsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;

        var item = values[0];
        var collection = values[1];

        if (collection == null)
            return false;

        // Try to use IList.Contains for better performance
        if (collection is IList list)
        {
            return list.Contains(item);
        }

        // Fallback to enumeration for other IEnumerable types
        if (collection is IEnumerable enumerable)
        {
            foreach (var obj in enumerable)
            {
                if (Equals(obj, item))
                    return true;
            }
        }

        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a boolean to a background brush color (for favorite button styling).
/// </summary>
public class BooleanToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value is bool boolValue && boolValue;
        return isTrue
            ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) // Warning/Gold color
            : new SolidColorBrush(Color.FromRgb(9, 9, 11)); // Dark background
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a resource key string to a Geometry resource.
/// Used to dynamically load icon geometries from resource dictionary.
/// </summary>
public class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string iconKey && !string.IsNullOrEmpty(iconKey))
        {
            // Try to find the resource in the application resources
            if (Application.Current.TryFindResource(iconKey) is Geometry geometry)
            {
                return geometry;
            }
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a non-empty string into an <see cref="ImageSource"/>.
/// Returns <c>null</c> when the input is null, whitespace, or cannot be converted.
/// Uses proper caching to prevent image flashing and re-loading.
/// </summary>
public class StringToImageSourceConverter : IValueConverter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<BitmapImage>> _imageCache = new();
    private static readonly object _cleanupLock = new();
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private const int MaxCacheSize = 500;
    private const int CleanupIntervalMinutes = 5;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        // Periodic cleanup of dead references
        CleanupCacheIfNeeded();

        // Try to get cached image first
        if (_imageCache.TryGetValue(path, out var weakRef) && weakRef.TryGetTarget(out var cachedImage))
        {
            return cachedImage;
        }

        try
        {
            var uri = new Uri(path, UriKind.RelativeOrAbsolute);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Prevent re-decoding and use WPF's internal cache to avoid flashing
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();

            if (bitmap.CanFreeze)
                bitmap.Freeze();

            // Cache the bitmap using a weak reference to allow GC when needed
            _imageCache[path] = new WeakReference<BitmapImage>(bitmap);

            return bitmap;
        }
        catch (Exception ex)
        {
            // Log the error for debugging but don't crash the UI
            System.Diagnostics.Debug.WriteLine($"[StringToImageSourceConverter] Failed to load image from '{path}': {ex.Message}");
            return null;
        }
    }

    private static void CleanupCacheIfNeeded()
    {
        // Only check cleanup interval from one thread at a time
        if (!Monitor.TryEnter(_cleanupLock))
            return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCleanup).TotalMinutes < CleanupIntervalMinutes)
                return;

            _lastCleanup = now;

            // Create snapshot to avoid race conditions during enumeration
            var snapshot = _imageCache.ToArray();

            // Remove dead weak references to prevent dictionary from growing unbounded
            foreach (var kvp in snapshot)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    _imageCache.TryRemove(kvp.Key, out _);
                }
            }

            // If cache still exceeds limit after cleanup, remove half the entries
            // Note: ConcurrentDictionary doesn't guarantee enumeration order,
            // but WeakReferences ensure proper GC regardless of removal order
            if (_imageCache.Count > MaxCacheSize)
            {
                var currentSnapshot = _imageCache.Keys.ToArray();
                var toRemove = currentSnapshot.Length / 2;
                for (var i = 0; i < toRemove && i < currentSnapshot.Length; i++)
                {
                    _imageCache.TryRemove(currentSnapshot[i], out _);
                }
            }
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Multi-value converter for "No results" visibility.
/// Shows "No results" only when: not searching AND count is zero.
/// values[0] = IsSearching (bool), values[1] = count (int or ICollection)
/// </summary>
public class NoResultsVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return Visibility.Collapsed;

        var isSearching = values[0] is bool b && b;
        var count = values[1] switch
        {
            int i => i,
            ICollection c => c.Count,
            _ => 0
        };

        // Only show "No results" when not searching AND count is zero
        return !isSearching && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Multi-value converter for favorite button visibility.
/// Shows button when: card is hovered OR mod is favorited.
/// values[0] = IsMouseOver (bool), values[1] = ModId (object), values[2] = FavoriteMods collection
/// </summary>
public class FavoriteButtonVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] == DependencyProperty.UnsetValue ||
            values[1] == DependencyProperty.UnsetValue || values[2] == DependencyProperty.UnsetValue)
            return Visibility.Collapsed;

        var isMouseOver = values[0] is bool b && b;
        var modId = values[1];
        var favoriteMods = values[2];

        // Check if mod is favorited
        var isFavorited = false;
        if (favoriteMods is IList list && modId != null)
        {
            isFavorited = list.Contains(modId);
        }
        else if (favoriteMods is IEnumerable enumerable && modId != null)
        {
            foreach (var obj in enumerable)
            {
                if (Equals(obj, modId))
                {
                    isFavorited = true;
                    break;
                }
            }
        }

        // Show button when hovered OR favorited
        return isMouseOver || isFavorited ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Multi-value converter for install button visibility.
/// Shows button when the card is hovered or the mod is already installed.
/// values[0] = IsMouseOver (bool), values[1] = IsInstalled (bool)
/// </summary>
public class InstallButtonVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue ||
            values[1] == DependencyProperty.UnsetValue)
            return Visibility.Collapsed;

        var isMouseOver = values[0] is bool b && b;
        var isInstalled = values[1] is bool installed && installed;

        // Show button when hovered or when installed so the checkmark remains visible
        return isMouseOver || isInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Multi-value converter for favorite button visibility (bool result).
/// Returns true when: card is hovered OR mod is favorited.
/// values[0] = IsMouseOver (bool), values[1] = ModId (object), values[2] = FavoriteMods collection
/// </summary>
public class FavoriteButtonShouldShowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] == DependencyProperty.UnsetValue ||
            values[1] == DependencyProperty.UnsetValue || values[2] == DependencyProperty.UnsetValue)
            return false;

        var isMouseOver = values[0] is bool b && b;
        var modId = values[1];
        var favoriteMods = values[2];

        // Check if mod is favorited
        var isFavorited = false;
        if (favoriteMods is IList list && modId != null)
        {
            isFavorited = list.Contains(modId);
        }
        else if (favoriteMods is IEnumerable enumerable && modId != null)
        {
            foreach (var obj in enumerable)
            {
                if (Equals(obj, modId))
                {
                    isFavorited = true;
                    break;
                }
            }
        }

        // Return true when hovered OR favorited
        return isMouseOver || isFavorited;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Multi-value converter for install button visibility (bool result).
/// Returns true when the card is hovered or the mod is already installed.
/// values[0] = IsMouseOver (bool), values[1] = IsInstalled (bool)
/// </summary>
public class InstallButtonShouldShowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue ||
            values[1] == DependencyProperty.UnsetValue)
            return false;

        var isMouseOver = values[0] is bool b && b;
        var isInstalled = values[1] is bool installed && installed;

        // Return true when hovered or when installed so the checkmark remains visible
        return isMouseOver || isInstalled;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Extracts the category name from a CategoryGroupKey.
/// The CategoryGroupKey format is "{Order:D10}|{CategoryName}".
/// </summary>
public class CategoryNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string groupKey || string.IsNullOrEmpty(groupKey))
            return string.Empty;

        // The format is "{Order:D10}|{CategoryName}"
        var pipeIndex = groupKey.IndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < groupKey.Length - 1)
            return groupKey.Substring(pipeIndex + 1);

        return groupKey;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
