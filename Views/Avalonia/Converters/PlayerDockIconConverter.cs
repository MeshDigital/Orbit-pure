using System;
using System.Globalization;
using Avalonia.Data.Converters;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Converts PlayerDockLocation to icon for toggle button.
/// </summary>
public class PlayerDockIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PlayerDockLocation location)
        {
            return location == PlayerDockLocation.BottomBar ? "⬇️" : "➡️";
        }
        if (value is bool isBottom)
        {
            return isBottom ? "⬇️" : "➡️";
        }
        return "➡️";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
