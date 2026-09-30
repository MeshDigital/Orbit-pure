using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Engine;

public class DropCountdownCuesTests
{
    // 174 BPM: one bar = 240/174 s. Downbeat at 0.5 s, 300 s track.
    private const double Bpm = 174, Downbeat = 0.5, Duration = 300;
    private static readonly double Bar = 240.0 / Bpm;
    private static double AtBar(int n) => Downbeat + n * Bar;
    private static readonly int[] DnB = { 16, 8 };

    private static OrbitCue Auto(double t, string name, CueRole role = CueRole.Custom) =>
        new() { Timestamp = t, Name = name, Role = role, Source = CueSource.Auto, SlotIndex = -1 };

    // ── Templates ──

    [Theory]
    [InlineData("Drum and Bass", 174, DropCountdownCues.DnB)]
    [InlineData("Hardstyle", 150, DropCountdownCues.DnB)]
    [InlineData("Techno", 132, DropCountdownCues.DnB)]
    [InlineData("Tech House", 126, DropCountdownCues.LongBlend)]
    [InlineData("Uplifting Trance", 138, DropCountdownCues.LongBlend)]
    [InlineData("Hip-Hop", 92, DropCountdownCues.QuickMix)]
    [InlineData("Pop", 118, DropCountdownCues.QuickMix)]
    [InlineData(null, 174, DropCountdownCues.DnB)]
    public void Auto_PicksTheTemplateByGenre(string? genre, double bpm, string expected) =>
        Assert.Equal(expected, DropCountdownCues.AutoTemplate(genre, bpm));

    [Fact]
    public void Templates_ResolveToBarsLargestFirst()
    {
        Assert.Equal(new[] { 16, 8 }, DropCountdownCues.ResolveBars(DropCountdownCues.DnB, null, 174));
        Assert.Equal(new[] { 32, 16 }, DropCountdownCues.ResolveBars(DropCountdownCues.LongBlend, null, 124));
        Assert.Equal(new[] { 8, 4 }, DropCountdownCues.ResolveBars(DropCountdownCues.QuickMix, null, 95));
        Assert.Equal(new[] { 32, 16, 8 }, DropCountdownCues.ResolveBars(DropCountdownCues.Custom, null, 174, "8, 32,16"));
        Assert.Empty(DropCountdownCues.ResolveBars(DropCountdownCues.Off, "Drum and Bass", 174));
    }

    [Fact]
    public void OldNumericSettings_BecomeCustom()
    {
        Assert.Equal((DropCountdownCues.Custom, "16,8"), DropCountdownCues.Normalize("16,8", null));
        Assert.Equal(new[] { 16, 8 }, DropCountdownCues.ResolveBars("16,8", "House", 124));
        Assert.Equal(DropCountdownCues.Auto, DropCountdownCues.Normalize("nonsense", null).Mode);
    }

    // ── Layout ──

    [Fact]
    public void Layout_PutsDrop1OnABC_AndDrop2OnDEF()
    {
        var one = DropCountdownCues.Layout(1, AtBar(32), DnB, Bpm);
        Assert.Equal(new[] { ("[IN -16]", 0), ("[IN -8]", 1), ("[DROP 1]", 2) }, one.Select(c => (c.Name, c.SlotIndex)));
        Assert.Equal(AtBar(16), one[0].Timestamp, 6);

        var two = DropCountdownCues.Layout(2, AtBar(96), new[] { 32, 16, 8 }, Bpm);
        Assert.Equal(new[] { ("[IN -32]", -1), ("[IN -16]", 3), ("[IN -8]", 4), ("[DROP 2]", 5) }, two.Select(c => (c.Name, c.SlotIndex)));
    }

    [Fact]
    public void Layout_SkipsBuildInsBeforeTheTrackStarts()
    {
        var group = DropCountdownCues.Layout(1, 10 * Bar, DnB, Bpm);
        Assert.Equal(new[] { "[IN -8]", "[DROP 1]" }, group.Select(c => c.Name));
    }

    [Fact]
    public void OutTime_Is32BarsAfterTheLastDrop_LeavingRoomToMixOut()
    {
        Assert.Equal(AtBar(128), DropCountdownCues.OutTime(AtBar(96), Bpm, Downbeat, Duration)!.Value, 6);
        // Near the end: pulled back so at least 16 bars remain.
        double late = DropCountdownCues.OutTime(AtBar(190), Bpm, Downbeat, Duration)!.Value;
        Assert.True(Duration - late >= 16 * Bar - 1e-6);
    }

    // ── One-click drop ──

    [Fact]
    public void PlaceDrop_DoesEverything_AndRemovesTheAutoCues()
    {
        var cues = new List<OrbitCue> { Auto(AtBar(0), "Intro", CueRole.Intro), Auto(AtBar(40), "Drop 1", CueRole.Drop), Auto(280, "Outro", CueRole.Outro) };
        var mine = new OrbitCue { Timestamp = AtBar(60), Name = "My vocal", Source = CueSource.User, SlotIndex = 2 };
        cues.Add(mine);

        var result = DropCountdownCues.PlaceDrop(cues, AtBar(32), null, DnB, Bpm, Downbeat, Duration, out var drop);

        Assert.Equal("[DROP 1]", drop.Name);
        Assert.DoesNotContain(result, c => c.Source == CueSource.Auto);           // auto cues gone
        Assert.Contains(mine, result);                                             // yours stay…
        Assert.Equal(-1, mine.SlotIndex);                                          // …but pad C belongs to Drop 1
        Assert.Equal(new[] { ("[IN -16]", 0), ("[IN -8]", 1), ("[DROP 1]", 2), ("[OUT]", 6) },
            result.Where(c => c != mine).Select(c => (c.Name, c.SlotIndex)));
        Assert.Equal(AtBar(64), result.Single(c => c.Name == "[OUT]").Timestamp, 6);
    }

    [Fact]
    public void PlaceDrop_NumbersDropsByPosition_AndOutFollowsTheLastDrop()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(96), null, DnB, Bpm, Downbeat, Duration, out _);
        list = DropCountdownCues.PlaceDrop(list, AtBar(32), null, DnB, Bpm, Downbeat, Duration, out _);

        Assert.Equal(AtBar(32), list.Single(c => c.Name == "[DROP 1]").Timestamp, 6);
        Assert.Equal(AtBar(96), list.Single(c => c.Name == "[DROP 2]").Timestamp, 6);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, list.Select(c => c.SlotIndex).OrderBy(i => i));
        Assert.Equal(AtBar(128), list.Single(c => c.Name == "[OUT]").Timestamp, 6);
        Assert.Equal(1, list.Count(c => c.Name == "[OUT]"));
    }

    [Fact]
    public void PlaceDrop_NearAnExistingDrop_MovesIt()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(32), null, DnB, Bpm, Downbeat, Duration, out _);
        list = DropCountdownCues.PlaceDrop(list, AtBar(34), null, DnB, Bpm, Downbeat, Duration, out var moved);

        Assert.Single(list, c => c.Role == CueRole.Drop);
        Assert.Equal(AtBar(34), moved.Timestamp, 6);
        Assert.Equal(AtBar(18), list.Single(c => c.Name == "[IN -16]").Timestamp, 6);   // build-ins followed
        Assert.Equal(2, list.Count(c => c.Role == CueRole.Build));
    }

    [Fact]
    public void PlaceDrop_WithANumber_ReplacesThatDrop()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(32), null, DnB, Bpm, Downbeat, Duration, out _);
        list = DropCountdownCues.PlaceDrop(list, AtBar(96), null, DnB, Bpm, Downbeat, Duration, out _);

        list = DropCountdownCues.PlaceDrop(list, AtBar(112), 2, DnB, Bpm, Downbeat, Duration, out var drop2);

        Assert.Equal("[DROP 2]", drop2.Name);
        Assert.Equal(2, list.Count(c => c.Role == CueRole.Drop));
        Assert.DoesNotContain(list, c => c.Role == CueRole.Drop && System.Math.Abs(c.Timestamp - AtBar(96)) < 0.01);
        Assert.Equal(4, list.Count(c => c.Role == CueRole.Build)); // no orphaned build-ins
    }

    [Fact]
    public void AnOutYouMoved_StaysPut()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(32), null, DnB, Bpm, Downbeat, Duration, out _);
        list.Single(c => c.Name == "[OUT]").Timestamp = AtBar(100);

        list = DropCountdownCues.PlaceDrop(list, AtBar(96), null, DnB, Bpm, Downbeat, Duration, out _);

        Assert.Equal(AtBar(100), list.Single(c => c.Name == "[OUT]").Timestamp, 6);
    }

    // ── Linking ──

    [Fact]
    public void BuildIns_FollowTheDrop_UnlessMovedByHand()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(32), null, DnB, Bpm, Downbeat, Duration, out var drop);
        var in8 = list.Single(c => c.Name == "[IN -8]");
        in8.Timestamp = AtBar(20); // moved by hand → unlinked

        double from = drop.Timestamp;
        drop.Timestamp = AtBar(40);
        list = DropCountdownCues.Rebuild(list, drop, DnB, Bpm, previousDropTime: from);

        Assert.Equal(AtBar(24), list.Single(c => c.Name == "[IN -16]").Timestamp, 6);
        Assert.Contains(list, c => c.Name == "[IN -8]" && System.Math.Abs(c.Timestamp - AtBar(32)) < 1e-6); // fresh one
        Assert.Contains(in8, list);                                                                           // yours kept
    }

    [Fact]
    public void OlderCountdownNames_AreStillRecognised()
    {
        var old = new OrbitCue { Timestamp = AtBar(16), Name = "16 Bars to Drop 1", Role = CueRole.Build };
        Assert.True(DropCountdownCues.IsCountdownFor(old, AtBar(32), Bpm));
        Assert.Equal(1, DropCountdownCues.DropNumber("Drop 1 ✓AI"));
        Assert.Equal(2, DropCountdownCues.DropNumber("[DROP 2]"));
    }

    [Fact]
    public void RemoveFor_OnlyRemovesThatDropsBuildIns()
    {
        var list = DropCountdownCues.PlaceDrop(new List<OrbitCue>(), AtBar(32), null, DnB, Bpm, Downbeat, Duration, out _);
        list = DropCountdownCues.PlaceDrop(list, AtBar(96), null, DnB, Bpm, Downbeat, Duration, out _);

        var left = DropCountdownCues.RemoveFor(list, AtBar(32), Bpm);

        Assert.Equal(2, left.Count(c => c.Role == CueRole.Build));
        Assert.All(left.Where(c => c.Role == CueRole.Build), c => Assert.True(c.Timestamp > AtBar(32)));
    }
}
