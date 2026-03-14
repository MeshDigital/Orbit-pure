using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

public class MatchToColorConverter : IValueConverter
{
    public static readonly MatchToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double percentage)
        {
            if (percentage >= 80) return Brush.Parse("#4CAF50"); // Green
            if (percentage >= 50) return Brush.Parse("#FF9800"); // Orange
            if (percentage >= 20) return Brush.Parse("#F44336"); // Red
            return Brush.Parse("#9E9E9E"); // Gray
        }
        
        if (value is int intPercentage)
        {
            if (intPercentage >= 80) return Brush.Parse("#4CAF50");
            if (intPercentage >= 50) return Brush.Parse("#FF9800");
            if (intPercentage >= 20) return Brush.Parse("#F44336");
            return Brush.Parse("#9E9E9E");
        }

        return Brush.Parse("#9E9E9E");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
