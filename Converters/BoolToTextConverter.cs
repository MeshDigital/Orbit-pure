using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a boolean to text values for toggle buttons.
/// </summary>
public class BoolToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isCollapsed && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                return isCollapsed ? parts[0] : parts[1];
            }
        }
        return "Toggle";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}