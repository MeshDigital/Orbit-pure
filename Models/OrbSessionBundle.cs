using System;
using System.Collections.Generic;

namespace SLSKDONET.Models;

/// <summary>
/// Top-level manifest stored inside a .orbsession ZIP bundle.
/// </summary>
public class OrbSessionManifest
{
    public string Version     { get; set; } = "1.0";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string OrbitBuild  { get; set; } = "ORBIT";
    public int TrackCount     { get; set; }
}

/// <summary>
/// Per-track metadata snapshot embedded in the bundle.
/// Allows the recipient to preview and reconstruct analysis without re-running Essentia.
/// </summary>
public class OrbSessionTrack
{
    public string? FilePath       { get; set; }
    public string? TrackUniqueHash { get; set; }
    public string? Title          { get; set; }
    public string? Artist         { get; set; }
    public string? Album          { get; set; }
    public string? Genre          { get; set; }
    public double? BPM            { get; set; }
    public string? MusicalKey     { get; set; }
    public double? Energy         { get; set; }
    public int?    ManualEnergy   { get; set; }
    public int?    DurationMs     { get; set; }
    public int?    Bitrate        { get; set; }
    public List<OrbCuePoint> CuePoints { get; set; } = [];
}

public class OrbCuePoint
{
    public double  TimestampSeconds { get; set; }
    public string  Label            { get; set; } = "";
    public string  Color            { get; set; } = "#FFFFFF";
}
