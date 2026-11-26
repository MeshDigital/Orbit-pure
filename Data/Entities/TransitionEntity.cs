using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

public enum TransitionScopeKind
{
    Playlist = 0,
    Set = 1
}

public enum TransitionOrigin
{
    Manual = 0,
    AutoKept = 1
}

/// <summary>
/// Unified persisted transition record (Epic #517 / FO1a #518), replacing the split
/// <c>PlaylistTrackTransitions</c> and <c>SetTrackTransitions</c> tables with one canonical store.
/// </summary>
[Table("Transitions")]
public class TransitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public TransitionScopeKind ScopeKind { get; set; } = TransitionScopeKind.Playlist;

    [Required]
    public Guid ScopeId { get; set; }

    [Required]
    public Guid OutgoingItemId { get; set; }

    [Required]
    public Guid IncomingItemId { get; set; }

    public string? OutgoingTrackHash { get; set; }
    public string? IncomingTrackHash { get; set; }

    /// <summary>
    /// When true (default), this row also answers for the same track pair in other playlists/sets
    /// unless overridden by a container-scoped row. False = "This playlist only".
    /// </summary>
    public bool UseAsPairDefault { get; set; } = true;

    /// <summary>
    /// Canonical DJ technique name: Auto, Cut, Crossfade, EchoOut, FilterSweep, EqSwap, WaveDuck, Loop.
    /// Replaces the split PresetName + TransitionType columns.
    /// </summary>
    [Required]
    public string Technique { get; set; } = "Auto";

    public int DurationBars { get; set; } = 16;

    public double? SourceTriggerSeconds { get; set; }
    public double? TargetTriggerSeconds { get; set; }

    // Audio parameter overrides (union of PlaylistTrackTransitions and SetTrackTransitions)
    public float? EchoDecayFactor { get; set; }
    public float? FilterStartFrequency { get; set; }
    public float? FilterEndFrequency { get; set; }
    public float? EqLowGain { get; set; }
    public float? EqMidGain { get; set; }
    public float? EqHighGain { get; set; }
    public float? WaveDuckDepth { get; set; }
    public bool? FilterSweepRising { get; set; }
    public bool? EqSwapLow { get; set; }
    public bool? EqSwapMid { get; set; }
    public bool? EqSwapHigh { get; set; }
    public float? EqLowCrossoverHz { get; set; }
    public float? EqHighCrossoverHz { get; set; }
    public int? LoopBars { get; set; }
    public int? LoopRepeats { get; set; }

    public TransitionOrigin Origin { get; set; } = TransitionOrigin.Manual;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Maps legacy (PresetName, TransitionType) columns onto the unified <see cref="Technique"/> name.
    /// </summary>
    public static string MapLegacyTechnique(string? presetName, string? transitionType)
    {
        var preset = presetName?.Trim();
        var type = transitionType?.Trim();

        if (string.Equals(preset, "Auto", StringComparison.OrdinalIgnoreCase))
            return "Auto";
        if (string.Equals(preset, "Fade", StringComparison.OrdinalIgnoreCase))
            return "Crossfade";
        if (string.Equals(preset, "Rise", StringComparison.OrdinalIgnoreCase))
            return "FilterSweep";
        if (string.Equals(preset, "Blend", StringComparison.OrdinalIgnoreCase))
            return "EqSwap";
        if (string.Equals(preset, "Wave", StringComparison.OrdinalIgnoreCase))
            return "WaveDuck";
        if (string.Equals(preset, "Melt", StringComparison.OrdinalIgnoreCase))
            return string.Equals(type, "EchoOut", StringComparison.OrdinalIgnoreCase) ? "EchoOut" : "Melt";
        if (string.Equals(preset, "DoubleDrop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "DoubleDrop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Loop", StringComparison.OrdinalIgnoreCase))
            return "Loop";
        if (string.Equals(type, "Cut", StringComparison.OrdinalIgnoreCase))
            return "Cut";
        if (string.Equals(type, "EchoOut", StringComparison.OrdinalIgnoreCase))
            return "EchoOut";
        if (string.Equals(type, "FilterSweep", StringComparison.OrdinalIgnoreCase))
            return "FilterSweep";
        if (string.Equals(type, "EqSwap", StringComparison.OrdinalIgnoreCase))
            return "EqSwap";
        if (string.Equals(type, "WaveDuck", StringComparison.OrdinalIgnoreCase))
            return "WaveDuck";
        if (string.Equals(type, "Crossfade", StringComparison.OrdinalIgnoreCase))
            return "Crossfade";

        return string.IsNullOrWhiteSpace(preset) ? "Auto" : preset;
    }
}
