using System;
using System.Globalization;
using System.Windows.Data;

namespace VintageStoryModManager.Converters
{
    /// <summary>
    /// Converts a boolean IsCompactView to a width value for DataGrid columns.
    /// Usage: ConverterParameter="80,40" (normal,compact)
    /// </summary>
    public class CompactViewWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isCompact = value is bool b && b;
            double resolvedWidth = isCompact ? 40.0 : 80.0;

            if (parameter is string param)
            {
                var parts = param.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out double normal) &&
                    double.TryParse(parts[1], out double compact))
                {
                    resolvedWidth = isCompact ? compact : normal;
                }
            }

            // DataGrid column widths use DataGridLength rather than a raw double, so return the
            // appropriate type for the binding target to avoid designer type warnings.
            if (targetType == typeof(System.Windows.Controls.DataGridLength))
            {
                return new System.Windows.Controls.DataGridLength(resolvedWidth);
            }

            return resolvedWidth;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("CompactViewWidthConverter does not support ConvertBack.");
        }
    }
}