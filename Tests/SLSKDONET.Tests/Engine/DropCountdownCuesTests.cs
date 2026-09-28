using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Engine;

public class DropCountdownCuesTests
{
    // 174 BPM: one bar = 240/174 s.
    private const double Bpm = 174;
    private static readonly double Bar = 240.0 / Bpm;

    private static OrbitCue Drop(double t, string name = "Drop 1") => new() { Timestamp = t, Name = name, Role = CueRole.Drop, SlotIndex = 0 };

    [Theory]
    [InlineData("Drum and Bass", new[] { 32, 16, 8 })]
    [InlineData("Tech House", new[] { 32, 16 })]
    [InlineData(null, new[] { 32, 16, 8 })]
    public void Auto_PicksBarsByGenreFamily(string? genre, int[] expected)
    {
        Assert.Equal(expected, DropCountdownCues.ResolveBars(DropCountdownCues.Auto, genre, genre == "Tech House" ? 126 : 174));
    }

    [Fact]
    public void FixedAndOffModes()
    {
        Assert.Equal(new[] { 16, 8 }, DropCountdownCues.ResolveBars("16,8", "House", 124));
        Assert.Equal(new[] { 32, 16, 8 }, DropCountdownCues.ResolveBars("8,32,16", null, 174)); // largest first
        Assert.Empty(DropCountdownCues.ResolveBars(DropCountdownCues.Off, "Drum and Bass", 174));
    }

    [Fact]
    public void Rebuild_PlacesCountdownsExactlyThatManyBarsBeforeTheDrop()
    {
        var drop = Drop(120);
        var result = DropCountdownCues.Rebuild(new[] { drop }, drop, new[] { 32, 16, 8 }, Bpm);

        Assert.Equal(4, result.Count);
        foreach (var n in new[] { 32, 16, 8 })
        {
            var c = Assert.Single(result, x => x.Name == $"{n} Bars to Drop 1");
            Assert.Equal(120 - n * Bar, c.Timestamp, 6);
            Assert.Equal(CueRole.Build, c.Role);
            Assert.Equal(CueSource.User, c.Source);
        }
        // Drop keeps pad A; countdowns take the next free pads.
        Assert.Equal(new[] { 1, 2, 3 }, result.Where(c => c.Role == CueRole.Build).Select(c => c.SlotIndex).OrderBy(i => i));
    }

    [Fact]
    public void Rebuild_ReplacesOldCountdowns_WhenTheDropMoves_AndLeavesHandPlacedCuesAlone()
    {
        var drop = Drop(120);
        var mine = new OrbitCue { Timestamp = 100, Name = "My build", Role = CueRole.Build, SlotIndex = -1 };
        var first = DropCountdownCues.Rebuild(new[] { drop, mine }, drop, new[] { 16, 8 }, Bpm);

        drop.Timestamp = 130;
        var moved = DropCountdownCues.Rebuild(first, drop, new[] { 16, 8 }, Bpm);

        Assert.Equal(2, moved.Count(c => DropCountdownCues.IsCountdownFor(c, "Drop 1")));
        Assert.Equal(130 - 16 * Bar, moved.Single(c => c.Name == "16 Bars to Drop 1").Timestamp, 6);
        Assert.Contains(mine, moved);
    }

    [Fact]
    public void Rebuild_SkipsCountdownsBeforeTheTrackStart_AndFollowsARename()
    {
        var drop = Drop(10 * Bar, "Drop 1"); // only room for 8 bars
        var first = DropCountdownCues.Rebuild(new[] { drop }, drop, new[] { 32, 16, 8 }, Bpm);
        Assert.Single(first, c => c.Role == CueRole.Build);

        drop.Name = "Big drop";
        var renamed = DropCountdownCues.Rebuild(first, drop, new[] { 32, 16, 8 }, Bpm, previousDropName: "Drop 1");
        Assert.DoesNotContain(renamed, c => c.Name.EndsWith("Drop 1"));
        Assert.Single(renamed, c => c.Name == "8 Bars to Big drop");
    }

    [Fact]
    public void RemoveFor_DropsOnlyThatDropsCountdowns()
    {
        var drop1 = Drop(120, "Drop 1");
        var drop2 = Drop(240, "Drop 2");
        var cues = DropCountdownCues.Rebuild(new[] { drop1, drop2 }, drop1, new[] { 8 }, Bpm);
        cues = DropCountdownCues.Rebuild(cues, drop2, new[] { 8 }, Bpm);

        var left = DropCountdownCues.RemoveFor(cues, "Drop 1");

        Assert.DoesNotContain(left, c => c.Name == "8 Bars to Drop 1");
        Assert.Contains(left, c => c.Name == "8 Bars to Drop 2");
    }
}
