using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a color string (hex) to a SolidColorBrush.
/// </summary>
public class ColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorString)
        {
            try
            {
                var color = Color.Parse(colorString);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Gray);
            }
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}