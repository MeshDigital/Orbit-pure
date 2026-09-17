using SLSKDONET.Models.Timeline;
using Xunit;

namespace SLSKDONET.Tests.Models.Timeline;

/// <summary>
/// Regression coverage: ToTransitionModel() previously dropped WaveDuckDepth/FilterSweepRising
/// entirely (the fields didn't exist on the persisted entity yet), so a saved Wave/Melt transition
/// with a custom duck depth silently fell back to TransitionModel's hardcoded default instead of
/// the value the user actually saved.
/// </summary>
public class PlaylistTrackTransitionTests
{
    [Fact]
    public void ToTransitionModel_AppliesWaveDuckDepth_WhenSet()
    {
        var transition = new PlaylistTrackTransition { Type = TransitionType.WaveDuck, WaveDuckDepth = 0.85f };

        var model = transition.ToTransitionModel();

        Assert.Equal(0.85f, model.WaveDuckDepth);
    }

    [Fact]
    public void ToTransitionModel_AppliesFilterSweepRising_WhenSet()
    {
        var transition = new PlaylistTrackTransition { Type = TransitionType.FilterSweep, FilterSweepRising = true };

        var model = transition.ToTransitionModel();

        Assert.True(model.FilterSweepRising);
    }

    [Fact]
    public void ToTransitionModel_LeavesDefaults_WhenNotSet()
    {
        var transition = new PlaylistTrackTransition { Type = TransitionType.WaveDuck };
        var defaultModel = new TransitionModel();

        var model = transition.ToTransitionModel();

        Assert.Equal(defaultModel.WaveDuckDepth, model.WaveDuckDepth);
        Assert.Equal(defaultModel.FilterSweepRising, model.FilterSweepRising);
    }

    [Fact]
    public void ToTransitionModel_AppliesEqSwapBands_WhenSet()
    {
        var transition = new PlaylistTrackTransition
        {
            Type = TransitionType.EqSwap,
            EqSwapLow = false,
            EqSwapMid = true,
            EqSwapHigh = true,
            EqLowCrossoverHz = 300f,
            EqHighCrossoverHz = 5000f,
        };

        var model = transition.ToTransitionModel();

        Assert.False(model.EqSwapLow);
        Assert.True(model.EqSwapMid);
        Assert.True(model.EqSwapHigh);
        Assert.Equal(300f, model.EqLowCrossoverHz);
        Assert.Equal(5000f, model.EqHighCrossoverHz);
    }

    [Fact]
    public void ToTransitionModel_LeavesEqSwapDefaults_WhenNotSet()
    {
        var transition = new PlaylistTrackTransition { Type = TransitionType.EqSwap };
        var defaultModel = new TransitionModel();

        var model = transition.ToTransitionModel();

        Assert.Equal(defaultModel.EqSwapLow, model.EqSwapLow);
        Assert.Equal(defaultModel.EqSwapMid, model.EqSwapMid);
        Assert.Equal(defaultModel.EqSwapHigh, model.EqSwapHigh);
        Assert.Equal(defaultModel.EqLowCrossoverHz, model.EqLowCrossoverHz);
        Assert.Equal(defaultModel.EqHighCrossoverHz, model.EqHighCrossoverHz);
    }
}
