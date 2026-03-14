using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Models
{
    // Keeping inside Models namespace for compatibility with existing XAML reference
    // "xmlns:models="clr-namespace:SLSKDONET.Models""
    public class SizeConverter : IValueConverter
    {
        public static readonly SizeConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long size)
            {
                return FormatSize(size);
            }
            if (value is int sizeInt)
            {
                return FormatSize(sizeInt);
            }
            if (value is double sizeDouble)
            {
                 return FormatSize((long)sizeDouble);
            }
            return "0 B";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
        
        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
    }
}
