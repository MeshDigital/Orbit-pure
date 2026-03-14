using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Converts boolean to color (e.g., for status indicators)
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Brushes.Green : Brushes.Gray;
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
