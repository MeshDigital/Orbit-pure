using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Services;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Presence dot fill: green=Online, yellow=Away, gray=Offline, transparent=Unknown.
/// Unknown deliberately renders as no dot at all rather than gray/offline — a row that hasn't
/// received a watch result yet should look unset, not "confirmed offline".
/// </summary>
public class PresenceToDotBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is UserPresenceState s ? s switch
        {
            UserPresenceState.Online => new SolidColorBrush(Color.Parse("#00C878")),
            UserPresenceState.Away => new SolidColorBrush(Color.Parse("#DDB800")),
            UserPresenceState.Offline => new SolidColorBrush(Color.Parse("#6A6A6A")),
            _ => Brushes.Transparent,
        } : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
