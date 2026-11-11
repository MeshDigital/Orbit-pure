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

    /// <summary>
    /// Regression (2026-11-11): Essentia's ticks are quantised to 512-sample frames (11.6 ms at 44.1 kHz),
    /// so a steady 174 BPM track's single intervals alternate between ~172.27 and ~178.2 BPM. The old
    /// median/0.3% detector flagged 95 of 5,901 real (steady) tracks as variable-tempo.
    /// </summary>
    [Theory]
    [InlineData(174.0)]
    [InlineData(172.0)]
    [InlineData(128.0)]
    [InlineData(87.0)]
    public void FrameQuantisedSteadyTrack_FitsAsOneSection(double bpm)
    {
        const double frame = 512.0 / 44100.0;
        var ticks = Enumerable.Range(0, 900)
            .Select(k => Math.Round((0.37 + k * 60.0 / bpm) / frame) * frame)
            .ToArray();

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Single(sections);
        Assert.True(Math.Abs(sections[0].Bpm - bpm) < 0.05, $"section BPM {sections[0].Bpm:F3} vs {bpm}");
    }

    /// <summary>
    /// Regression (2026-11-11, second pass on a real library): the beat tracker drops to half-time in a
    /// DnB breakdown (ticks every other beat: 86 ↔ 174 BPM) and misses single beats. Neither is a tempo
    /// change; 94 steady tracks were still flagged variable until intervals were octave-folded.
    /// </summary>
    [Fact]
    public void HalfTimeBreakdownAndMissedBeats_StayOneSection()
    {
        const double period = 60.0 / 174.0;
        var ticks = new List<double>();
        double t = 0.25;
        for (int i = 0; i < 256; i++) { ticks.Add(t); t += period; }          // full-time
        for (int i = 0; i < 64; i++) { ticks.Add(t); t += 2 * period; }       // half-time breakdown
        for (int i = 0; i < 256; i++)
        {
            ticks.Add(t);
            t += (i % 37 == 0) ? 2 * period : period;                           // an occasional missed beat
        }

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Single(sections);
        Assert.True(Math.Abs(sections[0].Bpm - 174.0) < 0.5, $"section BPM {sections[0].Bpm:F2}");
    }

    [Fact]
    public void WanderingTrackerTicks_AreNotReportedAsTempoSections()
    {
        // A tracker that loses the beat in a breakdown: intervals wander ±12% for 128 beats.
        var rng = new System.Random(7);
        const double period = 60.0 / 174.0;
        var ticks = new List<double>();
        double t = 0.2;
        for (int i = 0; i < 256; i++) { ticks.Add(t); t += period; }
        for (int i = 0; i < 128; i++) { ticks.Add(t); t += period * (0.88 + 0.24 * rng.NextDouble()) * (i < 64 ? 1.2 : 0.9); }
        for (int i = 0; i < 256; i++) { ticks.Add(t); t += period; }

        Assert.Single(BeatGridFitter.FitSections(ticks));
    }

    [Fact]
    public void MetricRatioLock_IsNotATempoChange()
    {
        // 87 → 116 BPM is 4:3 — the tracker locking to a dotted pulse of a steady 174 track.
        var ticks = VariableGrid(87.0, 200, 116.0, 200);
        Assert.Single(BeatGridFitter.FitSections(ticks));
    }

    [Fact]
    public void FrameQuantisedVariableTrack_StillSplitsAtTheRealChange()
    {
        const double frame = 512.0 / 44100.0;
        var exact = VariableGrid(108, 200, 128, 300);
        var ticks = exact.Select(t => Math.Round(t / frame) * frame).ToArray();

        var sections = BeatGridFitter.FitSections(ticks);

        Assert.Equal(2, sections.Count);
        Assert.InRange(sections[1].StartBeatIndex, 198, 202);
        Assert.True(Math.Abs(sections[0].Bpm - 108.0) < 0.1);
        Assert.True(Math.Abs(sections[1].Bpm - 128.0) < 0.1);
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
