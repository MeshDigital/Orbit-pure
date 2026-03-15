using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a ratio (0.0-1.0) to a circular progress path geometry for health rings.
/// </summary>
public class CircularProgressConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double ratio || ratio < 0 || ratio > 1)
            return null;

        // Create a circular arc path for the progress ring
        // Center at (30,30) with radius 26 (to fit in 60x60 circle with 4px stroke)
        const double centerX = 30;
        const double centerY = 30;
        const double radius = 26;

        // Calculate the angle (ratio * 360 degrees)
        double angle = ratio * 360;

        // Convert to radians
        double radians = angle * Math.PI / 180;

        // Calculate end point
        double endX = centerX + radius * Math.Sin(radians);
        double endY = centerY - radius * Math.Cos(radians);

        // Create the arc path
        // Large arc flag: 1 if angle > 180, 0 otherwise
        int largeArcFlag = angle > 180 ? 1 : 0;

        string pathData = $"M {centerX} {centerY - radius} A {radius} {radius} 0 {largeArcFlag} 1 {endX} {endY}";

        return Geometry.Parse(pathData);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}