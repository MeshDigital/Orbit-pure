using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services;

/// <summary>
/// "The Heart" of Phase 3B.
/// Actively monitors download progress and intervenes when transfers stall.
/// Distinguishes between "Queued" (Passive) and "Stalled" (Active Failure).
/// </summary>
public class DownloadHealthMonitor : IDisposable
{
    private readonly ILogger<DownloadHealthMonitor> _logger;
    private readonly DownloadManager _downloadManager;
    private CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    
    // Track previous bytes to calculate delta
    private readonly ConcurrentDictionary<string, long> _previousBytes = new();

    public DownloadHealthMonitor(
        ILogger<DownloadHealthMonitor> logger,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _downloadManager = downloadManager;
    }

    public void StartMonitoring()
    {
        if (_monitorTask != null) return;
        
        _cts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_cts.Token);
        _logger.LogInformation("💓 Download Health Monitor started.");
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await CheckHealthAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detailed Health Monitor crash");
        }
    }

    private async Task CheckHealthAsync()
    {
        var activeDownloads = _downloadManager.ActiveDownloads
            .Where(d => d.State == PlaylistTrackState.Downloading)
            .ToList();

        // 1. Cleanup stale trackers for downloads that are no longer active
        var activeIds = activeDownloads.Select(d => d.GlobalId).ToHashSet();
        foreach (var key in _previousBytes.Keys)
        {
            if (!activeIds.Contains(key))
            {
                _previousBytes.TryRemove(key, out _);
            }
        }

        // 2. Check each active download
        foreach (var ctx in activeDownloads)
        {
            // Thread-safe read
            long currentBytes = ctx.BytesReceived;
            long previousBytes = _previousBytes.GetOrAdd(ctx.GlobalId, currentBytes);
            
            // Calculate delta
            long delta = currentBytes - previousBytes;
            
            // Update previous for next tick
            _previousBytes[ctx.GlobalId] = currentBytes;

            // Phase 0.3: Calculate Speed (Window is 15 seconds)
            ctx.CurrentSpeed = delta / 15;

            if (delta > 0)
            {
                // HEALTHY: Progress made
                ctx.StallCount = 0;
            }
            else
            {
                // STALLED: No progress
                ctx.StallCount++;
                
                // Determine threshold based on Adaptive Logic
                int threshold = CalculateStallThreshold(ctx);
                
                if (ctx.StallCount >= threshold)
                {
                     await HandleStalledDownloadAsync(ctx, ctx.StallCount * 15);
                }
            }
        }
    }

    /// <summary>
    /// Adaptive Timeout Logic:
    /// - Normal: 4 ticks (60 seconds)
    /// - Late Stage (>90%): 8 ticks (120 seconds) to allow for slow finishes
    /// </summary>
    private int CalculateStallThreshold(DownloadContext ctx)
    {
        if (ctx.TotalBytes > 0 && ctx.BytesReceived > (ctx.TotalBytes * 0.9))
        {
            return 8; // 120 seconds for >90% complete
        }
        return 4; // 60 seconds default
    }

    private async Task HandleStalledDownloadAsync(DownloadContext ctx, int stalledSeconds)
    {
        try
        {
            // Extract username (Soulseek filenames are handled in adapter, but context Model doesn't explicitly store Username? 
            // Model has ResolvedFilePath, but not current peer username explicitly.
            // CHECK: DownloadCheckpointState stores SoulseekUsername. 
            // CHECK: In DownloadManager, DownloadFileAsync takes 'Track bestMatch' which has Username.
            // GAP: DownloadContext needs to expose 'CurrentPeer' or we need to look it up.
            // Assumption: We might need to add CurrentPeer to DownloadContext in Phase 3B.
            // For now, I will assume we can get it or fail gracefully.
            
            // Wait, DownloadManager passes `bestMatch` to `DownloadFileAsync`, but doesn't store it in `ctx`.
            // I should add `CurrentPeer` to `DownloadContext`.
            
            _logger.LogWarning("⚠️ ACTIVE INTERVENTION: Track {Title} stalled for {Seconds}s. Triggering Auto-Retry.", 
                ctx.Model.Title, stalledSeconds);

            // Execute the Auto-Retry on the Manager
            // This needs the username to blacklist.
            // I will update DownloadContext to store CurrentPeer first.
            await _downloadManager.AutoRetryStalledDownloadAsync(ctx.GlobalId);
            
            // Mark intervention time in context
            ctx.LastIntervention = DateTime.UtcNow;
            ctx.StallCount = 0; // Reset counter
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to handle stalled download for {GlobalId}", ctx.GlobalId);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _monitorTask?.Wait();
        _cts.Dispose();
    }
}
