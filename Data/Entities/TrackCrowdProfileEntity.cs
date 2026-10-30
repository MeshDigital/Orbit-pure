using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 1: persisted per-track crowd profile (a <c>CrowdProfileVector</c> serialized as JSON).
/// Powers set-flow analysis and bridge-discovery distance calculations beyond BPM/key.
/// </summary>
[Table("TrackCrowdProfiles")]
public class TrackCrowdProfileEntity
{
    [Key]
    public string TrackUniqueHash { get; set; } = string.Empty;

    /// <summary>Serialized CrowdProfileVector (6 dimensions × { mean, variance }).</summary>
    public string CrowdProfileJson { get; set; } = "{}";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
