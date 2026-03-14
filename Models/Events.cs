using SLSKDONET.ViewModels;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Models;

/// <summary>
/// Typed event records for the EventBus system.
/// These replace anonymous tuples and custom event handlers.
/// </summary>

// Download Manager Events
public record TrackUpdatedEvent(PlaylistTrackViewModel Track);
public record ProjectAddedEvent(Guid ProjectId);
public record ProjectUpdatedEvent(Guid ProjectId);
public record ExternalDiscoveryRequestedEvent(string TrackHash);
public record ProjectDeletedEvent(Guid ProjectId);
public record DownloadManagerHydratedEvent(int TrackCount);

// Soulseek Adapter Events
public record SoulseekStateChangedEvent(string State, bool IsConnected);
public record SoulseekConnectionStatusEvent(string Status, string Username);
public record TransferProgressEvent(string Filename, string Username, long BytesTransferred, long TotalBytes);
public record TransferFinishedEvent(string Filename, string Username);
public record TransferCancelledEvent(string Filename, string Username);
public record TransferFailedEvent(string Filename, string Username, string Error);
public record SharedFilesStatusEvent(int Count, string Directory);

// Library Service Events
public record LibraryEntryAddedEvent(LibraryEntry Entry);
public record LibraryEntryUpdatedEvent(LibraryEntry Entry);
public record LibraryEntryDeletedEvent(string UniqueHash);
public record LibraryMetadataEnrichedEvent(int Count);

// Player Events
public record TrackPlaybackStartedEvent(string FilePath, string Artist, string Title);
public record TrackPlaybackPausedEvent();
public record TrackPlaybackResumedEvent();
public record TrackPlaybackStoppedEvent();
public record PlaybackProgressEvent(TimeSpan Position, TimeSpan Duration);

// Navigation & Global UI Events
public record NavigationEvent(PageType PageType);
public record TrackSelectionChangedEvent(PlaylistTrack? Track); // Phase 12.6: Inspector Sync
public record PlayTrackRequestEvent(PlaylistTrackViewModel Track);
public record AddToQueueRequestEvent(PlaylistTrackViewModel Track);
public record PlayAlbumRequestEvent(System.Collections.Generic.IEnumerable<PlaylistTrack> Tracks);
public record DownloadAlbumRequestEvent(object Album); // object to handle AlbumNode or PlaylistJob
public record RequestTheaterModeEvent();
public record AddToTimelineRequestEvent(System.Collections.Generic.IEnumerable<PlaylistTrack> Tracks);


public record AddToProjectRequestEvent(System.Collections.Generic.IEnumerable<PlaylistTrack> Tracks); // Phase 12.7: Context Menu Actions
public record RevealFileRequestEvent(string FilePath);
public record SeekRequestEvent(double PositionPercent); // 0.0 to 1.0
public record SeekToSecondsRequestEvent(double Seconds);

// Explicit Track Events (missing in record list but used in code)
public record TrackAddedEvent(PlaylistTrack TrackModel, PlaylistTrackState? InitialState = null);
public record TrackRemovedEvent(string TrackGlobalId);
public record TrackMovedEvent(string TrackGlobalId, Guid OldProjectId, Guid NewProjectId);
public record TrackStateChangedEvent(string TrackGlobalId, Guid ProjectId, PlaylistTrackState State, DownloadFailureReason FailureReason = DownloadFailureReason.None, string? Error = null, SearchAttemptLog? SearchLog = null, string? PeerName = null);
// Phase 2.5: Enhanced with byte-level progress tracking
public record TrackProgressChangedEvent(string TrackGlobalId, double Progress, long BytesReceived, long TotalBytes);
public record TrackMetadataUpdatedEvent(string TrackGlobalId);
public record ForceStartRequestEvent(string TrackGlobalId);
public record BumpToTopRequestEvent(string TrackGlobalId); // [NEW] Overhaul Phase

// Audio Analysis Pipeline (Phase 3 Integration + Phase 1 Progress)
public record TrackAnalysisCompletedEvent(string TrackGlobalId, bool Success, string? ErrorMessage = null) { public Guid? DatabaseId { get; init; } }
public record TrackAnalysisStartedEvent(string TrackGlobalId, string FileName) { public Guid? DatabaseId { get; init; } }

// Phase 24: Stem Workspace Communication
public record OpenStemWorkspaceRequestEvent(string TrackGlobalId);
public record AnalysisProgressEvent(string TrackGlobalId, string CurrentStep, int ProgressPercent, float BpmConfidence = 0, float KeyConfidence = 0, float IntegrityScore = 0);
public record TrackAnalysisFailedEvent(string TrackGlobalId, string Error);
public record TrackAnalysisRequestedEvent(string TrackGlobalId, AnalysisTier Tier = AnalysisTier.Tier1); // New Trigger Event
public record StemSeparationRequestedEvent(string TrackGlobalId, string FilePath);

// Analysis Queue Visibility (Glass Box Architecture)
public record AnalysisQueueStatusChangedEvent(
    int QueuedCount,
    int ProcessedCount,
    string? CurrentTrackHash,
    bool IsPaused,
    string PerformanceMode = "Unknown",
    int MaxConcurrency = 0
);

public record AnalysisCompletedEvent(
    string TrackHash,
    bool Success,
    string? ErrorMessage = null
);

// Phase 8: Automation & Upgrade Events
public record AutoDownloadTrackEvent(string TrackGlobalId, Track BestMatch);
public record AutoDownloadUpgradeEvent(string TrackGlobalId, Track BestMatch);
public record UpgradeAvailableEvent(string TrackGlobalId, Track BestMatch);

// Phase 2A: Crash Recovery Events
public record RecoveryCompletedEvent(
    int ResumedCount,
    int CleanedCount,
    int FailureCount,
    int DeadLetterCount,
    TimeSpan RecoveryDuration);
// Phase 10: Connectivity & Background Events
public record GlobalStatusEvent(string Message, bool IsActive, bool IsError = false);
