using System;
using System.Threading;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Models;

/// <summary>
/// Internal state tracking context for a download managed by DownloadManager.
/// Replaces the use of PlaylistTrackViewModel within the service layer.
/// </summary>
public class DownloadContext
{
    public PlaylistTrack Model { get; }
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    
    // Transient State
    public PlaylistTrackState State { get; set; } = PlaylistTrackState.Pending;
    public double Progress { get; set; }
    public string GlobalId => Model.TrackUniqueHash;
    public string? ErrorMessage { get; set; }
    
    // Industrial Prep Helper Properties
    public string ArtistName => Model.Artist;
    public string TrackTitle => Model.Title;

    // Phase 3B: Peer Blacklisting for Health Monitor
    public HashSet<string> BlacklistedUsers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentUsername { get; set; } // Track active peer for Health Monitor

    // Phase 2.5: Resumable Download Tracking
    public long TotalBytes { get; set; }        // Remote file size
    
    // Phase 2A: Thread-safe progress tracking (Interlocked for atomic updates)
    private long _bytesReceived;
    public long BytesReceived 
    { 
        get => Interlocked.Read(ref _bytesReceived);
        set => Interlocked.Exchange(ref _bytesReceived, value);
    }
    
    public bool IsResuming { get; set; }        // UI/Log feedback for "Resuming" vs "Downloading"
    
    // Phase 0.3: Health Metrics
    public long CurrentSpeed { get; set; }      // Bytes per second
    public int StallCount { get; set; }         // Consecutive stalled ticks
    public DateTime? LastIntervention { get; set; } // When did the Health Monitor last kick in?

    // Phase 3A: Finalization Guard (prevents heartbeat race conditions)
    // 0 = false, 1 = true
    private int _isFinalizing; 
    public bool IsFinalizing 
    {
        get => Interlocked.CompareExchange(ref _isFinalizing, 0, 0) == 1;
        set => Interlocked.Exchange(ref _isFinalizing, value ? 1 : 0);
    }

    // Reliability (Phase 7: DJ's Studio)
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; }
    
    // Phase: Download Failure Diagnostics
    public DownloadFailureReason? FailureReason { get; set; }
    public List<SearchAttemptLog> SearchAttempts { get; set; } = new();
    public string? DetailedFailureMessage { get; set; }
    public string? StalledReason { get; set; } // [NEW] Overhaul Phase
    public bool IsVip { get; set; } // [NEW] Overhaul Phase: Bypasses semaphore
    public DateTime? SearchStartedAt { get; set; } // Phase 10

    public DownloadContext(PlaylistTrack model)
    {
        Model = model;
        SearchStartedAt = model.SearchStartedAt;
        
        // Map initial state from persistence
        if (model.Status == TrackStatus.Downloaded)
        {
            State = PlaylistTrackState.Completed;
            Progress = 100;
        }
    }

    public bool IsActive => State == PlaylistTrackState.Searching || 
                           State == PlaylistTrackState.Downloading || 
                           State == PlaylistTrackState.Queued;
}
