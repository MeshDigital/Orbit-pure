using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

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

    /// <summary>Finds or creates the knowledge edge for a (source, target, transition-type) triple.</summary>
    public static async Task<TransitionKnowledgeEntity> GetOrCreateKnowledgeEdgeAsync(
        AppDbContext db,
        string sourceTrackId,
        string targetTrackId,
        KnowledgeTransitionType type)
    {
        var existing = await db.TransitionKnowledge
            .FirstOrDefaultAsync(e =>
                e.SourceTrackId == sourceTrackId &&
                e.TargetTrackId == targetTrackId &&
                e.TransitionType == type)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var edge = new TransitionKnowledgeEntity
        {
            SourceTrackId = sourceTrackId,
            TargetTrackId = targetTrackId,
            TransitionType = type,
        };
        db.TransitionKnowledge.Add(edge);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return edge;
    }

    /// <summary>Appends an append-only evidence row and persists it.</summary>
    public static async Task AddEvidenceAsync(
        AppDbContext db,
        Guid transitionKnowledgeId,
        TransitionEvidenceSource source,
        double score,
        double confidence,
        Guid? performanceSessionId)
    {
        db.TransitionEvidence.Add(new TransitionEvidenceEntity
        {
            TransitionKnowledgeId = transitionKnowledgeId,
            Source = source,
            Score = score,
            Confidence = confidence,
            PerformanceSessionId = performanceSessionId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Gets the single DJ fingerprint row, creating it on first use.</summary>
    public static async Task<DjFingerprintEntity> GetOrCreateFingerprintAsync(AppDbContext db)
    {
        var existing = await db.DjFingerprints.FirstOrDefaultAsync().ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var fingerprint = new DjFingerprintEntity();
        db.DjFingerprints.Add(fingerprint);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return fingerprint;
    }

    /// <summary>
    /// Maps a SetProject's slots (ordered by Position) to exportable <see cref="PlaylistTrack"/>
    /// models, resolving file paths/titles via <see cref="LibraryEntryEntity"/> and annotating each
    /// slot's <c>Comments</c> with the incoming transition's recScore / confidence / freshness.
    /// </summary>
    public static async Task<List<PlaylistTrack>> BuildAnnotatedSetExportAsync(AppDbContext db, Guid setProjectId)
    {
        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId)
            .ConfigureAwait(false);
        if (set is null)
            return new List<PlaylistTrack>();

        var ordered = set.Tracks.OrderBy(t => t.Position).ToList();
        var hashes = ordered
            .Select(t => t.TrackUniqueHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .ToList();

        var entryList = await db.LibraryEntries
            .Where(e => hashes.Contains(e.UniqueHash))
            .ToListAsync()
            .ConfigureAwait(false);
        var entries = entryList.ToDictionary(e => e.UniqueHash, StringComparer.Ordinal);

        var fingerprint = await GetOrCreateFingerprintAsync(db).ConfigureAwait(false);

        var tracks = new List<PlaylistTrack>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var slot = ordered[i];
            entries.TryGetValue(slot.TrackUniqueHash, out var entry);

            var track = new PlaylistTrack
            {
                TrackUniqueHash = slot.TrackUniqueHash,
                ResolvedFilePath = entry?.FilePath ?? string.Empty,
                Title = entry?.Title ?? string.Empty,
                Artist = entry?.Artist ?? string.Empty,
                TrackNumber = i + 1,
            };

            if (i > 0)
            {
                var from = ordered[i - 1];
                var edge = await db.TransitionKnowledge
                    .FirstOrDefaultAsync(x =>
                        x.SourceTrackId == from.TrackUniqueHash &&
                        x.TargetTrackId == slot.TrackUniqueHash)
                    .ConfigureAwait(false);

                if (edge is not null)
                {
                    var djFit = DjFingerprintService.ComputeDjFit(
                        edge.TransitionType,
                        fingerprint.PreferredTransitionTypesJson,
                        fingerprint.AvoidancePatternsJson);
                    var rec = KnowledgeScoringService.ComputeRecommendationScore(edge, djFit);
                    track.Comments = $"ORBIT rec={rec:F1} conf={edge.ConfidenceLevel} fresh={edge.Freshness:F1}";
                }
            }

            tracks.Add(track);
        }

        return tracks;
    }
}
