using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Library.Rekordbox;

/// <summary>
/// A single Rekordbox TEMPO grid anchor: a BPM value effective from a point in time.
/// <see cref="Battito"/> is the 1-based position (1–4) of that beat within its bar, relative to Bar 1.
/// </summary>
public sealed record TempoAnchor(double InizioSeconds, double Bpm, int Battito = 1);

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

        var singleAnchor = new[] { new TempoAnchor(downbeatOffsetSeconds, fallbackBpm, Battito: 1) };

        bool hasEnoughBeatData = beatTimestampsSeconds.Count >= options.SmoothingWindowBeats + options.MinSustainedBeats;
        bool isUnstable = bpmStability.HasValue && bpmStability.Value < options.StabilityGateThreshold;

        if (!hasEnoughBeatData || !isUnstable)
            return singleAnchor;

        var beats = beatTimestampsSeconds;
        int downbeatIndex = NearestBeatIndex(beats, downbeatOffsetSeconds);
        var localBpm = new double[beats.Count - 1];
        for (int i = 0; i < localBpm.Length; i++)
        {
            var delta = beats[i + 1] - beats[i];
            localBpm[i] = delta > 0 ? 60.0 / delta : fallbackBpm;
        }

        var smoothed = SmoothRollingMean(localBpm, Math.Max(1, options.SmoothingWindowBeats));

        var anchors = new List<TempoAnchor>
        {
            new(downbeatOffsetSeconds, smoothed.Length > 0 ? smoothed[0] : fallbackBpm, ComputeBattito(downbeatIndex, downbeatIndex)),
        };
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

            anchors.Add(new TempoAnchor(anchorTime, candidateBpm, ComputeBattito(anchorBeatIndex, downbeatIndex)));
            baselineBpm = candidateBpm;
            lastAnchorTime = anchorTime;
        }

        return anchors;
    }

    /// <summary>
    /// Emits one TEMPO anchor at every point where a Rekordbox-sourced PQTZ grid changes tempo, so
    /// the export round-trips Rekordbox's own grid losslessly (issue #223). Rekordbox's per-beat BPM
    /// is not stored — only the beat times — so the section BPMs are recovered from the beat
    /// intervals and a run is split when the local tempo drifts by more than <paramref name="changeThresholdRatio"/>.
    /// </summary>
    public static IReadOnlyList<TempoAnchor> DeriveRekordboxLosslessAnchors(
        IReadOnlyList<double> beatTimestampsSeconds,
        double downbeatOffsetSeconds,
        double changeThresholdRatio = 0.005)
    {
        if (beatTimestampsSeconds.Count < 2)
            return Array.Empty<TempoAnchor>();

        var beats = beatTimestampsSeconds;
        int n = beats.Count;
        int downbeatIndex = NearestBeatIndex(beats, downbeatOffsetSeconds);

        var anchors = new List<TempoAnchor>();
        int runStart = 0;
        // A tempo change is at beat i (1 <= i <= n-2) when the interval to its successor differs
        // from the interval to its predecessor by more than the threshold.
        for (int i = 1; i <= n - 2; i++)
        {
            double prevTempo = 60.0 / Math.Max(beats[i] - beats[i - 1], 1e-9);
            double curTempo = 60.0 / Math.Max(beats[i + 1] - beats[i], 1e-9);
            if (Math.Abs(curTempo - prevTempo) / prevTempo > changeThresholdRatio)
            {
                anchors.Add(new TempoAnchor(beats[runStart], SectionBpm(beats, runStart, i - 1), ComputeBattito(runStart, downbeatIndex)));
                runStart = i;
            }
        }

        anchors.Add(new TempoAnchor(beats[runStart], SectionBpm(beats, runStart, n - 1), ComputeBattito(runStart, downbeatIndex)));
        return anchors;
    }

    /// <summary>Median BPM over the intervals of the beats in the inclusive range [startBeat, endBeatInclusive].</summary>
    private static double SectionBpm(IReadOnlyList<double> beats, int startBeat, int endBeatInclusive)
    {
        var bpms = new List<double>();
        for (int j = startBeat; j < endBeatInclusive; j++)
        {
            double interval = beats[j + 1] - beats[j];
            if (interval > 0) bpms.Add(60.0 / interval);
        }
        if (bpms.Count == 0) return 0;
        bpms.Sort();
        return bpms[bpms.Count / 2];
    }

    private static int NearestBeatIndex(IReadOnlyList<double> beats, double time)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < beats.Count; i++)
        {
            double d = Math.Abs(beats[i] - time);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>1-based beat position within the bar (1–4), relative to Bar 1. Pickup beats before Bar 1 count backwards (beat 4 of the previous bar, etc.).</summary>
    internal static int ComputeBattito(int anchorBeatIndex, int downbeatBeatIndex) =>
        (((anchorBeatIndex - downbeatBeatIndex) % 4) + 4) % 4 + 1;

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
