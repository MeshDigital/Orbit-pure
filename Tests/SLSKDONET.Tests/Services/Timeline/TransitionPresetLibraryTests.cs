using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Timeline;
using Xunit;

namespace SLSKDONET.Tests.Services.Timeline;

public class TransitionPresetLibraryTests
{
    [Theory]
    [InlineData("Fade", TransitionType.Crossfade)]
    [InlineData("Rise", TransitionType.FilterSweep)]
    [InlineData("Blend", TransitionType.EqSwap)]
    [InlineData("Wave", TransitionType.WaveDuck)]
    [InlineData("Melt", TransitionType.EchoOut)]
    public void Build_MapsPresetNameToExpectedTransitionType(string preset, TransitionType expected)
    {
        var model = TransitionPresetLibrary.Build(preset);
        Assert.Equal(expected, model.Type);
    }

    [Fact]
    public void Build_Rise_SweepsHighPassRising()
    {
        var model = TransitionPresetLibrary.Build("Rise");
        Assert.True(model.FilterSweepRising);
    }

    [Fact]
    public void Build_DurationBarsOverride_TakesPrecedenceOverPresetDefault()
    {
        var model = TransitionPresetLibrary.Build("Fade", durationBarsOverride: 4);
        Assert.Equal(16.0, model.DurationBeats); // 4 bars * 4 beats/bar
    }

    [Fact]
    public void Build_Auto_NoScore_FallsBackToGenericCrossfade()
    {
        var model = TransitionPresetLibrary.Build("Auto");
        Assert.Equal(TransitionType.Crossfade, model.Type);
    }

    [Fact]
    public void Build_Auto_HighCompatibility_PicksLongCrossfade()
    {
        var score = new TrackPairCompatibilityScorer.PairScore(90, "lock", 90, "smooth", 90);
        var model = TransitionPresetLibrary.Build("Auto", score);
        Assert.Equal(TransitionType.Crossfade, model.Type);
        Assert.Equal(64.0, model.DurationBeats); // 16 bars
    }

    [Fact]
    public void Build_Auto_LowCompatibility_PicksShortCrossfade()
    {
        var score = new TrackPairCompatibilityScorer.PairScore(10, "risky", 10, "mismatch", 10);
        var model = TransitionPresetLibrary.Build("Auto", score);
        Assert.Equal(TransitionType.Crossfade, model.Type);
        Assert.Equal(16.0, model.DurationBeats); // 4 bars
    }

    [Fact]
    public void ApplyCustomOverrides_OnlyOverridesProvidedFields()
    {
        var model = TransitionPresetLibrary.Build("Fade");
        var originalFilterEnd = model.FilterEndFrequency;

        TransitionPresetLibrary.ApplyCustomOverrides(model, echoDecayFactor: 0.9f, filterStartFrequency: null, filterEndFrequency: null);

        Assert.Equal(0.9f, model.EchoDecayFactor);
        Assert.Equal(originalFilterEnd, model.FilterEndFrequency);
    }

    [Fact]
    public void ApplyCustomOverrides_WaveDuckDepthAndFilterSweepRising_OnlyOverrideWhenProvided()
    {
        var model = TransitionPresetLibrary.Build("Wave");
        var originalDepth = model.WaveDuckDepth;

        TransitionPresetLibrary.ApplyCustomOverrides(model, null, null, null);
        Assert.Equal(originalDepth, model.WaveDuckDepth); // untouched when not supplied
        Assert.False(model.FilterSweepRising);

        TransitionPresetLibrary.ApplyCustomOverrides(model, null, null, null, waveDuckDepth: 0.8f, filterSweepRising: true);
        Assert.Equal(0.8f, model.WaveDuckDepth);
        Assert.True(model.FilterSweepRising);
    }

    [Fact]
    public void ApplyCustomOverrides_EqSwapBands_OnlyOverrideWhenProvided()
    {
        var model = TransitionPresetLibrary.Build("Blend");
        Assert.True(model.EqSwapLow); // preset default
        Assert.False(model.EqSwapMid);
        Assert.False(model.EqSwapHigh);

        TransitionPresetLibrary.ApplyCustomOverrides(model, null, null, null);
        Assert.True(model.EqSwapLow); // untouched when not supplied
        Assert.Equal(250f, model.EqLowCrossoverHz);
        Assert.Equal(4000f, model.EqHighCrossoverHz);

        TransitionPresetLibrary.ApplyCustomOverrides(
            model, null, null, null,
            eqSwapLow: false, eqSwapMid: true, eqSwapHigh: true,
            eqLowCrossoverHz: 300f, eqHighCrossoverHz: 5000f);

        Assert.False(model.EqSwapLow);
        Assert.True(model.EqSwapMid);
        Assert.True(model.EqSwapHigh);
        Assert.Equal(300f, model.EqLowCrossoverHz);
        Assert.Equal(5000f, model.EqHighCrossoverHz);
    }

    [Theory]
    [InlineData(TransitionType.Cut, SLSKDONET.Services.Audio.TransitionType.Cut)]
    [InlineData(TransitionType.Crossfade, SLSKDONET.Services.Audio.TransitionType.Crossfade)]
    [InlineData(TransitionType.FilterSweep, SLSKDONET.Services.Audio.TransitionType.FilterSweep)]
    [InlineData(TransitionType.EqSwap, SLSKDONET.Services.Audio.TransitionType.EqSwap)]
    [InlineData(TransitionType.EchoOut, SLSKDONET.Services.Audio.TransitionType.EchoOut)]
    [InlineData(TransitionType.WaveDuck, SLSKDONET.Services.Audio.TransitionType.WaveDuck)]
    public void ToAutomationType_MapsAllDspTypes(TransitionType input, SLSKDONET.Services.Audio.TransitionType expected)
    {
        Assert.Equal(expected, input.ToAutomationType());
    }
}
