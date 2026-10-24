using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Services.Transitions;
using Xunit;

namespace SLSKDONET.Tests.Engine;

/// <summary>The Auto plan prefers the outgoing track's marked outro region for the mix-out and the
/// incoming track's marked intro region for the mix-in.</summary>
public class SectionMarkerPlanningTests
{
    private const double Bpm = 174;
    private static readonly double Bar = 240.0 / Bpm;
    private const double Downbeat = 0.5;
    private static double AtBar(int bars) => Downbeat + bars * Bar;

    private static TrackStructure Dnb() => new()
    {
        DurationSeconds = 300, Bpm = Bpm, FirstDownbeat = Downbeat,
        Drop1 = AtBar(32), Drop2 = AtBar(96), OutroCue = 295, LastSound = 298, Energy = 0.8,
    };

    [Fact]
    public void Sections_TrustTheMarkedIntroEndAndOutro()
    {
        var unmarked = TransitionPlanner.ComputeSections(Dnb());
        var marked = TransitionPlanner.ComputeSections(Dnb() with { IntroEndMarker = AtBar(16), OutroStartMarker = AtBar(112) });

        Assert.Equal(AtBar(128), unmarked.OutroStart, 3);        // the estimate: Drop 2 + 32 bars
        Assert.Equal(AtBar(112), marked.OutroStart, 3);          // the DJ's outro
        Assert.Equal(AtBar(32), unmarked.IntroEnd, 3);           // the estimate: Drop 1
        Assert.Equal(AtBar(16), marked.IntroEnd, 3);             // the DJ's intro end
    }

    [Fact]
    public void Relaxed_MixesOutFromTheMarkedOutro_OverTheMarkedIntro()
    {
        var outgoing = Dnb() with { OutroStartMarker = AtBar(112) };
        var incoming = Dnb() with { IntroEndMarker = AtBar(16) };

        var plan = TransitionPlanner.PlanKind(outgoing, incoming, PlannedTransitionKind.Relaxed)!;

        Assert.Equal(AtBar(112), plan.SourceTriggerSeconds, 3);  // starts where A's outro starts
        Assert.Equal(Downbeat, plan.TargetTriggerSeconds, 3);    // B comes in at its intro…
        Assert.Equal(16, plan.DurationBars);                     // …and the blend fits inside it
    }

    [Fact]
    public void AutoPlan_BlendsInsideTheMarkedOutro()
    {
        var outgoing = Dnb() with { OutroStartMarker = AtBar(112) };
        var plan = TransitionPlanner.Plan(outgoing, Dnb(), compatibility: 95);
        var unmarkedPlan = TransitionPlanner.Plan(Dnb(), Dnb(), compatibility: 95);

        double outro = AtBar(112);
        Assert.True(plan.SourceTriggerSeconds <= outro + 1e-3 && plan.SourceTriggerSeconds + plan.DurationSeconds >= outro - 1e-3,
            $"blend {plan.SourceTriggerSeconds:0.0}–{plan.SourceTriggerSeconds + plan.DurationSeconds:0.0} s misses the outro at {outro:0.0} s");
        Assert.NotEqual(unmarkedPlan.SourceTriggerSeconds, plan.SourceTriggerSeconds, 3);
    }

    [Fact]
    public void AnEarlyMarkedOutro_IsHonouredEvenBeforeHalfTheTrack()
    {
        var outgoing = Dnb() with { OutroStartMarker = AtBar(60) };   // ~83 s of a 300 s track
        var plan = TransitionPlanner.Plan(outgoing, Dnb(), compatibility: 30);
        Assert.True(plan.SourceTriggerSeconds <= AtBar(60) + 1e-3);
    }

    [Fact]
    public void BuildStructure_ReadsTheSectionMarkerRows()
    {
        var features = new AudioFeaturesEntity { TrackDuration = 300, Bpm = (float)Bpm, DownbeatOffsetSeconds = (float)Downbeat, Energy = 0.8f };
        var cues = new List<CuePointEntity>
        {
            new() { TimestampInSeconds = AtBar(16), Type = CuePointType.IntroEnd, Label = "Intro end" },
            new() { TimestampInSeconds = AtBar(112), Type = CuePointType.OutroStart, Label = "Outro" },
        };

        var s = TransitionPlanService.BuildStructure(features, cues);

        Assert.Equal(AtBar(16), s.IntroEndMarker!.Value, 3);
        Assert.Equal(AtBar(112), s.OutroStartMarker!.Value, 3);
    }
}
