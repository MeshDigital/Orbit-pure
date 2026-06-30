using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services;

/// <summary>
/// Camelot wheel key display and harmonic compatibility calculations.
/// Converts standard musical key notation (e.g., "C#m") to Camelot notation (e.g., "8B")
/// and identifies compatible mixing keys for smooth transitions.
/// </summary>
public sealed class CamelotKeyDisplayService
{
    // Standard key to Camelot mapping: major = A (uppercase), minor = B (lowercase)
    private static readonly Dictionary<string, string> KeyToCamelot = new()
    {
        // Major keys (A side)
        ["C"] = "8A",    ["G"] = "9A",    ["D"] = "10A",   ["A"] = "11A",   ["E"] = "12A",   ["B"] = "1A",
        ["F#"] = "2A",   ["C#"] = "3A",   ["G#"] = "4A",   ["D#"] = "5A",   ["A#"] = "6A",   ["F"] = "7A",

        // Minor keys (B side)
        ["Am"] = "8B",   ["Em"] = "9B",   ["Bm"] = "10B",  ["F#m"] = "11B", ["C#m"] = "12B", ["G#m"] = "1B",
        ["D#m"] = "2B",  ["A#m"] = "3B",  ["F m"] = "4B",  ["C m"] = "5B",  ["G m"] = "6B",  ["D m"] = "7B",

        // Enharmonic equivalents (flats)
        ["Db"] = "3A",   ["Ab"] = "4A",   ["Eb"] = "5A",   ["Bb"] = "6A",   ["F"] = "7A",
        ["Bbm"] = "3B",  ["Ebm"] = "4B",  ["Abm"] = "5B",  ["Dbm"] = "6B",  ["Gbm"] = "7B",

        // Natural variations
        ["C"] = "8A",    ["Cm"] = "5B",
        ["G"] = "9A",    ["Gm"] = "6B",
        ["D"] = "10A",   ["Dm"] = "7B",
        ["A"] = "11A",   ["Am"] = "8B",
        ["E"] = "12A",   ["Em"] = "9B",
        ["B"] = "1A",    ["Bm"] = "10B",
    };

    private static readonly Dictionary<string, List<string>> HarmonicCompatibility = new()
    {
        // Each Camelot key has compatible mixing keys: ±1 semitone (adjacent numbers) + same relative major/minor
        ["1A"] = new() { "12A", "1A", "2A", "1B", "12B" },
        ["2A"] = new() { "1A", "2A", "3A", "2B", "1B" },
        ["3A"] = new() { "2A", "3A", "4A", "3B", "2B" },
        ["4A"] = new() { "3A", "4A", "5A", "4B", "3B" },
        ["5A"] = new() { "4A", "5A", "6A", "5B", "4B" },
        ["6A"] = new() { "5A", "6A", "7A", "6B", "5B" },
        ["7A"] = new() { "6A", "7A", "8A", "7B", "6B" },
        ["8A"] = new() { "7A", "8A", "9A", "8B", "7B" },
        ["9A"] = new() { "8A", "9A", "10A", "9B", "8B" },
        ["10A"] = new() { "9A", "10A", "11A", "10B", "9B" },
        ["11A"] = new() { "10A", "11A", "12A", "11B", "10B" },
        ["12A"] = new() { "11A", "12A", "1A", "12B", "11B" },

        ["1B"] = new() { "12B", "1B", "2B", "1A", "12A" },
        ["2B"] = new() { "1B", "2B", "3B", "2A", "1A" },
        ["3B"] = new() { "2B", "3B", "4B", "3A", "2A" },
        ["4B"] = new() { "3B", "4B", "5B", "4A", "3A" },
        ["5B"] = new() { "4B", "5B", "6B", "5A", "4A" },
        ["6B"] = new() { "5B", "6B", "7B", "6A", "5A" },
        ["7B"] = new() { "6B", "7B", "8B", "7A", "6A" },
        ["8B"] = new() { "7B", "8B", "9B", "8A", "7A" },
        ["9B"] = new() { "8B", "9B", "10B", "9A", "8A" },
        ["10B"] = new() { "9B", "10B", "11B", "10A", "9A" },
        ["11B"] = new() { "10B", "11B", "12B", "11A", "10A" },
        ["12B"] = new() { "11B", "12B", "1B", "12A", "11A" },
    };

    /// <summary>
    /// Convert standard key notation (e.g., "C#m", "Ab") to Camelot notation (e.g., "8B", "4A").
    /// </summary>
    public string ConvertToCamelot(string standardKey)
    {
        if (string.IsNullOrWhiteSpace(standardKey))
            return "?";

        string normalized = standardKey.Trim();

        if (KeyToCamelot.TryGetValue(normalized, out var camelot))
            return camelot;

        // Try without space
        normalized = normalized.Replace(" ", "");
        if (KeyToCamelot.TryGetValue(normalized, out camelot))
            return camelot;

        return "?";
    }

    /// <summary>
    /// Get all compatible mixing keys for a given Camelot key.
    /// Includes the key itself, adjacent semitones, and relative major/minor.
    /// </summary>
    public List<string> GetCompatibleKeys(string camelotKey)
    {
        if (HarmonicCompatibility.TryGetValue(camelotKey, out var compatible))
            return compatible;

        return new() { camelotKey };
    }

    /// <summary>
    /// Check if two Camelot keys are harmonically compatible for mixing.
    /// </summary>
    public bool AreKeysCompatible(string camelot1, string camelot2)
    {
        var compatible = GetCompatibleKeys(camelot1);
        return compatible.Contains(camelot2);
    }

    /// <summary>
    /// Get relative major/minor key for a Camelot key (e.g., "8A" <-> "8B").
    /// </summary>
    public string GetRelativeKey(string camelotKey)
    {
        if (string.IsNullOrWhiteSpace(camelotKey) || camelotKey.Length < 2)
            return "?";

        char number = camelotKey[camelotKey.Length - 2];
        char mode = camelotKey[camelotKey.Length - 1];

        char oppositeMode = mode == 'A' ? 'B' : 'A';
        return $"{number}{oppositeMode}";
    }

    /// <summary>
    /// Get CSS/UI color for Camelot key visualization (circle of fifths coloring).
    /// Returns hex color for harmonic family grouping.
    /// </summary>
    public string GetKeyColor(string camelotKey)
    {
        return camelotKey switch
        {
            // Warm colors (C-G region)
            "1A" or "1B" => "#FFD700", // Gold
            "2A" or "2B" => "#FFA500", // Orange
            "3A" or "3B" => "#FF6B6B", // Red-Orange

            // Red colors (D-A region)
            "4A" or "4B" => "#FF4444", // Red
            "5A" or "5B" => "#E74C3C", // Dark Red
            "6A" or "6B" => "#C0392B", // Crimson

            // Purple colors (B-F# region)
            "7A" or "7B" => "#9B59B6", // Purple
            "8A" or "8B" => "#8E44AD", // Deep Purple
            "9A" or "9B" => "#3498DB", // Blue

            // Cool colors (G#-D# region)
            "10A" or "10B" => "#1E90FF", // Dodger Blue
            "11A" or "11B" => "#00CED1", // Dark Turquoise
            "12A" or "12B" => "#20B2AA", // Light Sea Green

            _ => "#CCCCCC" // Gray for unknown
        };
    }

    /// <summary>
    /// Format Camelot key for UI display with optional compatibility info.
    /// </summary>
    public string FormatForDisplay(string camelotKey, bool includeCompatible = false)
    {
        if (includeCompatible && HarmonicCompatibility.TryGetValue(camelotKey, out var compatible))
        {
            var otherCompatible = compatible.Where(k => k != camelotKey).ToList();
            return $"{camelotKey} [→ {string.Join(", ", otherCompatible)}]";
        }

        return camelotKey;
    }
}
