using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

public class NumericGreaterThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value != null && parameter != null && 
            double.TryParse(value.ToString(), out double val) && 
            double.TryParse(parameter.ToString(), out double target))
        {
            return val > target;
        }
        
        if (value is double dVal && parameter is double dTarget)
            return dVal > dTarget;
            
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NumericIsZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return i == 0;
        if (value is double d) return d == 0;
        if (value is float f) return f == 0;
        return value == null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NumericIsNotZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return i != 0;
        if (value is double d) return d != 0;
        if (value is float f) return f != 0;
        return value != null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public static class NumericConverters
{
    public static readonly IValueConverter GreaterThan = new NumericGreaterThanConverter();
    public static readonly IValueConverter IsZero = new NumericIsZeroConverter();
    public static readonly IValueConverter IsNotZero = new NumericIsNotZeroConverter();
}

