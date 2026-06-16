using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Value converter that maps log levels or symbols (e.g. ERROR, WARN, INFO) to custom forensic colors/brushes.
    /// </summary>
    public class ForensicLevelToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string level)
            {
                switch (level.ToUpperInvariant())
                {
                    case "ERROR":
                    case "FAIL":
                    case "REJECTED":
                    case "❌":
                        return SolidColorBrush.Parse("#FF5252"); // Red
                    case "WARN":
                    case "STALL":
                    case "STALLED":
                    case "🚧":
                    case "⚠️":
                        return SolidColorBrush.Parse("#FFD740"); // Yellow
                    case "INFO":
                    case "ℹ️":
                    case "SUCCESS":
                    case "ACCEPTED":
                    case "✅":
                    case "📚":
                        return SolidColorBrush.Parse("#4EC9B0"); // Mint/Teal
                    case "SPECTRAL":
                        return SolidColorBrush.Parse("#BD93F9"); // Purple
                    default:
                        return SolidColorBrush.Parse("#888888"); // Gray
                }
            }
            return SolidColorBrush.Parse("#888888");
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
