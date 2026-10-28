using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Services;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Presence dot fill from the theme's semantic tokens: BrushSuccess = Online, BrushWarning = Away,
/// BrushText3 = Offline, transparent = Unknown. Unknown deliberately renders as no dot at all
/// rather than grey/offline: a row that hasn't received a watch result yet should look unset, not
/// "confirmed offline".
/// </summary>
public class PresenceToDotBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is UserPresenceState s ? s switch
        {
            UserPresenceState.Online => Token("BrushSuccess"),
            UserPresenceState.Away => Token("BrushWarning"),
            UserPresenceState.Offline => Token("BrushText3"),
            _ => Brushes.Transparent,
        } : Brushes.Transparent;

    private static IBrush Token(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
