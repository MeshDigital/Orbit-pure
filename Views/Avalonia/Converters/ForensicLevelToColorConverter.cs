using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Maps log levels or legacy level symbols (ERROR, WARN, INFO, ...) to the semantic status
    /// brushes in Themes/DesignTokens.axaml (<c>BrushError</c>, <c>BrushWarning</c>,
    /// <c>BrushAccent</c>, <c>BrushInfo</c>; anything else <c>BrushText2</c>).
    /// </summary>
    public class ForensicLevelToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var key = value is string level ? KeyFor(level) : "BrushText2";

            if (Application.Current is { } app
                && app.TryGetResource(key, app.ActualThemeVariant, out var res)
                && res is IBrush brush)
                return brush;

            return Brushes.Gray;
        }

        private static string KeyFor(string level) => level.ToUpperInvariant() switch
        {
            "ERROR" or "FAIL" or "REJECTED" or "❌" => "BrushError",
            "WARN" or "STALL" or "STALLED" or "🚧" or "⚠️" => "BrushWarning",
            "INFO" or "ℹ️" or "SUCCESS" or "ACCEPTED" or "✅" or "📚" => "BrushAccent",
            "SPECTRAL" => "BrushInfo",
            _ => "BrushText2",
        };

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
