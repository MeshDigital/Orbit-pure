using System;
using System.Collections.Generic;
using SLSKDONET.Services.Library.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Rekordbox;

/// <summary>
/// Battito (1-based beat position within the bar) and the lossless Rekordbox round-trip mode (#223).
/// </summary>
public class TempoGridBattitoTests
{
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

    [Theory]
    [InlineData(0, 0, 1)]   // Bar 1, beat 1
    [InlineData(1, 0, 2)]   // beat 2
    [InlineData(2, 0, 3)]   // beat 3
    [InlineData(3, 0, 4)]   // beat 4
    [InlineData(4, 0, 1)]   // next bar, beat 1
    [InlineData(1, 2, 4)]   // pickup right before Bar 1 (downbeat at index 2) -> beat 4 of the previous bar
    [InlineData(5, 2, 4)]   // downbeat idx 2, anchor idx 5 -> (5-2)=3 -> beat 4
    public void ComputeBattito_MatchesPositionRelativeToBarOne(int anchorIdx, int downbeatIdx, int expected)
    {
        Assert.Equal(expected, TempoGridDeriver.ComputeBattito(anchorIdx, downbeatIdx));
    }

    [Fact]
    public void DeriveAnchors_MultiAnchor_CarriesCorrectBattito()
    {
        var beats = BuildBeatGrid((60, 128.0), (60, 132.0));
        int downbeatIndex = 0; // beats[0] is Bar 1

        var anchors = TempoGridDeriver.DeriveAnchors(beats, downbeatOffsetSeconds: beats[0], fallbackBpm: 128.0, bpmStability: 0.3f);

        Assert.True(anchors.Count >= 2);
        Assert.Equal(1, anchors[0].Battito);
        foreach (var anchor in anchors)
        {
            // The anchor's InizioSeconds is a beat in the grid; its Battito must match its index.
            int idx = 0;
            double best = double.MaxValue;
            for (int i = 0; i < beats.Count; i++)
            {
                double d = Math.Abs(beats[i] - anchor.InizioSeconds);
                if (d < best) { best = d; idx = i; }
            }
            Assert.Equal(TempoGridDeriver.ComputeBattito(idx, downbeatIndex), anchor.Battito);
        }
    }

    [Fact]
    public void DeriveRekordboxLosslessAnchors_SplitsAtTempoChange()
    {
        var beats = BuildBeatGrid((40, 108.0), (40, 128.0));

        var anchors = TempoGridDeriver.DeriveRekordboxLosslessAnchors(beats, downbeatOffsetSeconds: beats[0]);

        Assert.Equal(2, anchors.Count);
        Assert.Equal(beats[0], anchors[0].InizioSeconds, 2);
        Assert.Equal(108.0, anchors[0].Bpm, 1);
        Assert.Equal(1, anchors[0].Battito);

        Assert.Equal(beats[40], anchors[1].InizioSeconds, 2);
        Assert.Equal(128.0, anchors[1].Bpm, 1);
    }

    [Fact]
    public void DeriveRekordboxLosslessAnchors_ConstantGrid_ReturnsSingleAnchor()
    {
        var beats = BuildBeatGrid((120, 174.0));

        var anchors = TempoGridDeriver.DeriveRekordboxLosslessAnchors(beats, downbeatOffsetSeconds: beats[0]);

        Assert.Single(anchors);
        Assert.Equal(174.0, anchors[0].Bpm, 1);
    }

    [Fact]
    public void RekordboxLosslessAnchors_ReproduceBeatTimes_WithinOneMillisecond()
    {
        var beats = BuildBeatGrid((40, 108.0), (40, 128.0), (40, 124.0));
        var anchors = TempoGridDeriver.DeriveRekordboxLosslessAnchors(beats, beats[0]);

        // Reconstruct beats the way Rekordbox interpolates TEMPO anchors: from each Inizio at Bpm
        // until the next anchor (or past the end).
        var reconstructed = new List<double>();
        for (int a = 0; a < anchors.Count; a++)
        {
            double start = anchors[a].InizioSeconds;
            double end = a + 1 < anchors.Count ? anchors[a + 1].InizioSeconds : beats[^1] + 1.0;
            double period = 60.0 / anchors[a].Bpm;
            for (double t = start; t < end - 1e-9; t += period)
                reconstructed.Add(t);
        }

        Assert.True(reconstructed.Count >= beats.Count, $"reconstructed {reconstructed.Count} beats, expected >= {beats.Count}");
        for (int i = 0; i < beats.Count; i++)
            Assert.Equal(beats[i], reconstructed[i], 3); // 1 ms tolerance
    }
}
