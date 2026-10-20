using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using SLSKDONET.Utils;

namespace SLSKDONET.Converters;

/// <summary>
/// Formats a count the same way everywhere ("3,267", see <see cref="CountFormat"/>). Use it
/// instead of <c>StringFormat='{}{0:N0}'</c>, which follows the OS culture. An optional
/// ConverterParameter is a template with {0} ("{0} files found").
/// </summary>
public sealed class CountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? formatted = value switch
        {
            int i => CountFormat.N(i),
            long l => CountFormat.N(l),
            short s => CountFormat.N(s),
            uint u => CountFormat.N((long)u),
            double d => CountFormat.N((long)Math.Round(d)),
            null => null,
            _ => value.ToString(),
        };
        if (formatted == null) return null;
        return parameter is string template && template.Contains("{0}")
            ? string.Format(CultureInfo.InvariantCulture, template, formatted)
            : formatted;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
