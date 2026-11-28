using System;
using System.Linq;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Engine.Cueing;

/// <summary>
/// DJ report (2026-11-28): "when I set the drop it snaps to some other point further from the
/// cursor" and "setting Bar 1 on the drop's bass hit doesn't visibly do anything".
/// </summary>
public sealed class DropPlacementAndGridAnchorTests
{
    private const double Bpm = 174.0;
    private static double Beat => 60.0 / Bpm;
    private static double Bar => 4 * Beat;

    private static CueEditorSession Session(double playhead)
    {
        var audition = new Mock<ICueAudition>();
        audition.SetupGet(a => a.PositionSeconds).Returns(playhead);
        audition.SetupGet(a => a.IsPlaying).Returns(false);
        var session = new CueEditorSession(new Mock<ICuePointService>().Object, audition.Object,
            new CueSessionRegistry(new AppConfig { DropCountdownMode = DropCountdownCues.DnB }));
        session.Load(new CueTrackInfo("h", "Track", null, Bpm, 0, 300.0, "Drum & Bass"), Array.Empty<CuePointEntity>());
        return session;
    }

    [Fact]
    public void Drop_WithBeatSnap_LandsOnTheNearestBeat_NotTheNearestBar()
    {
        // The bass hit is 2 beats after a bar line: the old code jumped half a bar (0.69 s) away.
        double hit = 40 * Bar + 2 * Beat + 0.01;
        var session = Session(hit);

        session.SetDropAtPlayheadCommand.Execute().Subscribe();

        var drop = session.Cues.First(c => c.Role == CueRole.Drop);
        Assert.Equal(40 * Bar + 2 * Beat, drop.Timestamp, 3);
        Assert.Contains("not on a bar line", session.StatusText);
    }

    [Fact]
    public void Drop_WithSnapOff_LandsExactlyOnTheCursor()
    {
        double hit = 40 * Bar + 0.123;
        var session = Session(hit);
        session.SnapToGrid = false;

        session.SetDropAtPlayheadCommand.Execute().Subscribe();

        Assert.Equal(hit, session.Cues.First(c => c.Role == CueRole.Drop).Timestamp, 4);
    }

    [Fact]
    public void Drop_OnABarLine_SaysNothingAboutTheGrid()
    {
        var session = Session(40 * Bar + 0.005);
        session.SetDropAtPlayheadCommand.Execute().Subscribe();
        Assert.Equal(40 * Bar, session.Cues.First(c => c.Role == CueRole.Drop).Timestamp, 3);
        Assert.DoesNotContain("not on a bar line", session.StatusText);
    }

    [Fact]
    public void AnchorBeats_ShiftsTheWholeGridSoABeatLandsExactlyOnTheAnchor()
    {
        var beats = Enumerable.Range(0, 400).Select(i => Math.Round(0.05 + i * Beat, 4)).ToArray();
        double anchor = beats[200] + 0.023; // the kick is 23 ms after the analysed beat

        var shifted = BeatGridEditService.AnchorBeats(beats, Bpm, anchor, 300);

        Assert.Contains(shifted, b => Math.Abs(b - anchor) < 1e-4);
        Assert.Equal(beats[201] + 0.023, shifted.First(b => b > anchor + 0.01), 4);
        Assert.Equal(beats.Length, shifted.Length);
    }

    [Fact]
    public void AnchorBeats_WithoutABeatList_BuildsAConstantGridThroughTheAnchor()
    {
        double anchor = 88.31;
        var grid = BeatGridEditService.AnchorBeats(Array.Empty<double>(), Bpm, anchor, 300);
        Assert.Contains(grid, b => Math.Abs(b - anchor) < 1e-3);
        Assert.True(grid[0] >= 0 && grid[0] < Beat);
    }
}
