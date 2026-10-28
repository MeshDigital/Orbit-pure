using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Services.Platform;
using SLSKDONET.Services.Setup;
using SLSKDONET.Tests.Services.Platform;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

/// <summary>
/// The wizard's audio step tells the user which heavy features their PC can't run, and installs
/// FFmpeg without being asked when it's missing.
/// </summary>
public class AudioStepHardwareTests
{
    private static AudioStepViewModel Create(FakeAudio audio, bool analyzeNewDownloads = true)
    {
        var settings = new Mock<ISetupSettingsGateway>();
        settings.Setup(s => s.ReadAudio()).Returns(new SetupAudioSettings("WasapiShared", null, analyzeNewDownloads));
        return new AudioStepViewModel(audio, settings.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task WeakLaptop_ListsStemSeparationAsSwitchedOff()
    {
        var step = Create(new FakeAudio { Hardware = HardwareCapabilitiesTests.Machine(ramGb: 8, cores: 4) });
        await step.InitializeAsync();

        Assert.True(step.HasBlockedFeatures);
        var blocked = Assert.Single(step.BlockedFeatures);
        Assert.Equal(HardwareFeature.StemSeparation, blocked.Feature);
        Assert.False(string.IsNullOrEmpty(blocked.Reason));
        Assert.True(step.AnalyzeNewDownloads);
    }

    [Fact]
    public async Task MachineBelowAnalysisMinimum_TurnsAutoAnalysisOff()
    {
        var step = Create(new FakeAudio { Hardware = HardwareCapabilitiesTests.Machine(ramGb: 2, cores: 2) });
        await step.InitializeAsync();

        Assert.False(step.IsAnalysisAllowed);
        Assert.False(step.AnalyzeNewDownloads);
        Assert.Contains(step.BlockedFeatures, f => f.Feature == HardwareFeature.TrackAnalysis);
    }

    [Fact]
    public async Task StrongPc_RunsEverything()
    {
        var step = Create(new FakeAudio());
        await step.InitializeAsync();

        Assert.True(step.RunsEverything);
        Assert.Empty(step.BlockedFeatures);
    }

    [Fact]
    public async Task MissingFfmpeg_IsInstalledAutomatically()
    {
        var audio = new FakeAudio { FfmpegAvailable = false, CanInstallFfmpeg = true };
        var step = Create(audio);

        await step.InitializeAsync();
        await step.InstallFfmpegAsync(); // no-op if the automatic install is already done

        Assert.Equal(1, audio.FfmpegInstalls);
        Assert.True(step.FfmpegIsOk);
        Assert.False(step.ShowInstallFfmpegButton);
    }

    [Fact]
    public async Task MissingFfmpegWhereItCantBeInstalled_ShowsTheManualHint()
    {
        var audio = new FakeAudio { FfmpegAvailable = false, CanInstallFfmpeg = false };
        var step = Create(audio);

        await step.InitializeAsync();

        Assert.Equal(0, audio.FfmpegInstalls);
        Assert.True(step.ShowManualFfmpegHelp);
        Assert.Contains("package manager", step.ManualFfmpegHint);
    }
}
