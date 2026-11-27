using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Export;

/// <summary>Canonical representation of one of the 24 musical keys (DX0 #555).</summary>
public readonly record struct MusicalKeyInfo(
    string Camelot,
    string OpenKey,
    string Musical,
    int TraktorInt);

/// <summary>
/// Single canonical 24-key converter shared across Rekordbox, Traktor, VirtualDJ, Serato,
/// and <see cref="Integrations.TraktorMetadataImporter"/> (DX0 #555).
/// </summary>
public static class KeyNotation
{
    // Ordered by Traktor MUSICAL_KEY VALUE (0..23) — matches TraktorMetadataImporter.TraktorKeys.
    public static readonly IReadOnlyList<MusicalKeyInfo> AllKeys = new[]
    {
        new MusicalKeyInfo("8A",  "1m",  "Am",  0),
        new MusicalKeyInfo("8B",  "8d",  "C",   1),
        new MusicalKeyInfo("9A",  "2m",  "Em",  2),
        new MusicalKeyInfo("9B",  "9d",  "G",   3),
        new MusicalKeyInfo("10A", "3m",  "Bm",  4),
        new MusicalKeyInfo("10B", "10d", "D",   5),
        new MusicalKeyInfo("11A", "4m",  "F#m", 6),
        new MusicalKeyInfo("11B", "11d", "A",   7),
        new MusicalKeyInfo("12A", "5m",  "C#m", 8),
        new MusicalKeyInfo("12B", "12d", "E",   9),
        new MusicalKeyInfo("1A",  "6m",  "G#m", 10),
        new MusicalKeyInfo("1B",  "7d",  "B",   11),
        new MusicalKeyInfo("2A",  "7m",  "D#m", 12),
        new MusicalKeyInfo("2B",  "2d",  "F#",  13),
        new MusicalKeyInfo("3A",  "8m",  "A#m", 14),
        new MusicalKeyInfo("3B",  "3d",  "C#",  15),
        new MusicalKeyInfo("4A",  "9m",  "Fm",  16),
        new MusicalKeyInfo("4B",  "4d",  "G#",  17),
        new MusicalKeyInfo("5A",  "10m", "Cm",  18),
        new MusicalKeyInfo("5B",  "5d",  "D#",  19),
        new MusicalKeyInfo("6A",  "11m", "Gm",  20),
        new MusicalKeyInfo("6B",  "6d",  "A#",  21),
        new MusicalKeyInfo("7A",  "12m", "Dm",  22),
        new MusicalKeyInfo("7B",  "1d",  "F",   23),
    };

    private static readonly Dictionary<string, MusicalKeyInfo> Lookup = BuildLookup();

    private static Dictionary<string, MusicalKeyInfo> BuildLookup()
    {
        var map = new Dictionary<string, MusicalKeyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in AllKeys)
        {
            map[k.Camelot] = k;
            map[k.OpenKey] = k;
            map[k.Musical] = k;
            // Leading-zero Camelot/OpenKey forms (e.g. "08A", "01m")
            if (k.Camelot.Length == 2) map["0" + k.Camelot] = k;
            if (k.OpenKey.Length == 2) map["0" + k.OpenKey] = k;
        }

        // Enharmonic & verbose aliases
        var aliases = new (string Alias, string Canonical)[]
        {
            ("Gbm", "F#m"), ("Dbm", "C#m"), ("Abm", "G#m"), ("Ebm", "D#m"), ("Bbm", "A#m"),
            ("Gb",  "F#"),  ("Db",  "C#"),  ("Ab",  "G#"),  ("Eb",  "D#"),  ("Bb",  "A#"),
            ("Cmaj", "C"), ("Dmaj", "D"), ("Emaj", "E"), ("Fmaj", "F"), ("Gmaj", "G"), ("Amaj", "A"), ("Bmaj", "B"),
            ("Amin", "Am"), ("Bmin", "Bm"), ("Cmin", "Cm"), ("Dmin", "Dm"), ("Emin", "Em"), ("Fmin", "Fm"), ("Gmin", "Gm"),
        };
        foreach (var (alias, canonical) in aliases)
        {
            if (map.TryGetValue(canonical, out var info))
                map[alias] = info;
        }
        return map;
    }

    public static bool TryFromTraktorInt(int value, out MusicalKeyInfo info)
    {
        if (value >= 0 && value < AllKeys.Count)
        {
            info = AllKeys[value];
            return true;
        }
        info = default;
        return false;
    }

    public static bool TryParse(string? text, out MusicalKeyInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (Lookup.TryGetValue(trimmed, out info))
            return true;

        // Accept "A minor" / "C major" style strings
        var normalized = text.Trim()
            .Replace(" minor", "m", StringComparison.OrdinalIgnoreCase)
            .Replace(" min", "m", StringComparison.OrdinalIgnoreCase)
            .Replace(" major", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" maj", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Lookup.TryGetValue(normalized, out info);
    }

    public static string? ToCamelot(string? text) => TryParse(text, out var k) ? k.Camelot : null;
    public static string? ToOpenKey(string? text) => TryParse(text, out var k) ? k.OpenKey : null;
    public static string? ToMusical(string? text) => TryParse(text, out var k) ? k.Musical : null;
    public static int? ToTraktorInt(string? text) => TryParse(text, out var k) ? k.TraktorInt : null;
}
