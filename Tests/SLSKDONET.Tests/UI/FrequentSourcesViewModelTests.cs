using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Input;
using SLSKDONET.Tests.Helpers;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Settings;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.UI;

public class FrequentSourcesViewModelTests
{
    private sealed class SutContext : IDisposable
    {
        public SettingsViewModel ViewModel { get; }
        public Mock<IFileInteractionService> FileInteraction { get; }
        public string ConfigPath { get; }

        public SutContext(SettingsViewModel viewModel, Mock<IFileInteractionService> fileInteraction, string configPath)
        {
            ViewModel = viewModel;
            FileInteraction = fileInteraction;
            ConfigPath = configPath;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
        }
    }

    private static SutContext BuildSut(bool enableFrequentSources, string frequentSourcesStagingPath)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"orbit-frequent-sources-settings-{Guid.NewGuid():N}.ini");
        var config = new AppConfig
        {
            EnableFrequentSources = enableFrequentSources,
            FrequentSourcesStagingPath = frequentSourcesStagingPath
        };

        var configManager = new ConfigManager(configPath);

        var fileInteraction = new Mock<IFileInteractionService>();
        fileInteraction
            .Setup(x => x.OpenFolderDialogAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var tokenStorage = new Mock<ISecureTokenStorage>();
        tokenStorage
            .Setup(x => x.LoadRefreshTokenAsync())
            .ReturnsAsync((string?)null);

        var spotifyAuth = new SpotifyAuthService(
            NullLogger<SpotifyAuthService>.Instance,
            config,
            tokenStorage.Object);

        var spotifyMetadata = new Mock<ISpotifyMetadataService>();

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(x => x.IsConnected).Returns(false);
        soulseek
            .Setup(x => x.ApplyRuntimeNetworkConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        soulseek
            .Setup(x => x.RefreshShareStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var credentialService = new Mock<ISoulseekCredentialService>();
        var lifecycle = new Mock<IConnectionLifecycleService>();

        var keyboardMappingService = new Mock<IKeyboardMappingService>();
        keyboardMappingService
            .SetupGet(x => x.ActiveProfile)
            .Returns(new KeyboardProfile { Name = "Test Profile", Bindings = new List<KeyboardBinding>() });
        keyboardMappingService
            .Setup(x => x.GetConflicts())
            .Returns(Array.Empty<(KeyboardBinding a, KeyboardBinding b)>());

        var keyboardMappings = new KeyboardMappingsViewModel(
            keyboardMappingService.Object,
            telemetry: null,
            NullLogger<KeyboardMappingsViewModel>.Instance);

        var vm = new SettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            config,
            configManager,
            fileInteraction.Object,
            spotifyAuth,
            spotifyMetadata.Object,
            databaseService: null!,
            libraryFolderScannerService: null!,
            new EventBusService(),
            soulseek.Object,
            credentialService.Object,
            lifecycle.Object,
            keyboardMappings);

        return new SutContext(vm, fileInteraction, configPath);
    }

    [ProfileTest("frequent-sources")]
    public void ViewModelLoadsSourcesWhenEnabled()
    {
        using var sut = BuildSut(enableFrequentSources: true, frequentSourcesStagingPath: @"C:\Orbit\FrequentSources");

        Assert.True(sut.ViewModel.EnableFrequentSources);
        Assert.Equal(@"C:\Orbit\FrequentSources", sut.ViewModel.FrequentSourcesStagingPath);
        Assert.NotNull(sut.ViewModel.BrowseFrequentSourcesStagingPathCommand);
    }

    [ProfileTest("frequent-sources")]
    public async Task BrowseCommandOpensDialog()
    {
        using var sut = BuildSut(enableFrequentSources: false, frequentSourcesStagingPath: string.Empty);
        var selectedPath = @"C:\Orbit\PrefetchStage";

        sut.FileInteraction
            .Setup(x => x.OpenFolderDialogAsync("Select Frequent Sources Staging Folder"))
            .ReturnsAsync(selectedPath);

        var command = Assert.IsType<AsyncRelayCommand>(sut.ViewModel.BrowseFrequentSourcesStagingPathCommand);
        await command.ExecuteAsync(null);

        Assert.Equal(selectedPath, sut.ViewModel.FrequentSourcesStagingPath);
        sut.FileInteraction.Verify(x => x.OpenFolderDialogAsync("Select Frequent Sources Staging Folder"), Times.Once);
    }
}