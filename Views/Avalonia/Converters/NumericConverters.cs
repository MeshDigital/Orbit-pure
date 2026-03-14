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
    public static readonly IValueConverter FloatFallback = new FloatFallbackConverter();
}

/// <summary>
/// Phase 1.0: Converts a percentage value (0-100) to a pixel width for progress bars.
/// ConverterParameter = max width in pixels.
/// Example: SearchScore=45, Parameter=60 → Width=27px
/// </summary>
public class PercentToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = 0;
        if (value is double d) percent = d;
        else if (value is int i) percent = i;
        else if (value is float f) percent = f;

        double maxWidth = 60;
        if (parameter != null && double.TryParse(parameter.ToString(), out double mw))
            maxWidth = mw;

        return Math.Clamp(percent / 100.0 * maxWidth, 0, maxWidth);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class FloatFallbackConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        float floatVal = 0f;
        if (value is float f) floatVal = f;
        else if (value is double d) floatVal = (float)d;
        else if (value != null && float.TryParse(value.ToString(), out float parsed)) floatVal = parsed;
        else return 0f;

        // Sprint 5C Hardening: Handle 1-9 Scaling for specific UI controls
        if (parameter is string p && p == "ScaleVibe")
        {
            // Map 1-9 -> 0-1
            return Math.Clamp((floatVal - 1.0f) / 8.0f, 0.0f, 1.0f);
        }

        return floatVal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
