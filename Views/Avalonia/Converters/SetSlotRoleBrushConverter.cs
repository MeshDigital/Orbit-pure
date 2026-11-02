using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Maps a <see cref="SetSlotRole"/> to its design token ("BrushRole" + role, Themes/DesignTokens.axaml),
/// so views never carry raw role colours.
/// </summary>
public sealed class SetSlotRoleBrushConverter : IValueConverter
{
    public static readonly SetSlotRoleBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SetSlotRole role || Application.Current is not { } app)
            return null;
        return app.TryGetResource("BrushRole" + role, app.ActualThemeVariant, out var brush) && brush is IBrush b
            ? b
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
