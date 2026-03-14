using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Converters;

/// <summary>
/// Universal boolean converter that maps true/false to values based on a parameter.
/// Usage: ConverterParameter='TrueValue|FalseValue'
/// Supports: String, Brush (via Color name), and Visibility (if target is Visibility).
/// </summary>
public class ParameterizedBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b) return null;
        if (parameter is not string paramStr) return b;

        var parts = paramStr.Split('|');
        if (parts.Length < 2) return b;

        var targetVal = b ? parts[0] : parts[1];

        // 1. Target is a Brush/Color
        if (targetType == typeof(IBrush))
        {
            try { return Brush.Parse(targetVal); }
            catch { return Brushes.Gray; }
        }

        // 2. Target is a String
        if (targetType == typeof(string))
        {
            return targetVal;
        }

        return targetVal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
