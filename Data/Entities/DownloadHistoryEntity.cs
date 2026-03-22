using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Persists a single resolved download attempt to the database.
/// One record per Completed or definitively-Failed download.
/// Captures search intelligence (duration, peer candidates, outcome) and
/// transfer details (peer, filename, format, bitrate).
/// </summary>
public class DownloadHistoryEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Unique hash of the track (links to PlaylistTrack / LibraryEntry).</summary>
    [Required, MaxLength(128)]
    public string TrackHash { get; set; } = string.Empty;

    /// <summary>Human-readable artist name for quick queries without joins.</summary>
    [MaxLength(256)]
    public string Artist { get; set; } = string.Empty;

    /// <summary>Human-readable title for quick queries without joins.</summary>
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Owning playlist / project ID (nullable — track may not belong to a project).</summary>
    [MaxLength(64)]
    public string? ProjectId { get; set; }

    // ── Search telemetry ──────────────────────────────────────────

    /// <summary>Number of search attempts made (retries included).</summary>
    public int SearchAttemptCount { get; set; }

    /// <summary>When the first search began (UTC).</summary>
    public DateTime? SearchStartedAt { get; set; }

    /// <summary>When the search ended (UTC): winner selected, no-results, or timeout.</summary>
    public DateTime? SearchEndedAt { get; set; }

    /// <summary>
    /// Human-readable outcome: "Matched", "NoResults", "Mp3Fallback", "Timeout", "Unknown".
    /// </summary>
    [MaxLength(32)]
    public string SearchOutcome { get; set; } = "Unknown";

    /// <summary>True when the FLAC search failed and MP3 fallback was activated.</summary>
    public bool UsedMp3Fallback { get; set; }

    /// <summary>Number of peers whose file was accepted (Matched state).</summary>
    public int MatchedCount { get; set; }

    /// <summary>Number of peers that were queued (waiting for slot).</summary>
    public int QueuedCount { get; set; }

    /// <summary>Number of peers whose file was rejected by quality guards.</summary>
    public int FilteredCount { get; set; }

    // ── Download peer / file ──────────────────────────────────────

    /// <summary>Soulseek username of the peer we downloaded from.</summary>
    [MaxLength(128)]
    public string? PeerUsername { get; set; }

    /// <summary>Remote filename/path as reported by the peer.</summary>
    [MaxLength(512)]
    public string? DownloadedFilename { get; set; }

    /// <summary>Audio format (e.g. "flac", "mp3", "wav").</summary>
    [MaxLength(16)]
    public string? DownloadedFormat { get; set; }

    /// <summary>Bitrate in kbps as reported in the search result.</summary>
    public int? DownloadedBitrateKbps { get; set; }

    // ── Final outcome ─────────────────────────────────────────────

    /// <summary>"Completed" or "Failed".</summary>
    [MaxLength(16)]
    public string FinalState { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this record was written.</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
