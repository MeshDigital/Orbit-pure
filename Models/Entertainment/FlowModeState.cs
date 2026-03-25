using SLSKDONET.Models.Musical;

namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// Represents the live runtime state of ORBIT's Flow Mode engine.
/// Flow Mode selects and transitions tracks automatically for a continuous DJ-like experience.
/// </summary>
public sealed record FlowModeState
{
    /// <summary>Whether Flow Mode is currently active.</summary>
    public bool IsActive { get; set; }

    /// <summary>The active <see cref="VibePreset"/> controlling transition weights.</summary>
    public VibePreset ActivePreset { get; set; } = VibePreset.SilkySmooth;

    /// <summary>
    /// Target energy nudge applied by the user: negative = more chill, positive = more energetic.
    /// Range: -1.0 to +1.0.
    /// </summary>
    public double EnergyNudge { get; set; }

    /// <summary>
    /// Titles of the next predicted tracks in the flow path (up to 5).
    /// </summary>
    public IReadOnlyList<string> FlowPathTitles { get; set; } = [];

    /// <summary>
    /// Predicted energy levels for each upcoming track in the flow path.
    /// Parallel array with <see cref="FlowPathTitles"/>.
    /// </summary>
    public IReadOnlyList<double> FlowPathEnergies { get; set; } = [];

    /// <summary>
    /// Crossfade duration in seconds that Flow Mode will use for the next transition.
    /// </summary>
    public double CrossfadeDurationSeconds { get; set; } = 4.0;

    /// <summary>
    /// Human-readable description of why the next track was chosen.
    /// </summary>
    public string NextTrackReason { get; set; } = string.Empty;
}
