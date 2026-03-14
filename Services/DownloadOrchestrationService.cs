using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels;
using SLSKDONET.Models; // Added for PlaylistTrackState

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates download lifecycle operations including start, pause, and cancel.
/// Extracted from MainViewModel to separate business logic from UI coordination.
/// </summary>
public class DownloadOrchestrationService
{
    private readonly ILogger<DownloadOrchestrationService> _logger;
    private readonly DownloadManager _downloadManager;
    
    public DownloadOrchestrationService(
        ILogger<DownloadOrchestrationService> logger,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _downloadManager = downloadManager;
    }
    
    /// <summary>
    /// Start all queued downloads.
    /// </summary>
    public async Task<DownloadOperationResult> StartDownloadsAsync()
    {
        try
        {
            _logger.LogInformation("Starting downloads");
            await _downloadManager.StartAsync();
            
            return new DownloadOperationResult
            {
                Success = true,
                Message = "Downloads completed"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download error");
            return new DownloadOperationResult
            {
                Success = false,
                Message = $"Download error: {ex.Message}"
            };
        }
    }
    
    /// <summary>
    /// Cancel all active downloads.
    /// </summary>
    public DownloadOperationResult CancelAllDownloads()
    {
        try
        {
            var tracks = _downloadManager.ActiveDownloads.ToList(); // Snapshot
            int cancelled = 0;
            
            foreach (var track in tracks)
            {
                if (track.State != PlaylistTrackState.Completed && track.State != PlaylistTrackState.Cancelled)
                {
                    _downloadManager.CancelTrack(track.GlobalId);
                    cancelled++;
                }
            }
            
            _logger.LogInformation("Cancelled {Count} downloads", cancelled);
            return new DownloadOperationResult
            {
                Success = true,
                Message = $"Cancelled {cancelled} download(s)",
                AffectedCount = cancelled
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel downloads");
            return new DownloadOperationResult
            {
                Success = false,
                Message = $"Cancel failed: {ex.Message}"
            };
        }
    }
    
    /// <summary>
    /// Pause all active downloads.
    /// </summary>
    public async Task<DownloadOperationResult> PauseAllDownloads()
    {
        try
        {
            var tracks = _downloadManager.ActiveDownloads.ToList(); // Snapshot
            int paused = 0;
            
            foreach (var track in tracks)
            {
                if (track.IsActive)
                {
                    await _downloadManager.PauseTrackAsync(track.GlobalId);
                    paused++;
                }
            }
            
            _logger.LogInformation("Paused {Count} downloads", paused);
            return new DownloadOperationResult
            {
                Success = true,
                Message = $"Paused {paused} download(s)",
                AffectedCount = paused
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause downloads");
            return new DownloadOperationResult
            {
                Success = false,
                Message = $"Pause failed: {ex.Message}"
            };
        }
    }
    
    /// <summary>
    /// Get download statistics.
    /// </summary>
    public DownloadStatistics GetStatistics()
    {
        var allTracks = _downloadManager.ActiveDownloads;
        
        return new DownloadStatistics
        {
            SuccessfulCount = allTracks.Count(t => t.State == PlaylistTrackState.Completed),
            FailedCount = allTracks.Count(t => t.State == PlaylistTrackState.Failed),
            PendingCount = allTracks.Count(t => t.State == SLSKDONET.Models.PlaylistTrackState.Pending || 
                                                 t.State == SLSKDONET.Models.PlaylistTrackState.Downloading || 
                                                 t.State == SLSKDONET.Models.PlaylistTrackState.Searching),
            TotalCount = allTracks.Count
        };
    }
}

/// <summary>
/// Result of a download operation.
/// </summary>
public class DownloadOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int AffectedCount { get; set; }
}

/// <summary>
/// Download statistics.
/// </summary>
public class DownloadStatistics
{
    public int SuccessfulCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }
    public int TotalCount { get; set; }
    
    public double ProgressPercentage
    {
        get
        {
            if (TotalCount == 0) return 0;
            return (SuccessfulCount / (double)TotalCount) * 100;
        }
    }
}
