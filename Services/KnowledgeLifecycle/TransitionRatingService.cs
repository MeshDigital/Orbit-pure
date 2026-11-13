using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>What the DJ has said about one A→B transition (by track hash).</summary>
public sealed record TransitionRatingSummary(double? LastRating, int RatingCount, int LivePlays, bool Verified)
{
    public static TransitionRatingSummary None { get; } = new(null, 0, 0, false);

    /// <summary>"Rated 85 · 2 ratings · played live once · verified", or a not-yet-rated hint.</summary>
    public string Text
    {
        get
        {
            if (RatingCount == 0 && LivePlays == 0)
                return "Not rated yet. Your ratings teach ORBIT which transitions work.";
            var parts = new System.Collections.Generic.List<string>();
            if (LastRating is double last)
                parts.Add($"Rated {last.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
            parts.Add(CountFormat.Of(RatingCount, "rating"));
            if (LivePlays > 0)
                parts.Add(LivePlays == 1 ? "played live once" : $"played live {CountFormat.N(LivePlays)} times");
            if (Verified)
                parts.Add("verified");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Rates transitions into the knowledge graph (rebuild plan P3.3): every rating is an append-only
/// <see cref="TransitionEvidenceSource.ManualRating"/> evidence row ("worked live" adds a
/// <see cref="TransitionEvidenceSource.LivePerformance"/> row), then the edge is recomputed.
/// Keyed by track hash, so a rating made in a playlist, a set or the player is the same knowledge.
/// </summary>
public interface ITransitionRatingService
{
    Task<TransitionRatingSummary> GetAsync(string fromHash, string toHash);
    Task<TransitionRatingSummary> RateAsync(string fromHash, string toHash, double rating, bool workedLive);
}

public sealed class TransitionRatingService : ITransitionRatingService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TransitionRatingService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TransitionRatingSummary> GetAsync(string fromHash, string toHash)
    {
        if (string.IsNullOrEmpty(fromHash) || string.IsNullOrEmpty(toHash))
            return TransitionRatingSummary.None;

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var edges = await db.TransitionKnowledge
            .AsNoTracking()
            .Where(e => e.SourceTrackId == fromHash && e.TargetTrackId == toHash)
            .Select(e => new { e.Id, e.Verified })
            .ToListAsync()
            .ConfigureAwait(false);
        if (edges.Count == 0)
            return TransitionRatingSummary.None;

        var edgeIds = edges.Select(e => e.Id).ToList();
        var evidence = await db.TransitionEvidence
            .AsNoTracking()
            .Where(e => edgeIds.Contains(e.TransitionKnowledgeId)
                     && (e.Source == TransitionEvidenceSource.ManualRating || e.Source == TransitionEvidenceSource.LivePerformance))
            .Select(e => new { e.Source, e.Score, e.CreatedAt })
            .ToListAsync()
            .ConfigureAwait(false);

        var ratings = evidence.Where(e => e.Source == TransitionEvidenceSource.ManualRating).ToList();
        return new TransitionRatingSummary(
            ratings.OrderByDescending(r => r.CreatedAt).Select(r => (double?)r.Score).FirstOrDefault(),
            ratings.Count,
            evidence.Count(e => e.Source == TransitionEvidenceSource.LivePerformance),
            edges.Any(e => e.Verified));
    }

    public async Task<TransitionRatingSummary> RateAsync(string fromHash, string toHash, double rating, bool workedLive)
    {
        if (string.IsNullOrEmpty(fromHash) || string.IsNullOrEmpty(toHash))
            throw new ArgumentException("Both tracks need a library identity to be rated.");
        var score = Math.Clamp(Math.Round(rating), 0, 100);

        await using (var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            // Intent tags (D5) come later; until then a rating attaches to the Blend edge.
            var edge = await KnowledgeLifecycleRepository
                .GetOrCreateKnowledgeEdgeAsync(db, fromHash, toHash, KnowledgeTransitionType.Blend)
                .ConfigureAwait(false);
            await KnowledgeLifecycleRepository
                .AddEvidenceAsync(db, edge.Id, TransitionEvidenceSource.ManualRating, score, 0.9, null)
                .ConfigureAwait(false);
            if (workedLive)
                await KnowledgeLifecycleRepository
                    .AddEvidenceAsync(db, edge.Id, TransitionEvidenceSource.LivePerformance, score, 1.0, null)
                    .ConfigureAwait(false);
            await KnowledgeLifecycleRepository.RecomputeAsync(edge.Id, db).ConfigureAwait(false);
        }

        return await GetAsync(fromHash, toHash).ConfigureAwait(false);
    }
}
