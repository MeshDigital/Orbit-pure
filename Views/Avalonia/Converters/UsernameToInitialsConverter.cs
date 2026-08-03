using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>Turns a Soulseek username into 1-2 uppercase initials for an avatar circle.</summary>
public class UsernameToInitialsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var username = value as string;
        if (string.IsNullOrWhiteSpace(username))
            return "?";

        var trimmed = username.Trim();
        if (trimmed.Length == 1)
            return trimmed.ToUpperInvariant();

        return trimmed[..2].ToUpperInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
