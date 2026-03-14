using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class RankColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double score)
            {
                if (double.IsNegativeInfinity(score)) return Brushes.Red;
                if (score > 1000) return new SolidColorBrush(Color.Parse("#1DB954")); // Spotify Green
                if (score > 500) return new SolidColorBrush(Color.Parse("#00A3FF"));  // Orbit Blue
                if (score > 0) return Brushes.White;
                return Brushes.Gray;
            }
            return Brushes.White;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
