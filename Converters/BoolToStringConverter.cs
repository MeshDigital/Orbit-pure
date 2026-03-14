using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a boolean value to one of two string values.
/// Usage: ConverterParameter='TrueValue|FalseValue'
/// Example: ConverterParameter='INST ONLY|VOCAL+INST'
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue)
            return parameter?.ToString()?.Split('|')[1] ?? "N/A";
        
        var parts = parameter?.ToString()?.Split('|');
        if (parts?.Length >= 2)
        {
            return boolValue ? parts[0] : parts[1];
        }
        
        return boolValue ? "True" : "False";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
