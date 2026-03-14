using System;
using System.Collections.Generic;
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
}
