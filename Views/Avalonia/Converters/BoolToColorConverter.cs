using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Converts boolean to color (e.g., for status indicators)
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        // Mirrors Themes/DesignTokens.axaml ColorSuccess / ColorText3 (was Brushes.Green #008000,
        // which read as near-black on the dark shell).
        private static readonly IBrush OnBrush = new ImmutableSolidColorBrush(Color.Parse("#4CC38A"));
        private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.Parse("#5C626B"));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? OnBrush : OffBrush;
            }
            return OffBrush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
