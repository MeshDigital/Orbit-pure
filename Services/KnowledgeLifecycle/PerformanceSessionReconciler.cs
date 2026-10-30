using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>A live audible: the DJ chose <see cref="TargetTrackId"/> after <see cref="SourceTrackId"/> on the fly.</summary>
public readonly record struct AudibleTransition(
    string SourceTrackId,
    string TargetTrackId,
    double? CrowdReaction,
    int? TransitionType);

/// <summary>
/// Phase 2 (Knowledge Lifecycle): pure plan-vs-reality reconciliation. Extracts the live audibles
/// from a recorded <see cref="PerformanceSessionEntity"/> sequence — keyed strictly on
/// <see cref="PerformanceSessionTrackEntity.AudibleChange"/> — and identifies planned tracks that
/// were skipped (cued but never brought into the master mix). See
/// DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §4.1.
/// </summary>
public static class PerformanceSessionReconciler
{
    /// <summary>
    /// Returns the audibles in the actual sequence: for each track marked <c>AudibleChange</c>,
    /// the transition from the previous actually-played track into it. Skipped/cued-but-never-played
    /// tracks are absent from the sequence and therefore produce no evidence.
    /// </summary>
    public static IReadOnlyList<AudibleTransition> ExtractAudibleTransitions(
        IReadOnlyList<PerformanceSessionTrackEntity> tracks)
    {
        if (tracks is null || tracks.Count < 2)
            return Array.Empty<AudibleTransition>();

        var sorted = tracks.OrderBy(t => t.SequenceOrder).ToList();
        var result = new List<AudibleTransition>();

        for (var i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            if (!current.AudibleChange)
                continue;

            var previous = sorted[i - 1];
            result.Add(new AudibleTransition(
                previous.TrackId,
                current.TrackId,
                current.CrowdReaction,
                current.TransitionType));
        }

        return result;
    }

    /// <summary>
    /// Planned slots whose <see cref="SetTrackEntity.Id"/> was never played (no session track
    /// points back at it via <see cref="PerformanceSessionTrackEntity.PlannedSlotId"/>). This is
    /// the "skipped N planned tracks to pivot" case — useful for negative evidence later.
    /// </summary>
    public static IReadOnlyList<string> FindSkippedPlannedTracks(
        IReadOnlyList<SetTrackEntity> planned,
        IReadOnlyList<PerformanceSessionTrackEntity> actual)
    {
        if (planned is null || planned.Count == 0)
            return Array.Empty<string>();

        var playedSlotIds = new HashSet<Guid>(
            (actual ?? Array.Empty<PerformanceSessionTrackEntity>())
                .Where(t => t.PlannedSlotId.HasValue)
                .Select(t => t.PlannedSlotId!.Value));

        return planned
            .Where(p => !playedSlotIds.Contains(p.Id))
            .Select(p => p.TrackUniqueHash)
            .ToList();
    }
}
