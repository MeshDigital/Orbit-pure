using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Library.Rekordbox;

/// <summary>A single Rekordbox TEMPO grid anchor: a BPM value effective from a point in time.</summary>
public sealed record TempoAnchor(double InizioSeconds, double Bpm);

/// <summary>Tuning knobs for <see cref="TempoGridDeriver.DeriveAnchors"/>. Defaults are conservative.</summary>
public sealed record TempoGridOptions(
    int SmoothingWindowBeats = 8,
    double DriftThresholdRatio = 0.015,
    int MinSustainedBeats = 16,
    double MinAnchorSpacingSeconds = 8.0,
    int MaxAnchors = 32,
    float StabilityGateThreshold = 0.7f);

/// <summary>
/// Derives one or more Rekordbox TEMPO anchors from a track's real beat-timestamp grid, instead
/// of a single hardcoded anchor at time 0. Pure logic, no I/O — independently unit-testable.
/// </summary>
public static class TempoGridDeriver
{
    /// <summary>
    /// Returns TEMPO anchors for a track. Always returns a single anchor at
    /// <paramref name="downbeatOffsetSeconds"/> (the common, safe case) unless the beat grid is
    /// long enough to analyze AND <paramref name="bpmStability"/> indicates a genuinely
    /// drifting/unstable tempo — matching this codebase's own documented convention that
    /// BpmStability &lt; 0.7 means "unstable/drifting tempo" (<see cref="SLSKDONET.Models.PlaylistTrack.BpmStability"/>).
    /// Returns an empty list when there is no usable BPM at all (caller should omit TEMPO entirely).
    /// </summary>
    public static IReadOnlyList<TempoAnchor> DeriveAnchors(
        IReadOnlyList<double> beatTimestampsSeconds,
        double downbeatOffsetSeconds,
        double fallbackBpm,
        float? bpmStability,
        TempoGridOptions? options = null)
    {
        options ??= new TempoGridOptions();

        if (fallbackBpm <= 0)
            return Array.Empty<TempoAnchor>();

        var singleAnchor = new[] { new TempoAnchor(downbeatOffsetSeconds, fallbackBpm) };

        bool hasEnoughBeatData = beatTimestampsSeconds.Count >= options.SmoothingWindowBeats + options.MinSustainedBeats;
        bool isUnstable = bpmStability.HasValue && bpmStability.Value < options.StabilityGateThreshold;

        if (!hasEnoughBeatData || !isUnstable)
            return singleAnchor;

        var beats = beatTimestampsSeconds;
        var localBpm = new double[beats.Count - 1];
        for (int i = 0; i < localBpm.Length; i++)
        {
            var delta = beats[i + 1] - beats[i];
            localBpm[i] = delta > 0 ? 60.0 / delta : fallbackBpm;
        }

        var smoothed = SmoothRollingMean(localBpm, Math.Max(1, options.SmoothingWindowBeats));

        var anchors = new List<TempoAnchor> { new(downbeatOffsetSeconds, smoothed.Length > 0 ? smoothed[0] : fallbackBpm) };
        double baselineBpm = anchors[0].Bpm;
        double lastAnchorTime = downbeatOffsetSeconds;
        int sustainedCount = 0;
        double candidateBpm = baselineBpm;

        for (int i = 1; i < smoothed.Length; i++)
        {
            var deviation = baselineBpm > 0 ? Math.Abs(smoothed[i] - baselineBpm) / baselineBpm : 0;
            if (deviation > options.DriftThresholdRatio)
            {
                sustainedCount++;
                candidateBpm = smoothed[i];
            }
            else
            {
                sustainedCount = 0;
            }

            if (sustainedCount < options.MinSustainedBeats)
                continue;

            int anchorBeatIndex = Math.Max(0, i - options.MinSustainedBeats + 1);
            double anchorTime = beats[Math.Min(anchorBeatIndex, beats.Count - 1)];
            sustainedCount = 0;

            if (anchorTime - lastAnchorTime < options.MinAnchorSpacingSeconds || anchors.Count >= options.MaxAnchors)
            {
                // Too close to the previous anchor, or hit the cap — rebase the baseline so we
                // don't keep re-triggering on the same drift, but don't emit a new anchor.
                baselineBpm = candidateBpm;
                continue;
            }

            anchors.Add(new TempoAnchor(anchorTime, candidateBpm));
            baselineBpm = candidateBpm;
            lastAnchorTime = anchorTime;
        }

        return anchors;
    }

    private static double[] SmoothRollingMean(double[] values, int window)
    {
        var result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int start = Math.Max(0, i - window / 2);
            int end = Math.Min(values.Length, start + window);
            start = Math.Max(0, end - window);

            double sum = 0;
            for (int j = start; j < end; j++)
                sum += values[j];
            result[i] = sum / Math.Max(1, end - start);
        }
        return result;
    }
}
