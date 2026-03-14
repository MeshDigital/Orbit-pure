using System;

namespace SLSKDONET.Services.Models.Export;

/// <summary>
/// Represents a TRACK element in the Rekordbox XML schema.
/// </summary>
public class RekordboxTrack
{
    public int TrackID { get; set; } // Unique ID in this export session
    public string Name { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    
    /// <summary>
    /// File Type: "1" for File.
    /// </summary>
    public string Kind { get; set; } = "1";
    
    /// <summary>
    /// File Size in Bytes.
    /// </summary>
    public long Size { get; set; }
    
    /// <summary>
    /// Total duration in seconds.
    /// </summary>
    public int TotalTime { get; set; }
    
    /// <summary>
    /// File creation date (formatted YYYY-MM-DD).
    /// </summary>
    public string DateAdded { get; set; } = string.Empty;
    
    public int BitRate { get; set; }
    public int SampleRate { get; set; } = 44100;
    
    // Pro DJ Fields
    public double AverageBpm { get; set; }
    public string Tonality { get; set; } = string.Empty; // Key (Camelot)
    public string Label { get; set; } = string.Empty;
    
    /// <summary>
    /// Absolute path URI: file://localhost/C:/Music/Track.mp3
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Cue points for Rekordbox export.
    /// </summary>
    public System.Collections.Generic.List<SLSKDONET.Models.OrbitCue> CuePoints { get; set; } = new();
}
