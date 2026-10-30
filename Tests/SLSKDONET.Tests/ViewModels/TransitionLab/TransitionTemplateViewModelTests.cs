using SLSKDONET.Models.Timeline;
using SLSKDONET.ViewModels.TransitionLab;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.TransitionLab;

public class TransitionTemplateViewModelTests
{
    [Fact]
    public void ToTransitionModel_MapsBarsToBeats()
    {
        var vm = new TransitionTemplateViewModel { BarsBeforeMix = 8, Type = TransitionType.EqSwap };

        var model = vm.ToTransitionModel();

        Assert.Equal(32.0, model.DurationBeats, 6); // 8 bars * 4 beats
        Assert.Equal(TransitionType.EqSwap, model.Type);
    }

    [Fact]
    public void ToTransitionModel_CarriesDspParameters()
    {
        var vm = new TransitionTemplateViewModel
        {
            EchoDecayFactor = 0.7f,
            EqSwapLow = true,
            EqSwapMid = true,
            LoopBars = 16,
            LoopRepeats = 2,
        };

        var model = vm.ToTransitionModel();

        Assert.Equal(0.7f, model.EchoDecayFactor);
        Assert.True(model.EqSwapLow);
        Assert.True(model.EqSwapMid);
        Assert.Equal(16, model.LoopBars);
        Assert.Equal(2, model.LoopRepeats);
    }

    [Fact]
    public void ToEntity_SerializesEffectsAndLoop()
    {
        var vm = new TransitionTemplateViewModel { BarsBeforeMix = 4, SourceCueRole = 6, TargetCueRole = 0 };

        var entity = vm.ToEntity("Peak Swap");

        Assert.Equal("Peak Swap", entity.Name);
        Assert.Equal(4, entity.BarsBeforeMix);
        Assert.Equal(6, entity.SourceCueRole);
        Assert.Equal(0, entity.TargetCueRole);
        Assert.Contains("Bars", entity.LoopSettingsJson);
        Assert.Contains("EqSwapLow", entity.EffectsJson);
    }
}
