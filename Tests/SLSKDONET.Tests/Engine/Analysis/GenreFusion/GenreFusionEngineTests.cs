using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis.GenreFusion;

/// <summary>
/// <see cref="GenreFusionEngine"/> v2 (GA5, #250): Discogs-400 + Jamendo voting per
/// <see cref="GenreBucket"/> through <see cref="GenreTaxonomy"/> with noisy-OR. Synthetic vectors
/// only; real-model behaviour is measured by Tests/GenreBenchmark, not asserted here.
/// </summary>
public class GenreFusionEngineTests
{
    private static GenreLabelScore S(string key, float score) => new(key, score);

    /// <summary>A 400-ish vector: the given styles plus low noise on many unrelated styles.</summary>
    private static IReadOnlyList<GenreLabelScore> Styles(params (string Key, float Score)[] hot)
    {
        var list = hot.Select(h => S(h.Key, h.Score)).ToList();
        for (var i = 0; i < 380; i++) list.Add(S($"Rock---Noise Style {i}", 0.02f)); // under the floor
        return list;
    }

    private static GenreFusionResult Fuse(float bpm, IReadOnlyList<GenreLabelScore>? discogs, params (string, float)[] jamendo)
    {
        var result = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo.Select(j => S(j.Item1, j.Item2)).ToArray(), bpm, discogs));
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void DnbHeavyDiscogs_IsDrumAndBass()
    {
        var r = Fuse(174, Styles(("Electronic---Drum n Bass", 0.55f), ("Electronic---Jungle", 0.12f), ("Electronic---Techno", 0.06f)),
            ("electronic", 0.5f), ("techno", 0.22f));
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Equal("Drum and Bass", r.WinnerLabel);
    }

    [Fact]
    public void JungleOnly_IsDrumAndBass_WithJungleSubgenre_AndAStableLabel()
    {
        var r = Fuse(170, Styles(("Electronic---Jungle", 0.4f)));
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Equal("Jungle", r.Subgenre);
        Assert.Equal("Drum and Bass", r.WinnerLabel); // label strings are an API
    }

    [Fact]
    public void HardstyleHeavy_At150_WinsAsOther_InsteadOfLeakingIntoDnbOrTechno()
    {
        var r = Fuse(150, Styles(("Electronic---Hardstyle", 0.5f), ("Electronic---Techno", 0.1f), ("Electronic---Hardcore", 0.2f)));
        Assert.Equal(GenreBucket.Other, r.Bucket);
        Assert.Equal("Hardstyle", r.WinnerLabel);
    }

    [Theory]
    [InlineData(0.5f, 0.3f, GenreBucket.House)]
    [InlineData(0.3f, 0.5f, GenreBucket.Techno)]
    public void TechHouseVersusTechno_IsDecidedByMass(float techHouse, float techno, GenreBucket expected)
    {
        var r = Fuse(126, Styles(("Electronic---Tech House", techHouse), ("Electronic---Techno", techno)));
        Assert.Equal(expected, r.Bucket);
        if (expected == GenreBucket.House) Assert.Equal("Tech House", r.Subgenre);
    }

    [Fact]
    public void AllZeroDiscogs_GivesNoVote_AndFallsThroughToJamendo()
    {
        var zeros = Enumerable.Range(0, 400).Select(i => S($"Electronic---Style {i}", 0f)).ToArray();
        var r = Fuse(174, zeros, ("drumnbass", 0.9f));
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.All(r.Candidates, c => Assert.Equal("onnx-jamendo", c.Source));
        Assert.False(r.RanHeuristic);
    }

    [Fact]
    public void NoRhythm_TheGateIsANoOp()
    {
        var r = Fuse(174, null, ("house", 0.6f), ("drumnbass", 0.5f));
        Assert.Equal(GenreBucket.House, r.Bucket); // tempo alone no longer overrules the models
        Assert.Empty(r.Gate!);
    }

    [Fact]
    public void NoDiscogsScores_StillFusesOnJamendo()
    {
        var r = Fuse(124, null, ("house", 0.8f), ("techno", 0.3f));
        Assert.Equal(GenreBucket.House, r.Bucket);
        Assert.Equal("House", r.WinnerLabel);
    }

    [Fact]
    public void NoisyOr_IsNotASum()
    {
        var r = Fuse(174, Styles(("Electronic---Drum n Bass", 0.5f), ("Electronic---Jungle", 0.5f)));
        var dnb = Assert.Single(r.Candidates, c => c.Source == "onnx-discogs400" && c.Bucket == GenreBucket.DrumAndBass);
        Assert.Equal(0.75f, dnb.Confidence, 4); // 1 − (1 − 0.5)(1 − 0.5)
    }

    [Fact]
    public void NoiseBelowTheFloor_DoesNotVoteForOther()
    {
        var r = Fuse(174, Styles(("Electronic---Drum n Bass", 0.3f)));
        Assert.DoesNotContain(r.Candidates, c => c.Bucket == GenreBucket.Other);
    }

    [Fact]
    public void JamendoBreakbeat_IsOther_NotDrumAndBass()
    {
        // v1 mapped Jamendo "breakbeat" to DnB; the taxonomy says Breaks is a confuser.
        var r = Fuse(174, null, ("breakbeat", 0.7f));
        Assert.Equal(GenreBucket.Other, r.Bucket);
        Assert.Equal("Breakbeat", r.WinnerLabel);
    }

    [Fact]
    public void WeightedTaxonomyMembers_CountPartially()
    {
        // Jamendo "minimal" is a 0.5-weight Techno member: 0.6 × 0.5 = 0.3.
        var r = Fuse(132, null, ("minimal", 0.6f));
        var techno = Assert.Single(r.Candidates);
        Assert.Equal(GenreBucket.Techno, techno.Bucket);
        Assert.Equal(0.3f, techno.Confidence, 4);
    }

    [Fact]
    public void JunkOnlyJamendo_NoRhythm_GivesNoGenre()
    {
        // The v1/v2 BPM heuristic is gone (GA6): tempo alone no longer names a genre.
        Assert.Null(GenreFusionEngine.Fuse(new GenreFusionInput(new[] { S("electronic", 0.9f) }, 174f)));
        Assert.Null(GenreFusionEngine.Fuse(new GenreFusionInput(Array.Empty<GenreLabelScore>(), 174f)));
        Assert.Null(GenreFusionEngine.Fuse(new GenreFusionInput(new[] { S("drumnbass", 0f) }, 90f)));
    }

    [Fact]
    public void NoModelVote_ReliableDnbRhythm_GivesACappedRhythmOnlyGuess()
    {
        var rhythm = new RhythmSignature(0.02, 0.95, 0.05, 0.95, 87, 174, 32, 0.9);
        var r = GenreFusionEngine.Fuse(new GenreFusionInput(Array.Empty<GenreLabelScore>(), 174f, null, rhythm));
        Assert.NotNull(r);
        Assert.True(r!.RanHeuristic);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.True(r.Confidence <= RhythmTempoGate.RhythmOnlyMaxConfidence);
        Assert.Contains(r.Gate!, e => e.RuleId == "dnb-break");
    }

    [Fact]
    public void ZeroConfidenceVotes_AreDropped()
    {
        var r = Fuse(174, null, ("drumnbass", 0.9f), ("techno", 0f));
        Assert.Equal("Drum and Bass", r.WinnerLabel);
        Assert.Single(r.Distribution);
    }
    [Fact]
    public void Distribution_IsKeyedByStableDisplayText()
    {
        var r = Fuse(140, Styles(("Electronic---Hard Techno", 0.4f), ("Electronic---Dubstep", 0.3f)));
        Assert.Contains("Hard Techno", r.Distribution.Keys);
        Assert.Contains("Dubstep", r.Distribution.Keys);
        Assert.Equal("Hard Techno", r.WinnerLabel); // still contains "techn" for substring consumers
    }

    [Fact]
    public void FusionVersion_IsBumpedForGa6() => Assert.Equal(3, GenreFusionEngine.FusionVersion);
}
