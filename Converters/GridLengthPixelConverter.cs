using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace SLSKDONET.Converters;

/// <summary>
/// Two-way double &lt;-&gt; pixel GridLength converter, for binding a drag-resizable
/// ColumnDefinition.Width directly to a persisted double width property. Needed because
/// GridSplitter resizes the ColumnDefinition itself (converting it to a pixel GridLength on
/// drag) — a separate Width binding on a child element inside that column never sees that change,
/// which is why a splitter can visually drag while the panel's actual content stays the old size.
/// </summary>
public sealed class GridLengthPixelConverter : IValueConverter
{
    public static readonly GridLengthPixelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return new GridLength(d, GridUnitType.Pixel);
        return GridLength.Auto;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength gl) return gl.Value;
        return null;
    }
}
