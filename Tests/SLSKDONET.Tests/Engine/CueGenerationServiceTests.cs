using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Engine.Snapping;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Engine;

/// <summary>
/// Coverage for the real cue-generation engine Cue Forge actually uses
/// (Engine.Cueing.CueGenerationService — distinct from the legacy, differently-namespaced
/// Services.CueGenerationService covered by Services/CueGenerationServiceTests.cs).
///
/// GenerateCues is a pure function (no DB access), so these tests exercise it directly
/// against hand-built AnalysisPipelineResult inputs — no database or mocking needed beyond
/// satisfying the constructor's IDbContextFactory dependency, which GenerateCues never touches.
/// </summary>
public class CueGenerationServiceTests
{
    private static CueGenerationService CreateService()
    {
        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        var breakbeatStrategy = new BreakbeatAnalysisStrategy(
            new SLSKDONET.Services.AudioAnalysis.DnBTransientDetectionService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SLSKDONET.Services.AudioAnalysis.DnBTransientDetectionService>.Instance));
        var fourOnFloorStrategy = new FourOnTheFloorAnalysisStrategy();
        return new CueGenerationService(factoryMock.Object, breakbeatStrategy, fourOnFloorStrategy);
    }

    private const double DurationSeconds = 240.0; // 4:00
    private const double Bpm = 174.0; // typical DnB tempo
    private const double DownbeatAnchor = 0.2;

    [Fact]
    public void GenerateCues_InvalidDuration_ReturnsEmpty()
    {
        var service = CreateService();
        var analysis = new AnalysisPipelineResult { Bpm = (float)Bpm, DurationSeconds = 0 };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);

        Assert.Empty(cues);
    }

    [Fact]
    public void GenerateCues_InvalidBpm_ReturnsEmpty()
    {
        var service = CreateService();
        var analysis = new AnalysisPipelineResult { Bpm = 0, DurationSeconds = DurationSeconds };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);

        Assert.Empty(cues);
    }

    [Fact]
    public void GenerateCues_AllPaths_ReturnCuesSortedByTimestamp()
    {
        var service = CreateService();

        foreach (var analysis in new[] { MlAnalysis(), DspAnalysis(), HeuristicAnalysis() })
        {
            var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
            var timestamps = cues.Select(c => c.TimestampInSeconds).ToList();

            Assert.Equal(timestamps.OrderBy(t => t), timestamps);
            Assert.All(cues, c => Assert.True(c.TimestampInSeconds >= 0));
        }
    }

    [Fact]
    public void GenerateCues_WithTwoPhraseSegments_UsesMlPath_NotFallbackGuess()
    {
        var service = CreateService();
        // Fallback (no signal) would place Drop 1 at duration * 0.35 = 84s. Put the real
        // segment somewhere clearly different so we can tell the ML path actually drove it.
        var analysis = MlAnalysis(dropStartSeconds: 150.0);

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");

        double fallbackTime = DurationSeconds * 0.35;
        Assert.True(Math.Abs(drop1.TimestampInSeconds - fallbackTime) > 20,
            "Drop 1 landed on the no-signal fallback position instead of the real phrase segment.");
        Assert.InRange(drop1.TimestampInSeconds, 140.0, 160.0);
    }

    [Fact]
    public void GenerateCues_WithSubBassReturnSignal_UsesDspPath_NotFallbackGuess()
    {
        var service = CreateService();
        double fallbackTime = DurationSeconds * 0.32;
        double realDropTime = 100.0;

        var analysis = DspAnalysis(subBassReturnSeconds: realDropTime);

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");

        Assert.True(Math.Abs(drop1.TimestampInSeconds - fallbackTime) > 15,
            "Drop 1 landed on the no-signal fallback position instead of the real sub-bass return candidate.");
        Assert.InRange(drop1.TimestampInSeconds, realDropTime - 10, realDropTime + 10);
    }

    [Fact]
    public void GenerateCues_PhraseDataCoversOnlyPartOfTrack_RerouteToDsp_EvenWithTwoCleanDrops()
    {
        // Regression test for a real bug found in this library (Chase & Status - a track whose
        // RekordboxPSSI-sourced phrase data was internally clean — exactly 2 well-formed "Drop"
        // candidates — but Rekordbox's own phrase-structure analysis had silently stopped tagging
        // at beat 291 of a 243s track (~107s), leaving the back two-thirds of the track
        // completely unanalysed (confirmed directly against the raw PSSI tag: every entry's Kind
        // mapped cleanly, so nothing was lost to label filtering — Rekordbox itself never
        // analysed that portion, likely an extended outro/breakdown/VIP section its phrase model
        // didn't recognise). Two clean drops entirely within the analysed first half say nothing
        // about whether a real, later drop exists in the untouched remainder, so this must
        // reroute to DSP the same way too few drops does — regardless of how clean the phrase
        // candidates within that partial coverage look.
        var service = CreateService();
        var analysis = new AnalysisPipelineResult
        {
            Bpm = (float)Bpm,
            DurationSeconds = DurationSeconds, // 240s
            PhraseSegments = new List<PhraseSegment>
            {
                new() { Label = "Intro", Start = 0f, Duration = 15f, Confidence = 0.9f },
                new() { Label = "Build", Start = 15f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 25f, Duration = 20f, Confidence = 0.9f },
                new() { Label = "Breakdown", Start = 45f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Build", Start = 55f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 65f, Duration = 20f, Confidence = 0.9f },
                new() { Label = "Outro", Start = 85f, Duration = 10f, Confidence = 0.9f },
            },
            // Phrase coverage ends at 95s — under 0.50 * 240s (an analyzed minority of the
            // track). An independent DSP signal sits far outside that covered range, at a
            // position no phrase candidate is near (GenerateCuesDsp only considers a
            // >=midpoint candidate for Drop 2, so this lands there — the point is it lands near
            // 200 at all, which Path 1's phrase data, capped entirely under 95s, could never
            // produce).
            SubBassReturnTimestamps = new List<double> { 200.0 },
        };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop2 = cues.First(c => c.Label == "Drop 2");

        // Path 1 (phrase) would have placed Drop 2 at ~65s (the second phrase "Drop" entry) —
        // landing near the DSP-only candidate at 200s instead proves the reroute happened.
        Assert.InRange(drop2.TimestampInSeconds, 190.0, 210.0);
    }

    [Fact]
    public void GenerateCues_PhraseCoverageMostlyComplete_KeepsPhrasePath_EvenWhenSlightlyShort()
    {
        // Regression test for a real bug found rerouting this: Basstripper - Hazmat had
        // RekordboxPSSI coverage of only 53% of the track, but the covered portion still held
        // the correct drop (4.1s from the real cue) — an earlier, more aggressive coverage
        // threshold (any coverage under 75%) rerouted this to DSP anyway, landing on a worse
        // independent candidate (47.1s off). The bar for distrusting an otherwise-clean phrase
        // source must be "most of the track was never analysed" (under 50%), not merely
        // "somewhat short" — this pins that a track with the majority (55%) of its duration
        // covered keeps using its own (correct) phrase-derived drops, not a DSP signal placed
        // somewhere a phrase candidate could never land.
        var service = CreateService();
        var analysis = new AnalysisPipelineResult
        {
            Bpm = (float)Bpm,
            DurationSeconds = DurationSeconds, // 240s
            PhraseSegments = new List<PhraseSegment>
            {
                new() { Label = "Intro", Start = 0f, Duration = 15f, Confidence = 0.9f },
                new() { Label = "Build", Start = 15f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 25f, Duration = 20f, Confidence = 0.9f },
                new() { Label = "Breakdown", Start = 45f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Build", Start = 55f, Duration = 10f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 65f, Duration = 20f, Confidence = 0.9f },
                new() { Label = "Outro", Start = 85f, Duration = 47f, Confidence = 0.9f }, // ends at 132s = 0.55 * 240
            },
            // Placed nowhere near either real phrase drop — if this were used, Drop 2 would not
            // land near 65s.
            SubBassReturnTimestamps = new List<double> { 200.0 },
        };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop2 = cues.First(c => c.Label == "Drop 2");

        Assert.InRange(drop2.TimestampInSeconds, 55.0, 75.0);
    }

    [Fact]
    public void GenerateCues_UnrecognizedGenre_UsesOldSubBassWeights_NotTheMismatchedFallback()
    {
        // Pins the Unknown-genre-family DropSignalWeights fallback to the pre-genre-family
        // default (subBass=0.85, energyJump=0.45, spectralFlux=1.0 — verified against
        // git show 07834bc^). Constructed so a sub-bass-return candidate at 60s only out-scores
        // a fixed-strength novelty-signature candidate at 90s under the CORRECT weights:
        //   subBass score = 0.85*(0.7+1.0*0.3*0)   = 0.595  (old, wrong weights: 0.70*0.7 = 0.49)
        //   novelty score = 0.6875*0.8              = 0.55   (independent of DropSignalWeights)
        // So with the previously-shipped (0.70, 0.60, 0.70) fallback the novelty candidate at
        // 90s would win instead — this test would have caught that regression.
        var service = CreateService();
        var analysis = new AnalysisPipelineResult
        {
            Bpm = 100f, // outside every GenreFamilyClassifier BPM bracket (118-150, 170-180)
            DurationSeconds = DurationSeconds,
            Genre = "Pop", // doesn't match any Breakbeat/FourOnTheFloor keyword either
            SubBassReturnTimestamps = new List<double> { 60.0 },
            NoveltyDropSignatures = new List<(double, double, float)> { (90.0, 80.0, 0.6875f) },
        };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");

        Assert.InRange(drop1.TimestampInSeconds, 50.0, 70.0);
    }

    [Fact]
    public void GenerateCues_NoSignalsAtAll_FallsBackToHeuristicPath_AndStillReturnsCues()
    {
        var service = CreateService();
        var analysis = HeuristicAnalysis();

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);

        Assert.NotEmpty(cues);
        Assert.All(cues, c => Assert.InRange(c.TimestampInSeconds, 0, DurationSeconds));
    }

    [Fact]
    public void GenerateCues_DspPath_ApproachCuesAreExactBeatMathFromDrop_NotDropoutDerived()
    {
        // Breakdown-signal-derived placement (SubBassDropoutTimestamps overriding a bar-math
        // default) was removed in favor of the DJ's actual cueing convention: the two cues
        // leading into a drop are always exactly 32 and 16 beats before it, not a separately
        // (and therefore separately fallible) detected structural landmark. A planted dropout
        // signal — even one right at the old default position — must have zero effect now.
        var service = CreateService();
        double dropTime = 100.0;
        double beat = 60.0 / Bpm;

        var analysis = DspAnalysis(subBassReturnSeconds: dropTime);
        analysis.SubBassDropoutTimestamps = new List<double> { 84.0 };
        analysis.Genre = "House";

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");
        var builds = cues.Where(c => c.Type == CuePointType.Build && c.TimestampInSeconds < drop1.TimestampInSeconds)
            .OrderBy(c => c.TimestampInSeconds).ToList();

        Assert.Equal(2, builds.Count);
        Assert.InRange(builds[0].TimestampInSeconds, drop1.TimestampInSeconds - 32 * beat - 0.01, drop1.TimestampInSeconds - 32 * beat + 0.01);
        Assert.InRange(builds[1].TimestampInSeconds, drop1.TimestampInSeconds - 16 * beat - 0.01, drop1.TimestampInSeconds - 16 * beat + 0.01);
        Assert.DoesNotContain(cues, c => c.Type == CuePointType.Breakdown);
    }

    [Fact]
    public void GenerateCues_MlPath_NormalizesStructuralAnalysisEngineOrdinalLabels()
    {
        // Regression test for a real bug: StructuralAnalysisEngine ("Heuristic" source) names
        // sections "Drop 1", "Drop 5", "Build 2" etc. instead of the plain "Drop"/"Build"
        // vocabulary Is() matches against. Before SanitizeSegments normalized these, every
        // Where(Is(..., "Drop")) filter silently matched nothing, so a track with perfectly
        // correct structural data (confirmed against a real Rekordbox-cued track: this exact
        // shape, with the real drop at ~66.6s) fell through to duration*0.35 as a blind guess
        // instead of using the real, correct answer sitting right there.
        var service = CreateService();
        var analysis = new AnalysisPipelineResult
        {
            Bpm = (float)Bpm,
            DurationSeconds = DurationSeconds,
            PhraseSegments = new List<PhraseSegment>
            {
                new() { Label = "Intro", Start = 0f, Duration = 44f, Confidence = 0.97f },
                new() { Label = "Build 1", Start = 44f, Duration = 22f, Confidence = 0.99f },
                new() { Label = "Drop 1", Start = 66f, Duration = 22f, Confidence = 0.75f },
                new() { Label = "Build 2", Start = 88f, Duration = 22f, Confidence = 0.78f },
                new() { Label = "Bridge 1", Start = 111f, Duration = 22f, Confidence = 0.62f },
                new() { Label = "Break", Start = 133f, Duration = 22f, Confidence = 0.97f },
                new() { Label = "Build 2", Start = 155f, Duration = 22f, Confidence = 0.60f },
                new() { Label = "Drop 5", Start = 177f, Duration = 66f, Confidence = 0.89f },
                new() { Label = "Outro", Start = 244f, Duration = 44f, Confidence = 0.72f },
            },
        };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");
        var drop2 = cues.First(c => c.Label == "Drop 2");

        Assert.InRange(drop1.TimestampInSeconds, 60, 72);
        Assert.InRange(drop2.TimestampInSeconds, 172, 182);
    }

    [Fact]
    public void GenerateCues_MlPath_ContiguousDropFragmentsOfSameSection_DontOutrankTheRealSecondDrop()
    {
        // Regression test for a real bug found in this library's Rekordbox PSSI data (Brk -
        // Obsession): the real first drop section (44.49s-132.90s) survived SanitizeSegments'
        // merge step as TWO back-to-back "Drop" entries instead of one, because fully merging
        // them would exceed the cluster-duration cap (a different guard, added to stop an
        // unrelated fragmented/duplicated-timeline track from collapsing into one absurd
        // multi-hundred-second blob). Picking the top 2 "Drop" candidates by raw individual
        // duration then picked BOTH fragments of drop 1 (each ~44s, nearly tied) instead of drop
        // 1 + the real, later second drop at 199.67s — placing "Drop 2" at 88.63s, still inside
        // drop 1's own section. Contiguous fragments must be grouped and ranked by total span so
        // the genuinely separate later section wins the second slot.
        var service = CreateService();
        var analysis = new AnalysisPipelineResult
        {
            Bpm = 178f,
            DurationSeconds = 270.34,
            PhraseSegments = new List<PhraseSegment>
            {
                new() { Label = "Intro", Start = 0.68f, Duration = 27.13f, Confidence = 0.9f },
                new() { Label = "Build", Start = 27.82f, Duration = 16.67f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 44.49f, Duration = 44.14f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 88.63f, Duration = 44.27f, Confidence = 0.9f },
                new() { Label = "Breakdown", Start = 132.90f, Duration = 22.29f, Confidence = 0.9f },
                new() { Label = "Build", Start = 155.19f, Duration = 44.48f, Confidence = 0.9f },
                new() { Label = "Drop", Start = 199.67f, Duration = 44.13f, Confidence = 0.9f },
                new() { Label = "Outro", Start = 243.80f, Duration = 10.79f, Confidence = 0.9f },
            },
        };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var drop1 = cues.First(c => c.Label == "Drop 1");
        var drop2 = cues.First(c => c.Label == "Drop 2");

        Assert.InRange(drop1.TimestampInSeconds, 40, 49);
        // Must land on the real, separate third candidate (~199.67s), not the second fragment of
        // drop 1 (~88.63s) — the specific value that regresses if the grouping is removed.
        Assert.InRange(drop2.TimestampInSeconds, 195, 204);
        Assert.True(drop2.TimestampInSeconds - drop1.TimestampInSeconds > 100,
            "Drop 2 landed inside drop 1's own contiguous section instead of on the real, separate later drop.");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static AnalysisPipelineResult MlAnalysis(double dropStartSeconds = 150.0) => new()
    {
        Bpm = (float)Bpm,
        DurationSeconds = DurationSeconds,
        PhraseSegments = new List<PhraseSegment>
        {
            new() { Label = "Intro", Start = 0f, Duration = 20f },
            new() { Label = "Build", Start = 20f, Duration = 15f },
            new() { Label = "Breakdown", Start = 35f, Duration = 10f },
            new() { Label = "Drop", Start = (float)dropStartSeconds, Duration = 30f },
            new() { Label = "Outro", Start = 220f, Duration = 20f },
        },
    };

    private static AnalysisPipelineResult DspAnalysis(double subBassReturnSeconds = 100.0) => new()
    {
        Bpm = (float)Bpm,
        DurationSeconds = DurationSeconds,
        SubBassReturnTimestamps = new List<double> { subBassReturnSeconds },
        SpectralFluxNovelty = Array.Empty<float>(),
        EnergyCurve = Enumerable.Range(0, (int)DurationSeconds)
            .Select(i => (float)Math.Clamp(Math.Sin(i / 20.0) * 0.5 + 0.5, 0, 1))
            .ToArray(),
    };

    private static AnalysisPipelineResult HeuristicAnalysis() => new()
    {
        Bpm = (float)Bpm,
        DurationSeconds = DurationSeconds,
        Transients = new List<TransientDataPoint>
        {
            new() { Timestamp = 30, ClusterClass = "Kick" },
            new() { Timestamp = 90, ClusterClass = "Kick" },
            new() { Timestamp = 150, ClusterClass = "Snare" },
        },
    };

    // ── FindEnergyJumpCandidates: track-wide baseline gating ────────────────

    [Fact]
    public void FindEnergyJumpCandidates_RejectsLocalSpike_BelowTrackMeanEnergy()
    {
        // A loud section establishes a high track mean; a later quiet valley briefly rises
        // 2x locally (0.05 -> 0.1) then settles back to quiet — a real local jump, but nowhere
        // near the track's actual mean energy, so it should NOT be treated as a drop candidate.
        var energyCurve = new float[40];
        for (int i = 0; i < 10; i++) energyCurve[i] = 0.9f;  // establishes a high track mean
        for (int i = 10; i < 20; i++) energyCurve[i] = 0.05f; // quiet valley
        for (int i = 20; i < 26; i++) energyCurve[i] = 0.1f;  // local-only spike within the valley
        for (int i = 26; i < 40; i++) energyCurve[i] = 0.05f; // back to quiet

        var candidates = CueGenerationService.FindEnergyJumpCandidates(energyCurve, duration: 40.0);

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindEnergyJumpCandidates_AcceptsLocalSpike_NearTrackMeanEnergy()
    {
        // A quiet section jumping to a sustained level close to the track's own mean — a
        // genuine candidate, should still be found (this fix adds a gate, not a blanket
        // suppression of every jump).
        var energyCurve = new float[40];
        for (int i = 0; i < 10; i++) energyCurve[i] = 0.1f;
        for (int i = 10; i < 40; i++) energyCurve[i] = 0.85f;

        var candidates = CueGenerationService.FindEnergyJumpCandidates(energyCurve, duration: 40.0);

        Assert.Contains(candidates, t => t is >= 8.0 and <= 12.0);
    }
}
