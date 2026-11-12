using System.Collections.Generic;
using SLSKDONET.Engine.Transitions;
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
    public void ShiftBeatMode_SnapsToBeat()
    {
        var r = TransitionSnap.Resolve(1.48, new List<SnapCandidate>(), Grid, PxPerSecond, TolerancePx, SnapMode.Beat);
        Assert.Equal(SnapKind.Beat, r.Kind);
        Assert.Equal(1.5, r.Seconds, 6);
    }

    [Fact]
    public void AltDisabled_ReturnsRaw()
    {
        var r = TransitionSnap.Resolve(1.28, new List<SnapCandidate>(), Grid, PxPerSecond, TolerancePx, SnapMode.Beat, disabled: true);
        Assert.Equal(SnapKind.None, r.Kind);
        Assert.Equal(1.28, r.Seconds, 6);
    }

    [Fact]
    public void AlignedCue_IsPreferredOverLoneCue()
    {
        var cues = new[]
        {
            new SnapCandidate(1.01, "B: Lone cue"),
            new SnapCandidate(1.03, "B: Drop 1", AlignedWithOutgoing: true)
        };
        var r = TransitionSnap.Resolve(1.02, cues, Grid, PxPerSecond, TolerancePx, SnapMode.Bar);
        Assert.Equal(SnapKind.AlignedCue, r.Kind);
        Assert.Equal(1.03, r.Seconds, 6);
    }

    [Fact]
    public void BuildCandidates_MarksAlignedPairs()
    {
        var incoming = new[] { (Seconds: 10.0, Label: "Drop 1"), (Seconds: 30.0, Label: "Drop 2") };
        var outgoing = new[] { (Seconds: 40.0, Label: "Drop 1") }; // shared = 10 when source = 30
        var result = TransitionSnap.BuildCandidates(incoming, outgoing, sourceTrigger: 30.0, toleranceSeconds: 0.05);
        Assert.True(result[0].AlignedWithOutgoing);
        Assert.False(result[1].AlignedWithOutgoing);
    }
}