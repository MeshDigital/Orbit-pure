using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

public class FirstRunDetectorTests
{
    private static readonly string Fallback = AppConfig.FallbackDownloadDirectory;

    private static FirstRunSignals Fresh() => new(false, 0, null, false, Fallback, null, 0, 0);

    [Fact]
    public void FreshInstall_ShowsWizard()
    {
        var result = FirstRunDetector.Evaluate(Fresh(), Fallback);
        Assert.Equal(FirstRunDecision.ShowWizard, result.Decision);
        Assert.True(result.ShouldShowWizard);
        Assert.False(result.IsUpgrade);
    }

    [Fact]
    public void EmptyDownloadDirectory_IsStillFresh()
    {
        var result = FirstRunDetector.Evaluate(Fresh() with { DownloadDirectory = "" }, Fallback);
        Assert.True(result.ShouldShowWizard);
    }

    [Fact]
    public void CompletedAtCurrentVersion_NeverShows()
    {
        var result = FirstRunDetector.Evaluate(
            Fresh() with { SetupCompleted = true, SetupVersion = FirstRunDetector.CurrentSetupVersion }, Fallback);
        Assert.Equal(FirstRunDecision.AlreadyCompleted, result.Decision);
        Assert.False(result.ShouldShowWizard);
        Assert.False(result.IsUpgrade);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CompletedAtOlderVersion_ShowsUpgrade(int setupVersion)
    {
        var result = FirstRunDetector.Evaluate(Fresh() with { SetupCompleted = true, SetupVersion = setupVersion }, Fallback);
        Assert.Equal(FirstRunDecision.UpgradeWizard, result.Decision);
        Assert.True(result.ShouldShowWizard);
        Assert.True(result.IsUpgrade);
    }

    public static TheoryData<FirstRunSignals> ExistingInstalls() => new()
    {
        Fresh() with { Username = "djquint" },
        Fresh() with { HasStoredCredentials = true },
        Fresh() with { DownloadDirectory = @"D:\Music\Soulseek" },
        Fresh() with { SharedFolderPath = @"D:\Share" },
        Fresh() with { TrackCount = 1 },
        Fresh() with { LibraryFolderCount = 2 },
    };

    [Theory]
    [MemberData(nameof(ExistingInstalls))]
    public void PreWizardExistingInstall_IsOfferedWhatIsNew(FirstRunSignals signals)
    {
        var result = FirstRunDetector.Evaluate(signals, Fallback);
        Assert.Equal(FirstRunDecision.UpgradeWizard, result.Decision);
        Assert.True(result.ShouldShowWizard);
        Assert.True(result.IsUpgrade);
        Assert.NotEmpty(result.Evidence);
    }

    [Fact]
    public void FallbackDownloadFolder_WithTrailingSeparatorOrCase_IsNotEvidence()
    {
        var variant = Fallback.ToUpperInvariant() + Path.DirectorySeparatorChar;
        Assert.True(FirstRunDetector.Evaluate(Fresh() with { DownloadDirectory = variant }, Fallback).ShouldShowWizard);
    }

    [Fact]
    public async Task Coordinator_PreWizardUser_ReportsUpgrade_AndDoesNotWriteConfig()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Config.Username = "existing-user";
        var coordinator = new FirstRunCoordinator(sandbox.Config, new NoCredentials(), NullLogger<FirstRunCoordinator>.Instance);

        var result = await coordinator.EvaluateAsync();

        Assert.Equal(FirstRunDecision.UpgradeWizard, result.Decision);
        // No silent marking anymore: the wizard writes [Setup] Completed when it closes.
        Assert.False(File.Exists(sandbox.ConfigPath));
    }

    [Fact]
    public async Task Coordinator_FreshInstall_DoesNotWriteConfig()
    {
        using var sandbox = new SetupSandbox();
        var result = await new FirstRunCoordinator(sandbox.Config, new NoCredentials(), NullLogger<FirstRunCoordinator>.Instance).EvaluateAsync();

        Assert.True(result.ShouldShowWizard);
        Assert.False(File.Exists(sandbox.ConfigPath));
    }

    [Fact]
    public async Task Coordinator_CompletedAtCurrentVersion_DoesNotShow()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Config.SetupCompleted = true;
        sandbox.Config.SetupVersion = FirstRunDetector.CurrentSetupVersion;
        var coordinator = new FirstRunCoordinator(sandbox.Config, new NoCredentials(), NullLogger<FirstRunCoordinator>.Instance);

        var result = await coordinator.EvaluateAsync();

        Assert.Equal(FirstRunDecision.AlreadyCompleted, result.Decision);
        Assert.False(result.ShouldShowWizard);
    }

    [Fact]
    public void SetupKeys_RoundTripThroughConfigIni()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Config.SetupCompleted = true;
        sandbox.Config.SetupVersion = 2;
        sandbox.Config.TourCompleted = true;
        sandbox.Config.AnalyzeNewDownloads = false;
        sandbox.Config.EditLabOutputDirectory = sandbox.Dir("edits");
        sandbox.ConfigManager.Save(sandbox.Config);

        var loaded = new ConfigManager(sandbox.ConfigPath).Load();
        Assert.True(loaded.SetupCompleted);
        Assert.Equal(2, loaded.SetupVersion);
        Assert.True(loaded.TourCompleted);
        Assert.False(loaded.AnalyzeNewDownloads);
        Assert.Equal(sandbox.Dir("edits"), loaded.EditLabOutputDirectory);
    }

    [Fact]
    public void OldConfigWithoutSetupSection_LoadsAsNotCompleted_AndAnalysisOn()
    {
        using var sandbox = new SetupSandbox();
        File.WriteAllText(sandbox.ConfigPath, "[Soulseek]\nUsername = someone\n");
        var loaded = new ConfigManager(sandbox.ConfigPath).Load();
        Assert.False(loaded.SetupCompleted);
        Assert.True(loaded.AnalyzeNewDownloads);
        Assert.Null(loaded.EditLabOutputDirectory);
    }

    private sealed class NoCredentials : ISoulseekCredentialService
    {
        public Task SaveCredentialsAsync(string username, string password) => Task.CompletedTask;
        public Task<(string? Username, string? Password)> LoadCredentialsAsync() => Task.FromResult<(string?, string?)>((null, null));
        public Task DeleteCredentialsAsync() => Task.CompletedTask;
    }
}
