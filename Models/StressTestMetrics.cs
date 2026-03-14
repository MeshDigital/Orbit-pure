using System;

namespace SLSKDONET.Models;

/// <summary>
/// Metrics captured during a Cockpit UI Stress Test.
/// </summary>
public class StressTestMetrics
{
    public double AverageFps { get; set; }
    public double MinFps { get; set; }
    public double MaxFps { get; set; }
    public double MaxCpuPercent { get; set; }
    public double AvgCpuPercent { get; set; }
    public long PeakMemoryMb { get; set; }
    public TimeSpan Duration { get; set; }
    public int FrameCount { get; set; }
    public int DroppedFrames { get; set; }
    
    /// <summary>
    /// Frame time variance (standard deviation) in milliseconds.
    /// Lower is better - a steady 55 FPS is better than jittery 60 FPS.
    /// </summary>
    public double JitterMs { get; set; }
    
    /// <summary>
    /// Pass criteria: Avg FPS >= 55 AND Max CPU < 20%
    /// </summary>
    public bool Passed => AverageFps >= 55 && MaxCpuPercent < 20;
    
    /// <summary>
    /// Human-readable verdict.
    /// </summary>
    public string Verdict => Passed 
        ? $"✅ PASS: {AverageFps:F1} FPS avg, {MaxCpuPercent:F1}% CPU peak" 
        : $"❌ FAIL: {AverageFps:F1} FPS avg (min {MinFps:F1}), {MaxCpuPercent:F1}% CPU peak";
}
