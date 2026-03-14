using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SLSKDONET.Utils;

/// <summary>
/// Pro DJ Utility: Normalizes musical keys for export.
/// Rekordbox prefers Camelot (e.g., "8A") or Standard (e.g., "Am") depending on user settings.
/// We will standardize on Camelot "8A" / "8B" format for maximum compatibility with the 'Tonality' field.
/// </summary>
public static class KeyConverter
{
    // Camelot Wheel Mapping
    private static readonly Dictionary<string, string> StandardToCamelot = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Abm", "1A" }, { "G#m", "1A" }, { "B",   "1B" }, { "Cb",  "1B" },
        { "Ebm", "2A" }, { "D#m", "2A" }, { "F#",  "2B" }, { "Gb",  "2B" },
        { "Bbm", "3A" }, { "A#m", "3A" }, { "Db",  "3B" }, { "C#",  "3B" },
        { "Fm",  "4A" },                  { "Ab",  "4B" }, { "G#",  "4B" },
        { "Cm",  "5A" },                  { "Eb",  "5B" }, { "D#",  "5B" },
        { "Gm",  "6A" },                  { "Bb",  "6B" }, { "A#",  "6B" },
        { "Dm",  "7A" },                  { "F",   "7B" },
        { "Am",  "8A" },                  { "C",   "8B" },
        { "Em",  "9A" },                  { "G",   "9B" },
        { "Bm",  "10A" },                 { "D",   "10B" },
        { "F#m", "11A" }, { "Gbm", "11A" }, { "A",   "11B" },
        { "Dbm", "12A" }, { "C#m", "12A" }, { "E",   "12B" }
    };

    // OpenKey Mapping (e.g. "1m" -> "6A", "1d" -> "6B") - Traktor style
    // Careful: Traktor/OpenKey numbering is rotated compared to Camelot
    // 1m (OpenKey) = Am = 8A (Camelot) ... This gets confusing.
    // Let's assume input is usually Spotify (Integer based) or already a String.
    
    // Spotify API returns integers 0-11 where 0=C, 1=C#, etc. 
    private static readonly string[] PitchClass = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>
    /// Converts a raw key string to Camelot Notation (e.g., "8A").
    /// Returns the original string if no known conversion exists.
    /// </summary>
    public static string ToCamelot(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        var cleanKey = key.Trim();

        // 1. Check if already Camelot (e.g., "8A", "12B")
        if (Regex.IsMatch(cleanKey, @"^(1[0-2]|[1-9])[AB]$", RegexOptions.IgnoreCase))
        {
            return cleanKey.ToUpper();
        }

        // 2. Try Standard Notation (e.g., "Am", "F#")
        if (StandardToCamelot.TryGetValue(cleanKey, out var camelot))
        {
            return camelot;
        }

        // Fallback: Return original user input
        return cleanKey;
    }

    /// <summary>
    /// Converts Spotify Integer Pitch + Mode to Camelot String
    /// Key: 0-11 (C, C#, ...), Mode: 0 (Minor), 1 (Major)
    /// </summary>
    public static string FromSpotify(int key, int mode)
    {
        if (key < 0 || key > 11) return string.Empty;

        // Determine Standard Notation
        var root = PitchClass[key];
        var isMinor = mode == 0;
        var standard = root + (isMinor ? "m" : "");

        // Convert to Camelot
        if (StandardToCamelot.TryGetValue(standard, out var camelot))
        {
            return camelot;
        }

        return standard;
    }
}
