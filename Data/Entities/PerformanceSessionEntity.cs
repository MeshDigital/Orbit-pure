using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the <em>reality</em> of a played set, for reconciliation against
/// a planned <see cref="SetListEntity"/> (the plan). The delta — the live audibles — is where new
/// <see cref="TransitionEvidenceEntity"/> rows come from (see §4.1).
/// </summary>
[Table("PerformanceSessions")]
public class PerformanceSessionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SetProjectId { get; set; }     // the SetListEntity this session was based on
    public Guid? EventProfileId { get; set; }
    public string? Venue { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime EndedAtUtc { get; set; }
    public string? Notes { get; set; }

    public ICollection<PerformanceSessionTrackEntity> Tracks { get; set; } = new List<PerformanceSessionTrackEntity>();
}

/// <summary>One actually-played track within a <see cref="PerformanceSessionEntity"/>.</summary>
[Table("PerformanceSessionTracks")]
public class PerformanceSessionTrackEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }          // FK -> PerformanceSessions

    public string TrackId { get; set; } = string.Empty;   // TrackUniqueHash
    public int SequenceIndex { get; set; }

    /// <summary>Actual transition INTO this track (int over the transition-type vocabulary).</summary>
    public int? TransitionType { get; set; }

    public double? PlannedDurationSeconds { get; set; }
    public double? ActualDurationSeconds { get; set; }

    /// <summary>0-100, optional post-gig rating.</summary>
    public double? CrowdReaction { get; set; }

    /// <summary>True when this track was present in the planned SetProject.</summary>
    public bool WasPlanned { get; set; }

    public Guid? PlannedSlotId { get; set; }
    public string? DeviationReason { get; set; }   // "crowd energy dropped", "request", …
}
