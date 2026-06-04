using System;
using System.Collections.Generic;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Models;

// Download Manager Events
public record TrackUpdatedEvent(PlaylistTrackViewModel Track);
public record ProjectAddedEvent(Guid ProjectId);
public record ProjectUpdatedEvent(Guid ProjectId);
public record ProjectDeletedEvent(Guid ProjectId);
public record DownloadManagerHydratedEvent(int TrackCount);

// Explicit Track Events (missing in record list but used in code)
public record TrackAddedEvent(PlaylistTrack TrackModel, PlaylistTrackState? InitialState = null);
public record BatchTracksAddedEvent(IReadOnlyList<(PlaylistTrack Track, PlaylistTrackState? InitialState)> Tracks); // Issue #4: Batch event for bulk imports
public record TrackRemovedEvent(string TrackGlobalId);
public record TrackMovedEvent(string TrackGlobalId, Guid OldProjectId, Guid NewProjectId);
public record TrackStateChangedEvent(string TrackGlobalId, Guid ProjectId, PlaylistTrackState State, DownloadFailureReason FailureReason = DownloadFailureReason.None, string? Error = null, SearchAttemptLog? SearchLog = null, string? PeerName = null);
// Phase 2.5: Enhanced with byte-level progress tracking
public record TrackProgressChangedEvent(string TrackGlobalId, double Progress, long BytesReceived, long TotalBytes, string? CorrelationId = null);
public record TrackMetadataUpdatedEvent(string TrackGlobalId);
public record ForceStartRequestEvent(string TrackGlobalId);
public record BumpToTopRequestEvent(string TrackGlobalId); // [NEW] Overhaul Phase

// Transfer lifecycle events
public record TransferProgressEvent(string Filename, string Username, long BytesTransferred, long TotalBytes);
public record TransferFinishedEvent(string Filename, string Username);
public record TransferCancelledEvent(string Filename, string Username);
public record TransferFailedEvent(string Filename, string Username, string Error);

// Phase 8: Automation & Upgrade Events
public record AutoDownloadTrackEvent(string TrackGlobalId, Track BestMatch, string? CorrelationId = null);
public record AutoDownloadUpgradeEvent(string TrackGlobalId, Track BestMatch, string? CorrelationId = null);
public record UpgradeAvailableEvent(string TrackGlobalId, Track BestMatch, string? CorrelationId = null);

// Phase 2A: Crash Recovery Events
public record RecoveryCompletedEvent(
    int ResumedCount,
    int CleanedCount,
    int FailureCount,
    int DeadLetterCount,
    TimeSpan RecoveryDuration);
