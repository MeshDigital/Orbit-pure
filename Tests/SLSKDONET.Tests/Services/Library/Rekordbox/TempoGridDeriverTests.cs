using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services.Library.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Rekordbox;

public class TempoGridDeriverTests
{
    /// <summary>Builds a beat-timestamp array from consecutive (beatCount, bpm) segments.</summary>
    private static List<double> BuildBeatGrid(params (int Count, double Bpm)[] segments)
    {
        var beats = new List<double>();
        double t = 0;
        foreach (var (count, bpm) in segments)
        {
            var interval = 60.0 / bpm;
            for (int i = 0; i < count; i++)
            {
                beats.Add(t);
                t += interval;
            }
        }
        return beats;
    }

    [Fact]
    public void DeriveAnchors_ConstantBpmBeatGrid_ReturnsSingleAnchor()
    {
        var beats = BuildBeatGrid((80, 128.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.3f);

        Assert.Single(anchors);
        Assert.Equal(128.0, anchors[0].Bpm, precision: 1);
    }

    [Fact]
    public void DeriveAnchors_HighStability_ReturnsSingleAnchorEvenWithJitteryGrid()
    {
        // A grid with a real BPM step, but high stability should still short-circuit to one anchor —
        // this is the safety net that keeps the vast majority of tracks on the old, safe behavior.
        var beats = BuildBeatGrid((40, 128.0), (40, 132.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.95f);

        Assert.Single(anchors);
    }

    [Fact]
    public void DeriveAnchors_NullStability_TreatedConservativelyAsStable()
    {
        var beats = BuildBeatGrid((40, 128.0), (40, 132.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: null);

        Assert.Single(anchors);
    }

    [Fact]
    public void DeriveAnchors_LowStabilityWithSustainedBpmStep_ReturnsTwoAnchorsAtCorrectOffsets()
    {
        var beats = BuildBeatGrid((60, 128.0), (60, 132.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.3f);

        Assert.True(anchors.Count >= 2, $"Expected at least 2 anchors, got {anchors.Count}");
        Assert.Equal(beats[0], anchors[0].InizioSeconds, precision: 2);
        Assert.Equal(128.0, anchors[0].Bpm, precision: 0);
        // The last anchor should reflect the new (higher) tempo.
        Assert.True(anchors[^1].Bpm > anchors[0].Bpm, "Expected the final anchor's BPM to reflect the tempo step up.");
    }

    [Fact]
    public void DeriveAnchors_LowStabilityWithBriefBlip_DoesNotEmitExtraAnchor()
    {
        // A single-beat outlier (e.g. a mis-detected tick) surrounded by constant tempo should not
        // be treated as a sustained tempo change.
        var beats = BuildBeatGrid((40, 128.0));
        // Inject one outlier beat, then resume the constant grid.
        beats[20] = beats[19] + 60.0 / 300.0; // a spuriously fast single interval
        for (int i = 21; i < beats.Count; i++)
            beats[i] = beats[20] + (i - 20) * (60.0 / 128.0);

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.3f);

        Assert.Single(anchors);
    }

    [Fact]
    public void DeriveAnchors_RespectsMinAnchorSpacing()
    {
        // Two tempo steps placed very close together in time — the second must be suppressed by
        // MinAnchorSpacingSeconds even though it individually qualifies as a sustained change.
        var beats = BuildBeatGrid((20, 128.0), (20, 132.0), (20, 128.0));
        var options = new TempoGridOptions(SmoothingWindowBeats: 4, MinSustainedBeats: 4, MinAnchorSpacingSeconds: 1000.0);

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.3f, options);

        // With an enormous MinAnchorSpacingSeconds, only the very first anchor can ever land.
        Assert.Single(anchors);
    }

    [Fact]
    public void DeriveAnchors_RespectsMaxAnchorsCap()
    {
        // Alternate tempo aggressively with a permissive config so many anchors WOULD be emitted
        // if uncapped — assert the hard cap holds.
        var segments = new List<(int, double)>();
        for (int i = 0; i < 20; i++)
            segments.Add((10, i % 2 == 0 ? 128.0 : 140.0));
        var beats = BuildBeatGrid(segments.ToArray());

        var options = new TempoGridOptions(SmoothingWindowBeats: 2, MinSustainedBeats: 2, MinAnchorSpacingSeconds: 0.0, MaxAnchors: 3);

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.1f, options);

        Assert.True(anchors.Count <= 3, $"Expected at most 3 anchors, got {anchors.Count}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void DeriveAnchors_EmptyOrShortBeatGrid_FallsBackToSingleAnchor(int beatCount)
    {
        var beats = Enumerable.Range(0, beatCount).Select(i => i * 0.5).ToList();

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: 1.23, fallbackBpm: 128.0, bpmStability: 0.1f);

        Assert.Single(anchors);
        Assert.Equal(1.23, anchors[0].InizioSeconds);
        Assert.Equal(128.0, anchors[0].Bpm);
    }

    [Fact]
    public void DeriveAnchors_UsesDownbeatOffsetAsFirstAnchorInizio()
    {
        var beats = BuildBeatGrid((80, 128.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: 0.417, fallbackBpm: 128.0, bpmStability: 0.9f);

        Assert.Equal(0.417, anchors[0].InizioSeconds);
    }

    [Fact]
    public void DeriveAnchors_NoBpm_ReturnsEmptyList()
    {
        var beats = BuildBeatGrid((80, 128.0));

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: 0.0, fallbackBpm: 0.0, bpmStability: 0.3f);

        Assert.Empty(anchors);
    }
}
