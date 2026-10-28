using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.Musical;
using SLSKDONET.Utils;

namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// Represents the live runtime state of ORBIT's Flow Mode engine.
/// Flow Mode selects and transitions tracks automatically for a continuous DJ-like experience.
///
/// The state is an immutable snapshot replaced wholesale on every change (see
/// <c>FlowModeService.RebuildFlowPath</c>), which is what makes it safe to bind from the UI on any
/// thread and cheap to diff.
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
    /// The planned next tracks, best first. Empty while Flow is off, or when it has nothing to work
    /// with (see <see cref="Status"/> / <see cref="StatusMessage"/>).
    /// </summary>
    public IReadOnlyList<FlowStep> FlowPath { get; set; } = [];

    /// <summary>Why the path is empty, or <see cref="FlowPlanStatus.Ready"/> when it is not.</summary>
    public FlowPlanStatus Status { get; set; } = FlowPlanStatus.Off;

    /// <summary>Human-readable explanation for a non-ready status (empty when ready).</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>The track the path was planned from (its display name), for "planned from X".</summary>
    public string PlannedFromName { get; set; } = string.Empty;

    /// <summary>Crossfade duration in seconds Flow will use for the next transition.</summary>
    public double CrossfadeDurationSeconds { get; set; } = 4.0;

    /// <summary>
    /// One-line summary of why the first hop was chosen, e.g. "Picked for same key, ±0.4 BPM".
    /// Shown under the visualizer and next to the Flow toggle.
    /// </summary>
    public string NextTrackReason { get; set; } = string.Empty;

    /// <summary>Playable queue tracks that were scored when this snapshot was produced.</summary>
    public int EvaluatedCandidates { get; set; }

    /// <summary>Of <see cref="EvaluatedCandidates"/>, how many had usable analysis.</summary>
    public int AnalysedCandidates { get; set; }

    /// <summary>When this snapshot was planned (null when Flow never planned).</summary>
    public DateTime? PlannedAt { get; set; }

    /// <summary>The immediate next hop, or null.</summary>
    public FlowStep? NextStep => FlowPath.Count > 0 ? FlowPath[0] : null;

    /// <summary>Everything after the immediate next hop.</summary>
    public IReadOnlyList<FlowStep> LaterSteps => FlowPath.Skip(1).ToList();

    public bool HasPath => FlowPath.Count > 0;

    /// <summary>True when Flow is on but has nothing to show (drives the "why" empty state).</summary>
    public bool IsBlocked => IsActive && !HasPath;

    public string PresetName => ActivePreset.ToDisplayName();

    /// <summary>"Energy +20%" / "Energy −40%" / "Neutral energy" — invariant, signed.</summary>
    public string EnergyNudgeText =>
        Math.Abs(EnergyNudge) < 0.005
            ? "Neutral energy"
            : NumFormat.Ui($"Energy {(EnergyNudge > 0 ? "+" : "−")}{(int)Math.Round(Math.Abs(EnergyNudge) * 100)}%");

    /// <summary>"3 of 12 queued analysed" — how much Flow had to work with.</summary>
    public string CandidateCoverageText =>
        EvaluatedCandidates == 0
            ? string.Empty
            : NumFormat.Ui($"{AnalysedCandidates} of {EvaluatedCandidates} queued track{(EvaluatedCandidates == 1 ? "" : "s")} analysed");
}
