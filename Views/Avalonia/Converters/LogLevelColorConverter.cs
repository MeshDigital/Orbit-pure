using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class LogLevelColorConverter : IValueConverter
    {
        public static readonly LogLevelColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is ForensicLevel level)
            {
                return level switch
                {
                    ForensicLevel.Error => Brushes.Red,
                    ForensicLevel.Warning => Brushes.Orange,
                    ForensicLevel.Info => Brushes.LightGray,
                    ForensicLevel.Debug => Brushes.Gray,
                    ForensicLevel.Success => Brushes.LimeGreen,
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
}
