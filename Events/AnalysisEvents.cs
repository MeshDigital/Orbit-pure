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
public record AnalysisProgressEvent(string TrackGlobalId, string CurrentStep, int ProgressPercent, float BpmConfidence = 0, float KeyConfidence = 0, float IntegrityScore = 0, int StageIndex = 0, int StageCount = 0);
public record TrackAnalysisFailedEvent(string TrackGlobalId, string Error);
/// <summary>
/// Requests an analysis run for one track.
/// </summary>
/// <param name="TrackGlobalId">The track's unique hash.</param>
/// <param name="Tier">Analysis tier (kept for compatibility with the tiered pipeline).</param>
/// <param name="IsHighPriority">Queues ahead of normal work.</param>
/// <param name="Engines">
/// Which engines to run. The default is every engine one pipeline pass can produce; a track that
/// is only missing its key can be queued for <see cref="AnalysisEngine.Key"/> alone.
/// </param>
public record TrackAnalysisRequestedEvent(
    string TrackGlobalId,
    AnalysisTier Tier = AnalysisTier.Tier1,
    bool IsHighPriority = false,
    AnalysisEngine Engines = AnalysisEngine.Pipeline);
public record TrackAnalysisUpdatedEvent(string TrackGlobalId, DateTime UpdatedAtUtc, Guid? DatabaseId = null);
public record StemSeparationRequestedEvent(string TrackGlobalId, string FilePath);

// Loudness backfill (LoudnessBackfillService): measures integrated LUFS for tracks analysed before
// the real loudness meter existed, without a full re-analysis.
/// <summary>Measure this one track's loudness now (the inspector's "Measure" link).</summary>
public record MeasureTrackLoudnessRequestedEvent(string TrackGlobalId);
/// <summary>A track's loudness was measured and written to every stored copy. <c>Lufs</c> is null
/// when the file decoded but has no gated loudness (silence) or when the measurement failed
/// (<c>Success</c> false, <c>Error</c> says why).</summary>
public record TrackLoudnessMeasuredEvent(string TrackGlobalId, double? Lufs, bool Success, string? Error = null);
/// <summary>Bulk backfill progress. Published after every track and once when the run ends.</summary>
public record LoudnessBackfillProgressEvent(
    int Done,
    int Total,
    int Failed,
    int Measured,
    bool IsRunning,
    bool IsWaitingForAnalysis);

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
