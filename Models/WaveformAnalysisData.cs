using System;

namespace SLSKDONET.Models;

/// <summary>
/// Container for high-fidelity waveform analysis data.
/// Stores compressed Peak and RMS values for visualization.
/// </summary>
public class WaveformAnalysisData
{
    /// <summary>
    /// Array of peak amplitude values (0-255).
    /// Represents the maximum excursion in each time window.
    /// Used for the main "white" waveform shape.
    /// </summary>
    public byte[] PeakData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Array of RMS (Root Mean Square) energy values (0-255).
    /// Represents the average energy/loudness in each time window.
    /// Used for the "blue/red" body color.
    /// </summary>
    public byte[] RmsData { get; set; } = Array.Empty<byte>();

    public byte[] LowData { get; set; } = Array.Empty<byte>();
    public byte[] MidData { get; set; } = Array.Empty<byte>();
    public byte[] HighData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The number of data points per second of audio.
    /// Default is usually 100 (10ms resolution).
    /// </summary>
    public int PointsPerSecond { get; set; } = 100;

    /// <summary>
    /// Duration of the analyzed audio in seconds.
    /// </summary>
    public double DurationSeconds { get; set; }
    
    public bool IsEmpty => PeakData.Length == 0 || RmsData.Length == 0;
}
