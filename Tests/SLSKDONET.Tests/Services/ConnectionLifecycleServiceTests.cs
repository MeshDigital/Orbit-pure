using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class ConnectionLifecycleServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static (ConnectionLifecycleService Service, EventBusService EventBus, Mock<ISoulseekAdapter> Soulseek)
        CreateService()
    {
        var eventBus = new EventBusService();
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(false);

        var service = new ConnectionLifecycleService(
            NullLogger<ConnectionLifecycleService>.Instance,
            soulseek.Object,
            eventBus);

        return (service, eventBus, soulseek);
    }

    /// <summary>Drives service to LoggedIn state using the event-based path.</summary>
    private static async Task DriveToLoggedIn(
        ConnectionLifecycleService service,
        EventBusService eventBus,
        Mock<ISoulseekAdapter> soulseek)
    {
        soulseek.Setup(s => s.ConnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        service.AutoReconnectEnabled = false; // disable reconnect loop for test isolation

        await service.RequestConnectAsync("password");
        // State is now Connecting; simulate Soulseek firing the LoggedIn state change
            eventBus.Publish(new SoulseekStateChangedEvent("Connected, LoggedIn", true));
        // Subject.OnNext is synchronous so state is now LoggedIn
    }

    // ── tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestConnectAsync_WhenDisconnected_TransitionsToConnecting()
    {
        var (service, eventBus, soulseek) = CreateService();
        var captured = new List<ConnectionLifecycleStateChangedEvent>();
        eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(e => captured.Add(e));

        soulseek.Setup(s => s.ConnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        service.AutoReconnectEnabled = false;

        await service.RequestConnectAsync("password");

        // First transition should be Disconnected → Connecting
        Assert.Contains(captured, e => e.Previous == "Disconnected" && e.Current == "Connecting");
        // Adapter was called exactly once
        soulseek.Verify(s => s.ConnectAsync("password", It.IsAny<CancellationToken>()), Times.Once);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task RequestConnectAsync_WhenCoolingDown_RejectsWithoutCallingAdapter()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;

        // Boot to LoggedIn, then simulate kick → CoolingDown
        await DriveToLoggedIn(service, eventBus, soulseek);
        soulseek.Invocations.Clear(); // reset call count

        eventBus.Publish(new SoulseekConnectionStatusEvent("kicked", "test"));
        // Kick handler transitions to CoolingDown
        Assert.Equal(ConnectionLifecycleState.CoolingDown, service.CurrentState);

        // Now request connect — should be rejected
        await service.RequestConnectAsync("password2");

        soulseek.Verify(s => s.ConnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task RequestConnectAsync_WhenAlreadyLoggedIn_DoesNotCallAdapterAgain()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;

        await DriveToLoggedIn(service, eventBus, soulseek);
        Assert.Equal(ConnectionLifecycleState.LoggedIn, service.CurrentState);
        soulseek.Invocations.Clear();

        // Second connect request should be a no-op
        await service.RequestConnectAsync("password");

        soulseek.Verify(s => s.ConnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task SoulseekStateChangedEvent_LoggedIn_TransitionsFromConnectingToLoggedIn()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;

        await DriveToLoggedIn(service, eventBus, soulseek);

        Assert.Equal(ConnectionLifecycleState.LoggedIn, service.CurrentState);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task SoulseekStateChangedEvent_Disconnected_WhenLoggedIn_TransitionsToDisconnected()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;

        await DriveToLoggedIn(service, eventBus, soulseek);

            eventBus.Publish(new SoulseekStateChangedEvent("Disconnected", false));

        Assert.Equal(ConnectionLifecycleState.Disconnected, service.CurrentState);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task KickedEvent_WhenLoggedIn_TransitionsToCoolingDown()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;
        var captured = new List<ConnectionLifecycleStateChangedEvent>();
        eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(e => captured.Add(e));

        await DriveToLoggedIn(service, eventBus, soulseek);

        eventBus.Publish(new SoulseekConnectionStatusEvent("kicked", "test"));

        Assert.Equal(ConnectionLifecycleState.CoolingDown, service.CurrentState);
        Assert.Contains(captured, e => e.Previous == "LoggedIn" && e.Current == "CoolingDown");

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task RequestDisconnectAsync_WhenLoggedIn_TransitionsToDisconnectingThenCallsAdapter()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;

        await DriveToLoggedIn(service, eventBus, soulseek);

        soulseek.Setup(s => s.DisconnectAsync()).Returns(Task.CompletedTask);

        await service.RequestDisconnectAsync("manual");

        soulseek.Verify(s => s.DisconnectAsync(), Times.Once);

        service.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public async Task InvalidTransition_IsIgnoredAndDoesNotPublishEvent()
    {
        var (service, eventBus, soulseek) = CreateService();
        service.AutoReconnectEnabled = false;
        var captured = new List<ConnectionLifecycleStateChangedEvent>();
        eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(e => captured.Add(e));

        await DriveToLoggedIn(service, eventBus, soulseek);
        captured.Clear(); // discard boot events

            // LoggedIn → LoggingIn is not a valid lifecycle transition.
            // Publish a raw "Connected" state (still connected, but not LoggedIn),
            // which the handler maps to LoggingIn. From LoggedIn this should be ignored.
            eventBus.Publish(new SoulseekStateChangedEvent("Connected", true));

        // LoggedIn → LoggingIn is not a valid transition so state should remain LoggedIn
        Assert.Equal(ConnectionLifecycleState.LoggedIn, service.CurrentState);
        // No new transition events published
        Assert.Empty(captured);

        service.Dispose();
        eventBus.Dispose();
    }
}
