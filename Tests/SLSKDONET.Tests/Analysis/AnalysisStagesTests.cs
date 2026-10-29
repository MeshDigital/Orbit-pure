using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class AnalysisStagesTests
{
    [Theory]
    [InlineData("Checking file integrity…", 1)]
    [InlineData("Decoding audio...", 2)]
    [InlineData("Extracting waveform...", 3)]
    [InlineData("Measuring loudness & energy...", 4)]
    [InlineData("Running AI genre/mood...", 5)]
    [InlineData("Running BPM/key analysis...", 6)]
    [InlineData("Essentia not installed — skipping BPM/key", 6)]
    [InlineData("Computing structure", 7)]
    [InlineData("Generating cue points", 7)]
    [InlineData("Saving to database...", 8)]
    [InlineData("Done", 8)]
    public void Describe_MapsKnownStepToItsStage(string step, int expectedIndex)
    {
        var stage = AnalysisStages.Describe(step);
        Assert.Equal(expectedIndex, stage.Index);
        Assert.Equal(AnalysisStages.Count, stage.Count);
        Assert.False(string.IsNullOrWhiteSpace(stage.Label));
    }

    [Fact]
    public void Describe_UnknownStepFallsBackToFirstStage()
    {
        var stage = AnalysisStages.Describe("something unexpected");
        Assert.Equal(1, stage.Index);
    }

    [Fact]
    public void Count_IsEightStages()
    {
        Assert.Equal(8, AnalysisStages.Count);
    }
}
