using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class LedColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "●" => Brushes.Cyan,         // Active/Processing
                    "✅" => Brushes.LimeGreen,     // Good/Secure
                    "⚠️" => Brushes.Orange,       // Warning/Suspect
                    "○" => Brushes.DimGray,        // Idle/Off
                    _ => Brushes.Gray
                };
            }
            // Fallback for non-string input
            return Brushes.Transparent;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
