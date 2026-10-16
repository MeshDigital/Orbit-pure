using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Converters;

/// <summary>
/// Maps a <see cref="StemType"/> to its data-identity brush from Themes/DesignTokens.axaml
/// (<c>BrushStemVocals</c>, <c>BrushStemDrums</c>, <c>BrushStemBass</c>, <c>BrushStemOther</c>).
/// Piano has no dedicated token and shares the "Other" colour. Falls back to <c>BrushText2</c>.
/// </summary>
public sealed class StemTypeToBrushConverter : IValueConverter
{
    public static readonly StemTypeToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StemType.Vocals => "BrushStemVocals",
            StemType.Drums  => "BrushStemDrums",
            StemType.Bass   => "BrushStemBass",
            StemType.Piano  => "BrushStemOther",
            StemType.Other  => "BrushStemOther",
            _               => "BrushText2",
        };

        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
            return brush;

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
