using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a BPM value to a foreground text color:
///   &lt;100  → cool blue  (#64B5F6)
///   100-140 → green     (#4CAF50)
///   &gt;140  → warm orange-red (#FF7043)
/// </summary>
public class BpmForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double bpm = value switch
        {
            double d => d,
            float f  => f,
            int i    => i,
            _        => 0
        };

        return bpm switch
        {
            < 100  => new SolidColorBrush(Color.Parse("#64B5F6")), // cool blue
            > 140  => new SolidColorBrush(Color.Parse("#FF7043")), // warm orange-red
            _      => new SolidColorBrush(Color.Parse("#4CAF50"))  // green (default)
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a BPM value to a chip background color:
///   &lt;100  → dark blue  (#1A2233)
///   100-140 → dark green (#1E2A1E)
///   &gt;140  → dark orange (#2A1E14)
/// </summary>
public class BpmBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double bpm = value switch
        {
            double d => d,
            float f  => f,
            int i    => i,
            _        => 0
        };

        return bpm switch
        {
            < 100  => new SolidColorBrush(Color.Parse("#1A2233")), // dark blue
            > 140  => new SolidColorBrush(Color.Parse("#2A1E14")), // dark orange-red
            _      => new SolidColorBrush(Color.Parse("#1E2A1E"))  // dark green (default)
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
