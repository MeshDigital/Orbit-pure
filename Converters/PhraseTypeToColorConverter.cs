using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts a musical PhraseType to a DAW-style color brush.
/// </summary>
public class PhraseTypeToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PhraseType type)
        {
            var hex = type switch
            {
                PhraseType.Intro => "#1DB954",      // green (Spotify-ish)
                PhraseType.Verse => "#2196F3",      // blue
                PhraseType.Chorus => "#9C27B0",     // purple
                PhraseType.Build => "#FF9800",      // orange
                PhraseType.Drop => "#E22134",       // red (Aggressive)
                PhraseType.Breakdown => "#00BCD4",   // cyan
                PhraseType.Bridge => "#607D8B",      // grey-blue
                PhraseType.Outro => "#FFEB3B",       // yellow
                PhraseType.Riser => "#FF5722",       // deep orange
                PhraseType.Filter => "#4CAF50",      // light green
                PhraseType.Ambient => "#9E9E9E",     // grey
                _ => "#707070"                       // dark grey for unknown
            };

            return SolidColorBrush.Parse(hex);
        }

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
