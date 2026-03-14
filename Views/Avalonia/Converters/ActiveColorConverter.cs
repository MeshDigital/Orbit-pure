using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Returns a vibrant "active" color when true, and a muted color when false.
    /// Used for toggle buttons in the Unified Command Bar.
    /// </summary>
    public class ActiveColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue)
            {
                // Vibrant teal/green for active state
                return Color.Parse("#4EC9B0");
            }
            
            // Muted gray for inactive state
            return Color.Parse("#A0A0A0");
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
