using System;
using System.Reactive.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Manages the audio analysis job queue and coordinates with the event bus.
/// Provides stealth mode (low-CPU) scheduling and publishes status events for
/// the Glass Box architecture (transparent queue visibility in the UI).
/// </summary>
public class AnalysisQueueService : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalysisQueueService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDisposable _requestSubscription;

    private bool _isStealthMode;
    private int _queuedCount;
    private int _processedCount;
    private string? _currentTrackHash;

    public bool IsStealthMode => _isStealthMode;

    public AnalysisQueueService(
        IEventBus eventBus,
        ILogger<AnalysisQueueService> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;

        _requestSubscription = _eventBus
            .GetEvent<TrackAnalysisRequestedEvent>()
            .Subscribe(OnAnalysisRequested);
    }

    /// <summary>
    /// Enables or disables stealth mode (reduced CPU usage for background analysis).
    /// Publishes a status event so the UI reflects the new mode immediately.
    /// </summary>
    public void SetStealthMode(bool enabled)
    {
        _isStealthMode = enabled;
        var mode = enabled ? "Stealth (Low CPU)" : "Standard";
        _logger.LogInformation("Analysis queue performance mode set to: {Mode}", mode);

        PublishStatus(mode);
    }

    private void OnAnalysisRequested(TrackAnalysisRequestedEvent evt)
    {
        _logger.LogDebug("Analysis requested for track {TrackGlobalId} at tier {Tier}",
            evt.TrackGlobalId, evt.Tier);

        _queuedCount++;
        _currentTrackHash = evt.TrackGlobalId;
        PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
    }

    private void PublishStatus(string performanceMode)
    {
        _eventBus.Publish(new AnalysisQueueStatusChangedEvent(
            QueuedCount: _queuedCount,
            ProcessedCount: _processedCount,
            CurrentTrackHash: _currentTrackHash,
            IsPaused: false,
            PerformanceMode: performanceMode));
    }

    public void Dispose()
    {
        _requestSubscription.Dispose();
    }
}
