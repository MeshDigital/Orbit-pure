using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a ratio (0.0-1.0) to a width in pixels for progress bars.
/// </summary>
public class RatioToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ratio)
        {
            // Assuming the progress bar width is 50, convert ratio to pixels
            return ratio * 50.0;
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}