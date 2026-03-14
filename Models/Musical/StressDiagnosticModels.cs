using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Models.Musical
{
    /// <summary>
    /// Severity level for setlist stress-test diagnostics.
    /// </summary>
    public enum StressSeverity
    {
        Healthy = 0,    // Score 0-39: Green on HealthBar
        Warning = 1,    // Score 40-69: Yellow on HealthBar
        Critical = 2    // Score 70-100: Red on HealthBar
    }

    /// <summary>
    /// Primary failure category identified by stress-test.
    /// </summary>
    public enum TransitionFailureType
    {
        DeadEnd,            // Composite low score across all dimensions
        EnergyPlateau,      // Flat gradient, no momentum
        HarmonicClash,      // Key incompatibility
        VocalConflict,      // Vocal overlap issues
        TempoJump,          // BPM incompatibility
        StructuralMismatch, // Phrase/archetype incompatibility
        None                // No problem detected
    }

    /// <summary>
    /// Bridging track suggestion to resolve a transition dead-end.
    /// Contains full reasoning and metadata for UI display.
    /// </summary>
    public class RescueSuggestion
    {
        /// <summary>
        /// The bridging track from library that solves the transition gap.
        /// </summary>
        public object TargetTrack { get; set; } = null!; // Dynamic reference - actual type injected by service

        /// <summary>
        /// Numerical score (0-100) indicating quality of bridge.
        /// Composite: EnergyFit (30%) + TempoFit (30%) + HarmonicFit (40%)
        /// </summary>
        public int BridgeQualityScore { get; set; }

        /// <summary>
        /// Human-readable rationale for why this track solves the problem.
        /// Examples:
        ///   "Lifts energy from 0.62 → 0.78, perfect staging for the drop"
        ///   "Key compatible with both (8A → 11A → 10B), harmonic landing"
        ///   "Vocal-to-instrumental bridge: clears the vocal clash safely"
        /// </summary>
        public string WhyItFitsFull { get; set; } = string.Empty;

        /// <summary>
        /// Concise one-liner for the HealthBar tooltip.
        /// Example: "Energy bridge + Harmonic safe zone"
        /// </summary>
        public string WhyItFitsShort { get; set; } = string.Empty;

        /// <summary>
        /// Description of problems this rescue solves.
        /// Example: "Energy Plateau + Harmonic Clash"
        /// </summary>
        public string ProblemsAddressed { get; set; } = string.Empty;

        /// <summary>
        /// Estimated optimal cut point in seconds (for waveform highlighting).
        /// </summary>
        public double OptimalCutSeconds { get; set; }
    }

    /// <summary>
    /// Stress diagnostics for a single transition (Track[i] → Track[i+1]).
    /// Represents the per-transition result from the stress-test scan.
    /// </summary>
    public class TransitionStressPoint
    {
        /// <summary>
        /// Index of the source track (0-based) in the setlist.
        /// </summary>
        public int FromTrackIndex { get; set; }

        /// <summary>
        /// Index of the destination track.
        /// </summary>
        public int ToTrackIndex { get; set; }

        /// <summary>
        /// Composite failure severity (0-100).
        /// Weighted: EnergyPlateau (25%) + HarmonicIncompatibility (35%)
        ///           + VocalClash (30%) + TempoJitter (10%)
        /// </summary>
        public int SeverityScore { get; set; }

        /// <summary>
        /// Categorization of the primary failure type.
        /// </summary>
        public TransitionFailureType PrimaryFailure { get; set; }

        /// <summary>
        /// Textual classification for display.
        /// Examples: "Dead-End", "Energy Plateau", "Vocal Clash"
        /// </summary>
        public string PrimaryProblem { get; set; } = string.Empty;

        /// <summary>
        /// Detailed reasoning for why this transition fails.
        /// Built by MentorReasoningBuilder for display in Forensic Inspector.
        /// Includes sections like "Energy Analysis", "Harmonic Verdict", etc.
        /// </summary>
        public string FailureReasoning { get; set; } = string.Empty;

        /// <summary>
        /// 1-3 rescue track suggestions ordered by BridgeQualityScore (highest first).
        /// </summary>
        public List<RescueSuggestion> RescueSuggestions { get; set; } = new();

        /// <summary>
        /// Returns the SeverityLevel based on SeverityScore.
        /// </summary>
        public StressSeverity SeverityLevel
        {
            get
            {
                if (SeverityScore < 40) return StressSeverity.Healthy;
                if (SeverityScore < 70) return StressSeverity.Warning;
                return StressSeverity.Critical;
            }
        }
    }

    /// <summary>
    /// Complete stress-test diagnostic report for an entire setlist.
    /// Returned by SetlistStressTestService.RunDiagnosticAsync().
    /// Includes overall health score, per-transition stress points, and mentoring narrative.
    /// </summary>
    public class StressDiagnosticReport
    {
        /// <summary>
        /// Reference to the setlist that was analyzed.
        /// </summary>
        public Guid SetListId { get; set; }

        /// <summary>
        /// Overall setlist health (0-100).
        /// 100 = flawless; 50 = usable with caution; <30 = major rework needed.
        /// Calculated as: 100 - (average of all transition SeverityScores)
        /// </summary>
        public int OverallHealthScore { get; set; }

        /// <summary>
        /// Collection of per-transition diagnostics.
        /// Index [i] = diagnostics for transition from Track[i] to Track[i+1].
        /// </summary>
        public List<TransitionStressPoint> StressPoints { get; set; } = new();

        /// <summary>
        /// Count of critical (red) transitions.
        /// </summary>
        public int CriticalCount => StressPoints.FindAll(s => s.SeverityScore >= 70).Count;

        /// <summary>
        /// Count of warning (yellow) transitions.
        /// </summary>
        public int WarningCount => StressPoints.FindAll(s => s.SeverityScore >= 40 && s.SeverityScore < 70).Count;

        /// <summary>
        /// Count of healthy (green) transitions.
        /// </summary>
        public int HealthyCount => StressPoints.FindAll(s => s.SeverityScore < 40).Count;

        /// <summary>
        /// Timestamp of when scan completed.
        /// </summary>
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Overall duration of setlist in seconds.
        /// Useful for comparison and trend analysis.
        /// </summary>
        public double SetlistDurationSeconds { get; set; }

        /// <summary>
        /// Mentoring narrative explaining the overall setlist arc.
        /// Built by MentorReasoningBuilder with sections, bullets, and verdict.
        /// Explains energy flow, key journey, vocal management strategy, etc.
        /// </summary>
        public string SetlistNarrativeMentoring { get; set; } = string.Empty;

        /// <summary>
        /// Quick summary message for UI display.
        /// Examples:
        ///   "Perfect flow detected! Ready for broadcast."
        ///   "3 warning points: Consider rescuing transition #4 and #8."
        ///   "⚠ Critical: 2 dead-ends detected. Major rework recommended."
        /// </summary>
        public string QuickSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Phase 6: Result of applying a rescue track to a setlist.
    /// Indicates success/failure, action taken (INSERT or REPLACE), and updated setlist.
    /// </summary>
    public class ApplyRescueResult
    {
        /// <summary>
        /// Whether the rescue operation succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable message describing the result.
        /// Examples:
        ///   "✓ Rescue track 'Pump It' inserted as bridge."
        ///   "✗ Invalid transition indices."
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The action taken: "INSERT" (bridge) or "REPLACE" (swap).
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Updated setlist with rescue track applied.
        /// </summary>
        public SetListEntity UpdatedSetlist { get; set; } = null!;

        /// <summary>
        /// Number of transitions affected by the change.
        /// Usually 2 (before and after the new/replaced track).
        /// </summary>
        public int AffectedTransitions { get; set; }

        /// <summary>
        /// Updated stress report after rescue was applied.
        /// (Will be calculated if needed for immediate feedback)
        /// </summary>
        public StressDiagnosticReport UpdatedReport { get; set; } = null!;
    }
}
