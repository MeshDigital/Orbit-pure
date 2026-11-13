using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Engine;

public class TransitionSnapTests
{
    private static readonly BarGrid Grid = new(DownbeatSeconds: 0, BeatSeconds: 0.5);
    private const double PxPerSecond = 100.0;
    private const double TolerancePx = 8.0; // 0.08 s

    [Fact]
    public void CueWithinTolerance_BeatsBar()
    {
        var cues = new[] { new SnapCandidate(1.04, "B: Drop 1") };
        var r = TransitionSnap.Resolve(1.02, cues, Grid, PxPerSecond, TolerancePx, SnapMode.Bar);
        Assert.Equal(SnapKind.IncomingCue, r.Kind);
        Assert.Equal(1.04, r.Seconds, 6);
    }

    [Fact]
    public void CueOutsideTolerance_FallsBackToBar()
    {
        var cues = new[] { new SnapCandidate(1.50, "B: Drop 1") };
        var r = TransitionSnap.Resolve(1.95, cues, Grid, PxPerSecond, TolerancePx, SnapMode.Bar);
        Assert.Equal(SnapKind.Bar, r.Kind);
        Assert.Equal(2.0, r.Seconds, 6);
    }

    [Fact]
    public void FarFromAnyBar_StillSnapsToTheNearestBar()
    {
        // The overlay drag has always jumped bar by bar; the magnet must not turn that into free movement.
        var r = TransitionSnap.Resolve(1.3, new List<SnapCandidate>(), Grid, PxPerSecond, TolerancePx, SnapMode.Bar);
        Assert.Equal(SnapKind.Bar, r.Kind);
        Assert.Equal(2.0, r.Seconds, 6);
    }

    [Fact]
    public void ShiftBeatMode_SnapsToBeat()
    {
        var r = TransitionSnap.Resolve(1.48, new List<SnapCandidate>(), Grid, PxPerSecond, TolerancePx, SnapMode.Beat);
        Assert.Equal(SnapKind.Beat, r.Kind);
        Assert.Equal(1.5, r.Seconds, 6);
    }

    [Fact]
    public void AltDisabled_ReturnsRaw()
    {
        var cues = new[] { new SnapCandidate(1.28, "B: Drop 1") };
        var r = TransitionSnap.Resolve(1.27, cues, Grid, PxPerSecond, TolerancePx, SnapMode.Beat, disabled: true);
        Assert.Equal(SnapKind.None, r.Kind);
        Assert.Equal(1.27, r.Seconds, 6);
    }

    [Fact]
    public void AlignedCue_IsPreferredOverLoneCue()
    {
        var cues = new[]
        {
            new SnapCandidate(1.01, "B: Lone cue"),
            new SnapCandidate(1.03, "B: Drop 1 ↔ A: Drop 2", AlignedWithOutgoing: true)
        };
        var r = TransitionSnap.Resolve(1.02, cues, Grid, PxPerSecond, TolerancePx, SnapMode.Bar);
        Assert.Equal(SnapKind.AlignedCue, r.Kind);
        Assert.Equal(1.03, r.Seconds, 6);
    }

    [Fact]
    public void BuildCandidates_PlacesEachPairWhereBothCuesSoundTogether()
    {
        // A mixes out at 30 s; A's drop at 46 s is 16 s into the blend. B plays at 1.02×, so B's drop
        // at 64 s lands on A's drop for a mix-in of 64 − 1.02·16 = 47.68 s.
        var incoming = new[] { (Seconds: 64.0, Label: "Drop") };
        var outgoing = new[] { (Seconds: 46.0, Label: "Drop"), (Seconds: 20.0, Label: "Intro") };
        var result = TransitionSnap.BuildCandidates(incoming, outgoing, sourceTrigger: 30.0, incomingRate: 1.02, incomingDuration: 300);

        Assert.Contains(result, c => !c.AlignedWithOutgoing && c.Seconds == 64.0 && c.Label == "B: Drop");
        var pair = Assert.Single(result, c => c.AlignedWithOutgoing);
        Assert.Equal(47.68, pair.Seconds, 6);
        Assert.Equal("B: Drop ↔ A: Drop", pair.Label);
        // A's intro (before the mix-out) can't line up with B: B isn't playing yet then.
    }

    [Fact]
    public void BuildCandidates_DropsTargetsOutsideB()
    {
        var incoming = new[] { (Seconds: 5.0, Label: "Intro") };
        var outgoing = new[] { (Seconds: 50.0, Label: "Drop") }; // 20 s into the blend → mix-in −15 s
        var result = TransitionSnap.BuildCandidates(incoming, outgoing, sourceTrigger: 30.0, incomingRate: 1.0, incomingDuration: 300);
        Assert.DoesNotContain(result, c => c.AlignedWithOutgoing);
    }

    // ── TransitionOverlayModel.SnapTargetTrigger (the drag path) ──

    private static TransitionOverlayModel Overlay(IReadOnlyList<SnapCandidate>? candidates = null) =>
        TransitionOverlayModel.Build(128, 128, 200, 30.2, 30, 10, 300, 300, 0.2, null, null, null)
            with { IncomingSnapCandidates = candidates ?? new List<SnapCandidate>() };

    [Fact]
    public void Drag_WithoutCuesInReach_MatchesTheExistingBarAndBeatSnap()
    {
        var m = Overlay();
        Assert.Equal(m.ShiftedTargetTrigger(1.0), m.SnapTargetTrigger(1.0, 20, 8, altHeld: false, shiftHeld: false).Seconds, 9);
        Assert.Equal(m.ShiftedTargetTrigger(1.0, snapToBar: false), m.SnapTargetTrigger(1.0, 20, 8, altHeld: false, shiftHeld: true).Seconds, 9);
        Assert.Equal(m.ShiftedTargetTrigger(1000), m.SnapTargetTrigger(1000, 20, 8, false, false).Seconds, 9); // clamped inside B
    }

    [Fact]
    public void Drag_NearACue_SnapsToIt_AndAltMovesFreely()
    {
        // Dragging 1 s puts the raw mix-in at 29.2 s; a cue at 29.25 s is 1 px away at 20 px/s.
        var m = Overlay(new[] { new SnapCandidate(29.25, "B: Drop") });
        var snapped = m.SnapTargetTrigger(1.0, 20, 8, altHeld: false, shiftHeld: false);
        Assert.Equal(SnapKind.IncomingCue, snapped.Kind);
        Assert.Equal(29.25, snapped.Seconds, 9);

        var free = m.SnapTargetTrigger(1.0, 20, 8, altHeld: true, shiftHeld: false);
        Assert.Equal(SnapKind.None, free.Kind);
        Assert.Equal(29.2, free.Seconds, 9);
    }
}
