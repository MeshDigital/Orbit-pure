using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Builds and caches per-section feature vectors for every analyzed track.
///
/// Each track can have up to N <see cref="SectionFeatureVector"/> entries —
/// one for each detected structural section (Intro, Build, Drop, Breakdown,
/// Outro, etc.) stored in <see cref="TrackPhraseEntity"/>.
///
/// Vectors are derived from existing DB data — no new database schema is required:
///   • Section energy, start/end times  → from <see cref="TrackPhraseEntity"/>
///   • Arousal, danceability, spectral  → from <see cref="AudioFeaturesEntity"/>
///
/// Primary consumer: <see cref="Playlist.PlaylistOptimizer"/> uses
/// <see cref="GetOutroSectionAsync"/> / <see cref="GetIntroSectionAsync"/> to add a
/// transition-quality term to greedy edge costs, i.e. "how smoothly can this track's
/// OUTRO flow into the next track's INTRO?".
///
/// Secondary consumer (future): a "Section Match" inspector panel can call
/// <see cref="GetSectionsAsync"/> to show which tracks have the most similar drop,
/// breakdown, or build section.
/// </summary>
public sealed class SectionVectorService
{
    private readonly ILogger<SectionVectorService> _logger;

    /// <summary>
    /// Multiplier applied to section Euclidean distance to convert it into
    /// <see cref="Playlist.PlaylistOptimizerOptions.SectionTransitionWeight"/>-adjusted
    /// edge cost units.  Max raw distance in 4-D feature space is sqrt(4) = 2.
    /// Default scale = 3.0 puts section mismatch on par with 1–2 Camelot steps.
    /// </summary>
    public const double DefaultTransitionCostScale = 3.0;

    // hash → ordered vector list (populated lazily per-track, never evicted until
    // InvalidateAll() is called or a specific hash is re-analysed).
    private readonly ConcurrentDictionary<string, IReadOnlyList<SectionFeatureVector>> _cache = new();

    public SectionVectorService(ILogger<SectionVectorService> logger)
    {
        _logger = logger;
    }

    // ── Public query API ───────────────────────────────────────────────────

    /// <summary>
    /// Returns all section vectors for <paramref name="trackHash"/>, ordered by
    /// <see cref="TrackPhraseEntity.OrderIndex"/>.
    /// Returns an empty list if the track has no phrase data stored.
    /// </summary>
    public async Task<IReadOnlyList<SectionFeatureVector>> GetSectionsAsync(
        string trackHash,
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(trackHash, out var cached))
            return cached;

        var vectors = await LoadFromDbAsync(trackHash, ct);
        _cache[trackHash] = vectors;
        return vectors;
    }

    /// <summary>
    /// Returns the cached section list synchronously after a <see cref="PreloadAsync"/> call.
    /// Returns an empty list if the hash was not yet loaded.
    /// </summary>
    public IReadOnlyList<SectionFeatureVector> GetCached(string trackHash)
        => _cache.TryGetValue(trackHash, out var v) ? v : Array.Empty<SectionFeatureVector>();

    /// <summary>
    /// Returns the best Intro section for this track (highest confidence).
    /// Null if no intro phrase was detected.
    /// </summary>
    public async Task<SectionFeatureVector?> GetIntroSectionAsync(
        string trackHash,
        CancellationToken ct = default)
        => (await GetSectionsAsync(trackHash, ct))
            .Where(s => s.SectionType == PhraseType.Intro)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

    /// <summary>
    /// Returns the best Outro section for this track (highest confidence).
    /// Null if no outro phrase was detected.
    /// </summary>
    public async Task<SectionFeatureVector?> GetOutroSectionAsync(
        string trackHash,
        CancellationToken ct = default)
        => (await GetSectionsAsync(trackHash, ct))
            .Where(s => s.SectionType == PhraseType.Outro)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

    /// <summary>
    /// Returns the highest-energy section (typically the Drop).
    /// </summary>
    public async Task<SectionFeatureVector?> GetPeakSectionAsync(
        string trackHash,
        CancellationToken ct = default)
        => (await GetSectionsAsync(trackHash, ct))
            .OrderByDescending(s => s.EnergyLevel)
            .FirstOrDefault();

    /// <summary>
    /// Returns the section matching <paramref name="type"/> with the highest confidence,
    /// or null if none exists for this track.
    /// </summary>
    public async Task<SectionFeatureVector?> GetSectionAsync(
        string trackHash,
        PhraseType type,
        CancellationToken ct = default)
        => (await GetSectionsAsync(trackHash, ct))
            .Where(s => s.SectionType == type)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

    public async Task<float[]?> GetIntroVectorAsync(string trackHash, CancellationToken ct = default)
        => (await GetIntroSectionAsync(trackHash, ct))?.Embedding;

    public async Task<float[]?> GetOutroVectorAsync(string trackHash, CancellationToken ct = default)
        => (await GetOutroSectionAsync(trackHash, ct))?.Embedding;

    public async Task<float[]?> GetDropVectorAsync(string trackHash, int dropIndex = 1, CancellationToken ct = default)
        => (await GetSectionsAsync(trackHash, ct))
            .Where(s => s.SectionType == PhraseType.Drop)
            .OrderByDescending(s => s.Confidence)
            .Skip(Math.Max(0, dropIndex - 1))
            .Select(s => s.Embedding)
            .FirstOrDefault(e => e is { Length: > 0 });

    // ── Transition scoring ─────────────────────────────────────────────────

    /// <summary>
    /// Computes a transition cost between the OUTRO of <paramref name="fromHash"/>
    /// and the INTRO of <paramref name="toHash"/> for use inside
    /// <see cref="Playlist.PlaylistOptimizer"/>.
    ///
    /// Returns 0.0 when either track lacks phrase data (no penalty — defers entirely
    /// to the scalar edge cost so tracks without section data are not penalised).
    ///
    /// Higher value = worse transition (energy/spectral mismatch at the join point).
    /// The caller multiplies this by
    /// <see cref="Playlist.PlaylistOptimizerOptions.SectionTransitionWeight"/>.
    /// </summary>
    public async Task<double> TransitionCostAsync(
        string fromHash,
        string toHash,
        CancellationToken ct = default)
    {
        var outro = await GetOutroSectionAsync(fromHash, ct);
        var intro = await GetIntroSectionAsync(toHash, ct);
        if (outro is null || intro is null) return 0.0;
        return outro.DistanceTo(intro);
    }

    // ── Sync helpers (post-preload) ────────────────────────────────────────

    /// <summary>
    /// Computes outro→intro transition cost entirely from the in-memory cache.
    /// Call <see cref="PreloadAsync"/> first; returns 0 if either hash is not cached.
    /// </summary>
    public double TransitionCostCached(string fromHash, string toHash)
    {
        var fromSections = GetCached(fromHash);
        var toSections   = GetCached(toHash);

        var outro = fromSections.Where(s => s.SectionType == PhraseType.Outro)
                                .OrderByDescending(s => s.Confidence)
                                .FirstOrDefault();
        var intro = toSections.Where(s => s.SectionType == PhraseType.Intro)
                               .OrderByDescending(s => s.Confidence)
                               .FirstOrDefault();

        if (outro is null || intro is null) return 0.0;
        return outro.DistanceTo(intro);
    }

    public double TransitionScoreCached(string fromHash, string toHash)
    {
        var fromSections = GetCached(fromHash);
        var toSections = GetCached(toHash);

        var outro = fromSections.Where(s => s.SectionType == PhraseType.Outro)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();
        var intro = toSections.Where(s => s.SectionType == PhraseType.Intro)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        if (outro is null || intro is null)
            return 0.5;

        return outro.TransitionScore(intro);
    }

    public double DropSimilarityCached(string fromHash, string toHash)
    {
        var fromDrop = GetCached(fromHash)
            .Where(s => s.SectionType == PhraseType.Drop)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();
        var toDrop = GetCached(toHash)
            .Where(s => s.SectionType == PhraseType.Drop)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        if (fromDrop is null || toDrop is null)
            return 0.0;

        return Math.Clamp(1.0 - (fromDrop.DistanceTo(toDrop) / 2.0), 0.0, 1.0);
    }

    // ── Bulk preload ───────────────────────────────────────────────────────

    /// <summary>
    /// Warms the cache for all given hashes in a single sweep.
    /// Call once before a greedy playlist optimisation pass to ensure
    /// <see cref="TransitionCostCached"/> returns meaningful values without
    /// async overhead inside the O(n²) inner loop.
    /// </summary>
    public async Task PreloadAsync(
        IEnumerable<string> trackHashes,
        CancellationToken ct = default)
    {
        foreach (var hash in trackHashes.Where(h => !_cache.ContainsKey(h)))
        {
            if (ct.IsCancellationRequested) break;
            await GetSectionsAsync(hash, ct);
        }
    }

    // ── Cache management ───────────────────────────────────────────────────

    /// <summary>Evicts one track so it will be reloaded from DB on next query.</summary>
    public void Invalidate(string trackHash) => _cache.TryRemove(trackHash, out _);

    /// <summary>Clears the entire cache (e.g. after a bulk re-analysis run).</summary>
    public void InvalidateAll() => _cache.Clear();

    // ── Private DB load ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SectionFeatureVector>> LoadFromDbAsync(
        string trackHash,
        CancellationToken ct)
    {
        try
        {
            using var db = new AppDbContext();

            // Load all phrase sections for this track, in temporal order.
            var phrases = await db.TrackPhrases
                .Where(p => p.TrackUniqueHash == trackHash)
                .OrderBy(p => p.OrderIndex)
                .ToListAsync(ct);

            if (phrases.Count == 0)
                return Array.Empty<SectionFeatureVector>();

            // Load aggregate features for context enrichment.
            var af = await db.AudioFeatures
                .Where(f => f.TrackUniqueHash == trackHash)
                .FirstOrDefaultAsync(ct);

            // Infer total duration from stored value or from phrase boundaries as fallback.
            float totalDuration = (af != null && af.TrackDuration > 0)
                ? (float)af.TrackDuration
                : phrases.Max(p => p.EndTimeSeconds);

            if (totalDuration <= 0f) totalDuration = 1f; // guard against div-by-zero

            // Normalise track-level scalars to [0,1].
            float arousalNorm  = af != null ? Math.Clamp(af.Arousal  / 9f,      0f, 1f) : 0.5f;
            float danceability = af != null ? Math.Clamp(af.Danceability,        0f, 1f) : 0.5f;
            // SpectralCentroid is in Hz; 20 kHz = max audible reference.
            float spectral     = af != null ? Math.Clamp(af.SpectralCentroid / 20_000f, 0f, 1f) : 0.3f;

            var vectors = phrases
                .Select(p =>
                {
                    var sectionEmbedding = TryParseEmbedding(p.SectionEmbeddingJson);
                    var fallbackEmbedding = PickTrackEmbedding(af);
                    var effectiveEmbedding = sectionEmbedding is { Length: > 0 } ? sectionEmbedding : fallbackEmbedding;

                    return new SectionFeatureVector
                    {
                        SectionType        = p.Type,
                        EnergyLevel        = Math.Clamp(p.EnergyLevel, 0f, 1f),
                        StartRatio         = Math.Clamp(p.StartTimeSeconds / totalDuration, 0f, 1f),
                        DurationRatio      = Math.Clamp(p.DurationSeconds / totalDuration, 0f, 1f),
                        Arousal            = arousalNorm,
                        Danceability       = danceability,
                        SpectralBrightness = spectral,
                        Confidence         = Math.Clamp(p.Confidence, 0f, 1f),
                        Embedding          = effectiveEmbedding,
                        EmbeddingMagnitude = p.EmbeddingMagnitude > 0 ? p.EmbeddingMagnitude : ComputeMagnitude(effectiveEmbedding),
                    };
                })
                .ToList();

            _logger.LogDebug(
                "[SectionVectorService] Loaded {Count} section(s) for {Hash}: {Types}",
                vectors.Count,
                trackHash[..Math.Min(8, trackHash.Length)],
                string.Join(", ", vectors.Select(v => v.SectionType)));

            return vectors;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SectionFeatureVector>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SectionVectorService] Failed loading sections for hash {Hash}", trackHash);
            return Array.Empty<SectionFeatureVector>();
        }
    }

    private static float[]? TryParseEmbedding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<float[]>(json);
            return parsed is { Length: > 0 } ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static float[]? PickTrackEmbedding(AudioFeaturesEntity? features)
    {
        if (features == null)
            return null;

        if (features.DeepTextureEmbedding is { Length: > 0 })
            return features.DeepTextureEmbedding;

        if (features.VectorEmbedding is { Length: > 0 })
            return features.VectorEmbedding;

        return null;
    }

    private static float ComputeMagnitude(float[]? embedding)
    {
        if (embedding is not { Length: > 0 })
            return 0f;

        double sum = 0d;
        foreach (var value in embedding)
            sum += value * value;

        return (float)Math.Sqrt(sum);
    }
}
