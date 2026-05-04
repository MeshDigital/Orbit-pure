using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services;

/// <summary>
/// Manages the audio analysis job queue and coordinates with the event bus.
/// Provides stealth mode (low-CPU) scheduling and publishes status events for
/// the Glass Box architecture (transparent queue visibility in the UI).
///
/// Parallelism is controlled via <see cref="AppConfig.MaxConcurrentAnalyses"/>:
///   0 = auto (ProcessorCount / 2, minimum 1)
///   1 = sequential (safe for low-RAM machines)
///  >1 = explicit parallelism cap
/// </summary>
public class AnalysisQueueService : IDisposable
{
    /// <summary>
    /// In stealth mode every analysis job yields to the OS for this duration
    /// before being dispatched, keeping the UI thread free for interaction.
    /// </summary>
    private static readonly TimeSpan StealthModeThrottleDelay = TimeSpan.FromMilliseconds(250);

    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalysisQueueService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAudioAnalysisService? _audioAnalysisService;
    private readonly AnalyzeTrackStructureJob? _analyzeTrackStructureJob;
    private readonly IDisposable _requestSubscription;
    private readonly SemaphoreSlim _parallelismGate;

    private bool _isStealthMode;
    private int _queuedCount;
    private int _processedCount;
    private string? _currentTrackHash;

    public bool IsStealthMode => _isStealthMode;

    /// <summary>Maximum number of concurrent analysis jobs.</summary>
    public int MaxWorkers { get; }

    public AnalysisQueueService(
        IEventBus eventBus,
        ILogger<AnalysisQueueService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        IAudioAnalysisService? audioAnalysisService = null,
        AnalyzeTrackStructureJob? analyzeTrackStructureJob = null,
        AppConfig? config = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        _audioAnalysisService = audioAnalysisService;
        _analyzeTrackStructureJob = analyzeTrackStructureJob;

        int requested = config?.MaxConcurrentAnalyses ?? 0;
        MaxWorkers = requested > 0
            ? requested
            : Math.Max(1, Environment.ProcessorCount / 2);

        _parallelismGate = new SemaphoreSlim(MaxWorkers, MaxWorkers);

        _requestSubscription = _eventBus
            .GetEvent<TrackAnalysisRequestedEvent>()
            .Subscribe(OnAnalysisRequested);

        _logger.LogInformation(
            "AnalysisQueue initialised: MaxWorkers={W} (requested={R})",
            MaxWorkers, requested);
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

        // Fire-and-forget the async dispatch so the subscription callback returns quickly.
        // Exceptions are caught and logged so failures are observable (Glass Box guarantee).
        _ = DispatchAnalysisJobAsync(evt).ContinueWith(
            t => _logger.LogError(t.Exception, "Unhandled error dispatching analysis job for {TrackGlobalId}", evt.TrackGlobalId),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Dispatches an analysis job, inserting a stealth-mode throttle delay when enabled
    /// so CPU-intensive work does not starve the UI scheduler.
    /// The parallelism gate (<see cref="_parallelismGate"/>) ensures at most
    /// <see cref="MaxWorkers"/> jobs run concurrently.
    /// </summary>
    private async Task DispatchAnalysisJobAsync(TrackAnalysisRequestedEvent evt)
    {
        await _parallelismGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isStealthMode)
            {
                // Yield for the configured delay so the UI thread stays responsive.
                await Task.Delay(StealthModeThrottleDelay).ConfigureAwait(false);
            }

            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

            var filePath = await db.PlaylistTracks
                .AsNoTracking()
                .Where(t => t.TrackUniqueHash == evt.TrackGlobalId)
                .Select(t => t.ResolvedFilePath)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = await db.LibraryEntries
                    .AsNoTracking()
                    .Where(t => t.UniqueHash == evt.TrackGlobalId)
                    .Select(t => t.FilePath)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                var message = $"Track file not found for analysis: {evt.TrackGlobalId}";
                _logger.LogWarning("{Message}", message);
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, message));
                _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, message));
                return;
            }

            if (_audioAnalysisService is null || _analyzeTrackStructureJob is null)
            {
                _logger.LogWarning("Analysis services are not available; completing request for {TrackGlobalId} in no-op mode.", evt.TrackGlobalId);
                _processedCount++;
                _currentTrackHash = null;
                PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
                return;
            }

            _eventBus.Publish(new TrackAnalysisStartedEvent(evt.TrackGlobalId, Path.GetFileName(filePath)));

            var analysis = await _audioAnalysisService
                .AnalyzeFileAsync(filePath, evt.TrackGlobalId, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            if (analysis is null)
            {
                var message = $"Audio analysis returned no result for {Path.GetFileName(filePath)}";
                _logger.LogWarning("{Message}", message);
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, message));
                _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, message));
                return;
            }

            await _analyzeTrackStructureJob.ExecuteAsync(evt.TrackGlobalId).ConfigureAwait(false);

            _processedCount++;
            _currentTrackHash = null;
            PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
            _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis job failed for {TrackGlobalId}", evt.TrackGlobalId);
            _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, ex.Message));
            _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, ex.Message));
        }
        finally
        {
            _currentTrackHash = null;
            PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
            _parallelismGate.Release();
        }
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
        _parallelismGate.Dispose();
    }
}
