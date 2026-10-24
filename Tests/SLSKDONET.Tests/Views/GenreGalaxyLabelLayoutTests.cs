using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.Views;

/// <summary>
/// The Dashboard's Genre Galaxy drew every name above its bubble, so "drumstep" sat on top of
/// "uk garage" and "bassline" on top of "jungle", and the counts of overlapping bubbles collided.
/// </summary>
public class GenreGalaxyLabelLayoutTests
{
    // ~6.5 px per character at 12 px, like the real label paint.
    private static float Label(string s) => s.Length * 6.5f;
    private static float Count(string s) => s.Length * 6f;

    private static readonly GalaxyLabelInput[] Crowded =
    {
        new("drum & bass", 300, 110, 40, "drum & bass", "412"),
        new("drumstep", 250, 96, 24, "drumstep", "31"),
        new("uk garage", 258, 104, 22, "uk garage", "28"),   // almost on top of drumstep
        new("bassline", 360, 128, 22, "bassline", "19"),
        new("jungle", 366, 120, 21, "jungle", "17"),        // almost on top of bassline
        new("dubstep", 180, 140, 30, "dubstep", "88"),
        new("house", 440, 80, 28, "house", "64"),
        new("techno", 120, 70, 26, "techno", "45"),
    };

    private static List<GalaxyLabelPlacement> Run(IDictionary<string, int>? previous = null) =>
        GenreGalaxyLabelLayout.Layout(Crowded, 640, 240, Label, 15, Count, 13, previous);

    [Fact]
    public void NoTwoLabelsOverlap()
    {
        var placed = Run();
        Assert.Equal(Crowded.Length, placed.Count);
        for (int i = 0; i < placed.Count; i++)
            for (int j = i + 1; j < placed.Count; j++)
                Assert.False(placed[i].Box.Intersects(placed[j].Box),
                    $"'{placed[i].Text}' overlaps '{placed[j].Text}'");
    }

    [Fact]
    public void CountsInsideBubbles_NeverOverlapEachOtherOrALabel()
    {
        var placed = Run();
        var inside = placed.Where(p => p.CountInside).ToList();
        for (int i = 0; i < inside.Count; i++)
            for (int j = i + 1; j < inside.Count; j++)
                Assert.False(inside[i].CountBox.Intersects(inside[j].CountBox), $"counts of {inside[i].Key} and {inside[j].Key} collide");
        foreach (var c in inside)
            foreach (var l in placed)
                Assert.False(c.CountBox.Intersects(l.Box), $"count of {c.Key} is under label '{l.Text}'");
    }

    [Fact]
    public void ACountThatCannotSitInside_MovesIntoTheLabel()
    {
        var placed = Run();
        // uk garage's centre is 10 px from drumstep's: both counts cannot fit inside.
        var garage = placed.Single(p => p.Key == "uk garage");
        var drumstep = placed.Single(p => p.Key == "drumstep");
        Assert.False(garage.CountInside && drumstep.CountInside);
        foreach (var p in placed.Where(p => !p.CountInside))
            Assert.EndsWith(" · " + p.Count, p.Text);
    }

    [Fact]
    public void LabelsStayInsideTheCanvas()
    {
        foreach (var p in Run())
        {
            Assert.True(p.Box.Left >= 0 && p.Box.Top >= 0, p.Text);
            Assert.True(p.Box.Right <= 640 && p.Box.Bottom <= 240, p.Text);
        }
    }

    [Fact]
    public void PreviousSpotIsKept_WhenStillFree_SoLabelsDoNotFlicker()
    {
        var memory = new Dictionary<string, int>();
        var first = Run(memory).ToDictionary(p => p.Key, p => p.Candidate);
        var second = Run(memory).ToDictionary(p => p.Key, p => p.Candidate);
        Assert.Equal(first, second);
    }

    [Fact]
    public void FarSpots_GetALeaderLine()
    {
        foreach (var p in Run())
            Assert.Equal(p.Candidate >= 4, p.Leader);
    }
}
