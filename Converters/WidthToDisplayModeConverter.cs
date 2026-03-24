using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a double width to a SplitViewDisplayMode for automatic sidebar responsiveness.
/// Usage: Converter={StaticResource WidthToDisplayModeConverter}, ConverterParameter=1100
/// </summary>
public class WidthToDisplayModeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double width && parameter is string paramStr && double.TryParse(paramStr, out var threshold))
        {
            return width < threshold ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        }

        return SplitViewDisplayMode.Inline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
