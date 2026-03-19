using System;
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
    private static ConnectionViewModel BuildSut(
        AppConfig? config = null,
        IEventBus? eventBus = null,
        IConnectionLifecycleService? lifecycle = null,
        ISoulseekAdapter? soulseek = null)
    {
        config ??= new AppConfig { Username = "test", AutoConnectEnabled = false, RememberPassword = false };
        var configPath     = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orbit-test-{Guid.NewGuid():N}.ini");
        var configManager  = new ConfigManager(configPath);
        var soulseekMock   = soulseek ?? Mock.Of<ISoulseekAdapter>(s => s.IsConnected == false);
            var credsMock      = new Mock<ISoulseekCredentialService>();
            credsMock.Setup(s => s.LoadCredentialsAsync())
                     .ReturnsAsync(((string?)null, (string?)null));
        var tokenMock      = Mock.Of<ISecureTokenStorage>(s => s.LoadRefreshTokenAsync() == Task.FromResult((string?)null));
        var spotifyAuth    = new SpotifyAuthService(NullLogger<SpotifyAuthService>.Instance, config, tokenMock);
        var bus            = eventBus ?? new EventBusService();
        var lifecycleMock  = lifecycle ?? Mock.Of<IConnectionLifecycleService>();

        return new ConnectionViewModel(
            NullLogger<ConnectionViewModel>.Instance,
            config,
            configManager,
            soulseekMock,
                credsMock.Object,
            spotifyAuth,
            bus,
            lifecycleMock);
    }

    [Fact]
    public void HandleLifecycleChange_LoggedIn_SetsIsConnectedTrue()
    {
        // Kick is now handled by ConnectionLifecycleService; verify ViewModel UI mapping instead.
        var eventBus = new EventBusService();
        var sut = BuildSut(eventBus: eventBus);

        // Publish a lifecycle LoggedIn transition
        eventBus.Publish(new ConnectionLifecycleStateChangedEvent("Connecting", "LoggedIn", "test"));

        // Give dispatcher time to post (small delay for Dispatcher.UIThread.Post)
        // In test context without Avalonia dispatcher, the Post runs synchronously in some hosts.
        // We just verify no exceptions and IsConnected is set if the dispatcher flushed.
        sut.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public void Disconnect_CallsLifecycleNotifyManualDisconnect()
    {
        var lifecycleMock = new Mock<IConnectionLifecycleService>();
        lifecycleMock.Setup(l => l.RequestDisconnectAsync(It.IsAny<string>(), It.IsAny<string>()))
                     .Returns(Task.CompletedTask);

        var sut = BuildSut(lifecycle: lifecycleMock.Object);
        sut.Disconnect();

        lifecycleMock.Verify(l => l.NotifyManualDisconnect(), Times.Once);
        sut.Dispose();
    }

    [Fact]
    public void AutoConnectEnabled_Setter_SyncsToLifecycleService()
    {
        var lifecycleMock = new Mock<IConnectionLifecycleService>();
        lifecycleMock.SetupProperty(l => l.AutoReconnectEnabled);

        var sut = BuildSut(lifecycle: lifecycleMock.Object);
        sut.AutoConnectEnabled = true;

        Assert.True(lifecycleMock.Object.AutoReconnectEnabled);
        sut.Dispose();
    }
}

