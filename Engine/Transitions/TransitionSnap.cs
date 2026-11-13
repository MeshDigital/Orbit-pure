using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Transitions;

public enum SnapKind { None, Beat, Bar, IncomingCue, AlignedCue }

public enum SnapMode { Bar, Beat }

/// <summary>A mix-in point (seconds in B) the drag can snap to.</summary>
public sealed record SnapCandidate(double Seconds, string Label, bool AlignedWithOutgoing = false);

public readonly record struct BarGrid(double DownbeatSeconds, double BeatSeconds);

public readonly record struct SnapResult(double Seconds, SnapKind Kind, string? Label);

/// <summary>
/// Pure magnetic-snap resolver for the transition overlay (issue #241). Kept free of view types
/// so it survives the shared-editor extraction (#204).
/// All seconds here are B's own track seconds (the mix-in point / target trigger).
/// </summary>
public static class TransitionSnap
{
    /// <summary>
    /// Resolves <paramref name="rawTargetSeconds"/>: a cue candidate within the pixel tolerance wins
    /// (aligned cue pairs before lone B cues); otherwise the position always lands on the nearest
    /// bar (or beat) of B, as the overlay drag always has. <paramref name="disabled"/> (Alt) returns
    /// the raw position.
    /// </summary>
    /// <param name="pixelsPerSecond">Screen pixels per second of B (shared px/s ÷ B's playback rate).</param>
    public static SnapResult Resolve(
        double rawTargetSeconds,
        IReadOnlyList<SnapCandidate> candidates,
        BarGrid grid,
        double pixelsPerSecond,
        double tolerancePx,
        SnapMode mode,
        bool disabled = false)
    {
        if (disabled)
            return new SnapResult(rawTargetSeconds, SnapKind.None, null);

        if (pixelsPerSecond > 0 && tolerancePx > 0)
        {
            double toleranceSeconds = tolerancePx / pixelsPerSecond;
            SnapCandidate? bestAligned = null, bestCue = null;
            double bestAlignedDelta = double.MaxValue, bestCueDelta = double.MaxValue;

            foreach (var c in candidates)
            {
                var delta = Math.Abs(c.Seconds - rawTargetSeconds);
                if (delta > toleranceSeconds) continue;
                if (c.AlignedWithOutgoing)
                {
                    if (delta < bestAlignedDelta) { bestAlignedDelta = delta; bestAligned = c; }
                }
                else if (delta < bestCueDelta)
                {
                    bestCueDelta = delta;
                    bestCue = c;
                }
            }

            if (bestAligned is { } aligned)
                return new SnapResult(aligned.Seconds, SnapKind.AlignedCue, aligned.Label);
            if (bestCue is { } cue)
                return new SnapResult(cue.Seconds, SnapKind.IncomingCue, cue.Label);
        }

        double unit = mode == SnapMode.Beat
            ? Math.Max(1e-6, grid.BeatSeconds)
            : Math.Max(1e-6, grid.BeatSeconds * 4.0);
        double snapped = grid.DownbeatSeconds + Math.Round((rawTargetSeconds - grid.DownbeatSeconds) / unit) * unit;
        return mode == SnapMode.Beat
            ? new SnapResult(snapped, SnapKind.Beat, null)
            : new SnapResult(snapped, SnapKind.Bar, null);
    }

    /// <summary>
    /// Builds the cue targets for dragging B:
    /// <list type="bullet">
    /// <item>each of B's cues on the mix point itself (mix-in = that cue);</item>
    /// <item>each (A cue, B cue) pair placed so both play at the same moment. With A's mix-out at
    /// <paramref name="sourceTrigger"/>, A's cue <c>a</c> sounds at shared time <c>a − sourceTrigger</c>,
    /// and B's cue <c>b</c> at <c>(b − T) / rate</c> for mix-in <c>T</c>; they coincide for
    /// <c>T = b − rate·(a − sourceTrigger)</c>. Only pairs where B is already playing
    /// (A's cue at or after the mix-out) and <c>T</c> lies inside B are kept.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<SnapCandidate> BuildCandidates(
        IEnumerable<(double Seconds, string Label)> incomingCues,
        IEnumerable<(double Seconds, string Label)> outgoingCues,
        double sourceTrigger,
        double incomingRate,
        double incomingDuration)
    {
        if (incomingRate <= 0) incomingRate = 1.0;
        var incoming = incomingCues.ToList();
        var outgoing = outgoingCues.ToList();
        bool InsideB(double t) => t >= 0 && (incomingDuration <= 0 || t <= incomingDuration);

        var result = new List<SnapCandidate>();
        foreach (var b in incoming)
        {
            if (InsideB(b.Seconds))
                result.Add(new SnapCandidate(b.Seconds, $"B: {b.Label}"));
        }

        foreach (var a in outgoing)
        {
            double aShared = a.Seconds - sourceTrigger;
            if (aShared < 0) continue; // B isn't playing yet at that A cue
            foreach (var b in incoming)
            {
                double target = b.Seconds - incomingRate * aShared;
                if (InsideB(target))
                    result.Add(new SnapCandidate(target, $"B: {b.Label} ↔ A: {a.Label}", AlignedWithOutgoing: true));
            }
        }
        return result;
    }
}
