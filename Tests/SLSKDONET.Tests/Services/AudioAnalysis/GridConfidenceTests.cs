using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// GridConfidence = 0.5·(TicksUsed/totalTicks) + 0.5·(1 − ResidualRmsBeats/0.12), clamped to [0,1].
/// A clean steady fit scores high; a jittered or half-agreeing fit scores low.
/// </summary>
public class GridConfidenceTests
{
    [Fact]
    public void CleanFit_ScoresHigh()
    {
        var fit = new BeatGridFitter.Fit(PeriodSeconds: 0.5, PhaseSeconds: 0.1, Bpm: 120, TicksUsed: 100, ResidualRmsBeats: 0.01);
        float confidence = GridWriteRules.ComputeConfidence(fit, totalTicks: 100);
        Assert.InRange(confidence, 0.9f, 1.0f);
    }

    [Fact]
    public void JitteredFit_ScoresLow()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.1, 120, TicksUsed: 50, ResidualRmsBeats: 0.10);
        float confidence = GridWriteRules.ComputeConfidence(fit, totalTicks: 100);
        Assert.True(confidence < 0.5f, $"expected low confidence, got {confidence}");
    }

    [Fact]
    public void HalfAgreeingFit_ScoresMidToLow()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.1, 120, TicksUsed: 60, ResidualRmsBeats: 0.05);
        float confidence = GridWriteRules.ComputeConfidence(fit, totalTicks: 100);
        Assert.InRange(confidence, 0.3f, 0.8f);
    }

    [Fact]
    public void ZeroTicks_ScoresZero()
    {
        var fit = new BeatGridFitter.Fit(0.5, 0.1, 120, TicksUsed: 0, ResidualRmsBeats: 0.12);
        Assert.Equal(0f, GridWriteRules.ComputeConfidence(fit, totalTicks: 0));
    }
}
