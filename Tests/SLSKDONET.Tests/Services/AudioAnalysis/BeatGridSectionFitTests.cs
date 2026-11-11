using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>Piecewise-constant section fitting (issue #226).</summary>
public class BeatGridSectionFitTests
{
    private static double[] VariableGrid(double bpm1, int beats1, double bpm2, int beats2)
    {
        var ticks = new List<double>();
        double t = 0;
        for (int i = 0; i < beats1; i++) { ticks.Add(t); t += 60.0 / bpm1; }
        for (int i = 0; i < beats2; i++) { ticks.Add(t); t += 60.0 / bpm2; }
        return ticks.ToArray();
    }

    private static double[] SteadyGrid(double bpm, int beats)
    {
        var ticks = new List<double>();
        double t = 0;
        for (int i = 0; i < beats; i++) { ticks.Add(t); t += 60.0 / bpm; }
        return ticks.ToArray();
    }

    [Fact]
    public void Synthetic108to128_FitsAsTwoSections_Within005Bpm()
    {
        var ticks = VariableGrid(108.0, 200, 128.0, 200);

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Equal(2, sections.Count);
        Assert.Equal(0, sections[0].StartBeatIndex);
        Assert.Equal(108.0, sections[0].Bpm, 2);
        Assert.Equal(200, sections[1].StartBeatIndex);
        Assert.Equal(128.0, sections[1].Bpm, 2);
        Assert.True(Math.Abs(sections[0].Bpm - 108.0) < 0.05);
        Assert.True(Math.Abs(sections[1].Bpm - 128.0) < 0.05);
    }

    [Fact]
    public void SteadyTrack_FitsAsOneSection()
    {
        var ticks = SteadyGrid(174.0, 500);

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Single(sections);
        Assert.Equal(174.0, sections[0].Bpm, 2);
    }

    [Fact]
    public void SectionsStartAtTheBeatTimes()
    {
        var ticks = VariableGrid(108.0, 200, 128.0, 200);

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Equal(ticks[0], sections[0].StartSeconds, 6);
        Assert.Equal(ticks[200], sections[1].StartSeconds, 6);
    }

    [Fact]
    public void TooShortTrack_ReturnsEmptyOrSingleSection()
    {
        var ticks = SteadyGrid(174.0, 5); // fewer than 2*minSectionBeats
        var sections = BeatGridFitter.FitSections(ticks);
        Assert.True(sections.Count <= 1);
    }

    [Fact]
    public void EmptyOrSingleBeat_ReturnsEmpty()
    {
        Assert.Empty(BeatGridFitter.FitSections(new double[0]));
        Assert.Empty(BeatGridFitter.FitSections(new[] { 0.0 }));
    }

    [Fact]
    public void LocalBpmAt_ReturnsTheSectionBpm()
    {
        var ticks = VariableGrid(108.0, 200, 128.0, 200);
        Assert.Equal(108.0, BeatGridFitter.LocalBpmAt(ticks, 10.0, 108.0), 2);
        Assert.Equal(128.0, BeatGridFitter.LocalBpmAt(ticks, ticks[250], 108.0), 2);
    }

    [Fact]
    public void LocalBpmAt_SteadyTrack_ReturnsFallback()
    {
        var ticks = SteadyGrid(174.0, 500);
        Assert.Equal(174.0, BeatGridFitter.LocalBpmAt(ticks, 100.0, 174.0), 2);
    }
}
