using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): EF Core access for the knowledge graph. The DB-backed
/// <see cref="RecomputeAsync"/> is the bridge between the stateless <see cref="KnowledgeScoringService"/>
/// math and the persisted <see cref="TransitionKnowledgeEntity"/> materialized view.
/// </summary>
public static class KnowledgeLifecycleRepository
{
    /// <summary>SQL-side coarse BPM pre-filter cap, before the in-memory Camelot/gate pass.</summary>
    private const int SqlCandidateCap = 500;

    public static Task<AudioFeaturesEntity?> GetAudioFeaturesAsync(AppDbContext db, string trackHash)
        => db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash);

    /// <summary>Bounded SQL query: only tracks within the seed's BPM band (plus a hard cap).</summary>
    public static async Task<List<AudioFeaturesEntity>> GetCandidateFeaturesAsync(AppDbContext db, AudioFeaturesEntity seed)
    {
        var minBpm = (float)(seed.Bpm * (1 - TransitionCandidateFilter.DefaultBpmBand));
        var maxBpm = (float)(seed.Bpm * (1 + TransitionCandidateFilter.DefaultBpmBand));
        return await db.AudioFeatures
            .Where(f => f.TrackUniqueHash != seed.TrackUniqueHash)
            .Where(f => f.Bpm >= minBpm && f.Bpm <= maxBpm)
            .Take(SqlCandidateCap)
            .ToListAsync();
    }

    /// <summary>
    /// Loads the accrued <see cref="TransitionEvidenceEntity"/> rows for a knowledge edge, runs the
    /// <see cref="KnowledgeScoringService"/> math, and persists the materialized
    /// <see cref="TransitionKnowledgeEntity"/> view.
    /// </summary>
    public static async Task RecomputeAsync(Guid transitionId, AppDbContext db)
    {
        var knowledge = await db.TransitionKnowledge.FindAsync(transitionId).ConfigureAwait(false);
        if (knowledge is null)
            return;

        var evidence = await db.TransitionEvidence
            .Where(e => e.TransitionKnowledgeId == transitionId)
            .ToListAsync()
            .ConfigureAwait(false);

        KnowledgeScoringService.Recompute(knowledge, evidence, DateTime.UtcNow);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Insert-or-ignore a candidate keyed by its idempotency key (restart-safe dedupe).</summary>
    public static async Task UpsertCandidateAsync(AppDbContext db, TransitionCandidateEntity candidate)
    {
        var exists = await db.TransitionCandidates
            .AnyAsync(c => c.IdempotencyKey == candidate.IdempotencyKey)
            .ConfigureAwait(false);
        if (exists)
            return;

        db.TransitionCandidates.Add(candidate);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
