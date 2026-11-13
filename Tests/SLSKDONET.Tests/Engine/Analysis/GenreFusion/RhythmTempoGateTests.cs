using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis.GenreFusion;

/// <summary>GA6 / #251: rhythm &amp; tempo gate rules on synthetic bucket scores + signatures.</summary>
public class RhythmTempoGateTests
{
    private static RhythmSignature Sig(double fourOnFloor, double backbeat, double half, double brk, double tempoHigh, double reliability = 1.0) =>
        new(fourOnFloor, backbeat, half, brk, tempoHigh / 2, tempoHigh, 32, reliability);

    private static readonly RhythmSignature Hardcore172 = Sig(0.95, 0.9, 0.0, 0.2, 172);
    private static readonly RhythmSignature DnbTwoStep174 = Sig(0.02, 0.95, 0.05, 0.95, 174);
    private static readonly RhythmSignature HalfTime140 = Sig(0.03, 0.05, 0.95, 0.8, 140);
    private static readonly RhythmSignature House124 = Sig(0.95, 0.95, 0.0, 0.25, 124);
    private static readonly RhythmSignature TwoStep132 = Sig(0.1, 0.8, 0.1, 0.9, 132);

    private static GenreBucket Winner(IReadOnlyDictionary<GenreBucket, double> scores) => scores.OrderByDescending(kv => kv.Value).First().Key;

    private static GenreFusionResult FuseWith(RhythmSignature? rhythm, params (string Key, float Score)[] discogs) =>
        GenreFusionEngine.Fuse(new GenreFusionInput(System.Array.Empty<GenreLabelScore>(), 0f,
            discogs.Select(d => new GenreLabelScore(d.Key, d.Score)).ToArray(), rhythm))!;

    [Fact]
    public void Hardcore4x4At172_WithModelDnb_IsNotDnb()
    {
        var r = FuseWith(Hardcore172, ("Electronic---Drum n Bass", 0.55f), ("Electronic---Hardcore", 0.3f));
        Assert.NotEqual(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Contains(r.Gate!, e => e.RuleId == "hardcore-4x4" && e.Bucket == GenreBucket.DrumAndBass && e.Factor < 1);
    }

    [Fact]
    public void DubstepHalfTimeAt140_WithModelTechno_IsDubstep()
    {
        var r = FuseWith(HalfTime140, ("Electronic---Techno", 0.5f), ("Electronic---Dubstep", 0.3f));
        Assert.Equal(GenreBucket.Dubstep, r.Bucket);
    }

    [Fact]
    public void DnbReadAt87_WithBackbeat_IsDnb()
    {
        // Tracker reports 87: folded to 87/174, so the 160–185 rule still fires.
        var at87 = new RhythmSignature(0.02, 0.95, 0.05, 0.95, 87, 174, 32, 1.0);
        var r = FuseWith(at87, ("Electronic---Drum n Bass", 0.3f), ("Electronic---Techno", 0.35f), ("Electronic---House", 0.2f));
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
    }

    [Fact]
    public void ConfidentModelWinnerWithMargin_IsNeverPenalised()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.House] = 0.9, [GenreBucket.Techno] = 0.3 };
        var weird = Sig(0.02, 0.95, 0.05, 0.95, 174); // "DnB drums" under a sure House model
        var gated = RhythmTempoGate.Apply(raw, weird);
        Assert.Equal(0.9, gated.Scores[GenreBucket.House], 9);
        Assert.Equal(GenreBucket.House, Winner(gated.Scores));
    }

    [Fact]
    public void ReliabilityZero_IsIdenticalToNoGate()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.DrumAndBass] = 0.4, [GenreBucket.Other] = 0.3 };
        var gated = RhythmTempoGate.Apply(raw, Hardcore172 with { Reliability = 0 });
        Assert.Equal(raw, gated.Scores);
        Assert.Empty(gated.Evidence);

        var withGate = FuseWith(Hardcore172 with { Reliability = 0 }, ("Electronic---Drum n Bass", 0.4f), ("Electronic---Hardcore", 0.3f));
        var without = FuseWith(null, ("Electronic---Drum n Bass", 0.4f), ("Electronic---Hardcore", 0.3f));
        Assert.Equal(without.Bucket, withGate.Bucket);
        Assert.Equal(without.Confidence, withGate.Confidence);
    }

    [Fact]
    public void NoScoreDropsBelowTheFloor()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.House] = 0.4, [GenreBucket.Dubstep] = 0.3, [GenreBucket.Techno] = 0.35 };
        var gated = RhythmTempoGate.Apply(raw, DnbTwoStep174);
        foreach (var (bucket, score) in gated.Scores)
            Assert.True(score >= RhythmTempoGate.MinFactor * raw[bucket] - 1e-12, $"{bucket}: {score}");
    }

    [Fact]
    public void FactorsScaleWithReliability()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.DrumAndBass] = 0.4, [GenreBucket.House] = 0.4 };
        var full = RhythmTempoGate.Apply(raw, DnbTwoStep174).Scores[GenreBucket.House];
        var half = RhythmTempoGate.Apply(raw, DnbTwoStep174 with { Reliability = 0.5 }).Scores[GenreBucket.House];
        Assert.True(full < half && half < 0.4, $"full {full}, half {half}");
    }

    [Fact]
    public void HalftimeDnb_IsNotPushedTowardsDubstep()
    {
        // 174 grid, snare once per bar, real DnB model mass: keep DnB.
        var halftimeDnb = Sig(0.02, 0.1, 0.9, 0.9, 174);
        var r = FuseWith(halftimeDnb, ("Electronic---Halftime", 0.4f), ("Electronic---Dubstep", 0.3f));
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.DoesNotContain(r.Gate!, e => e.Bucket == GenreBucket.DrumAndBass && e.Factor < 1);
    }

    [Fact]
    public void FourOnTheFloorAt124_KeepsHouseVersusTechnoForTheModels()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.House] = 0.35, [GenreBucket.Techno] = 0.3, [GenreBucket.DrumAndBass] = 0.3 };
        var gated = RhythmTempoGate.Apply(raw, House124);
        Assert.Equal(0.35, gated.Scores[GenreBucket.House], 9);
        Assert.Equal(0.3, gated.Scores[GenreBucket.Techno], 9);
        Assert.True(gated.Scores[GenreBucket.DrumAndBass] < 0.3 * 0.5);
    }

    [Fact]
    public void TwoStepAt132_PrefersOtherOverHouse()
    {
        var r = FuseWith(TwoStep132, ("Electronic---House", 0.35f), ("Electronic---UK Garage", 0.3f));
        Assert.Equal(GenreBucket.Other, r.Bucket);
    }

    [Fact]
    public void EveryAppliedFactorIsInTheEvidence()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.DrumAndBass] = 0.4, [GenreBucket.House] = 0.3, [GenreBucket.Dubstep] = 0.2 };
        var gated = RhythmTempoGate.Apply(raw, DnbTwoStep174);
        Assert.Contains(gated.Evidence, e => e.RuleId == "dnb-break" && e.Bucket == GenreBucket.DrumAndBass && e.Factor > 1);
        Assert.Contains(gated.Evidence, e => e.RuleId == "dnb-break" && e.Bucket == GenreBucket.House && e.Factor < 1);
        Assert.All(gated.Evidence, e => Assert.Contains("tempo 87/174", e.Inputs));
    }

    [Fact]
    public void TheGateNeverInventsAVote()
    {
        var raw = new Dictionary<GenreBucket, double> { [GenreBucket.House] = 0.3 };
        var gated = RhythmTempoGate.Apply(raw, DnbTwoStep174);
        Assert.Single(gated.Scores);
    }
}
