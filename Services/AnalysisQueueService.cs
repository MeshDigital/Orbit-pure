using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly SemaphoreSlim _dispatchSignal = new(0);
    private readonly CancellationTokenSource _dispatchCts = new();
    private readonly Task _dispatchLoopTask;
    private readonly ConcurrentDictionary<string, byte> _activeTrackHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scheduledTrackHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<TrackAnalysisRequestedEvent> _highPriorityQueue = new();
    private readonly ConcurrentQueue<TrackAnalysisRequestedEvent> _normalPriorityQueue = new();

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

        _dispatchLoopTask = Task.Run(() => RunDispatchLoopAsync(_dispatchCts.Token));

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
        if (string.IsNullOrWhiteSpace(evt.TrackGlobalId))
        {
            _logger.LogWarning("Ignoring analysis request with empty track hash.");
            return;
        }

        if (!_scheduledTrackHashes.TryAdd(evt.TrackGlobalId, 0))
        {
            _logger.LogDebug("Suppressing duplicate analysis request for track {TrackGlobalId}", evt.TrackGlobalId);
            PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
            return;
        }

        _logger.LogDebug("Analysis requested for track {TrackGlobalId} at tier {Tier}",
            evt.TrackGlobalId, evt.Tier);

        Interlocked.Increment(ref _queuedCount);
        EnqueueRequest(evt);
        PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
    }

    private void EnqueueRequest(TrackAnalysisRequestedEvent evt)
    {
        if (evt.IsHighPriority)
        {
            _highPriorityQueue.Enqueue(evt);
        }
        else
        {
            _normalPriorityQueue.Enqueue(evt);
        }

        _dispatchSignal.Release();
    }

    private async Task RunDispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _dispatchSignal.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (!TryDequeueNext(out var evt) || evt is null)
                {
                    continue;
                }

                await _parallelismGate.WaitAsync(cancellationToken).ConfigureAwait(false);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DispatchAnalysisJobAsync(evt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled error dispatching analysis job for {TrackGlobalId}", evt.TrackGlobalId);
                    }
                    finally
                    {
                        _parallelismGate.Release();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch loop failed");
            }
        }
    }

    private bool TryDequeueNext(out TrackAnalysisRequestedEvent? evt)
    {
        if (_highPriorityQueue.TryDequeue(out var high))
        {
            evt = high;
            return true;
        }

        if (_normalPriorityQueue.TryDequeue(out var normal))
        {
            evt = normal;
            return true;
        }

        evt = null;
        return false;
    }

    /// <summary>
    /// Dispatches an analysis job, inserting a stealth-mode throttle delay when enabled
    /// so CPU-intensive work does not starve the UI scheduler.
    /// The queue dispatcher ensures high-priority requests are processed first.
    /// </summary>
    private async Task DispatchAnalysisJobAsync(TrackAnalysisRequestedEvent evt)
    {
        var completedOrFailed = false;
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
                PublishProgress(evt.TrackGlobalId, "File not found", 100);
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, message));
                _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, message));
                completedOrFailed = true;
                return;
            }

            if (_audioAnalysisService is null || _analyzeTrackStructureJob is null)
            {
                _logger.LogWarning("Analysis services are not available; completing request for {TrackGlobalId} in no-op mode.", evt.TrackGlobalId);
                Interlocked.Increment(ref _processedCount);
                PublishProgress(evt.TrackGlobalId, "Analysis services unavailable", 100);
                _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, "Analysis services unavailable"));
                completedOrFailed = true;
                PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
                return;
            }

            _activeTrackHashes.TryAdd(evt.TrackGlobalId, 0);
            _currentTrackHash = evt.TrackGlobalId;
            await UpdateTrackAnalysisStatusAsync(evt.TrackGlobalId, AnalysisStatus.Processing, null).ConfigureAwait(false);
            _eventBus.Publish(new TrackAnalysisStartedEvent(evt.TrackGlobalId, Path.GetFileName(filePath)));
            PublishProgress(evt.TrackGlobalId, "Analyzing audio", 20);

            var analysis = await _audioAnalysisService
                .AnalyzeFileAsync(filePath, evt.TrackGlobalId, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            if (analysis is null)
            {
                var message = $"Audio analysis returned no result for {Path.GetFileName(filePath)}";
                _logger.LogWarning("{Message}", message);
                PublishProgress(evt.TrackGlobalId, "Analysis returned no result", 100);
                await UpdateTrackAnalysisStatusAsync(evt.TrackGlobalId, AnalysisStatus.Failed, message).ConfigureAwait(false);
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, message));
                _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, message));
                completedOrFailed = true;
                return;
            }

            PublishProgress(evt.TrackGlobalId, "Computing structure", 75);
            await _analyzeTrackStructureJob.ExecuteAsync(evt.TrackGlobalId).ConfigureAwait(false);
            await UpdateTrackAnalysisStatusAsync(evt.TrackGlobalId, AnalysisStatus.Completed, null).ConfigureAwait(false);
            _eventBus.Publish(new TrackAnalysisUpdatedEvent(evt.TrackGlobalId, DateTime.UtcNow, analysis.Id));

            Interlocked.Increment(ref _processedCount);
            PublishProgress(evt.TrackGlobalId, "Completed", 100);
            PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
            _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, true));
            completedOrFailed = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis job failed for {TrackGlobalId}", evt.TrackGlobalId);
            PublishProgress(evt.TrackGlobalId, "Analysis failed", 100);
            await UpdateTrackAnalysisStatusAsync(evt.TrackGlobalId, AnalysisStatus.Failed, ex.Message).ConfigureAwait(false);
            _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, ex.Message));
            _eventBus.Publish(new TrackAnalysisCompletedEvent(evt.TrackGlobalId, false, ex.Message));
            completedOrFailed = true;
        }
        finally
        {
            if (completedOrFailed)
            {
                var remaining = Interlocked.Decrement(ref _queuedCount);
                if (remaining < 0)
                {
                    Interlocked.Exchange(ref _queuedCount, 0);
                }
            }

            _activeTrackHashes.TryRemove(evt.TrackGlobalId, out _);
            _scheduledTrackHashes.TryRemove(evt.TrackGlobalId, out _);
            _currentTrackHash = _activeTrackHashes.Keys.FirstOrDefault()
                ?? _scheduledTrackHashes.Keys.FirstOrDefault();
            PublishStatus(_isStealthMode ? "Stealth (Low CPU)" : "Standard");
        }
    }

    private async Task UpdateTrackAnalysisStatusAsync(string trackGlobalId, AnalysisStatus status, string? error)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        var playlistRows = await db.PlaylistTracks
            .Where(t => t.TrackUniqueHash == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in playlistRows)
        {
            row.AnalysisStatus = status;
            row.QualityDetails = string.IsNullOrWhiteSpace(error) ? null : error;
            if (status == AnalysisStatus.Completed)
            {
                row.IsEnriched = true;
                row.AvailabilityState = TrackAvailabilityState.Ready;
            }
        }

        var libraryRows = await db.LibraryEntries
            .Where(t => t.UniqueHash == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in libraryRows)
        {
            row.AnalysisStatus = status;
            row.QualityDetails = string.IsNullOrWhiteSpace(error) ? null : error;
            if (status == AnalysisStatus.Completed)
            {
                row.IsEnriched = true;
                row.AvailabilityState = TrackAvailabilityState.Ready;
            }
        }

        var masterTracks = await db.Tracks
            .Where(t => t.GlobalId == trackGlobalId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var row in masterTracks)
        {
            if (status == AnalysisStatus.Completed)
            {
                row.AvailabilityState = TrackAvailabilityState.Ready;
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    private void PublishProgress(string trackGlobalId, string step, int percent)
    {
        _eventBus.Publish(new AnalysisProgressEvent(
            trackGlobalId,
            step,
            Math.Clamp(percent, 0, 100)));
    }

    private void PublishStatus(string performanceMode)
    {
        _eventBus.Publish(new AnalysisQueueStatusChangedEvent(
            QueuedCount: Math.Max(0, Volatile.Read(ref _queuedCount)),
            ProcessedCount: Math.Max(0, Volatile.Read(ref _processedCount)),
            CurrentTrackHash: _currentTrackHash,
            IsPaused: false,
            PerformanceMode: performanceMode,
            MaxConcurrency: MaxWorkers));
    }

    public void Dispose()
    {
        _requestSubscription.Dispose();
        _dispatchCts.Cancel();
        try
        {
            _dispatchLoopTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        _dispatchCts.Dispose();
        _dispatchSignal.Dispose();
        _parallelismGate.Dispose();
    }
}
