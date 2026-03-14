using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

public class BoolToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            if (boolValue)
            {
                if (parameter is string paramStr && double.TryParse(paramStr, NumberStyles.Any, culture, out var width))
                {
                    return width;
                }
                if (parameter is double dWidth) return dWidth;
                return double.NaN; // Auto
            }
            return 0.0;
        }
        return double.NaN;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
