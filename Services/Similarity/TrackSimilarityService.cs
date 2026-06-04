using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// A10.3 similarity core over persisted fingerprints and optional section vectors.
/// Produces whole-track, segment-level, and blended similarity with explainable tags.
///
/// A10.6 hardening: ScoreAsync results are memoized in _resultCache so repeated inspector
/// opens for the same pair (left, right, profile) return instantly from memory.
/// Call InvalidateResultCache(hash) when a fingerprint is re-analysed.
/// </summary>
public sealed class TrackSimilarityService
{
    private static readonly double WholeTrackBlendWeight = ScoringConstants.Matching.WholeTrackBlendWeight;
    private static readonly double SegmentBlendWeight = ScoringConstants.Matching.SegmentBlendWeight;

    // A10.6: result cache for ScoreAsync — keyed by (leftHash, rightHash, profile).
    // Capacity is intentionally modest: inspector pairwise use accesses a small working
    // set of recent pairs. When the cache exceeds MaxResultCacheSize entries we clear it
    // entirely — simple, safe, no partial-state risk.
    private const int MaxResultCacheSize = 256;
    private readonly ConcurrentDictionary<(string Left, string Right, TrackSimilarityProfile Profile), TrackSimilarityResult>
        _resultCache = new();

    private static readonly (PhraseType Type, double Weight)[] SegmentRoleWeights =
    [
        (PhraseType.Intro, ScoringConstants.Matching.SegmentIntroWeight),
        (PhraseType.Build, ScoringConstants.Matching.SegmentBuildWeight),
        (PhraseType.Drop, ScoringConstants.Matching.SegmentDropWeight),
        (PhraseType.Breakdown, ScoringConstants.Matching.SegmentBreakdownWeight),
        (PhraseType.Outro, ScoringConstants.Matching.SegmentOutroWeight),
    ];

    private readonly HarmonicCompatibilityService _harmonicCompatibilityService;
    private readonly TrackFingerprintStore? _fingerprintStore;
    private readonly SectionVectorService? _sectionVectorService;

    public TrackSimilarityService(HarmonicCompatibilityService harmonicCompatibilityService)
    {
        _harmonicCompatibilityService = harmonicCompatibilityService;
    }

    public TrackSimilarityService(
        HarmonicCompatibilityService harmonicCompatibilityService,
        TrackFingerprintStore fingerprintStore,
        SectionVectorService sectionVectorService)
        : this(harmonicCompatibilityService)
    {
        _fingerprintStore = fingerprintStore;
        _sectionVectorService = sectionVectorService;
    }

    public async Task<TrackSimilarityResult?> ScoreAsync(
        string leftTrackHash,
        string rightTrackHash,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        CancellationToken ct = default)
    {
        if (_fingerprintStore is null)
            throw new InvalidOperationException("TrackSimilarityService requires TrackFingerprintStore for hash-based scoring.");

        // Fast path: return cached result without any I/O or compute.
        var cacheKey = (leftTrackHash, rightTrackHash, profile);
        if (_resultCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var left = await _fingerprintStore.GetAsync(leftTrackHash, ct).ConfigureAwait(false);
        var right = await _fingerprintStore.GetAsync(rightTrackHash, ct).ConfigureAwait(false);
        if (left is null || right is null)
            return null;

        IReadOnlyList<SectionFeatureVector> leftSections = Array.Empty<SectionFeatureVector>();
        IReadOnlyList<SectionFeatureVector> rightSections = Array.Empty<SectionFeatureVector>();

        if (_sectionVectorService is not null)
        {
            leftSections = await _sectionVectorService.GetSectionsAsync(leftTrackHash, ct).ConfigureAwait(false);
            rightSections = await _sectionVectorService.GetSectionsAsync(rightTrackHash, ct).ConfigureAwait(false);
        }

        var result = Score(left, right, leftSections, rightSections, profile);

        // Populate cache, evicting all entries if the cap is reached.
        if (_resultCache.Count >= MaxResultCacheSize)
            _resultCache.Clear();
        _resultCache[cacheKey] = result;

        return result;
    }

    /// <summary>
    /// Evicts all cached ScoreAsync results involving <paramref name="trackHash"/> from
    /// either the left or right position. Call this when a fingerprint is re-analysed.
    /// </summary>
    public void InvalidateResultCache(string trackHash)
    {
        foreach (var key in _resultCache.Keys)
        {
            if (string.Equals(key.Left, trackHash, StringComparison.Ordinal) ||
                string.Equals(key.Right, trackHash, StringComparison.Ordinal))
                _resultCache.TryRemove(key, out _);
        }
    }

    public TrackSimilarityResult Score(
        TrackFingerprint left,
        TrackFingerprint right,
        IReadOnlyList<SectionFeatureVector>? leftSections = null,
        IReadOnlyList<SectionFeatureVector>? rightSections = null,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var vectorScores = new SimilarityVectorScores
        {
            Harmonic = _harmonicCompatibilityService.Score(left, right),
            Energy = ComputeEnergySimilarity(left.Energy, right.Energy),
            Rhythm = ComputeRhythmSimilarity(left.Rhythm, right.Rhythm),
            Timbre = ComputeTimbreSimilarity(left.Timbre, right.Timbre),
            Structure = ComputeStructureSimilarity(left.Structure, right.Structure),
            Mood = ComputeMoodSimilarity(left.Mood, right.Mood),
        };

        var wholeTrackSimilarity = ComputeWholeTrackSimilarity(profile, vectorScores, left, right);
        var segmentScores = ComputeSegmentScores(leftSections ?? Array.Empty<SectionFeatureVector>(), rightSections ?? Array.Empty<SectionFeatureVector>(), wholeTrackSimilarity);
        var segmentSimilarity =
            segmentScores.Intro * ScoringConstants.Matching.SegmentIntroWeight +
            segmentScores.Build * ScoringConstants.Matching.SegmentBuildWeight +
            segmentScores.Drop * ScoringConstants.Matching.SegmentDropWeight +
            segmentScores.Breakdown * ScoringConstants.Matching.SegmentBreakdownWeight +
            segmentScores.Outro * ScoringConstants.Matching.SegmentOutroWeight;
        var finalSimilarity = Math.Clamp((wholeTrackSimilarity * WholeTrackBlendWeight) + (segmentSimilarity * SegmentBlendWeight), 0.0, 1.0);

        return new TrackSimilarityResult
        {
            Profile = profile,
            WholeTrackSimilarity = wholeTrackSimilarity,
            SegmentSimilarity = segmentSimilarity,
            FinalSimilarity = finalSimilarity,
            VectorScores = vectorScores,
            SegmentScores = segmentScores,
            ReasonTags = BuildReasonTags(vectorScores, segmentScores, wholeTrackSimilarity, finalSimilarity),
        };
    }

    public async Task<TrackSimilaritySnapshot?> BuildSnapshotAsync(
        string leftTrackHash,
        string rightTrackHash,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        CancellationToken ct = default)
    {
        if (_fingerprintStore is null)
            throw new InvalidOperationException("TrackSimilarityService requires TrackFingerprintStore for hash-based scoring.");

        var left = await _fingerprintStore.GetAsync(leftTrackHash, ct).ConfigureAwait(false);
        var right = await _fingerprintStore.GetAsync(rightTrackHash, ct).ConfigureAwait(false);
        if (left is null || right is null)
            return null;

        IReadOnlyList<SectionFeatureVector> leftSections = Array.Empty<SectionFeatureVector>();
        IReadOnlyList<SectionFeatureVector> rightSections = Array.Empty<SectionFeatureVector>();

        if (_sectionVectorService is not null)
        {
            leftSections = await _sectionVectorService.GetSectionsAsync(leftTrackHash, ct).ConfigureAwait(false);
            rightSections = await _sectionVectorService.GetSectionsAsync(rightTrackHash, ct).ConfigureAwait(false);
        }

        return new TrackSimilaritySnapshot(
            left,
            right,
            leftSections,
            rightSections,
            Score(left, right, leftSections, rightSections, profile));
    }

    private static double ComputeWholeTrackSimilarity(
        TrackSimilarityProfile profile,
        SimilarityVectorScores scores,
        TrackFingerprint left,
        TrackFingerprint right)
    {
        var weights = profile switch
        {
            TrackSimilarityProfile.EnergyDrive => new Dictionary<string, double>
            {
                ["harmonic"] = ScoringConstants.Matching.EnergyDriveHarmonicWeight,
                ["energy"] = ScoringConstants.Matching.EnergyDriveEnergyWeight,
                ["rhythm"] = ScoringConstants.Matching.EnergyDriveRhythmWeight,
                ["timbre"] = ScoringConstants.Matching.EnergyDriveTimbreWeight,
                ["structure"] = ScoringConstants.Matching.EnergyDriveStructureWeight,
                ["mood"] = ScoringConstants.Matching.EnergyDriveMoodWeight,
            },
            TrackSimilarityProfile.GenreCohesion => new Dictionary<string, double>
            {
                ["harmonic"] = ScoringConstants.Matching.GenreCohesionHarmonicWeight,
                ["energy"] = ScoringConstants.Matching.GenreCohesionEnergyWeight,
                ["rhythm"] = ScoringConstants.Matching.GenreCohesionRhythmWeight,
                ["timbre"] = ScoringConstants.Matching.GenreCohesionTimbreWeight,
                ["structure"] = ScoringConstants.Matching.GenreCohesionStructureWeight,
                ["mood"] = ScoringConstants.Matching.GenreCohesionMoodWeight,
            },
            _ => new Dictionary<string, double>
            {
                ["harmonic"] = ScoringConstants.Matching.BlendSafeHarmonicWeight,
                ["energy"] = ScoringConstants.Matching.BlendSafeEnergyWeight,
                ["rhythm"] = ScoringConstants.Matching.BlendSafeRhythmWeight,
                ["timbre"] = ScoringConstants.Matching.BlendSafeTimbreWeight,
                ["structure"] = ScoringConstants.Matching.BlendSafeStructureWeight,
                ["mood"] = ScoringConstants.Matching.BlendSafeMoodWeight,
            },
        };

        var confidenceScaledWeights = new Dictionary<string, double>
        {
            ["harmonic"] = weights["harmonic"] * HarmonicConfidence(left.Harmonic, right.Harmonic),
            ["energy"] = weights["energy"] * PairConfidence(left.Energy.Confidence, right.Energy.Confidence),
            ["rhythm"] = weights["rhythm"] * PairConfidence(left.Rhythm.Confidence, right.Rhythm.Confidence),
            ["timbre"] = weights["timbre"] * PairConfidence(left.Timbre.Confidence, right.Timbre.Confidence),
            ["structure"] = weights["structure"] * PairConfidence(left.Structure.Confidence, right.Structure.Confidence),
            ["mood"] = weights["mood"] * PairConfidence(left.Mood.Confidence, right.Mood.Confidence),
        };

        var totalWeight = confidenceScaledWeights.Values.Sum();
        if (totalWeight <= 0)
            confidenceScaledWeights = weights;
        else
            confidenceScaledWeights = confidenceScaledWeights.ToDictionary(kvp => kvp.Key, kvp => kvp.Value / totalWeight);

        return Math.Clamp(
            scores.Harmonic * confidenceScaledWeights["harmonic"] +
            scores.Energy * confidenceScaledWeights["energy"] +
            scores.Rhythm * confidenceScaledWeights["rhythm"] +
            scores.Timbre * confidenceScaledWeights["timbre"] +
            scores.Structure * confidenceScaledWeights["structure"] +
            scores.Mood * confidenceScaledWeights["mood"],
            0.0,
            1.0);
    }

    private static SegmentSimilarityScores ComputeSegmentScores(
        IReadOnlyList<SectionFeatureVector> leftSections,
        IReadOnlyList<SectionFeatureVector> rightSections,
        double fallbackScore)
    {
        var scores = SegmentRoleWeights.ToDictionary(
            role => role.Type,
            role => ScoreRole(leftSections, rightSections, role.Type, fallbackScore));

        return new SegmentSimilarityScores
        {
            Intro = scores[PhraseType.Intro],
            Build = scores[PhraseType.Build],
            Drop = scores[PhraseType.Drop],
            Breakdown = scores[PhraseType.Breakdown],
            Outro = scores[PhraseType.Outro],
        };
    }

    private static double ScoreRole(
        IReadOnlyList<SectionFeatureVector> leftSections,
        IReadOnlyList<SectionFeatureVector> rightSections,
        PhraseType role,
        double fallbackScore)
    {
        var left = leftSections.Where(s => s.SectionType == role).OrderByDescending(s => s.Confidence).FirstOrDefault();
        var right = rightSections.Where(s => s.SectionType == role).OrderByDescending(s => s.Confidence).FirstOrDefault();
        if (left is null || right is null)
            return fallbackScore;

        var rawScore = Math.Clamp(1.0 - (left.DistanceTo(right) / 2.0), 0.0, 1.0);
        var confidence = PairConfidence(left.Confidence, right.Confidence);
        return Math.Clamp((rawScore * confidence) + (fallbackScore * (1.0 - confidence)), 0.0, 1.0);
    }

    private static IReadOnlyList<string> BuildReasonTags(
        SimilarityVectorScores vectorScores,
        SegmentSimilarityScores segmentScores,
        double wholeTrackSimilarity,
        double finalSimilarity)
    {
        var tags = new List<string>();

        if (vectorScores.Harmonic >= 0.85)
            tags.Add("Strong harmonic alignment");
        if (vectorScores.Rhythm >= 0.8)
            tags.Add("Rhythm and tempo sit close");
        if (vectorScores.Timbre >= 0.75 && vectorScores.Mood >= 0.75)
            tags.Add("Genre and mood stay cohesive");
        if (segmentScores.Drop >= 0.8)
            tags.Add("Drop sections align well");
        if (segmentScores.Outro >= 0.8 && segmentScores.Intro >= 0.8)
            tags.Add("Intro and outro transition cleanly");
        if (segmentScores.Drop >= 0.7 && finalSimilarity > wholeTrackSimilarity)
            tags.Add("Section fit lifts the overall match");

        if (tags.Count == 0)
            tags.Add(finalSimilarity >= 0.65 ? "Balanced multi-vector match" : "Partial match with weaker transition fit");

        return tags;
    }

    private static double ComputeEnergySimilarity(EnergyVector left, EnergyVector right)
        => AverageAbsoluteSimilarity(
            left.GlobalEnergy, right.GlobalEnergy,
            left.IntroEnergy, right.IntroEnergy,
            left.BuildEnergy, right.BuildEnergy,
            left.DropEnergy, right.DropEnergy,
            left.BreakdownEnergy, right.BreakdownEnergy,
            left.OutroEnergy, right.OutroEnergy,
            left.DropIntensity, right.DropIntensity,
            left.BreakdownDepth, right.BreakdownDepth);

    private static double ComputeRhythmSimilarity(RhythmVector left, RhythmVector right)
        => AverageAbsoluteSimilarity(
            left.TempoNormalized, right.TempoNormalized,
            left.SwingGrooveScore, right.SwingGrooveScore,
            left.BeatHistogramSignature, right.BeatHistogramSignature,
            left.PercussiveDensity, right.PercussiveDensity);

    private static double ComputeTimbreSimilarity(TimbreVector left, TimbreVector right)
        => AverageAbsoluteSimilarity(
            left.MfccTextureProxy, right.MfccTextureProxy,
            left.SpectralCentroidProfile, right.SpectralCentroidProfile,
            left.BrightnessWarmthBalance, right.BrightnessWarmthBalance,
            left.SpectralComplexity, right.SpectralComplexity);

    private static double ComputeStructureSimilarity(StructureVector left, StructureVector right)
        => AverageAbsoluteSimilarity(
            left.PhraseMapDensity, right.PhraseMapDensity,
            left.IntroLengthRatio, right.IntroLengthRatio,
            left.BuildLengthRatio, right.BuildLengthRatio,
            left.DropLengthRatio, right.DropLengthRatio,
            left.BreakdownLengthRatio, right.BreakdownLengthRatio,
            left.OutroLengthRatio, right.OutroLengthRatio,
            left.BuildUpSlope, right.BuildUpSlope);

    private static double ComputeMoodSimilarity(MoodVector left, MoodVector right)
        => AverageAbsoluteSimilarity(
            left.Danceability, right.Danceability,
            left.Aggressiveness, right.Aggressiveness,
            left.Acousticness, right.Acousticness,
            left.TonalVsPercussiveBalance, right.TonalVsPercussiveBalance);

    private static double AverageAbsoluteSimilarity(params float[] values)
    {
        if (values.Length == 0 || values.Length % 2 != 0)
            return 0.0;

        var pairCount = values.Length / 2;
        double diffSum = 0.0;

        for (var index = 0; index < values.Length; index += 2)
        {
            diffSum += Math.Abs(values[index] - values[index + 1]);
        }

        return Math.Clamp(1.0 - (diffSum / pairCount), 0.0, 1.0);
    }

    private static double PairConfidence(float left, float right)
        => Math.Clamp((left + right) * 0.5, 0.0, 1.0);

    private static double HarmonicConfidence(HarmonicVector? left, HarmonicVector? right)
    {
        if (left is null || right is null)
            return 0.0;

        return PairConfidence(left.PrimaryConfidence, right.PrimaryConfidence);
    }
}

public sealed record TrackSimilaritySnapshot(
    TrackFingerprint Left,
    TrackFingerprint Right,
    IReadOnlyList<SectionFeatureVector> LeftSections,
    IReadOnlyList<SectionFeatureVector> RightSections,
    TrackSimilarityResult Result);