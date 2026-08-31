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
    public void GenerateCues_DspPath_BreakdownDerivedFromSubBassDropout_WhenNearExpectedBarOffset()
    {
        var service = CreateService();
        double dropTime = 100.0;
        // Bar-math default breakdown is 8 bars before the (bar-snapped) drop, ~88.5s here.
        // A dropout landing within 4 bars of that default should override it.
        double dropoutTime = 84.0;

        var analysis = DspAnalysis(subBassReturnSeconds: dropTime);
        analysis.SubBassDropoutTimestamps = new List<double> { dropoutTime };
        // Explicit non-Breakbeat genre so this test exercises SubBassDropoutTimestamps placement
        // in isolation — this fixture's 174 BPM alone would otherwise classify as Breakbeat and
        // pull in the resurrected DnB pre-drop-valley detector as an extra breakdown-candidate
        // source, which (run against this fixture's synthetic sine-wave energy curve, not real
        // audio) can coincidentally land closer to the bar-math default than the deliberately
        // planted dropout signal this test means to isolate. FourOnTheFloor's own extra source
        // (StructuralStrippingStartTimestamps) is unset/empty here, so it contributes nothing.
        analysis.Genre = "House";

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var breakdown = cues.Where(c => c.Type == CuePointType.Breakdown).OrderBy(c => c.TimestampInSeconds).First();

        Assert.InRange(breakdown.TimestampInSeconds, dropoutTime - 2, dropoutTime + 2);
    }

    [Fact]
    public void GenerateCues_DspPath_BreakdownIgnoresDistantDropout_UsesBarMathDefault()
    {
        var service = CreateService();
        double dropTime = 100.0;
        double distantDropoutTime = 60.0; // ~28 bars before the drop — not the same structural moment

        var analysis = DspAnalysis(subBassReturnSeconds: dropTime);
        analysis.SubBassDropoutTimestamps = new List<double> { distantDropoutTime };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var breakdown = cues.Where(c => c.Type == CuePointType.Breakdown).OrderBy(c => c.TimestampInSeconds).First();

        // Should fall back to the drop-anchored bar-math default (~8 bars before the drop),
        // not snap to an unrelated dropout elsewhere in the track.
        Assert.True(Math.Abs(breakdown.TimestampInSeconds - distantDropoutTime) > 20,
            "Breakdown snapped to a distant, unrelated dropout instead of the bar-math default.");
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
}
