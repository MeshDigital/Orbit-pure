using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>The audience/format context an <see cref="EventProfileEntity"/> describes.</summary>
public enum EventAudienceType
{
    Festival = 0,
    Club = 1,
    Warmup = 2,
    Opening = 3,
    Closing = 4,
    Radio = 5,
    Podcast = 6,
}

/// <summary>
/// Phase 1: the event context that shapes how a SetProject is built (festival vs warmup vs radio).
/// A SetProject optionally links to one EventProfile; Set Health fits against its energy curve.
/// </summary>
[Table("EventProfiles")]
public class EventProfileEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Ordered 0-100 energy targets over the timeline.</summary>
    public string? TargetEnergyCurveJson { get; set; }

    public double? BpmMin { get; set; }
    public double? BpmMax { get; set; }

    /// <summary>Preferred/acceptable genres.</summary>
    public string? GenresJson { get; set; }

    public int? DurationMinutes { get; set; }

    public EventAudienceType AudienceType { get; set; } = EventAudienceType.Club;

    /// <summary>Expected crowd vector (JSON of CrowdProfileVector).</summary>
    public string? CrowdProfileBiasJson { get; set; }
}
