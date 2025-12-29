using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VintageStoryModManager.Converters;

public class ThumbnailLayoutConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string target)
            return DependencyProperty.UnsetValue;

        var aspectRatio = GetAspectRatio(values);
        if (double.IsNaN(aspectRatio))
            return System.Windows.Data.Binding.DoNothing;

        var layout = Classify(aspectRatio);

        return target switch
        {
            "RowSpan" => layout == ThumbnailLayout.Square ? 2 : 1,
            "Stretch" => layout == ThumbnailLayout.Square ? Stretch.UniformToFill : Stretch.Uniform,
            "VerticalAlignment" => layout == ThumbnailLayout.Wide ? VerticalAlignment.Center : VerticalAlignment.Top,
            _ => DependencyProperty.UnsetValue
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static double GetAspectRatio(IReadOnlyList<object> values)
    {
        if (values.Count < 2)
            return double.NaN;

        var width = GetDouble(values[0]);
        var height = GetDouble(values[1]);

        return width > 0 && height > 0 ? width / height : double.NaN;
    }

    private static double GetDouble(object value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => double.NaN
        };
    }

    private static ThumbnailLayout Classify(double aspectRatio)
    {
        if (double.IsNaN(aspectRatio))
            return ThumbnailLayout.Standard;

        if (aspectRatio <= 1.1)
            return ThumbnailLayout.Square;

        return aspectRatio <= 1.7 ? ThumbnailLayout.Standard : ThumbnailLayout.Wide;
    }

    private enum ThumbnailLayout
    {
        Square,
        Standard,
        Wide
    }
}
