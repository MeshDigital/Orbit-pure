using System;
using System.Collections.Generic;

namespace SLSKDONET.ViewModels;

/// <summary>A labelled point on the overlay timeline (EQ swap, bass swap).</summary>
public readonly record struct OverlayMarker(double Seconds, string Label);

/// <summary>
/// The transition editor's overlay view as numbers: one shared timeline (seconds at the project
/// tempo, 0 = the mix-out point = where B comes in) on which the outgoing track A and the incoming
/// track B are laid over each other, aligned by their mix points and tempo-corrected to the
/// project BPM (B plays at <see cref="IncomingRate"/>× its native speed, as in auto-mix playback).
///
/// Shown range: <see cref="PreSeconds"/> of A alone, the blend window (<see cref="WindowSeconds"/>,
/// the Length in bars), then <see cref="PostSeconds"/> of B alone — the same layout as Preview, so
/// the preview position maps straight onto it. Immutable; the editor builds a new one whenever a
/// mix point, nudge, length, preset or tempo changes.
/// </summary>
public sealed record TransitionOverlayModel
{
    public double ProjectBpm { get; init; } = 128;
    public double OutgoingBpm { get; init; }
    public double IncomingBpm { get; init; }
    /// <summary>Mix-out point in A (seconds, nudge included).</summary>
    public double SourceTrigger { get; init; }
    /// <summary>Mix-in point in B (seconds).</summary>
    public double TargetTrigger { get; init; }
    public double WindowSeconds { get; init; }
    public double PreSeconds { get; init; }
    public double PostSeconds { get; init; }
    public double OutgoingDuration { get; init; }
    public double IncomingDuration { get; init; }
    /// <summary>B's first downbeat (seconds in B), for snapping a dragged mix-in onto B's grid.</summary>
    public double IncomingDownbeat { get; init; }
    /// <summary>A's / B's gain across the window (0..1, evenly sampled; same automation as playback).</summary>
    public IReadOnlyList<float> OutgoingGain { get; init; } = Array.Empty<float>();
    public IReadOnlyList<float> IncomingGain { get; init; } = Array.Empty<float>();
    public IReadOnlyList<OverlayMarker> Markers { get; init; } = Array.Empty<OverlayMarker>();

    public double StartTime => -PreSeconds;
    public double EndTime => WindowSeconds + PostSeconds;
    public double SpanSeconds => Math.Max(1e-6, EndTime - StartTime);

    public double BarSeconds => 240.0 / (ProjectBpm > 0 ? ProjectBpm : 128.0);

    /// <summary>Track-seconds of A per shared second (1 when A sets the project tempo).</summary>
    public double OutgoingRate => OutgoingBpm > 0 && ProjectBpm > 0 ? ProjectBpm / OutgoingBpm : 1.0;
    /// <summary>Track-seconds of B per shared second: B is sped up / slowed to the project tempo.</summary>
    public double IncomingRate => IncomingBpm > 0 && ProjectBpm > 0 ? ProjectBpm / IncomingBpm : 1.0;

    /// <summary>Where A is (its own seconds) at shared time <paramref name="t"/>.</summary>
    public double OutgoingTrackTime(double t) => SourceTrigger + t * OutgoingRate;
    /// <summary>Where B is (its own seconds) at shared time <paramref name="t"/>.</summary>
    public double IncomingTrackTime(double t) => TargetTrigger + t * IncomingRate;

    /// <summary>A sounds from the start of the view to the end of the window (and while it has audio).</summary>
    public bool OutgoingSoundsAt(double t) => t <= WindowSeconds + 1e-9 && OutgoingTrackTime(t) is var a && a >= 0 && (OutgoingDuration <= 0 || a <= OutgoingDuration);
    /// <summary>B sounds from the start of the window on (and while it has audio).</summary>
    public bool IncomingSoundsAt(double t) => t >= -1e-9 && IncomingTrackTime(t) is var b && b >= 0 && (IncomingDuration <= 0 || b <= IncomingDuration);

    /// <summary>The overlap (blend) window on the shared timeline.</summary>
    public (double Start, double End) Overlap => (0, WindowSeconds);

    public int WindowBars => (int)Math.Round(WindowSeconds / BarSeconds);

    public double OutgoingGainAt(double t) => t < 0 ? 1 : t > WindowSeconds ? 0 : Sample(OutgoingGain, t / Math.Max(1e-9, WindowSeconds), 1.0 - t / Math.Max(1e-9, WindowSeconds));
    public double IncomingGainAt(double t) => t < 0 ? 0 : t > WindowSeconds ? 1 : Sample(IncomingGain, t / Math.Max(1e-9, WindowSeconds), t / Math.Max(1e-9, WindowSeconds));

    private static double Sample(IReadOnlyList<float> curve, double fraction, double fallback)
    {
        if (curve.Count == 0) return Math.Clamp(fallback, 0, 1);
        double pos = Math.Clamp(fraction, 0, 1) * (curve.Count - 1);
        int i = (int)Math.Floor(pos);
        int j = Math.Min(i + 1, curve.Count - 1);
        return curve[i] + (curve[j] - curve[i]) * (pos - i);
    }

    /// <summary>Bar label on the shared grid: 1 is the first bar of the blend, -1 the bar before it.</summary>
    public static int BarLabel(int barIndexFromMixOut) => barIndexFromMixOut >= 0 ? barIndexFromMixOut + 1 : barIndexFromMixOut;

    /// <summary>
    /// The mix-in point after dragging B by <paramref name="dragSharedSeconds"/> on the shared
    /// timeline (to the right = B comes in from an earlier point of B, since its content moves later),
    /// snapped to B's own bar (or beat) grid and kept inside B.
    /// </summary>
    public double ShiftedTargetTrigger(double dragSharedSeconds, bool snapToBar = true)
    {
        double raw = TargetTrigger - dragSharedSeconds * IncomingRate;
        double beat = 60.0 / (IncomingBpm > 0 ? IncomingBpm : (ProjectBpm > 0 ? ProjectBpm : 128.0));
        double unit = snapToBar ? 4 * beat : beat;
        double snapped = IncomingDownbeat + Math.Round((raw - IncomingDownbeat) / unit) * unit;
        if (snapped < 0) snapped += Math.Ceiling(-snapped / unit) * unit;
        if (IncomingDuration > 0) snapped = Math.Min(snapped, Math.Max(0, IncomingDuration - unit));
        return Math.Max(0, snapped);
    }

    /// <summary>Shared-time offset at which B is drawn for a mix-in of <paramref name="targetTrigger"/>
    /// (0 for the current one): the ghost position while dragging.</summary>
    public double DrawOffsetFor(double targetTrigger) => (TargetTrigger - targetTrigger) / IncomingRate;

    /// <summary>Builds the overlay for the editor's current state.</summary>
    public static TransitionOverlayModel Build(
        double outgoingBpm, double incomingBpm, double sourceTrigger, double targetTrigger, double windowSeconds,
        double contextSeconds, double outgoingDuration, double incomingDuration, double incomingDownbeat,
        IReadOnlyList<float>? outgoingGain, IReadOnlyList<float>? incomingGain, IReadOnlyList<OverlayMarker>? markers)
    {
        double project = outgoingBpm > 0 ? outgoingBpm : 128.0;
        return new TransitionOverlayModel
        {
            ProjectBpm = project,
            OutgoingBpm = outgoingBpm,
            IncomingBpm = incomingBpm,
            SourceTrigger = sourceTrigger,
            TargetTrigger = targetTrigger,
            WindowSeconds = Math.Max(0, windowSeconds),
            PreSeconds = Math.Max(0, contextSeconds),
            PostSeconds = Math.Max(0, contextSeconds),
            OutgoingDuration = outgoingDuration,
            IncomingDuration = incomingDuration,
            IncomingDownbeat = Math.Max(0, incomingDownbeat),
            OutgoingGain = outgoingGain ?? Array.Empty<float>(),
            IncomingGain = incomingGain ?? Array.Empty<float>(),
            Markers = markers ?? Array.Empty<OverlayMarker>(),
        };
    }
}
