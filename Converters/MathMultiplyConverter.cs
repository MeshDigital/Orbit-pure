using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Multiplies a numeric value by a factor provided in the ConverterParameter.
/// Useful for scaling values for UI elements (e.g., Height="{Binding Energy, Converter={StaticResource MathMultiplyConverter}, ConverterParameter=20}").
/// </summary>
public class MathMultiplyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double val = 1.0;
        
        if (value is double d) val = d;
        else if (value is float f) val = f;
        else if (value is int i) val = i;
        else if (value is decimal dec) val = (double)dec;

        if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double multiplier))
        {
            return val * multiplier;
        }

        return val;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
