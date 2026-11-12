using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Transitions;

public enum SnapKind { None, Beat, Bar, IncomingCue, AlignedCue }

public enum SnapMode { Bar, Beat }

public sealed record SnapCandidate(double Seconds, string Label, bool AlignedWithOutgoing = false);

public readonly record struct BarGrid(double DownbeatSeconds, double BeatSeconds);

public readonly record struct SnapResult(double Seconds, SnapKind Kind, string? Label);

/// <summary>
/// Pure magnetic-snap resolver for the transition overlay (issue #241). Kept free of view types
/// so it survives the shared-editor extraction (#204).
/// </summary>
public static class TransitionSnap
{
    /// <summary>
    /// Resolves <paramref name="rawTargetSeconds"/> to the nearest candidate within the pixel
    /// tolerance, preferring aligned cue pairs, then incoming cues, then bars, then beats.
    /// </summary>
    public static SnapResult Resolve(
        double rawTargetSeconds,
        IReadOnlyList<SnapCandidate> candidates,
        BarGrid grid,
        double pixelsPerSecond,
        double tolerancePx,
        SnapMode mode,
        bool disabled = false)
    {
        if (disabled || pixelsPerSecond <= 0 || tolerancePx <= 0)
            return new SnapResult(rawTargetSeconds, SnapKind.None, null);

        double toleranceSeconds = tolerancePx / pixelsPerSecond;
        SnapCandidate? bestAligned = null;
        SnapCandidate? bestCue = null;
        double bestAlignedDelta = double.MaxValue;
        double bestCueDelta = double.MaxValue;

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

        double unit = mode == SnapMode.Beat
            ? Math.Max(1e-6, grid.BeatSeconds)
            : Math.Max(1e-6, grid.BeatSeconds * 4.0);
        double snapped = grid.DownbeatSeconds + Math.Round((rawTargetSeconds - grid.DownbeatSeconds) / unit) * unit;
        if (Math.Abs(snapped - rawTargetSeconds) > toleranceSeconds)
            return new SnapResult(rawTargetSeconds, SnapKind.None, null);

        return new SnapResult(snapped, mode == SnapMode.Beat ? SnapKind.Beat : SnapKind.Bar,
            mode == SnapMode.Beat ? "beat" : "bar");
    }

    /// <summary>
    /// Builds candidates for B's cues, marking any that line up with one of A's cues (A's cue in
    /// shared time = <c>cue.Seconds - sourceTrigger</c>).
    /// </summary>
    public static IReadOnlyList<SnapCandidate> BuildCandidates(
        IEnumerable<(double Seconds, string Label)> incomingCues,
        IEnumerable<(double Seconds, string Label)> outgoingCues,
        double sourceTrigger,
        double toleranceSeconds)
    {
        var outgoingShared = outgoingCues
            .Select(c => (Seconds: c.Seconds - sourceTrigger, c.Label))
            .ToList();

        var result = new List<SnapCandidate>();
        foreach (var cue in incomingCues)
        {
            bool aligned = outgoingShared.Any(a => Math.Abs(a.Seconds - cue.Seconds) <= toleranceSeconds);
            result.Add(new SnapCandidate(cue.Seconds, $"B: {cue.Label}", aligned));
        }
        return result;
    }
}