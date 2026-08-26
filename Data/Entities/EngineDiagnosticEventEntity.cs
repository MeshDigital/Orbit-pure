using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// One row of the "Engine Diagnostics" audit trail — structured, queryable record of what the
/// import/search pipeline did and why, so it can answer cross-track/cross-time questions like
/// "show me every search this week that found nothing" or "show me every import where the artist
/// got mis-split." Plain string EventType (not a DB-mapped enum) matches the existing
/// PlaylistActivityLogEntity.Action convention — see <see cref="Models.EngineDiagnosticEventType"/>
/// for the well-known values.
/// </summary>
[Table("EngineDiagnosticEvents")]
public class EngineDiagnosticEventEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(50)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Track content hash this event relates to, if any.</summary>
    public string? TrackHash { get; set; }

    /// <summary>Playlist this event relates to, if any (e.g. which playlist a pasted line was imported into).</summary>
    public Guid? PlaylistId { get; set; }

    /// <summary>The search query text, if this event is search-related.</summary>
    public string? Query { get; set; }

    /// <summary>One-line human-readable summary, shown directly in the diagnostics list without needing to parse DetailsJson.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Structured detail payload (candidate lists, score breakdowns, before/after import values, etc.), JSON-serialized.</summary>
    public string? DetailsJson { get; set; }
}
