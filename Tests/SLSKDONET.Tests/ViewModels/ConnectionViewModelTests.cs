using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class ConnectionViewModelTests
{
    [Fact]
    public async Task KickedEvent_SetsReconnectCooldownTimestamp()
    {
        var config = new AppConfig
        {
            Username = "test-user",
            AutoConnectEnabled = false,
            RememberPassword = false
        };

        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orbit-test-{Guid.NewGuid():N}.ini");
        var configManager = new ConfigManager(configPath);

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(false);

        var credentialService = new Mock<ISoulseekCredentialService>();
        credentialService
            .Setup(s => s.LoadCredentialsAsync())
            .ReturnsAsync((null, null));

        var secureTokenStorage = new Mock<ISecureTokenStorage>();
        secureTokenStorage
            .Setup(s => s.LoadRefreshTokenAsync())
            .ReturnsAsync((string?)null);

        var spotifyAuthService = new SpotifyAuthService(
            NullLogger<SpotifyAuthService>.Instance,
            config,
            secureTokenStorage.Object);

        var eventBus = new EventBusService();

        var sut = new ConnectionViewModel(
            NullLogger<ConnectionViewModel>.Instance,
            config,
            configManager,
            soulseek.Object,
            credentialService.Object,
            spotifyAuthService,
            eventBus);

        eventBus.Publish(new SoulseekConnectionStatusEvent("kicked", "test-user"));

        await Task.Delay(50);

        var cooldownField = typeof(ConnectionViewModel)
            .GetField("_reconnectCooldownUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cooldownField);

        var cooldownUntilUtc = (DateTime)cooldownField!.GetValue(sut)!;
        Assert.True(cooldownUntilUtc > DateTime.UtcNow.AddSeconds(40));

        sut.Dispose();
        eventBus.Dispose();
    }
}
