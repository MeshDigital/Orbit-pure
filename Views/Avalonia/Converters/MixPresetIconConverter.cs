using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>Maps a Mix preset name (Auto/Fade/Rise/Blend/Wave/Melt/Custom) to the emoji shown
/// next to it in the inline Mix module — gives each preset a distinct at-a-glance identity
/// instead of every transition looking the same until you read the label.</summary>
public class MixPresetIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var preset = value as string;
        return preset switch
        {
            "Fade" => "🌫️",
            "Rise" => "📈",
            "Blend" => "🎨",
            "Wave" => "🌊",
            "Melt" => "🔥",
            "Custom" => "⚙️",
            "Auto" => "🎚️",
            _ => "🔀",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
