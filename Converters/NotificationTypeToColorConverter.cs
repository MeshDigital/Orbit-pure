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
            "Success" => Brush.Parse("#4CC38A"), // ColorSuccess (Themes/DesignTokens.axaml)
            "Warning" => Brush.Parse("#E5A645"), // ColorWarning
            "Error" => Brush.Parse("#E5605A"), // ColorError
            _ => Brush.Parse("#5AA7E8"), // ColorInfo
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
