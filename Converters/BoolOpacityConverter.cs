using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

public class BoolOpacityConverter : IValueConverter
{
    public static readonly BoolOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            double activeOpacity = 1.0;
            double inactiveOpacity = 0.3;

            // Optional parameter parsing "ActiveOpacity;InactiveOpacity"
            if (parameter is string paramStr && paramStr.Contains(";"))
            {
                var parts = paramStr.Split(';');
                if (parts.Length >= 2)
                {
                    double.TryParse(parts[0], NumberStyles.Any, culture, out activeOpacity);
                    double.TryParse(parts[1], NumberStyles.Any, culture, out inactiveOpacity);
                }
            }
            else if (parameter is string singleParam && double.TryParse(singleParam, NumberStyles.Any, culture, out var parsed))
            {
                inactiveOpacity = parsed;
            }

            return boolValue ? activeOpacity : inactiveOpacity;
        }
        return 1.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
