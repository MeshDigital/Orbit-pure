using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit tests for the StructuralAnalysisEngine – pure heuristic, no I/O, fully deterministic.
/// </summary>
public class StructuralAnalysisEngineTests
{
    // ── ComputePhraseBoundaries ───────────────────────────────────────────

    [Fact]
    public void ComputePhraseBoundaries_ReturnsCorrectBeatInterval()
    {
        // 120 BPM → 0.5 s/beat
        var (beats, _) = StructuralAnalysisEngine.ComputePhraseBoundaries(120f, 10.0);

        Assert.True(beats.Count > 1);
        double interval = beats[1] - beats[0];
        Assert.InRange(interval, 0.499, 0.501);
    }

    [Fact]
    public void ComputePhraseBoundaries_FirstBoundaryIsZero()
    {
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(120f, 300.0);

        Assert.Equal(0.0, phrases[0], precision: 6);
    }

    [Fact]
    public void ComputePhraseBoundaries_PhraseIntervalEqualsBarDuration()
    {
        float bpm = 128f;
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, 600.0);

        // 16 bars × 4 beats × (60/128 s/beat)
        double expectedInterval = 16.0 * 4.0 * (60.0 / bpm);
        if (phrases.Count >= 2)
        {
            double actualInterval = phrases[1] - phrases[0];
            Assert.InRange(actualInterval, expectedInterval - 0.01, expectedInterval + 0.01);
        }
    }

    [Fact]
    public void ComputePhraseBoundaries_ThrowsOnZeroBpm()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StructuralAnalysisEngine.ComputePhraseBoundaries(0f, 300.0));
    }

    [Fact]
    public void ComputePhraseBoundaries_ThrowsOnZeroDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StructuralAnalysisEngine.ComputePhraseBoundaries(120f, 0.0));
    }

    // ── ComputeNovelty ────────────────────────────────────────────────────

    [Fact]
    public void ComputeNovelty_ClampNegativeDeltasToZero()
    {
        var energy = new List<float> { 0.8f, 0.6f, 0.4f }; // only decreasing
        var novelty = StructuralAnalysisEngine.ComputeNovelty(energy);

        Assert.Equal(3, novelty.Count);
        Assert.All(novelty, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ComputeNovelty_ReturnsPositiveDeltaForRisingEnergy()
    {
        var energy = new List<float> { 0.0f, 0.5f, 0.8f };
        var novelty = StructuralAnalysisEngine.ComputeNovelty(energy);

        Assert.Equal(0f, novelty[0]); // first element is always 0
        Assert.Equal(0.5f, novelty[1], precision: 4);
        Assert.Equal(0.3f, novelty[2], precision: 4);
    }

    [Fact]
    public void ComputeNovelty_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            StructuralAnalysisEngine.ComputeNovelty(null!));
    }

    // ── FindDrops ─────────────────────────────────────────────────────────

    [Fact]
    public void FindDrops_DetectsSingleObviousDrop()
    {
        // Build an energy curve: 30 s of low energy, then a spike at 32 s, then sustained high energy
        float bpm = 120f;
        double windowSeconds = 1.0;

        // Phrase boundary at 32 s (= 16 bars × 4 beats × 0.5 s)
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, 120.0);

        var energy = new List<float>();
        for (int i = 0; i < 120; i++)
        {
            if (i < 30)  energy.Add(0.3f); // build-up: low
            else         energy.Add(0.9f); // drop + sustain: high
        }
        // Add a spike at the phrase boundary (window 32)
        energy[32] = 1.0f;

        var drops = StructuralAnalysisEngine.FindDrops(energy, phrases, bpm, windowSeconds);

        Assert.NotEmpty(drops);
        // The drop should be around 32 s ± tolerance
        Assert.InRange(drops[0].TimestampSeconds, 28.0, 36.0);
        Assert.InRange(drops[0].Confidence, 0.0f, 1.0f);
    }

    [Fact]
    public void FindDrops_RejectsFakeDrop_LowSustainedEnergy()
    {
        // Spike at phrase boundary but energy drops immediately after (fake drop)
        float bpm = 120f;
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, 120.0);

        var energy = new List<float>();
        for (int i = 0; i < 120; i++)
        {
            if (i == 32) energy.Add(1.0f);      // spike only
            else         energy.Add(0.1f);       // low energy everywhere else
        }

        var drops = StructuralAnalysisEngine.FindDrops(energy, phrases, bpm);

        // A spike with no sustained energy should be rejected
        Assert.Empty(drops);
    }

    [Fact]
    public void FindDrops_LimitsToMaxDrops()
    {
        // Construct a curve with many spikes near phrase boundaries – should return at most MaxDrops
        float bpm = 120f;
        double windowSeconds = 1.0;
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, 600.0);

        var energy = Enumerable.Repeat(0.9f, 600).ToList();
        // Add spikes at every phrase boundary
        foreach (var boundary in phrases)
        {
            int idx = (int)boundary;
            if (idx < energy.Count)
                energy[idx] = 1.0f;
        }

        var drops = StructuralAnalysisEngine.FindDrops(energy, phrases, bpm, windowSeconds);

        Assert.InRange(drops.Count, 0, StructuralAnalysisEngine.MaxDrops);
    }

    [Fact]
    public void FindDrops_ReturnsEmptyForEmptyEnergyCurve()
    {
        var drops = StructuralAnalysisEngine.FindDrops(
            Array.Empty<float>(),
            new[] { 0.0, 32.0 },
            bpm: 120f);

        Assert.Empty(drops);
    }

    [Fact]
    public void FindDrops_ReturnsEmptyForNoPhraseBoundaries()
    {
        var drops = StructuralAnalysisEngine.FindDrops(
            Enumerable.Repeat(0.8f, 60).ToList(),
            Array.Empty<double>(),
            bpm: 120f);

        Assert.Empty(drops);
    }

    // ── Analyze (end-to-end) ──────────────────────────────────────────────

    [Fact]
    public void Analyze_WithZeroBpm_ReturnsEmptyResult()
    {
        var result = StructuralAnalysisEngine.Analyze(bpm: 0f, durationSeconds: 300.0);

        Assert.Empty(result.BeatTimestamps);
        Assert.Empty(result.PhraseBoundaries);
        Assert.Empty(result.Drops);
    }

    [Fact]
    public void Analyze_WithNoEnergyCurve_StillReturnsPhraseBoundaries()
    {
        var result = StructuralAnalysisEngine.Analyze(bpm: 128f, durationSeconds: 300.0);

        Assert.NotEmpty(result.BeatTimestamps);
        Assert.NotEmpty(result.PhraseBoundaries);
        Assert.Empty(result.Drops); // no drop detection without energy curve
    }

    [Fact]
    public void Analyze_DropTimestamps_FallWithinTrackDuration()
    {
        float bpm = 128f;
        double duration = 300.0;
        var energy = Enumerable.Range(0, (int)duration)
            .Select(i => i >= 60 ? 0.9f : 0.2f)
            .ToList<float>();
        energy[64] = 1.0f; // spike near first phrase boundary

        var result = StructuralAnalysisEngine.Analyze(bpm, duration, energy);

        foreach (var (ts, _) in result.Drops)
            Assert.InRange(ts, 0.0, duration);
    }

    [Fact]
    public void Analyze_WithEnergyCurve_EmitsStructuralSections()
    {
        float bpm = 128f;
        double duration = 300.0;
        var energy = Enumerable.Range(0, (int)duration)
            .Select(i => i < 48 ? 0.20f : i < 96 ? 0.55f : i < 144 ? 0.95f : i < 220 ? 0.35f : 0.18f)
            .ToList<float>();
        energy[96] = 1.0f;

        var result = StructuralAnalysisEngine.Analyze(bpm, duration, energy);

        Assert.NotEmpty(result.Sections);
        Assert.Equal(PhraseType.Intro, result.Sections.First().Type);
        Assert.Equal(PhraseType.Outro, result.Sections.Last().Type);
    }

    [Fact]
    public void Analyze_StrongPeak_ProducesDropOrBuildSection()
    {
        float bpm = 128f;
        double duration = 300.0;
        var energy = Enumerable.Range(0, (int)duration)
            .Select(i => i < 64 ? 0.15f : i < 96 ? 0.45f : i < 160 ? 0.98f : 0.25f)
            .ToList<float>();
        energy[96] = 1.0f;

        var result = StructuralAnalysisEngine.Analyze(bpm, duration, energy);

        Assert.Contains(result.Sections, s => s.Type == PhraseType.Drop || s.Type == PhraseType.Build);
    }
}
