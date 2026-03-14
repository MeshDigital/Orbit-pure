using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class PercentageToAngleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is float floatVal)
            {
                // Auto-detect if value is 0.0-1.0 or 0-100
                if (floatVal <= 1.05f) return floatVal * 360.0;
                return floatVal * 3.6;
            }
            if (value is double percentage)
            {
                if (percentage <= 1.05) return percentage * 360.0;
                return percentage * 3.6;
            }
            return 0;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
