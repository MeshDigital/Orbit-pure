using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class FlowHealthColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double health)
            {
                if (health >= 0.9) return Brushes.SpringGreen;
                if (health >= 0.7) return Brushes.Aqua;
                if (health >= 0.5) return Brushes.Yellow;
                if (health >= 0.3) return Brushes.Orange;
                return Brushes.Red;
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
