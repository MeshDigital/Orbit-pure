using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Moq;
using SLSKDONET.Data;
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
        return new CueGenerationService(factoryMock.Object);
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
    public void GenerateCues_NoSignalsAtAll_FallsBackToHeuristicPath_AndStillReturnsCues()
    {
        var service = CreateService();
        var analysis = HeuristicAnalysis();

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);

        Assert.NotEmpty(cues);
        Assert.All(cues, c => Assert.InRange(c.TimestampInSeconds, 0, DurationSeconds));
    }

    [Fact]
    public void GenerateCues_DspPath_BreakdownDerivedFromSubBassDropout()
    {
        var service = CreateService();
        double dropTime = 100.0;
        double dropoutTime = 60.0; // a real dropout candidate well before the drop

        var analysis = DspAnalysis(subBassReturnSeconds: dropTime);
        analysis.SubBassDropoutTimestamps = new List<double> { dropoutTime };

        var cues = service.GenerateCues("hash", analysis, DownbeatAnchor);
        var breakdown = cues.First(c => c.Label == "Breakdown");

        Assert.InRange(breakdown.TimestampInSeconds, dropoutTime - 10, dropoutTime + 10);
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
