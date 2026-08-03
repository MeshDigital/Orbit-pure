using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Deterministically maps a Soulseek username to one of a small fixed palette of avatar colors,
/// so the same user always gets the same color without needing per-user state or new theme
/// brushes.
/// </summary>
public class UsernameToColorConverter : IValueConverter
{
    private static readonly IBrush[] Palette =
    {
        new SolidColorBrush(Color.Parse("#E5735B")),
        new SolidColorBrush(Color.Parse("#E0A64E")),
        new SolidColorBrush(Color.Parse("#5BAE6E")),
        new SolidColorBrush(Color.Parse("#4FA3C7")),
        new SolidColorBrush(Color.Parse("#6E8FE0")),
        new SolidColorBrush(Color.Parse("#9A6FD1")),
        new SolidColorBrush(Color.Parse("#D65EA6")),
        new SolidColorBrush(Color.Parse("#5EC0AE")),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var username = value as string;
        if (string.IsNullOrWhiteSpace(username))
            return Palette[0];

        unchecked
        {
            var hash = 17;
            foreach (var c in username)
                hash = hash * 31 + c;

            var index = Math.Abs(hash) % Palette.Length;
            return Palette[index];
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
