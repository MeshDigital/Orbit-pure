using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Models;

namespace SLSKDONET.Converters;

public class ForensicVerdictConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ForensicEntryType entryType) return null;

        string param = parameter as string ?? "";

        return param switch
        {
            "Prefix" => entryType switch
            {
                ForensicEntryType.Section => "▓",
                ForensicEntryType.Bullet => "•",
                ForensicEntryType.Warning => "⚠",
                ForensicEntryType.Success => "✓",
                ForensicEntryType.Detail => "→",
                _ => ""
            },
            "Color" => entryType switch
            {
                ForensicEntryType.Section => new SolidColorBrush(Color.Parse("#FF9D00")), // Accent
                ForensicEntryType.Warning => new SolidColorBrush(Color.Parse("#FFCC00")), // Amber
                ForensicEntryType.Success => new SolidColorBrush(Color.Parse("#00FF88")), // Green
                _ => new SolidColorBrush(Color.Parse("#FFFFFF"))
            },
            "FontSize" => entryType switch
            {
                ForensicEntryType.Section => 14.0,
                ForensicEntryType.Verdict => 14.0,
                ForensicEntryType.Detail => 10.0,
                _ => 12.0
            },
            "FontWeight" => entryType switch
            {
                ForensicEntryType.Section => FontWeight.Bold,
                ForensicEntryType.Verdict => FontWeight.Bold,
                _ => FontWeight.Normal
            },
            _ => null
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
