using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts energy/arousal value (0-1 or 1-9) to a color gradient.
/// Low energy = Blue, Medium = Green, High = Red/Orange
/// </summary>
public class EnergyToColorConverter : IValueConverter
{
    // Predefined energy colors
    private static readonly Color LowEnergy = Color.FromRgb(66, 133, 244);   // Blue
    private static readonly Color MidEnergy = Color.FromRgb(52, 168, 83);    // Green
    private static readonly Color HighEnergy = Color.FromRgb(234, 67, 53);   // Red

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double energy = 0.5;

        if (value is float f)
            energy = f;
        else if (value is double d)
            energy = d;
        else if (value is int i)
            energy = i / 9.0; // Assume 1-9 scale
        
        // Normalize to 0-1 range
        if (energy > 1)
            energy = (energy - 1) / 8.0; // Convert 1-9 to 0-1

        // Clamp
        energy = Math.Max(0, Math.Min(1, energy));

        // Two-segment gradient: Blue -> Green -> Red
        Color resultColor;
        if (energy < 0.5)
        {
            // Blue to Green
            double t = energy * 2;
            resultColor = InterpolateColor(LowEnergy, MidEnergy, t);
        }
        else
        {
            // Green to Red
            double t = (energy - 0.5) * 2;
            resultColor = InterpolateColor(MidEnergy, HighEnergy, t);
        }

        return new SolidColorBrush(resultColor);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static Color InterpolateColor(Color from, Color to, double t)
    {
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t)
        );
    }
}

/// <summary>
/// Converts a 0-1 value to opacity (used for danceable/vocal indicators).
/// </summary>
public class OpacityMultiplierConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double multiplier = 1.0;
        if (parameter is string s && double.TryParse(s, out var m))
            multiplier = m;

        double val = 0.5;
        if (value is float f) val = f;
        else if (value is double d) val = d;

        // Clamp between 0.3 and 1.0 so it's never invisible
        return Math.Max(0.3, Math.Min(1.0, val * multiplier));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
