using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Converts a double value (0.0 to 1.0) to opacity for visual indicators.
    /// Used to make UI elements fade based on a property value (e.g., danceability).
    /// </summary>
    public class OpacityMultiplierConverter : IValueConverter
    {
        public static OpacityMultiplierConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double opacity && parameter is string multiplierStr && double.TryParse(multiplierStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double multiplier))
            {
                return opacity * multiplier;
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
