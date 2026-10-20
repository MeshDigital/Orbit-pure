using System.Globalization;

namespace SLSKDONET.Utils;

/// <summary>
/// The one way user-facing counts are written: "3,267" on every machine. "N0" alone follows the
/// OS culture, so a Dutch Windows showed "Analyse 3.267 new tracks" next to "2,400" elsewhere in
/// the UI. ORBIT's UI copy is English, so its numbers are too.
/// </summary>
public static class CountFormat
{
    public static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1 track" / "3,267 tracks" (regular plural: noun + "s" unless <paramref name="plural"/> is given).</summary>
    public static string Of(int value, string noun, string? plural = null) =>
        value == 1 ? $"1 {noun}" : $"{N(value)} {plural ?? noun + "s"}";
}
