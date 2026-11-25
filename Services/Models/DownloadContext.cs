using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SLSKDONET.Models;
using SLSKDONET.Services.Downloads;

namespace SLSKDONET.Services.Models;

/// <summary>
/// Internal state tracking context for a download managed by DownloadManager.
/// Replaces the use of PlaylistTrackViewModel within the service layer.
/// </summary>
public class DownloadContext
{
    public PlaylistTrack Model { get; }
    public string CorrelationId { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();

    // Transient State
    public PlaylistTrackState State { get; set; } = PlaylistTrackState.Pending;
    public double Progress { get; set; }
    public string GlobalId => Model.TrackUniqueHash;
    public string? ErrorMessage { get; set; }

    // Industrial Prep Helper Properties
    public string ArtistName => Model.Artist;
    public string TrackTitle => Model.Title;

    // Phase 3B / #316: Lock-free per-track peer blacklist (ConcurrentDictionary eliminates the
    // secondary lock(ctx.BlacklistedUsers) that previously created dual-lock ordering risk with
    // DownloadManager._collectionLock).
    private readonly ConcurrentDictionary<string, byte> _blacklistedUsers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a peer to this track's blacklist. Returns true if newly blacklisted.</summary>
    public bool BlacklistUser(string? username) =>
        !string.IsNullOrEmpty(username) && _blacklistedUsers.TryAdd(username, 0);

    /// <summary>Checks whether a peer is currently blacklisted for this track.</summary>
    public bool IsUserBlacklisted(string? username) =>
        !string.IsNullOrEmpty(username) && _blacklistedUsers.ContainsKey(username);

    /// <summary>Clears all blacklisted peers for this track (e.g. on hard retry).</summary>
    public void ClearBlacklistedUsers() => _blacklistedUsers.Clear();

    /// <summary>Returns a frozen case-insensitive snapshot of blacklisted peers for discovery.</summary>
    public HashSet<string> SnapshotBlacklistedUsers() =>
        new(_blacklistedUsers.Keys, StringComparer.OrdinalIgnoreCase);
    public string? CurrentUsername { get; set; } // Track active peer for Health Monitor
    public ActiveTransferRegistry.TransferKey? ActiveTransferKey { get; set; }
    public Track? HedgeMatch { get; set; }
    public Track? OverrideCandidate { get; set; }
    public bool HedgeAttempted { get; set; }

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

    /// <summary>The exact .part path of the current attempt (#328), so cleanup deletes the real file.</summary>
    public string? PartFilePath { get; set; }

    /// <summary>LF6 (#348): when re-downloading to upgrade an existing track, the old file path to replace in place once verified.</summary>
    public string? UpgradeSourcePath { get; set; }

    // Phase 0.3: Health Metrics
    public long CurrentSpeed { get; set; }      // Bytes per second
    public int StallCount { get; set; }         // Consecutive stalled ticks
    public DateTime? LastIntervention { get; set; } // When did the Health Monitor last kick in?

    // Phase 9: Hedged Resilience - Stall Detection
    public DateTime? LastSpeedUpdate { get; set; }
    public DateTime? StallStartTime { get; set; }
    public bool IsStalled { get; set; }

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

    // Slot-gated retry: the track is ineligible for re-selection until the scheduler has
    // dispatched at least this many tracks since the failure.  This ensures a failed track
    // waits for all currently-pending tracks to have at least one attempt before it competes
    // again, rather than immediately re-joining the front of the queue.
    public long? RetryAfterSlot { get; set; }

    // Phase: Download Failure Diagnostics
    public DownloadFailureReason? FailureReason { get; set; }
    public List<SearchAttemptLog> SearchAttempts { get; set; } = new();
    public string? DetailedFailureMessage { get; set; }
    public string? StalledReason { get; set; } // [NEW] Overhaul Phase
    public bool IsVip { get; set; } // [NEW] Overhaul Phase: prioritizes queue selection
    public DateTime? SearchStartedAt { get; set; } // Phase 10

    // Issue #9: State lifecycle tracking for stale queue sweeper
    public DateTime LastStateChangeTime { get; set; } = DateTime.UtcNow; // Tracks when current state was entered

    // Queue Velocity Tracking (zombie peer detection)
    // -1 = unknown (peer never reported a position)
    public int CurrentQueuePosition { get; set; } = -1;
    public DateTime? QueuePositionLastUpdated { get; set; }   // When peer last reported a position change
    public DateTime? QueueEnteredAt { get; set; }             // When we first entered the queued state

    public DownloadContext(PlaylistTrack model)
    {
        Model = model;
        CorrelationId = Guid.NewGuid().ToString("N");
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
