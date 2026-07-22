using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Maps <see cref="SLSKDONET.Views.NotificationType"/> to the accent color used by the toast host.
/// </summary>
public class NotificationTypeToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "Success" => Brush.Parse("#1DB954"),
            "Warning" => Brush.Parse("#FFB347"),
            "Error" => Brush.Parse("#FF5C5C"),
            _ => Brush.Parse("#4FA8E0"),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
