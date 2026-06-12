using System;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Models;

// Audio Analysis Pipeline (Phase 3 Integration + Phase 1 Progress)
public record TrackAnalysisCompletedEvent(string TrackGlobalId, bool Success, string? ErrorMessage = null) { public Guid? DatabaseId { get; init; } }
public record TrackAnalysisStartedEvent(string TrackGlobalId, string FileName) { public Guid? DatabaseId { get; init; } }

/// <summary>
/// Published by <see cref="SLSKDONET.Services.AnalyzeTrackStructureJob"/> when structural analysis
/// (drop detection, phrase boundary detection, auto-cue generation) completes or fails.
/// </summary>
public record TrackStructureAnalysisCompletedEvent(
    string TrackUniqueHash,
    bool Success,
    string? ErrorMessage = null);

// Phase 24: Stem Workspace Communication
public record OpenStemWorkspaceRequestEvent(PlaylistTrack Track, string? PreferredDeck = null, bool OpenStemRack = false);
public record AnalysisProgressEvent(string TrackGlobalId, string CurrentStep, int ProgressPercent, float BpmConfidence = 0, float KeyConfidence = 0, float IntegrityScore = 0);
public record TrackAnalysisFailedEvent(string TrackGlobalId, string Error);
public record TrackAnalysisRequestedEvent(
    string TrackGlobalId,
    AnalysisTier Tier = AnalysisTier.Tier1,
    bool IsHighPriority = false);
public record TrackAnalysisUpdatedEvent(string TrackGlobalId, DateTime UpdatedAtUtc, Guid? DatabaseId = null);
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
