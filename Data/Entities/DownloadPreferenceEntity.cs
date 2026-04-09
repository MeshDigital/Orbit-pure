using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Remembers the preferred uploader, format, and bitrate for a given track
/// so the DownloadDiscoveryService can consult it before scoring candidates.
/// One record per track hash — upserted on each successful download.
/// </summary>
public class DownloadPreferenceEntity
{
    [Key, MaxLength(128)]
    public string TrackHash { get; set; } = string.Empty;

    /// <summary>Soulseek username of the last successful uploader.</summary>
    [MaxLength(128)]
    public string? PreferredUsername { get; set; }

    /// <summary>File extension that was successfully downloaded (e.g. "flac", "mp3").</summary>
    [MaxLength(16)]
    public string? PreferredFormat { get; set; }

    /// <summary>Bitrate of the last successful download in kbps (0 = unknown).</summary>
    public int PreferredBitrate { get; set; }

    /// <summary>Full remote filename path that was last used — avoids re-scoring on re-download.</summary>
    [MaxLength(512)]
    public string? LastSuccessfulRemotePath { get; set; }

    /// <summary>UTC timestamp of the last successful download.</summary>
    public DateTime LastDownloadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How many times this preference has led to a successful download.</summary>
    public int SuccessCount { get; set; } = 1;
}
