using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Generates DJ-friendly cue names for high-BPM music (DnB, Dubstep, etc.).
///
/// Implements naming conventions:
/// - "Drop -64": 64 beats before the main drop (16 bars at 4/4)
/// - "Build +32": 32 beats into a build section (anticipation naming)
/// - Color-coded semantic naming (Green=Intro, Red=Drop, Blue=Outro, etc.)
/// </summary>
public sealed class DnBCueNamingService
{
    private const int HighBpmThreshold = 160;

    /// <summary>
    /// EDM/DnB standard color palette (Rekordbox compatible).
    /// </summary>
    public static readonly Dictionary<CueRole, (byte R, byte G, byte B, string HexColor)> SemanticColors = new()
    {
        { CueRole.Intro,      (0, 255, 0, "#00FF00") },      // Green: Mix-in/Intro
        { CueRole.Build,      (255, 255, 0, "#FFFF00") },    // Yellow: Build/Breakdown
        { CueRole.Drop,       (255, 0, 0, "#FF0000") },      // Red: Main drop
        { CueRole.Breakdown,  (255, 165, 0, "#FFA500") },    // Orange: Secondary drop/Breakdown2
        { CueRole.PhraseStart,(0, 255, 255, "#00FFFF") },    // Cyan: Phrase markers
        { CueRole.Outro,      (0, 0, 255, "#0000FF") },      // Blue: Outro/Mix-out
        { CueRole.Custom,     (200, 200, 200, "#C8C8C8") }   // Gray: User-defined
    };

    /// <summary>
    /// Generates a DJ-friendly cue name with pre-drop runway information.
    /// </summary>
    public static string GenerateCueName(
        CueRole role,
        float bpm,
        double currentTimestamp,
        double? nextDropTimestamp = null,
        int? hotCueSlot = null)
    {
        if (bpm < HighBpmThreshold)
            return role.ToString(); // Standard naming for non-DnB

        // For drops, show how many beats until the next drop
        if (role == CueRole.Drop && nextDropTimestamp.HasValue)
        {
            int beatsBefore = (int)CalculateBeatsUntilTimestamp(currentTimestamp, nextDropTimestamp.Value, bpm);
            if (beatsBefore > 0)
                return $"Drop -{beatsBefore}"; // e.g., "Drop -64"
        }

        // For buildups, show duration of runway
        if (role == CueRole.Build)
        {
            // Assume build typically lasts 32-64 beats
            return $"Build +32"; // Placeholder; could be made smarter
        }

        // For breakdowns, indicate subdrop or vocal section
        if (role == CueRole.Breakdown)
            return "Breakdown 2";

        // Standard naming for other roles
        return role switch
        {
            CueRole.Intro => "Intro",
            CueRole.Outro => "Outro",
            CueRole.PhraseStart => "Phrase",
            _ => role.ToString()
        };
    }

    /// <summary>
    /// Returns the semantic color for a cue role.
    /// </summary>
    public static (byte R, byte G, byte B) GetSemanticColor(CueRole role)
    {
        if (SemanticColors.TryGetValue(role, out var color))
            return (color.R, color.G, color.B);

        // Fallback to gray for unknown roles
        return (200, 200, 200);
    }

    /// <summary>
    /// Returns the hex color for a cue role.
    /// </summary>
    public static string GetSemanticColorHex(CueRole role)
    {
        if (SemanticColors.TryGetValue(role, out var color))
            return color.HexColor;

        return "#C8C8C8"; // Gray fallback
    }

    /// <summary>
    /// Calculates the number of beats between two timestamps.
    /// </summary>
    private static double CalculateBeatsUntilTimestamp(double fromSeconds, double toSeconds, float bpm)
    {
        if (toSeconds <= fromSeconds)
            return 0;

        double beatDurationSeconds = 60.0 / bpm;
        double timeDiff = toSeconds - fromSeconds;
        return timeDiff / beatDurationSeconds;
    }

    /// <summary>
    /// Generates pre-drop cue positioning suggestions for double-drop mixing.
    /// Returns timestamps at -64, -32, and -16 beats before a drop.
    /// </summary>
    public static List<PreDropCuePosition> GeneratePreDropCuePositions(
        double dropTimestamp,
        float bpm)
    {
        var positions = new List<PreDropCuePosition>();
        double beatDurationSeconds = 60.0 / bpm;

        var preDropBeats = new[] { 64, 32, 16 }; // Pre-drop positions

        foreach (int beats in preDropBeats)
        {
            double precueTimestamp = dropTimestamp - (beats * beatDurationSeconds);
            if (precueTimestamp >= 0)
            {
                positions.Add(new PreDropCuePosition
                {
                    TimestampSeconds = precueTimestamp,
                    BeatsUntilDrop = beats,
                    CueName = $"Drop -{beats}",
                    Purpose = beats == 64 ? "Load & prepare" : beats == 32 ? "Countdown begins" : "Final approach"
                });
            }
        }

        return positions;
    }
}

/// <summary>Represents a pre-drop cue position for double-drop mixing.</summary>
public sealed class PreDropCuePosition
{
    public double TimestampSeconds { get; init; }
    public int BeatsUntilDrop { get; init; }
    public string CueName { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
}
