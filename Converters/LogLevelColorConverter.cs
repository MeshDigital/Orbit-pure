using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

public class LogLevelColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string level)
        {
            var upperLevel = level.ToUpperInvariant();
            return upperLevel switch
            {
                "SUCCESS" => Brushes.LimeGreen,
                "INFO" => Brushes.DeepSkyBlue,
                "WARN" or "WARNING" => Brushes.Orange,
                "ERROR" or "FAIL" or "FAILED" => Brushes.Red,
                "DEBUG" => Brushes.Gray,
                _ => Brushes.White
            };
        }
        return Brushes.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
