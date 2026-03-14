using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Converts window width to visibility based on minimum width threshold.
/// Returns true if width >= threshold, false otherwise.
/// </summary>
public class WidthToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double width && parameter is string minWidthStr)
        {
            if (double.TryParse(minWidthStr, out double minWidth))
            {
                return width >= minWidth;
            }
        }
        
        // Default to visible if parsing fails
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException("WidthToVisibilityConverter does not support two-way binding");
    }
}
