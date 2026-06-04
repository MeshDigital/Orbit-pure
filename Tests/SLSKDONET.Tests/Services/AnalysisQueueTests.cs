using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class AnalysisQueueTests
{
    private readonly Mock<IEventBus> _eventBusMock;
    private readonly Mock<ILogger<AnalysisQueueService>> _loggerMock;
    private readonly Mock<IDbContextFactory<AppDbContext>> _dbFactoryMock;
    private readonly AnalysisQueueService _service;

    public AnalysisQueueTests()
    {
        _eventBusMock = new Mock<IEventBus>();
        _loggerMock = new Mock<ILogger<AnalysisQueueService>>();
        _dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();

        // Setup GetEvent to return an observable so Subscribe doesn't fail
        _eventBusMock.Setup(x => x.GetEvent<TrackAnalysisRequestedEvent>())
            .Returns(System.Reactive.Linq.Observable.Empty<TrackAnalysisRequestedEvent>());

        _service = new AnalysisQueueService(_eventBusMock.Object, _loggerMock.Object, _dbFactoryMock.Object);
    }

    [Fact]
    public void StealthMode_StartsDisabled()
    {
        Assert.False(_service.IsStealthMode);
    }

    [Fact]
    public void SetStealthMode_UpdatesPropertyAndPublishesEvent()
    {
        // Act
        _service.SetStealthMode(true);

        // Assert
        Assert.True(_service.IsStealthMode);
        _eventBusMock.Verify(x => x.Publish(It.Is<AnalysisQueueStatusChangedEvent>(e => e.PerformanceMode.Contains("Stealth"))), Times.Once);
    }

    [Fact]
    public void SetStealthMode_False_ReturnsToStandard()
    {
        // Arrange
        _service.SetStealthMode(true);

        // Act
        _service.SetStealthMode(false);

        // Assert
        Assert.False(_service.IsStealthMode);
        // We look for anything that isn't Stealth in the PerformanceMode string
        _eventBusMock.Verify(x => x.Publish(It.Is<AnalysisQueueStatusChangedEvent>(e => !e.PerformanceMode.Contains("Stealth"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DuplicateInFlightRequests_AreSuppressed_ForSameTrackHash()
    {
        var requestStream = new Subject<TrackAnalysisRequestedEvent>();
        var statusEvents = new List<AnalysisQueueStatusChangedEvent>();

        var eventBusMock = new Mock<IEventBus>();
        eventBusMock.Setup(x => x.GetEvent<TrackAnalysisRequestedEvent>())
            .Returns(requestStream);
        eventBusMock.Setup(x => x.Publish(It.IsAny<AnalysisQueueStatusChangedEvent>()))
            .Callback<AnalysisQueueStatusChangedEvent>(evt => statusEvents.Add(evt));

        var loggerMock = new Mock<ILogger<AnalysisQueueService>>();
        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options;
                return new AppDbContext(options);
            });

        using var service = new AnalysisQueueService(eventBusMock.Object, loggerMock.Object, dbFactoryMock.Object);
        service.SetStealthMode(true);

        requestStream.OnNext(new TrackAnalysisRequestedEvent("dup-track-hash"));
        requestStream.OnNext(new TrackAnalysisRequestedEvent("dup-track-hash"));

        await Task.Delay(100);

        Assert.NotEmpty(statusEvents);
        Assert.Equal(1, statusEvents.Max(e => e.QueuedCount));
    }
}
