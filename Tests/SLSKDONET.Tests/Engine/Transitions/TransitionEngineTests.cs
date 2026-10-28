using System.Collections.Generic;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Engine.Transitions;

/// <summary>
/// Coverage for TransitionEngine.OptimizeTransition — the cue-aware Mix editor's suggested
/// source/target trigger points. No test file existed for this before; it's the seam where
/// CueGenerationService's real cue data actually reaches the transition-suggestion UI.
/// </summary>
public class TransitionEngineTests
{
    private static TrackEntity MakeTrack(string bpm120Key = "8A", double? bpm = 174.0, string? key = "8A") => new()
    {
        GlobalId = "track",
        BPM = bpm,
        MusicalKey = key,
        CanonicalDuration = 240,
    };

    private static CuePointEntity Cue(double t, CuePointType type, string label) => new()
    {
        TrackUniqueHash = "hash",
        TimestampInSeconds = t,
        Type = type,
        Label = label,
    };

    /// <summary>
    /// A drop-in transition must never enter ON the drop — that cuts into it. The mix-in lands on
    /// the 8/16-bars-before-drop countdown marker instead ("32 Beats to Drop 1" here = 8 bars out).
    /// </summary>
    [Fact]
    public void OptimizeTransition_TempoJump_TargetsDropApproach_NotTheDrop()
    {
        var engine = new TransitionEngine();
        var source = MakeTrack(bpm: 174.0);
        var target = MakeTrack(bpm: 130.0); // >6% tempo jump from 174

        var sourceCues = new List<CuePointEntity> { Cue(210, CuePointType.Outro, "Outro") };
        var targetCues = new List<CuePointEntity>
        {
            Cue(0, CuePointType.Intro, "Intro"),
            Cue(33, CuePointType.Build, "32 Beats to Drop 1"),
            Cue(38, CuePointType.Build, "16 Beats to Drop 1"),
            Cue(44, CuePointType.Drop, "Drop 1"),
        };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        Assert.Equal(33, suggestion.TargetTriggerTime);
    }

    [Fact]
    public void OptimizeTransition_StandardCase_UsesOutroAndIntro()
    {
        var engine = new TransitionEngine();
        var source = MakeTrack(bpm: 174.0);
        var target = MakeTrack(bpm: 175.0); // within 6%, no tempo jump

        var sourceCues = new List<CuePointEntity> { Cue(210, CuePointType.Outro, "Outro") };
        var targetCues = new List<CuePointEntity> { Cue(2, CuePointType.Intro, "Intro") };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        Assert.Equal(210, suggestion.SourceTriggerTime);
        Assert.Equal(2, suggestion.TargetTriggerTime);
    }

    /// <summary>
    /// SelectedSourceCue/SelectedTargetCue let a UI highlight WHICH analyzed cue the suggestion
    /// picked (not just show a bare timestamp) — added for the Flow Builder/Mix editor cue-picker.
    /// Must point at the actual CuePointEntity instances passed in, not just match by value.
    /// </summary>
    [Fact]
    public void OptimizeTransition_StandardCase_ExposesTheSelectedCueEntities()
    {
        var engine = new TransitionEngine();
        var source = MakeTrack(bpm: 174.0);
        var target = MakeTrack(bpm: 175.0);

        var outroCue = Cue(210, CuePointType.Outro, "Outro");
        var introCue = Cue(2, CuePointType.Intro, "Intro");
        var sourceCues = new List<CuePointEntity> { outroCue };
        var targetCues = new List<CuePointEntity> { introCue };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        Assert.Same(outroCue, suggestion.SelectedSourceCue);
        Assert.Same(introCue, suggestion.SelectedTargetCue);
    }

    [Fact]
    public void OptimizeTransition_TempoJump_SelectedTargetCueIsTheApproachMarker()
    {
        var engine = new TransitionEngine();
        var source = MakeTrack(bpm: 174.0);
        var target = MakeTrack(bpm: 130.0);

        var dropCue = Cue(44, CuePointType.Drop, "Drop 1");
        var approachCue = Cue(33, CuePointType.Build, "32 Beats to Drop 1");
        var sourceCues = new List<CuePointEntity> { Cue(210, CuePointType.Outro, "Outro") };
        var targetCues = new List<CuePointEntity>
        {
            Cue(0, CuePointType.Intro, "Intro"),
            approachCue,
            dropCue,
        };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        Assert.Same(approachCue, suggestion.SelectedTargetCue);
        Assert.NotSame(dropCue, suggestion.SelectedTargetCue);
    }

    [Fact]
    public void OptimizeTransition_IncompatibleKeysWithVocalOverlap_ShiftsTargetToDropApproach()
    {
        var engine = new TransitionEngine();
        var source = new TrackEntity
        {
            GlobalId = "source", BPM = 174.0, MusicalKey = "8A", CanonicalDuration = 240,
            VocalEndSeconds = 215.0, // vocals run past the source's own outro cue
        };
        var target = new TrackEntity
        {
            GlobalId = "target", BPM = 174.0, MusicalKey = "3A", CanonicalDuration = 240, // not Camelot-compatible with 8A
            VocalStartSeconds = 5.0, // vocals start right at the target's intro
        };

        var sourceCues = new List<CuePointEntity> { Cue(210, CuePointType.Outro, "Outro") };
        var targetCues = new List<CuePointEntity>
        {
            Cue(0, CuePointType.Intro, "Intro"),
            Cue(33, CuePointType.Build, "32 Beats to Drop 1"),
            Cue(44, CuePointType.Drop, "Drop 1"),
        };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        // On the drop's approach (8 bars out), never on the drop itself.
        Assert.Equal(33, suggestion.TargetTriggerTime);
        Assert.True(suggestion.CompatibilityScore < 50.0);
    }

    [Fact]
    public void OptimizeTransition_ExplicitSharedMixCuesWinOverSectionsAndLegacyLabels()
    {
        var engine = new TransitionEngine();
        var source = MakeTrack(bpm: 174.0);
        var target = MakeTrack(bpm: 175.0);
        var mixOut = Cue(73.25, CuePointType.MixOut, "Mix-Out");
        var mixIn = Cue(18.75, CuePointType.MixIn, "Mix-In");
        var sourceCues = new List<CuePointEntity>
        {
            Cue(60, CuePointType.OutroStart, "Outro Start"),
            Cue(65, CuePointType.Outro, "Mix-Out Warning"),
            mixOut,
        };
        var targetCues = new List<CuePointEntity>
        {
            Cue(2, CuePointType.Intro, "Intro"),
            Cue(10, CuePointType.Intro, "Mix-In legacy"),
            mixIn,
        };

        var suggestion = engine.OptimizeTransition(source, target, sourceCues, targetCues);

        Assert.Equal(73.25, suggestion.SourceTriggerTime);
        Assert.Equal(18.75, suggestion.TargetTriggerTime);
        Assert.Same(mixOut, suggestion.SelectedSourceCue);
        Assert.Same(mixIn, suggestion.SelectedTargetCue);
    }

    [Theory]
    [InlineData("8A", "8A", true)]
    [InlineData("8A", "9A", true)]
    [InlineData("8A", "7A", true)]
    [InlineData("8A", "8B", true)]
    [InlineData("8A", "3A", false)]
    [InlineData("12A", "1A", true)] // wraps around the Camelot wheel
    public void AreCamelotKeysCompatible_MatchesWheelAdjacency(string a, string b, bool expected)
    {
        Assert.Equal(expected, TransitionEngine.AreCamelotKeysCompatible(a, b));
    }
}
