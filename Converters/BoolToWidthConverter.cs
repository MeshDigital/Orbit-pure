using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a boolean to a width value for collapsible panels.
/// </summary>
public class BoolToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isCollapsed && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2 && double.TryParse(parts[0], out var collapsedWidth) && double.TryParse(parts[1], out var expandedWidth))
            {
                return isCollapsed ? collapsedWidth : expandedWidth;
            }
        }
        return 300.0; // Default
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}