using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Converts a boolean fraud status to a color (Red for fraud).
    /// </summary>
    public class FraudColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isFraud && isFraud)
            {
                return new SolidColorBrush(Color.Parse("#EF4444")); // Red-500
            }
            return new SolidColorBrush(Color.Parse("#F59E0B")); // Amber-500 (Warning)
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Simple string concatenation converter.
    /// </summary>
    public class StringConcatConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var valStr = value?.ToString() ?? string.Empty;
            var paramStr = parameter?.ToString() ?? string.Empty;
            
            if (value is bool b)
            {
                return b ? paramStr : string.Empty;
            }

            return valStr + paramStr;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
