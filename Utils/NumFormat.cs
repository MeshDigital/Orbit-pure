using System;
using System.Globalization;

namespace SLSKDONET.Utils;

/// <summary>
/// The one way user-facing numbers are written. ORBIT's UI copy is English, so its numbers are
/// too: "174.0 BPM", "1.0x", "22.1 s", "91%", "+1.0 BPM", "2,400.5 GB" on every machine. Plain
/// <c>ToString("F1")</c> / <c>{x:0.0}</c> / <c>StringFormat={}{0:P0}</c> follow the OS culture, so a
/// Dutch or Belgian Windows showed "174,0" next to English text. Counts live in
/// <see cref="CountFormat"/>; everything else lives here (and in <c>NumberConverter</c> for XAML).
///
/// Input goes the other way: <see cref="TryParse(string?, out double)"/> accepts both "174.5" and
/// "174,5", so typing in the local style still works.
/// </summary>
public static class NumFormat
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Format provider for composite strings: invariant numbers, and P/P0/P1 percentages written
    /// without the invariant culture's space ("91%", not "91 %"). Use it through <see cref="Ui"/>.
    /// </summary>
    public static readonly IFormatProvider Provider = new UiFormatProvider();

    /// <summary>Formats an interpolated string with <see cref="Provider"/>: <c>NumFormat.Ui($"{gain:+0.0} dB · {score:P0}")</c>.</summary>
    public static string Ui(FormattableString text) => text.ToString(Provider);

    /// <summary>string.Format with <see cref="Provider"/>.</summary>
    public static string Format(string format, params object?[] args) => string.Format(Provider, format, args);

    // ── Plain numbers ──────────────────────────────────────────────────────────────────────

    /// <summary>Fixed decimals, no grouping: F(3.14159, 2) = "3.14".</summary>
    public static string F(double value, int decimals = 1) => value.ToString("F" + Clamp(decimals), Culture);

    /// <summary>Fixed decimals with thousands grouping: N(2400.5, 1) = "2,400.5".</summary>
    public static string N(double value, int decimals = 1) => value.ToString("N" + Clamp(decimals), Culture);

    /// <summary>Signed with a fixed number of decimals: "+1.0", "-0.5", "0.0" (never "-0.0").</summary>
    public static string Signed(double value, int decimals = 1)
    {
        var d = Clamp(decimals);
        var rounded = Math.Round(value, d, MidpointRounding.AwayFromZero);
        if (rounded == 0) return 0d.ToString("F" + d, Culture);
        return (rounded > 0 ? "+" : "") + rounded.ToString("F" + d, Culture);
    }

    // ── Domain helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Tempo with one decimal: "174.0".</summary>
    public static string Bpm(double bpm) => bpm.ToString("0.0", Culture);

    /// <summary>Tempo with one decimal, or <paramref name="missing"/> when unknown / zero.</summary>
    public static string Bpm(double? bpm, string missing = "—") =>
        bpm is { } b && b > 0 && !double.IsNaN(b) ? Bpm(b) : missing;

    /// <summary>Tempo difference: "+1.0 BPM", "-2.5 BPM", "0.0 BPM".</summary>
    public static string BpmDelta(double delta) => Signed(delta, 1) + " BPM";

    /// <summary>A 0–1 fraction as a percentage: Percent(0.91) = "91%".</summary>
    public static string Percent(double fraction, int decimals = 0) => Pct(fraction * 100, decimals);

    /// <summary>A value already in 0–100 as a percentage: PercentValue(91) = "91%", (91, 1) = "91.0%".</summary>
    public static string PercentValue(double value, int decimals = 0) => Pct(value, decimals);

    /// <summary>Signed percentage of a 0–100 value: "+1.0%", "-3.2%", "0.0%".</summary>
    public static string SignedPercent(double value, int decimals = 1) => Signed(value, decimals) + "%";

    /// <summary>Seconds: "22.1 s".</summary>
    public static string Seconds(double seconds, int decimals = 1) => F(seconds, decimals) + " s";

    /// <summary>Milliseconds: "3.2 ms".</summary>
    public static string Ms(double milliseconds, int decimals = 1) => F(milliseconds, decimals) + " ms";

    /// <summary>Decibels: "-3.2 dB", or signed "+1.5 dB" for gains.</summary>
    public static string Db(double db, int decimals = 1, bool signed = false) =>
        (signed ? Signed(db, decimals) : F(db, decimals)) + " dB";

    /// <summary>Integrated loudness: "-14.0 LUFS".</summary>
    public static string Lufs(double lufs) => F(lufs, 1) + " LUFS";

    /// <summary>Frequency in Hz written as kHz: Khz(44100) = "44.1 kHz".</summary>
    public static string Khz(double hz, int decimals = 1) => F(hz / 1000.0, decimals) + " kHz";

    /// <summary>A ratio / zoom / multiplier: "1.0x".</summary>
    public static string Ratio(double ratio, int decimals = 1) => F(ratio, decimals) + "x";

    /// <summary>
    /// A byte size: "512 B", "12 KB", "8.4 MB", "512 MB", "2,400.5 GB". GB is the top unit (DJs
    /// think of disks in GB) and keeps one decimal; MB keeps one below 100 MB; KB none.
    /// </summary>
    public static string Size(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        double b = bytes;
        if (b < 0) return "—";
        if (b >= gb) return N(b / gb, 1) + " GB";
        if (b >= mb) return N(b / mb, b >= 100 * mb ? 0 : 1) + " MB";
        if (b >= kb) return N(b / kb, 0) + " KB";
        return bytes.ToString(Culture) + " B";
    }

    /// <summary>A transfer rate: "512 KB/s", "1.2 MB/s" (below 1 KB/s: "0 KB/s").</summary>
    public static string Speed(double bytesPerSecond)
    {
        const double kb = 1024, mb = kb * 1024;
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0) bytesPerSecond = 0;
        return bytesPerSecond >= mb
            ? F(bytesPerSecond / mb, 1) + " MB/s"
            : F(bytesPerSecond / kb, 0) + " KB/s";
    }

    /// <summary>A clock duration: "3:07", "1:02:45" (negative values clamp to 0:00).</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? ((int)duration.TotalHours).ToString(Culture) + ":" + duration.Minutes.ToString("00", Culture) + ":" + duration.Seconds.ToString("00", Culture)
            : ((int)duration.TotalMinutes).ToString(Culture) + ":" + duration.Seconds.ToString("00", Culture);
    }

    /// <summary>A clock duration from seconds: Duration(187) = "3:07".</summary>
    public static string Duration(double seconds) =>
        double.IsNaN(seconds) || double.IsInfinity(seconds) ? "0:00" : Duration(TimeSpan.FromSeconds(seconds));

    // ── Parsing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a number typed by a person in either style: "174.5", "174,5", "1,234.5", "1.234,5",
    /// " -3 ". With a single separator kind, the separator is the decimal point (a typed BPM or
    /// gain is far more likely "174,5" than a thousands-grouped "1,745"). With both, the last one
    /// is the decimal point and the other is grouping.
    /// </summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty).Replace('−', '-');
        int lastDot = s.LastIndexOf('.');
        int lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            var group = lastDot > lastComma ? "," : ".";
            s = s.Replace(group, string.Empty);
            s = s.Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            // Only commas. One comma is a decimal comma; several are grouping ("1,234,567").
            s = s.IndexOf(',') == lastComma ? s.Replace(',', '.') : s.Replace(",", string.Empty);
        }
        else if (lastDot >= 0 && s.IndexOf('.') != lastDot)
        {
            // Several dots: Dutch-style grouping ("1.234.567").
            s = s.Replace(".", string.Empty);
        }
        return double.TryParse(s, NumberStyles.Float, Culture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary><see cref="TryParse(string?, out double)"/> as a nullable.</summary>
    public static double? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    // ── Internals ──────────────────────────────────────────────────────────────────────────

    private static int Clamp(int decimals) => Math.Clamp(decimals, 0, 6);

    private static string Pct(double value, int decimals)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "—";
        var d = Clamp(decimals);
        var rounded = Math.Round(value, d, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // no "-0%"
        return rounded.ToString("F" + d, Culture) + "%";
    }

    private sealed class UiFormatProvider : IFormatProvider, ICustomFormatter
    {
        public object? GetFormat(Type? formatType) =>
            formatType == typeof(ICustomFormatter) ? this
            : formatType == typeof(NumberFormatInfo) ? NumberFormatInfo.InvariantInfo
            : formatType == typeof(DateTimeFormatInfo) ? DateTimeFormatInfo.InvariantInfo
            : null;

        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            if (arg is null) return string.Empty;
            if (!string.IsNullOrEmpty(format) && (format[0] == 'P' || format[0] == 'p') && TryToDouble(arg, out var d))
            {
                int decimals = format.Length > 1 && int.TryParse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                return Percent(d, decimals);
            }
            return arg is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : arg.ToString() ?? string.Empty;
        }

        private static bool TryToDouble(object arg, out double d)
        {
            switch (arg)
            {
                case double x: d = x; return true;
                case float x: d = x; return true;
                case decimal x: d = (double)x; return true;
                case int x: d = x; return true;
                case long x: d = x; return true;
                case short x: d = x; return true;
                default: d = 0; return false;
            }
        }
    }
}
