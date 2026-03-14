using System;
using SLSKDONET.Data;

namespace SLSKDONET.Models;

/// <summary>
/// Real-time telemetry data for active analysis tasks.
/// Fed into the Mission Control "Forensic Cockpit" UI.
/// </summary>
public class AnalysisTelemetry
{
    public Guid RunId { get; set; }
    public string TrackUniqueHash { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    
    // The "Cockpit" Gauges (0.0 - 1.0)
    public float BpmConfidence { get; set; }   
    public float KeyConfidence { get; set; }   
    public float IntegrityScore { get; set; } 
    
    // Stage Tracking
    public AnalysisStage CurrentStage { get; set; }
    public float StageProgress { get; set; } // 0-100 for current stage
    public float OverallProgress { get; set; } // 0-100 for entire run
    
    // Performance Data
    public double CpuUsageMs { get; set; }
    public string? CurrentModel { get; set; } // e.g. "EffNet-Discogs-Genre"
    public int ThreadId { get; set; }
}
