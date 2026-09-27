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
    public void FindDrops_DetectsDropFarFromAnyPhraseGridBoundary()
    {
        // Phrase grid boundaries at 120 BPM (16 bars) are at 0, 32, 64, ... seconds. The old
        // implementation only searched within PhraseBoundaryToleranceSeconds (4s) of one of
        // those boundaries, so a real drop landing well off-grid — an odd-length intro, a
        // non-16-bar breakdown, BPM-drift compounding over minutes — would never be found at
        // all. Verified against real library tracks (Gancher & Ruin - Rituals, ShockOne -
        // Follow Me) where the true drop sat many bars off the rigid grid. The rise here is at
        // t=20, 12s from the nearest boundary (32s) — well outside the old tolerance.
        float bpm = 120f;
        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, 120.0);

        var energy = new List<float>();
        for (int i = 0; i < 120; i++)
            energy.Add(i < 20 ? 0.3f : 0.9f);

        var drops = StructuralAnalysisEngine.FindDrops(energy, phrases, bpm);

        Assert.NotEmpty(drops);
        Assert.InRange(drops[0].TimestampSeconds, 18.0, 22.0);
    }

    [Fact]
    public void FindDrops_PrefersDipPrecededCandidate_OverEqualNoveltyWithoutDip()
    {
        // Regression test for the pre-drop-silence scoring bonus: real EDM productions cut
        // energy right before the drop lands (HPF sweep + sample-drop silence) specifically to
        // make the drop hit harder, so a candidate preceded by that dip is far more likely the
        // real drop than an equal-strength novelty spike that isn't. Verified against a real
        // library track (Metrik - Simulation): multiple novelty peaks of comparable raw strength
        // existed, and only the dip-preceded one was the actual drop (confirmed by an
        // independent, pre-existing detection signal agreeing with it).
        float bpm = 120f;
        int duration = 110;
        var energy = new float[duration];

        for (int i = 0; i < 30; i++) energy[i] = 0.40f;        // buildup baseline for candidate A
        for (int i = 30; i < 59; i++) energy[i] = 0.85f;       // candidate A: jump with NO dip before it
        for (int i = 59; i < 79; i++) energy[i] = 0.40f;       // settle back down / buildup baseline for B
        energy[79] = 0.15f;                                     // candidate B: genuine pre-drop dip
        for (int i = 80; i < duration; i++) energy[i] = 0.60f; // candidate B: jump — same raw novelty as A

        var (_, phrases) = StructuralAnalysisEngine.ComputePhraseBoundaries(bpm, duration);
        var drops = StructuralAnalysisEngine.FindDrops(energy.ToList(), phrases, bpm);

        Assert.NotEmpty(drops);
        // Both candidates have identical raw novelty (0.45) — only the dip-scoring bonus can make
        // the dip-preceded one (t=80) rank ahead of the equal-strength, dip-less one (t=30).
        Assert.InRange(drops[0].TimestampSeconds, 78.0, 82.0);
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

    [Fact]
    public void Analyze_DropSectionStart_UsesActualDropTimestamp_NotGridBoundary()
    {
        // Regression test: a Drop-classified StructuralSection previously always reported its
        // enclosing phrase-grid slot's boundary as StartSeconds, discarding the actual detected
        // drop timestamp — losing up to a full phrase's worth of precision (commonly 20-40s).
        // Verified against real library tracks (Gancher & Ruin - Rituals, ShockOne - Follow Me)
        // this cost 40s+ of cue-placement accuracy. The section's Start must match where the drop
        // actually was, not the grid slot it happened to land inside.
        float bpm = 120f;
        double duration = 300.0;
        var energy = Enumerable.Range(0, (int)duration)
            .Select(i => i < 50 ? 0.2f : 0.9f)
            .ToList<float>();

        var result = StructuralAnalysisEngine.Analyze(bpm, duration, energy);

        // 16-bar grid slot at 120 BPM is [32, 64) — the drop at t=50 sits mid-slot, not at 32.
        var dropSection = Assert.Single(result.Sections, s => s.Type == PhraseType.Drop);
        Assert.InRange(dropSection.StartSeconds, 48.0, 52.0);
    }

    [Fact]
    public void Analyze_DropNearSharedGridBoundary_DoesNotCollapseIntoNearZeroDurationSliver()
    {
        // Regression test for a real bug found via a library track (Maduk, Lexurus, RIENK - New
        // Beginning): a single real drop landing right at a grid-slot boundary satisfied the
        // Start-override match for BOTH the section ending there and the section starting there,
        // collapsing the first into a spurious near-zero-duration "Drop" sliver directly beside
        // the real one (Drop@88.78 dur=0.01s next to the real Drop@89.00 dur=21.98s). No
        // Drop-classified section should end up with a degenerate duration purely because the
        // Start override matched a timestamp that actually belongs to a neighboring slot.
        float bpm = 120f;
        double duration = 200.0;
        // 16-bar grid slot boundary at 120 BPM falls at t=64 — the rise happens exactly there.
        var energy = Enumerable.Range(0, (int)duration)
            .Select(i => i < 64 ? 0.2f : 0.9f)
            .ToList<float>();

        var result = StructuralAnalysisEngine.Analyze(bpm, duration, energy);

        var dropSections = result.Sections.Where(s => s.Type == PhraseType.Drop).ToList();
        Assert.NotEmpty(dropSections);
        Assert.All(dropSections, s => Assert.True(s.EndSeconds - s.StartSeconds > 1.0,
            $"Drop section [{s.StartSeconds},{s.EndSeconds}) has a near-zero duration — likely a boundary drop claimed by two neighboring sections."));
    }
}
