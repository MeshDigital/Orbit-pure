using System.Linq;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// GridConfidence (see <see cref="GridWriteRules.ComputeConfidence"/>):
/// tempo = 0.5·(TicksUsed/totalTicks) + 0.5·(1 − ResidualRmsBeats/0.12);
/// confidence = 0.4·tempo + 0.6·barPhaseAgreement, or min(tempo, 0.75) without bar evidence.
/// </summary>
public class GridConfidenceTests
{
    private static readonly BeatGridFitter.Fit CleanFit = new(PeriodSeconds: 0.5, PhaseSeconds: 0.1, Bpm: 120, TicksUsed: 100, ResidualRmsBeats: 0.01);

    [Fact]
    public void CleanFit_WithoutBarEvidence_IsCappedButNotFlagged()
    {
        float confidence = GridWriteRules.ComputeConfidence(CleanFit, totalTicks: 100);
        Assert.Equal((float)GridWriteRules.NoBarEvidenceCap, confidence, 3);
        Assert.True(confidence >= GridWriteRules.ReviewThreshold);
    }

    [Fact]
    public void CleanFit_BarLinesConfirmedByTheMusic_ScoresHigh()
    {
        float confidence = GridWriteRules.ComputeConfidence(CleanFit, totalTicks: 100, barAgreement: 1.0);
        Assert.InRange(confidence, 0.95f, 1.0f);
    }

    [Fact]
    public void CleanTempo_ButBarOneOnTheWrongBeat_NeedsReview()
    {
        // The error DJs actually hit: perfect tempo, Bar 1 on beat 3 — arrangement changes miss the bar lines.
        float confidence = GridWriteRules.ComputeConfidence(CleanFit, totalTicks: 100, barAgreement: 0.1);
        Assert.True(confidence < GridWriteRules.ReviewThreshold, $"expected needs-review, got {confidence}");
    }

    [Fact]
    public void JitteredFit_ScoresLow()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.1, 120, TicksUsed: 50, ResidualRmsBeats: 0.10);
        float confidence = GridWriteRules.ComputeConfidence(fit, totalTicks: 100);
        Assert.True(confidence < 0.5f, $"expected low confidence, got {confidence}");
    }

    [Fact]
    public void ZeroTicks_ScoresZero()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.1, 120, TicksUsed: 0, ResidualRmsBeats: 0.12);
        Assert.Equal(0f, GridWriteRules.ComputeConfidence(fit, totalTicks: 0));
    }

    [Fact]
    public void BarPhaseAgreement_EventsOnBarLines_IsHigh_AndOnBeatThree_IsLow()
    {
        const double period = 0.5, downbeat = 1.0; // 120 BPM, bars every 2 s from 1.0 s
        var onBars = Enumerable.Range(1, 8).Select(k => downbeat + k * 4 * period).ToList();
        var events = new BeatGridFitter.StructuralEvents(onBars, System.Array.Empty<double>());

        Assert.Equal(1.0, BeatGridFitter.BarPhaseAgreement(period, downbeat, events)!.Value, 3);
        // The same music with Bar 1 placed two beats late: none of the changes land on a bar line.
        Assert.Equal(0.0, BeatGridFitter.BarPhaseAgreement(period, downbeat + 2 * period, events)!.Value, 3);
    }

    [Fact]
    public void BarPhaseAgreement_TooFewEvents_IsNull()
    {
        var events = new BeatGridFitter.StructuralEvents(new[] { 3.0, 5.0 }, System.Array.Empty<double>());
        Assert.Null(BeatGridFitter.BarPhaseAgreement(0.5, 1.0, events));
    }

    [Fact]
    public void RegularGridDetection_SeparatesFittedGridsFromRawTicks()
    {
        var fitted = Enumerable.Range(0, 64).Select(k => System.Math.Round(0.3 + k * 60.0 / 174, 4)).ToArray();
        const double frame = 512.0 / 44100.0;
        var raw = fitted.Select(t => System.Math.Round(t / frame) * frame).ToArray();

        Assert.True(GridConfidenceBackfillService.IsRegularGrid(fitted));
        Assert.False(GridConfidenceBackfillService.IsRegularGrid(raw));
    }
}
