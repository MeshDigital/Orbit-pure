using System;
using System.Reactive.Linq;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class CueForgeWaveformPreferenceTests
{
    private static CueForgeViewModel Create(AppConfig config)
    {
        var transport = new Mock<ICueForgeTransport>();
        transport.SetupGet(x => x.CurrentTrackChanged).Returns(Observable.Never<CueForgeTrackRef?>());
        transport.SetupGet(x => x.IsPlayingChanged).Returns(Observable.Never<bool>());
        transport.SetupGet(x => x.PositionChanged).Returns(Observable.Never<(double Fraction, double LengthSeconds)>());
        return new CueForgeViewModel(Mock.Of<ICuePointService>(), transport.Object, config);
    }

    [Fact]
    public void CleanDefaults_ShowOnlyBeatGrid()
    {
        var config = new AppConfig();
        Assert.False(config.WaveformShowEnergyCurve);
        Assert.False(config.WaveformShowOnsetDensity);
        Assert.False(config.WaveformShowVocalGhost);
        Assert.False(config.WaveformShowPhraseSections);
        Assert.True(config.WaveformShowBeatGrid);
    }

    [Fact]
    public void ExistingLayerChoices_AreLoadedAndChangesUpdateConfig()
    {
        var config = new AppConfig
        {
            WaveformShowEnergyCurve = true,
            WaveformShowOnsetDensity = true,
            WaveformShowVocalGhost = true,
            WaveformShowPhraseSections = true,
            WaveformGain = 1.5f,
        };
        using var vm = Create(config);

        Assert.True(vm.ShowEnergyCurve);
        Assert.True(vm.ShowOnsetDensity);
        Assert.True(vm.ShowVocalGhost);
        Assert.True(vm.ShowPhraseSections);
        Assert.Equal(1.5, vm.WaveformGain);

        vm.ShowEnergyCurve = false;
        vm.ShowOnsetDensity = false;
        Assert.False(config.WaveformShowEnergyCurve);
        Assert.False(config.WaveformShowOnsetDensity);
        Assert.True(config.WaveformShowVocalGhost);
    }

    [Fact]
    public async Task GainCommand_ClampsToNonDestructiveDisplayRange()
    {
        var config = new AppConfig();
        using var vm = Create(config);

        await vm.SetWaveformGainCommand.Execute(3.0);
        Assert.Equal(2.0, vm.WaveformGain);
        Assert.Equal(2.0f, config.WaveformGain);

        await vm.SetWaveformGainCommand.Execute(0.2);
        Assert.Equal(1.0, vm.WaveformGain);
        Assert.Equal(1.0f, config.WaveformGain);
    }
}
