using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class BreakbeatAnalysisStrategyTests
{
    private static BreakbeatAnalysisStrategy CreateStrategy()
        => new(new DnBTransientDetectionService(NullLogger<DnBTransientDetectionService>.Instance));

    private static GenreFamilyResult BreakbeatClassification()
        => new(GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown, 170, 180);

    [Fact]
    public void RefineBpm_AlreadyInBracket_ReturnsUnchanged()
    {
        var strategy = CreateStrategy();

        var (corrected, penalty, note) = strategy.RefineBpm(BreakbeatClassification(), rawBpm: 174f, confidence: 0.8f);

        Assert.Equal(174f, corrected);
        Assert.Equal(1f, penalty);
        Assert.Null(note);
    }

    [Fact]
    public void RefineBpm_HalfTimeMisdetection_DoublesIntoBracket()
    {
        var strategy = CreateStrategy();

        // 87.5 BPM half-time misdetection of a real 175 BPM DnB track.
        var (corrected, penalty, note) = strategy.RefineBpm(BreakbeatClassification(), rawBpm: 87.5f, confidence: 0.7f);

        Assert.Equal(175f, corrected);
        Assert.True(penalty < 1f);
        Assert.NotNull(note);
        Assert.Contains("bpm_breakbeat_family_corrected", note);
    }

    [Fact]
    public void RefineBpm_DoubleTimeMisdetection_HalvesIntoBracket()
    {
        var strategy = CreateStrategy();

        var (corrected, _, note) = strategy.RefineBpm(BreakbeatClassification(), rawBpm: 350f, confidence: 0.7f);

        Assert.Equal(175f, corrected);
        Assert.NotNull(note);
    }

    [Fact]
    public void RefineBpm_NoOctaveRelationToBracket_LeavesUnchanged()
    {
        var strategy = CreateStrategy();

        var (corrected, penalty, note) = strategy.RefineBpm(BreakbeatClassification(), rawBpm: 140f, confidence: 0.7f);

        Assert.Equal(140f, corrected);
        Assert.Equal(1f, penalty);
        Assert.Null(note);
    }

    [Fact]
    public void ResolvePreset_ReturnsRealDnBPreset_NotEdmCopy()
    {
        var strategy = CreateStrategy();

        var preset = strategy.ResolvePreset(FourOnTheFloorSubgenre.Unknown);

        Assert.Equal("DnB", preset.Genre);
        // The real DnB preset widened its breakdown to 32 bars — the old byte-for-byte EDM copy
        // had 16, which is the bug this preset update fixed.
        Assert.Equal(32, preset.BreakBars);
    }

    [Fact]
    public void GetSignalWeights_IsSubBassDominant()
    {
        var strategy = CreateStrategy();

        var weights = strategy.GetSignalWeights();

        Assert.True(weights.SubBassWeight > weights.SpectralFluxWeight);
        Assert.True(weights.SubBassWeight > weights.EnergyJumpWeight);
    }

    [Fact]
    public void GetFamilySpecificDropCandidates_ReturnsEmpty_NoIndependentDropSignal()
    {
        var strategy = CreateStrategy();
        var analysis = new AnalysisPipelineResult { DurationSeconds = 240, Bpm = 174 };

        var candidates = strategy.GetFamilySpecificDropCandidates(analysis, bpm: 174, downbeatAnchor: 0.2);

        Assert.Empty(candidates);
    }

    [Fact]
    public void GetFamilySpecificDropCandidates_BoostsSubBassReturn_CorroboratedByNearbyNoveltyPeak()
    {
        var strategy = CreateStrategy();
        var analysis = new AnalysisPipelineResult
        {
            DurationSeconds = 240,
            Bpm = 174,
            SubBassReturnTimestamps = new System.Collections.Generic.List<double> { 60.0 },
            // Within one beat (60/174 ≈ 0.345s) of the sub-bass return.
            NoveltyDropSignatures = new System.Collections.Generic.List<(double, double, float)> { (60.2, 55.0, 0.8f) },
        };

        var candidates = strategy.GetFamilySpecificDropCandidates(analysis, bpm: 174, downbeatAnchor: 0.2);

        var corroborated = Assert.Single(candidates);
        Assert.Equal(60.0, corroborated.Time);
        Assert.Equal(0.90f, corroborated.Score);
    }

    [Fact]
    public void GetFamilySpecificDropCandidates_NoCandidate_WhenNoveltyPeakIsFarAway()
    {
        var strategy = CreateStrategy();
        var analysis = new AnalysisPipelineResult
        {
            DurationSeconds = 240,
            Bpm = 174,
            SubBassReturnTimestamps = new System.Collections.Generic.List<double> { 60.0 },
            NoveltyDropSignatures = new System.Collections.Generic.List<(double, double, float)> { (90.0, 85.0, 0.8f) },
        };

        var candidates = strategy.GetFamilySpecificDropCandidates(analysis, bpm: 174, downbeatAnchor: 0.2);

        Assert.Empty(candidates);
    }

    [Fact]
    public void GetFamilySpecificBreakdownCandidates_DegenerateInput_ReturnsEmptyWithoutThrowing()
    {
        var strategy = CreateStrategy();
        var analysis = new AnalysisPipelineResult { DurationSeconds = 0, Bpm = 0, EnergyCurve = System.Array.Empty<float>() };

        var candidates = strategy.GetFamilySpecificBreakdownCandidates(analysis, bpm: 0, downbeatAnchor: 0);

        Assert.Empty(candidates);
    }

    [Fact]
    public void GetFamilySpecificBreakdownCandidates_RealEnergyCurve_ReturnsCandidatesWithinTrackDuration()
    {
        var strategy = CreateStrategy();
        double duration = 240.0;
        var energyCurve = Enumerable.Range(0, (int)duration)
            .Select(i => (float)System.Math.Clamp(System.Math.Sin(i / 20.0) * 0.5 + 0.5, 0, 1))
            .ToArray();
        var analysis = new AnalysisPipelineResult { DurationSeconds = duration, Bpm = 174, EnergyCurve = energyCurve };

        var candidates = strategy.GetFamilySpecificBreakdownCandidates(analysis, bpm: 174, downbeatAnchor: 0.2);

        Assert.All(candidates, t => Assert.InRange(t, 0, duration));
    }
}
