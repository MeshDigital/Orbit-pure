using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data;
using Avalonia.Data.Converters;
using SLSKDONET.Utils;

namespace SLSKDONET.Converters;

/// <summary>
/// Formats a number the same way on every machine (see <see cref="NumFormat"/>). Use it instead of
/// <c>StringFormat='{}{0:F1}'</c> / <c>{0:P0}</c>, which follow the OS culture ("174,0" on a Dutch
/// Windows). ConverterParameter is either
/// <list type="bullet">
/// <item>a preset: <c>bpm</c> ("174.0"), <c>pct</c> / <c>pct1</c> (0–1 fraction → "91%" / "91.0%"),
/// <c>size</c> (bytes → "8.4 MB"), <c>speed</c> (bytes/s → "1.2 MB/s"), <c>ratio</c> ("1.0x"),
/// <c>signed</c> ("+1.0"), <c>seconds</c> ("22.1 s"), <c>duration</c> (seconds or TimeSpan → "3:07"), or</item>
/// <item>a .NET format ("F1", "0.00") or a composite template ("{}{0:F1} BPM", "{}{0:+0.0;-0.0;0.0} dB"),
/// applied with the invariant culture; P formats render as "91%".</item>
/// </list>
/// ConvertBack (TwoWay text boxes) accepts "174.5" and "174,5".
/// </summary>
public sealed class NumberConverter : IValueConverter
{
    private static readonly Regex FirstNumber = new(@"[-+−]?\d[\d.,]*|[-+−]?[.,]\d+", RegexOptions.Compiled);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var p = parameter as string;
        if (p != null && p.StartsWith("{}", StringComparison.Ordinal)) p = p[2..]; // XAML escape, if the parser left it in

        if (value is TimeSpan ts)
            return p == "duration" || string.IsNullOrEmpty(p) ? NumFormat.Duration(ts) : NumFormat.Format(p, ts);

        if (!TryToDouble(value, out var d))
            return string.IsNullOrEmpty(p) || !p.Contains("{0") ? value.ToString() : NumFormat.Format(p, value);

        return p switch
        {
            null or "" => d.ToString(CultureInfo.InvariantCulture),
            "bpm" => NumFormat.Bpm(d),
            "pct" => NumFormat.Percent(d),
            "pct1" => NumFormat.Percent(d, 1),
            "size" => NumFormat.Size((long)d),
            "speed" => NumFormat.Speed(d),
            "ratio" => NumFormat.Ratio(d),
            "signed" => NumFormat.Signed(d),
            "seconds" => NumFormat.Seconds(d),
            "duration" => NumFormat.Duration(d),
            _ when p.Contains("{0") => NumFormat.Format(p, value),
            _ => NumFormat.Format("{0:" + p + "}", value),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (string.IsNullOrWhiteSpace(text))
            return Nullable.GetUnderlyingType(targetType) != null ? null : BindingOperations.DoNothing;

        var match = FirstNumber.Match(text);
        if (!match.Success || !NumFormat.TryParse(match.Value, out var d))
            return BindingOperations.DoNothing;

        try
        {
            if (underlying == typeof(double) || underlying == typeof(object)) return d;
            if (underlying == typeof(float)) return (float)d;
            if (underlying == typeof(decimal)) return (decimal)d;
            if (underlying == typeof(int)) return (int)Math.Round(d);
            if (underlying == typeof(long)) return (long)Math.Round(d);
            if (underlying == typeof(string)) return d.ToString(CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return BindingOperations.DoNothing;
        }
        return BindingOperations.DoNothing;
    }

    private static bool TryToDouble(object value, out double d)
    {
        switch (value)
        {
            case double x: d = x; return true;
            case float x: d = x; return true;
            case decimal x: d = (double)x; return true;
            case int x: d = x; return true;
            case long x: d = x; return true;
            case short x: d = x; return true;
            case uint x: d = x; return true;
            case ulong x: d = x; return true;
            default: d = 0; return false;
        }
    }
}
