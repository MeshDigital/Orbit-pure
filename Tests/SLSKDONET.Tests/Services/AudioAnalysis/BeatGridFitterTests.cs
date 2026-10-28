using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public class BeatGridFitterTests
{
    // Essentia's ticks are quantised to 512-sample frames at 44.1 kHz.
    private const double Frame = 512.0 / 44100.0;

    private static List<double> Ticks(double bpm, double firstBeat, int count, Func<int, bool>? keep = null)
    {
        double period = 60.0 / bpm;
        return Enumerable.Range(0, count)
            .Where(k => keep?.Invoke(k) ?? true)
            .Select(k => Math.Round((firstBeat + k * period) / Frame) * Frame)
            .ToList();
    }

    [Fact]
    public void FrameQuantisedTicks_RecoverTheRealTempo_NotAQuantisedOne()
    {
        // At 174 BPM every tick interval is 29 or 30 frames (178.2 / 172.27 BPM) — the values the
        // old median-based BPM snapped to.
        var fit = BeatGridFitter.FitTicks(Ticks(174.0, 0.41, 600), bpmHint: 174);

        Assert.NotNull(fit);
        Assert.InRange(fit!.Bpm, 173.98, 174.02);
        // Phase is the earliest grid beat at/after 0s — the first tick (0.41s) minus one beat.
        Assert.InRange(fit.PhaseSeconds, 0.41 - 60.0 / 174 - 0.01, 0.41 - 60.0 / 174 + 0.01);
    }

    [Fact]
    public void HalfTimeTicks_AreFittedAtTheHintedOctave()
    {
        // DnB: the tracker follows the half-time groove (87 BPM); the octave-corrected BPM says 178.
        var fit = BeatGridFitter.FitTicks(Ticks(87.0, 0.5, 300), bpmHint: 178);

        Assert.NotNull(fit);
        Assert.InRange(fit!.Bpm, 173.97, 174.03);
    }

    [Fact]
    public void GapsAndAWanderingStretch_DoNotDragTheTempo()
    {
        var ticks = Ticks(174.0, 0.2, 700, keep: k => k is < 200 or > 264); // 64-beat breakdown, no ticks
        // A stretch where the tracker drifted onto a different tempo.
        var wander = Enumerable.Range(0, 40).Select(k => 300.0 + k * 60.0 / 168.0);
        var all = ticks.Concat(wander).OrderBy(t => t).ToList();

        var fit = BeatGridFitter.FitTicks(all, bpmHint: 174);

        Assert.NotNull(fit);
        Assert.InRange(fit!.Bpm, 173.97, 174.03);
    }

    [Fact]
    public void TooFewOrTempolessTicks_ReturnNull()
    {
        Assert.Null(BeatGridFitter.FitTicks(Ticks(174, 0, 10), 174));

        var rng = new Random(7);
        double t = 0;
        var random = Enumerable.Range(0, 300).Select(_ => t += 0.2 + rng.NextDouble() * 0.6).ToList();
        Assert.Null(BeatGridFitter.FitTicks(random, 120));
    }

    [Fact]
    public void BarPhase_FollowsBassReturns_AndLagCorrectedDropouts()
    {
        var fit = new BeatGridFitter.Fit(PeriodSeconds: 0.5, PhaseSeconds: 0.25, Bpm: 120, TicksUsed: 100, ResidualRmsBeats: 0.01);
        double Beat(int k) => 0.25 + k * 0.5;

        // Real bar lines on beats 2, 6, 10, ... : returns land on them; dropouts one beat later.
        var events = new BeatGridFitter.StructuralEvents(
            BassReturns: new[] { Beat(34), Beat(66) },
            BassDropouts: new[] { Beat(51), Beat(99) });

        Assert.Equal(2, BeatGridFitter.EstimateBarPhase(fit, events));
    }

    [Fact]
    public void BarPhase_WithTooFewEvents_IsUndecided()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.25, 120, 100, 0.01);
        Assert.Null(BeatGridFitter.EstimateBarPhase(fit, new BeatGridFitter.StructuralEvents(new[] { 10.25 }, Array.Empty<double>())));
    }

    [Fact]
    public void ApplyTo_WritesAPreciseGridAndMovesTheDownbeatOntoIt()
    {
        var features = new AudioFeaturesEntity
        {
            Bpm = 178f,               // what the old half-time path stored
            TrackDuration = 200,
            DownbeatOffsetSeconds = 0.52,
        };
        var ticks = Ticks(87.0, 0.5, 280);

        Assert.True(BeatGridFitter.ApplyTo(features, ticks));

        Assert.InRange(features.Bpm, 173.97f, 174.03f);
        var grid = JsonSerializer.Deserialize<double[]>(features.BeatGridJson)!;
        Assert.InRange(grid[1] - grid[0], 60.0 / 174.03, 60.0 / 173.97);
        Assert.Contains(features.DownbeatOffsetSeconds, grid);
        Assert.InRange(features.DownbeatOffsetSeconds, 0.45, 0.55);
    }

    private static List<SLSKDONET.Services.Rekordbox.RekordboxBeat> RekordboxGrid(double bpm, double firstDownbeat, int count) =>
        Enumerable.Range(0, count)
            .Select(k => new SLSKDONET.Services.Rekordbox.RekordboxBeat(k % 4 + 1, bpm, Math.Round(firstDownbeat + k * 60.0 / bpm, 3)))
            .ToList();

    [Fact]
    public void RekordboxGrid_IsAdopted_WhenOrbitsTicksLineUpWithIt()
    {
        var features = new AudioFeaturesEntity { Bpm = 178f, TrackDuration = 200, DownbeatOffsetSeconds = 1.0 };
        var ticks = Ticks(87.0, 0.2, 280); // half-time ticks on the same audio

        Assert.True(BeatGridFitter.ApplyRekordboxGrid(features, RekordboxGrid(174, 0.2, 500), ticks));

        Assert.Equal(174f, features.Bpm);
        Assert.Equal(0.2, features.DownbeatOffsetSeconds, 3);
    }

    [Fact]
    public void RekordboxGrid_IsRejected_WhenItBelongsToADifferentCopy()
    {
        // Same tempo, but the Rekordbox file has a different lead-in: every beat is 150 ms off.
        var features = new AudioFeaturesEntity { Bpm = 174f, TrackDuration = 200, BeatGridJson = "[]", DownbeatOffsetSeconds = 1.0 };
        var ticks = Ticks(174.0, 0.2, 500);

        Assert.False(BeatGridFitter.ApplyRekordboxGrid(features, RekordboxGrid(174, 0.35, 500), ticks));
        Assert.Equal(1.0, features.DownbeatOffsetSeconds);
    }

    [Fact]
    public void ApplyTo_UnfittableTicks_LeavesTheEntityUntouched()
    {
        var features = new AudioFeaturesEntity { Bpm = 128f, BeatGridJson = "[1,2]", DownbeatOffsetSeconds = 1 };
        Assert.False(BeatGridFitter.ApplyTo(features, new[] { 1.0, 2.0 }));
        Assert.Equal(128f, features.Bpm);
        Assert.Equal("[1,2]", features.BeatGridJson);
    }
}
