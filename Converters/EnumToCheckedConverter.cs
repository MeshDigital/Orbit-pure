using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts an enum value to a bool for ToggleButton IsChecked bindings.
/// Returns true if the bound value matches ConverterParameter (string comparison).
/// On ConvertBack, parses ConverterParameter back to the target enum type when IsChecked=true.
/// Used by DownloadsPage batch profile chips.
/// </summary>
public class EnumToCheckedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter is not string paramStr)
            return false;

        return string.Equals(value.ToString(), paramStr, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only act when the button is being checked (true); ignore unchecks
        if (value is not bool isChecked || !isChecked)
            return Avalonia.AvaloniaProperty.UnsetValue;

        if (parameter is not string paramStr)
            return Avalonia.AvaloniaProperty.UnsetValue;

        try
        {
            return Enum.Parse(targetType, paramStr, ignoreCase: true);
        }
        catch
        {
            return Avalonia.AvaloniaProperty.UnsetValue;
        }
    }
}
