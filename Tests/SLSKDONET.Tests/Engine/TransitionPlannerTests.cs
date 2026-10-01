using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Engine;

public class TransitionPlannerTests
{
    private const double Bpm = 174;
    private static readonly double Bar = 240.0 / Bpm;
    private const double Downbeat = 0.5;
    private static double AtBar(int bars) => Downbeat + bars * Bar;

    /// <summary>A typical 300 s DnB track: drops at bar 32 and 96, an auto "Outro" cue 3 s from the end.</summary>
    private static TrackStructure Dnb(double energy = 0.8, double bpm = Bpm) => new()
    {
        DurationSeconds = 300, Bpm = bpm, FirstDownbeat = Downbeat,
        Drop1 = AtBar(32), Drop2 = AtBar(96), OutroCue = 295, LastSound = 298, Energy = energy,
    };

    [Fact]
    public void Sections_IgnoreAnOutroCueTooCloseToTheEnd_AndEndTheMainPart32BarsAfterTheLastDrop()
    {
        var s = TransitionPlanner.ComputeSections(Dnb());

        Assert.Equal(Downbeat, s.IntroStart, 3);
        Assert.Equal(AtBar(32), s.IntroEnd, 3);           // intro runs to Drop 1
        Assert.Equal(AtBar(128), s.OutroStart, 3);        // Drop 2 + 32 bars, not the 295 s cue
        Assert.Equal(298, s.OutroEnd, 3);                 // last sound, not the file end
    }

    [Fact]
    public void Sections_TrustAnOutroCueThatLeavesRoomToMix()
    {
        var s = TransitionPlanner.ComputeSections(Dnb() with { OutroCue = AtBar(160) });
        Assert.Equal(AtBar(160), s.OutroStart, 3);
    }

    [Fact]
    public void Sections_AlwaysLeaveAtLeast16BarsOfOutro()
    {
        // Drops late in a short track: Drop 2 + 32 bars would be past the end.
        var t = new TrackStructure { DurationSeconds = 180, Bpm = Bpm, FirstDownbeat = Downbeat, Drop1 = AtBar(32), Drop2 = AtBar(110), Energy = 0.8 };
        var s = TransitionPlanner.ComputeSections(t);
        Assert.True(s.OutroBars >= 8 - 1e-6, $"outro only {s.OutroBars:0.0} bars");
        Assert.True(s.OutroStart >= t.Drop2!.Value - 1e-6 || s.OutroBars >= 16 - 1e-6);
    }

    [Fact]
    public void Plan_NeverPicksDropSyncByItself_ItCutsTheOutgoingTrackShort()
    {
        var plan = TransitionPlanner.Plan(Dnb(), Dnb(), compatibility: 95);
        Assert.Equal(PlannedTransitionKind.Rolling, plan.Kind);
        Assert.True(plan.SourceTriggerSeconds >= 0.5 * 300);
    }

    [Fact]
    public void DropSync_AsAPreset_DoubleDrops()
    {
        var plan = TransitionPlanner.PlanKind(Dnb(), Dnb(), PlannedTransitionKind.DropSync)!;

        Assert.Equal(PlannedTransitionKind.DropSync, plan.Kind);
        Assert.Equal("Drop Sync", plan.PresetName);
        Assert.Equal(32, plan.DurationBars);
        Assert.Equal(AtBar(80), plan.SourceTriggerSeconds, 3);   // outgoing Drop 2 − 16 bars
        Assert.Equal(AtBar(16), plan.TargetTriggerSeconds, 3);   // incoming Drop 1 − 16 bars
        Assert.False(plan.VocalClash);
    }

    [Fact]
    public void Plan_EnergeticButLessCompatiblePair_Rolls()
    {
        var plan = TransitionPlanner.Plan(Dnb(), Dnb(), compatibility: 65);

        Assert.Equal(PlannedTransitionKind.Rolling, plan.Kind);
        Assert.Equal(32, plan.DurationBars);
        // The incoming drop lands exactly as the outgoing main part ends (bar 128).
        Assert.Equal(AtBar(112), plan.SourceTriggerSeconds, 3);
        Assert.Equal(AtBar(16), plan.TargetTriggerSeconds, 3);
        Assert.Equal(AtBar(128), plan.SourceTriggerSeconds + plan.DurationSeconds / 2, 3);
    }

    [Fact]
    public void Plan_CalmPair_MixesOutroIntoIntro()
    {
        var plan = TransitionPlanner.Plan(Dnb(energy: 0.3), Dnb(energy: 0.3), compatibility: 50);

        Assert.Equal(PlannedTransitionKind.Relaxed, plan.Kind);
        Assert.Equal(32, plan.DurationBars);                      // min(outro ≈ 88 bars, intro 32 bars)
        Assert.Equal(AtBar(128), plan.SourceTriggerSeconds, 3);
        Assert.Equal(Downbeat, plan.TargetTriggerSeconds, 3);
    }

    [Fact]
    public void Plan_TemposTooFarApart_KeepsItShort()
    {
        var plan = TransitionPlanner.Plan(Dnb(), Dnb(bpm: 140), compatibility: 90);
        Assert.Equal(PlannedTransitionKind.Relaxed, plan.Kind);
        Assert.Equal(8, plan.DurationBars);
    }

    [Fact]
    public void Plan_SkipsATransitionWhoseWindowPutsTwoVocalsTogether()
    {
        // Outgoing vocals around its second drop; incoming vocals in its build-up.
        var outgoing = Dnb() with { VocalStart = 100, VocalEnd = 140 };
        var incoming = Dnb() with { VocalStart = 20, VocalEnd = 60 };

        var plan = TransitionPlanner.Plan(outgoing, incoming, compatibility: 85);

        Assert.Equal(PlannedTransitionKind.Rolling, plan.Kind); // Drop Sync would overlap both vocals
        Assert.False(plan.VocalClash);
    }

    [Fact]
    public void VocalsOverlap_IgnoresAVocalRangeCoveringAlmostTheWholeTrack()
    {
        var t = Dnb() with { VocalStart = 5, VocalEnd = 290 };
        Assert.False(TransitionPlanner.VocalsOverlap(t, 100, 30));
        Assert.True(TransitionPlanner.VocalsOverlap(Dnb() with { VocalStart = 110, VocalEnd = 150 }, 100, 30));
        Assert.False(TransitionPlanner.VocalsOverlap(Dnb() with { VocalStart = 131, VocalEnd = 150 }, 100, 30));
    }

    [Theory]
    [InlineData(40, 32)]
    [InlineData(32, 32)]
    [InlineData(20, 16)]
    [InlineData(12, 8)]
    [InlineData(3, 8)]
    public void QuantiseDown_PicksTheLongestWholePhrase(double available, int expected) =>
        Assert.Equal(expected, TransitionPlanner.QuantiseDown(available));

    [Theory]
    [InlineData(174.0, 172.0, 174.0 / 172.0)]
    [InlineData(172.0, 174.0, 172.0 / 174.0)]
    [InlineData(87.0, 174.0, 1.0)]        // half-time reading of the same tempo
    [InlineData(175.0, 87.5, 1.0)]
    [InlineData(174.0, 140.0, 1.0)]       // too far apart — left alone
    [InlineData(0.0, 174.0, 1.0)]
    public void TempoSyncRatio_MatchesTheOutgoingTempo(double outgoing, double incoming, double expected) =>
        Assert.Equal(expected, AudioPlayerService.TempoSyncRatio(outgoing > 0 ? outgoing : null, incoming), 6);

    [Theory]
    [InlineData(0.25, 1f, 0f)]
    [InlineData(0.49, 1f, 0f)]
    [InlineData(0.51, 0f, 1f)]
    [InlineData(0.9, 0f, 1f)]
    public void HardLowSwap_SwitchesTheBassAtTheMidpointInTime_WhateverTheCurve(double time, float outLow, float inLow)
    {
        var engine = new SLSKDONET.Services.Audio.TransitionEngine();
        var region = new TransitionRegion
        {
            StartSample = 0, EndSample = 1000, Type = SLSKDONET.Services.Audio.TransitionType.EqSwap,
            Curve = TransitionCurve.EaseIn, // curved progress at t = 0.6 is only 0.36
            EqConfig = new EqBandSwapConfig { SwapLow = true, HardLowSwap = true },
        };
        var a = engine.CalculateAutomation(region, (long)(time * 1000));
        Assert.Equal(outLow, a.OutgoingLowGain);
        Assert.Equal(inLow, a.IncomingLowGain);
    }

    [Fact]
    public void LastSound_IsTheEndOfTheLastSecondAbove5PercentOfPeak()
    {
        Assert.Equal(5.0, TransitionPlanService.LastSoundSeconds("[1,1,1,0.8,0.5,0.01,0,0]", 8.0));
        Assert.Null(TransitionPlanService.LastSoundSeconds("[]", 8.0));
        Assert.Null(TransitionPlanService.LastSoundSeconds("not json", 8.0));
    }

    [Fact]
    public void BuildStructure_TakesDropsInOrder_AndSkipsLoops()
    {
        var f = new AudioFeaturesEntity { Bpm = 174, TrackDuration = 300, DownbeatOffsetSeconds = 0.5, Energy = 0.7f };
        var cues = new List<CuePointEntity>
        {
            new() { TimestampInSeconds = 140, Type = CuePointType.Drop },
            new() { TimestampInSeconds = 45, Type = CuePointType.Drop },
            new() { TimestampInSeconds = 60, Type = CuePointType.Drop, IsLoop = true },
            new() { TimestampInSeconds = 0.5, Type = CuePointType.Intro },
            new() { TimestampInSeconds = 280, Type = CuePointType.Outro },
        };

        var s = TransitionPlanService.BuildStructure(f, cues);

        Assert.Equal(45, s.Drop1);
        Assert.Equal(140, s.Drop2);
        Assert.Equal(0.5, s.IntroCue);
        Assert.Equal(280, s.OutroCue);
        Assert.Equal(0.7, s.Energy, 3);
    }

    [Fact]
    public void Plan_NeverMixesOutBeforeHalfTheTrackHasPlayed()
    {
        // Drops early in a 300 s track: Rolling and Relaxed would leave at ~1:18 and ~1:40.
        var early = new TrackStructure { DurationSeconds = 300, Bpm = Bpm, FirstDownbeat = Downbeat, Drop1 = AtBar(16), Drop2 = AtBar(40), LastSound = 298, Energy = 0.8 };

        var plan = TransitionPlanner.Plan(early, Dnb(), compatibility: 80);

        Assert.True(plan.SourceTriggerSeconds >= 150, $"mixed out at {plan.SourceTriggerSeconds:0.0}s");
        Assert.True(plan.SourceTriggerSeconds + plan.DurationSeconds <= 298 + 1e-6);
    }

    [Fact]
    public void DistinctDrops_TreatsAHandSetDropAndANearbyAutoDropAsOne_PreferringYours()
    {
        var cues = new List<CuePointEntity>
        {
            new() { TimestampInSeconds = 86.6, Type = CuePointType.Drop, IsAutoGenerated = false },  // your "Drop 1"
            new() { TimestampInSeconds = 88.3, Type = CuePointType.Drop, IsAutoGenerated = true },   // auto "[DROP 1] ✓AI"
            new() { TimestampInSeconds = 198.6, Type = CuePointType.Drop, IsAutoGenerated = true },  // auto "[DROP 2]"
        };

        Assert.Equal(new[] { 86.6, 198.6 }, TransitionPlanService.DistinctDrops(cues, 174));
    }
}
