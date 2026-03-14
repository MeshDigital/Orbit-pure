using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Tracks individual analysis runs for comprehensive audit trails and error tracking.
/// Each analysis attempt (including retries) gets a unique RunId.
/// </summary>
[Table("analysis_runs")]
public class AnalysisRunEntity
{
    [Key]
    public Guid RunId { get; set; } = Guid.NewGuid();
    
    // Track Identity
    [Required]
    [MaxLength(128)]
    public string TrackUniqueHash { get; set; } = string.Empty;
    
    [MaxLength(256)]
    public string TrackTitle { get; set; } = string.Empty; // For readability in logs
    
    [MaxLength(512)]
    public string FilePath { get; set; } = string.Empty;
    
    // Run Metadata
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
    
    // Tiered Analysis (Phase 21)
    public AnalysisTier Tier { get; set; } = AnalysisTier.Tier1;
    
    // Status Tracking
    public AnalysisRunStatus Status { get; set; } = AnalysisRunStatus.Queued;
    public int RetryAttempt { get; set; } = 0; // 0 = first attempt, 1+ = retries
    public int WorkerThreadId { get; set; } // Which worker processed this?
    
    // Error Handling
    public string? ErrorMessage { get; set; }
    public string? ErrorStackTrace { get; set; }
    
    [MaxLength(50)]
    public string? FailedStage { get; set; } // "FFmpeg", "Essentia", "Database"
    
    // Partial Success Tracking
    public bool WaveformGenerated { get; set; }
    public bool FfmpegAnalysisCompleted { get; set; }
    public bool EssentiaAnalysisCompleted { get; set; }
    public bool DatabaseSaved { get; set; }
    
    // Performance Metrics
    public long FfmpegDurationMs { get; set; }
    public long EssentiaDurationMs { get; set; }
    public long DatabaseSaveDurationMs { get; set; }
    
    // Provenance
    [MaxLength(50)]
    public string AnalysisVersion { get; set; } = string.Empty; // "Essentia-2.1-beta5"
    
    [MaxLength(50)]
    public string TriggerSource { get; set; } = string.Empty; // "AutoQueue", "ManualUser", "ReAnalyze"

    // Telemetry & Confidence (Phase 25)
    public float BpmConfidence { get; set; }   // 0.0 - 1.0
    public float KeyConfidence { get; set; }   // 0.0 - 1.0
    public float IntegrityScore { get; set; } // Forensic upscale detection result
    public AnalysisStage CurrentStage { get; set; }
}

public enum AnalysisRunStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    PartialSuccess = 3, // Some data extracted, some failed
    Failed = 4,
    Cancelled = 5
}
