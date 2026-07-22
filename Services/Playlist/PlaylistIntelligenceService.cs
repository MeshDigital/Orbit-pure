using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Services.Playlist;

public sealed class PlaylistIntelligenceService
{
    // A10.6 guardrails — protect the O(n²) greedy reorder from unbounded input.
    public const int MaxReorderTracks = 512;
    public const int MaxPathEdges = 1024;

    private readonly TrackFingerprintStore _fingerprintStore;
    private readonly TrackSimilarityService _trackSimilarityService;
    private readonly HarmonicCompatibilityService _harmonicCompatibilityService;
    private readonly SectionVectorService _sectionVectorService;

    public PlaylistIntelligenceService(
        TrackFingerprintStore fingerprintStore,
        TrackSimilarityService trackSimilarityService,
        HarmonicCompatibilityService harmonicCompatibilityService,
        SectionVectorService sectionVectorService)
    {
        _fingerprintStore = fingerprintStore;
        _trackSimilarityService = trackSimilarityService;
        _harmonicCompatibilityService = harmonicCompatibilityService;
        _sectionVectorService = sectionVectorService;
    }

    public async Task<IReadOnlyList<PlaylistRecommendation>> SuggestNextAsync(
        string currentTrackHash,
        IEnumerable<string> candidateHashes,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        int topK = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentTrackHash))
            return Array.Empty<PlaylistRecommendation>();

        var hashes = candidateHashes
            .Where(h => !string.IsNullOrWhiteSpace(h) && !string.Equals(h, currentTrackHash, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
            return Array.Empty<PlaylistRecommendation>();

        var allHashes = new List<string>(hashes.Count + 1) { currentTrackHash };
        allHashes.AddRange(hashes);
        var fingerprints = await LoadFingerprintLookupAsync(allHashes, ct).ConfigureAwait(false);
        if (!fingerprints.TryGetValue(currentTrackHash, out var currentFingerprint))
            return Array.Empty<PlaylistRecommendation>();

        var sectionLookup = await LoadSectionLookupAsync(allHashes, ct).ConfigureAwait(false);
        var currentSections = GetSections(sectionLookup, currentTrackHash);

        var recommendations = new List<PlaylistRecommendation>(hashes.Count);
        foreach (var candidateHash in hashes)
        {
            ct.ThrowIfCancellationRequested();
            if (!fingerprints.TryGetValue(candidateHash, out var candidateFingerprint))
                continue;

            var candidateSections = GetSections(sectionLookup, candidateHash);
            recommendations.Add(BuildSuggestNextRecommendation(currentFingerprint, candidateFingerprint, currentSections, candidateSections, profile));
        }

        return recommendations
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.SimilarityScore)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    public async Task<IReadOnlyList<PlaylistRecommendation>> InsertBetweenAsync(
        string fromTrackHash,
        string toTrackHash,
        IEnumerable<string> candidateHashes,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        int topK = 10,
        CancellationToken ct = default,
        double minConfidenceThreshold = 0.0,
        double structureSensitivity = 0.5)
    {
        if (string.IsNullOrWhiteSpace(fromTrackHash) || string.IsNullOrWhiteSpace(toTrackHash))
            return Array.Empty<PlaylistRecommendation>();

        var hashes = candidateHashes
            .Where(h => !string.IsNullOrWhiteSpace(h) &&
                        !string.Equals(h, fromTrackHash, StringComparison.Ordinal) &&
                        !string.Equals(h, toTrackHash, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
            return Array.Empty<PlaylistRecommendation>();

        var allHashes = new List<string>(hashes.Count + 2) { fromTrackHash, toTrackHash };
        allHashes.AddRange(hashes);
        var fingerprints = await LoadFingerprintLookupAsync(allHashes, ct).ConfigureAwait(false);
        if (!fingerprints.TryGetValue(fromTrackHash, out var fromFingerprint) ||
            !fingerprints.TryGetValue(toTrackHash, out var toFingerprint))
            return Array.Empty<PlaylistRecommendation>();

        var sectionLookup = await LoadSectionLookupAsync(allHashes, ct).ConfigureAwait(false);
        var fromSections = GetSections(sectionLookup, fromTrackHash);
        var toSections = GetSections(sectionLookup, toTrackHash);
        var normalizedSensitivity = Clamp01(structureSensitivity);
        var normalizedThreshold = Clamp01(minConfidenceThreshold);

        var recommendations = new List<PlaylistRecommendation>(hashes.Count);
        foreach (var candidateHash in hashes)
        {
            ct.ThrowIfCancellationRequested();
            if (!fingerprints.TryGetValue(candidateHash, out var candidateFingerprint))
                continue;

            var candidateSections = GetSections(sectionLookup, candidateHash);
            var recommendation = BuildInsertBetweenRecommendation(
                fromFingerprint,
                toFingerprint,
                candidateFingerprint,
                fromSections,
                toSections,
                candidateSections,
                profile,
                normalizedSensitivity);

            if (recommendation.Score >= normalizedThreshold)
            {
                recommendations.Add(recommendation);
            }
        }

        return recommendations
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.SimilarityScore)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    public async Task<PlaylistReorderResult> ReorderAsync(
        IEnumerable<string> trackHashes,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        EnergyCurvePattern energyCurve = EnergyCurvePattern.None,
        string? anchorTrackHash = null,
        CancellationToken ct = default)
    {
        var hashes = trackHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
            return new PlaylistReorderResult();

        if (hashes.Count > MaxReorderTracks)
            throw new ArgumentException(
                $"ReorderAsync input exceeds the {MaxReorderTracks}-track safety limit (got {hashes.Count}). " +
                "Split the set into smaller chunks before reordering.",
                nameof(trackHashes));

        var fingerprints = await LoadFingerprintLookupAsync(hashes, ct).ConfigureAwait(false);
        if (fingerprints.Count == 0)
            return new PlaylistReorderResult();

        var sectionLookup = await LoadSectionLookupAsync(fingerprints.Keys, ct).ConfigureAwait(false);
        var ordered = new List<string>(fingerprints.Count);
        var remaining = new HashSet<string>(fingerprints.Keys, StringComparer.Ordinal);

        string current;
        if (!string.IsNullOrWhiteSpace(anchorTrackHash) && remaining.Contains(anchorTrackHash))
        {
            current = anchorTrackHash;
        }
        else
        {
            current = fingerprints.Values
                .OrderBy(fp => fp.Energy.GlobalEnergy)
                .ThenBy(fp => fp.TrackUniqueHash, StringComparer.Ordinal)
                .First()
                .TrackUniqueHash;
        }

        ordered.Add(current);
        remaining.Remove(current);

        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var next = remaining
                .Select(candidateHash => BuildSuggestNextRecommendation(
                    fingerprints[current],
                    fingerprints[candidateHash],
                    GetSections(sectionLookup, current),
                    GetSections(sectionLookup, candidateHash),
                    profile))
                .OrderByDescending(r => r.Score)
                .First();

            current = next.TrackHash;
            ordered.Add(current);
            remaining.Remove(current);
        }

        ordered = ApplyEnergyCurve(ordered, fingerprints, energyCurve);

        var transitions = new List<PlaylistRecommendation>();
        for (var index = 0; index < ordered.Count - 1; index++)
        {
            transitions.Add(BuildSuggestNextRecommendation(
                fingerprints[ordered[index]],
                fingerprints[ordered[index + 1]],
                GetSections(sectionLookup, ordered[index]),
                GetSections(sectionLookup, ordered[index + 1]),
                profile));
        }

        return new PlaylistReorderResult
        {
            OrderedTrackHashes = ordered,
            TransitionRecommendations = transitions,
            AverageTransitionScore = transitions.Count == 0 ? 0 : transitions.Average(t => t.Score),
        };
    }

    public async Task<IReadOnlyDictionary<(string FromHash, string ToHash), PlaylistRecommendation>> ScorePathTransitionsAsync(
        IEnumerable<string> orderedTrackHashes,
        TrackSimilarityProfile profile = TrackSimilarityProfile.BlendSafe,
        CancellationToken ct = default)
    {
        var ordered = orderedTrackHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();
        if (ordered.Count < 2)
            return new Dictionary<(string FromHash, string ToHash), PlaylistRecommendation>();

        if (ordered.Count - 1 > MaxPathEdges)
            throw new ArgumentException(
                $"ScorePathTransitionsAsync input has {ordered.Count - 1} edges which exceeds the {MaxPathEdges}-edge safety limit. " +
                "Partition the path before scoring.",
                nameof(orderedTrackHashes));

        var fingerprints = await LoadFingerprintLookupAsync(ordered, ct).ConfigureAwait(false);
        if (fingerprints.Count < 2)
            return new Dictionary<(string FromHash, string ToHash), PlaylistRecommendation>();

        var sectionLookup = await LoadSectionLookupAsync(fingerprints.Keys, ct).ConfigureAwait(false);
        var result = new Dictionary<(string FromHash, string ToHash), PlaylistRecommendation>();

        for (var index = 0; index < ordered.Count - 1; index++)
        {
            ct.ThrowIfCancellationRequested();

            var fromHash = ordered[index];
            var toHash = ordered[index + 1];
            if (!fingerprints.TryGetValue(fromHash, out var fromFingerprint) ||
                !fingerprints.TryGetValue(toHash, out var toFingerprint))
                continue;

            var recommendation = BuildSuggestNextRecommendation(
                fromFingerprint,
                toFingerprint,
                GetSections(sectionLookup, fromHash),
                GetSections(sectionLookup, toHash),
                profile);

            result[(fromHash, toHash)] = recommendation;
        }

        return result;
    }

    private PlaylistRecommendation BuildSuggestNextRecommendation(
        TrackFingerprint current,
        TrackFingerprint candidate,
        IReadOnlyList<SectionFeatureVector> currentSections,
        IReadOnlyList<SectionFeatureVector> candidateSections,
        TrackSimilarityProfile profile)
    {
        var similarity = _trackSimilarityService.Score(current, candidate, currentSections, candidateSections, profile);
        var harmonic = _harmonicCompatibilityService.Score(current, candidate);
        var transition = ComputeTransitionFeasibility(currentSections, candidateSections);
        var score = Clamp01((similarity.FinalSimilarity * 0.50) + (harmonic * 0.30) + (transition * 0.20));

        var reasons = new List<string>(similarity.ReasonTags);
        if (transition >= 0.75)
            reasons.Add("Clean outro-to-intro transition");

        return new PlaylistRecommendation
        {
            TrackHash = candidate.TrackUniqueHash,
            Score = score,
            SimilarityScore = similarity.FinalSimilarity,
            HarmonicScore = harmonic,
            TransitionScore = transition,
            EnergyFitScore = similarity.VectorScores.Energy,
            ReasonTags = reasons,
        };
    }

    private PlaylistRecommendation BuildInsertBetweenRecommendation(
        TrackFingerprint from,
        TrackFingerprint to,
        TrackFingerprint candidate,
        IReadOnlyList<SectionFeatureVector> fromSections,
        IReadOnlyList<SectionFeatureVector> toSections,
        IReadOnlyList<SectionFeatureVector> candidateSections,
        TrackSimilarityProfile profile,
        double structureSensitivity)
    {
        var left = _trackSimilarityService.Score(from, candidate, fromSections, candidateSections, profile);
        var right = _trackSimilarityService.Score(candidate, to, candidateSections, toSections, profile);
        var harmonicFrom = _harmonicCompatibilityService.Score(from, candidate);
        var harmonicTo = _harmonicCompatibilityService.Score(candidate, to);
        var energyFit = ComputeInsertEnergyFit(from, candidate, to);
        var segmentCompatibility = ComputeSegmentCompatibilityScore(left, right);

        var leftSmoothness = ComputeTransitionSmoothnessScore(left, harmonicFrom, from, candidate, fromSections, candidateSections);
        var rightSmoothness = ComputeTransitionSmoothnessScore(right, harmonicTo, candidate, to, candidateSections, toSections);
        var smoothnessScore = (leftSmoothness + rightSmoothness) * 0.5;

        var playlistContinuity = ComputePlaylistContinuityScore(
            from,
            candidate,
            to,
            harmonicFrom,
            harmonicTo,
            energyFit,
            segmentCompatibility,
            fromSections,
            candidateSections,
            toSections);

        // Sensitivity slider behavior:
        // 0.0 -> smoother/looser with less structural dominance
        // 1.0 -> structure/segment-heavy scoring
        var smoothnessWeight = 0.70 - (0.25 * structureSensitivity);
        var segmentWeight = 0.10 + (0.25 * structureSensitivity);
        const double playlistContinuityWeight = 0.20;

        var score = Clamp01(
            (smoothnessScore * smoothnessWeight) +
            (segmentCompatibility * segmentWeight) +
            (playlistContinuity * playlistContinuityWeight));

        var reasons = new List<string>();
        reasons.AddRange(left.ReasonTags.Take(2));
        reasons.AddRange(right.ReasonTags.Take(2));
        if (energyFit >= 0.60)
            reasons.Add("Energy curve stays smooth between anchors");
        if (segmentCompatibility >= 0.72)
            reasons.Add("Intro/drop/breakdown/outro structure aligns across both transitions");
        if (playlistContinuity >= 0.72)
            reasons.Add("Maintains playlist arc without structural whiplash");

        return new PlaylistRecommendation
        {
            TrackHash = candidate.TrackUniqueHash,
            Score = score,
            SimilarityScore = (left.FinalSimilarity + right.FinalSimilarity) * 0.5,
            HarmonicScore = (harmonicFrom + harmonicTo) * 0.5,
            TransitionScore = (ComputeTransitionFeasibility(fromSections, candidateSections) + ComputeTransitionFeasibility(candidateSections, toSections)) * 0.5,
            EnergyFitScore = energyFit,
            ReasonTags = reasons.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    private static double ComputeSegmentCompatibilityScore(
        TrackSimilarityResult left,
        TrackSimilarityResult right)
    {
        var intro = (left.SegmentScores.Intro + right.SegmentScores.Intro) * 0.5;
        var build = (left.SegmentScores.Build + right.SegmentScores.Build) * 0.5;
        var drop = (left.SegmentScores.Drop + right.SegmentScores.Drop) * 0.5;
        var breakdown = (left.SegmentScores.Breakdown + right.SegmentScores.Breakdown) * 0.5;
        var outro = (left.SegmentScores.Outro + right.SegmentScores.Outro) * 0.5;

        return Clamp01(
            (intro * 0.15) +
            (build * 0.20) +
            (drop * 0.30) +
            (breakdown * 0.20) +
            (outro * 0.15));
    }

    private static double ComputeTransitionSmoothnessScore(
        TrackSimilarityResult similarity,
        double harmonic,
        TrackFingerprint from,
        TrackFingerprint to,
        IReadOnlyList<SectionFeatureVector> fromSections,
        IReadOnlyList<SectionFeatureVector> toSections)
    {
        var tempoScore = Clamp01(1.0 - Math.Abs(from.Rhythm.TempoNormalized - to.Rhythm.TempoNormalized));
        var energyScore = Clamp01(1.0 - Math.Abs(from.Energy.GlobalEnergy - to.Energy.GlobalEnergy));
        var transitionScore = ComputeTransitionFeasibility(fromSections, toSections);

        return Clamp01(
            (harmonic * 0.28) +
            (tempoScore * 0.22) +
            (energyScore * 0.20) +
            (transitionScore * 0.15) +
            (similarity.FinalSimilarity * 0.15));
    }

    private static double ComputePlaylistContinuityScore(
        TrackFingerprint from,
        TrackFingerprint candidate,
        TrackFingerprint to,
        double harmonicFrom,
        double harmonicTo,
        double energyFit,
        double segmentCompatibility,
        IReadOnlyList<SectionFeatureVector> fromSections,
        IReadOnlyList<SectionFeatureVector> candidateSections,
        IReadOnlyList<SectionFeatureVector> toSections)
    {
        var harmonicJourney = Clamp01((harmonicFrom + harmonicTo) * 0.5);

        var leftTempoDelta = candidate.Rhythm.TempoNormalized - from.Rhythm.TempoNormalized;
        var rightTempoDelta = to.Rhythm.TempoNormalized - candidate.Rhythm.TempoNormalized;
        var tempoArc = Clamp01(1.0 - Math.Abs(leftTempoDelta - rightTempoDelta));

        var leftTransition = ComputeTransitionFeasibility(fromSections, candidateSections);
        var rightTransition = ComputeTransitionFeasibility(candidateSections, toSections);
        var structuralContinuity = Clamp01(((leftTransition + rightTransition) * 0.5 + segmentCompatibility) * 0.5);

        return Clamp01(
            (energyFit * 0.35) +
            (harmonicJourney * 0.25) +
            (tempoArc * 0.20) +
            (structuralContinuity * 0.20));
    }

    private static List<string> ApplyEnergyCurve(
        List<string> ordered,
        IReadOnlyDictionary<string, TrackFingerprint> fingerprints,
        EnergyCurvePattern energyCurve)
    {
        if (energyCurve == EnergyCurvePattern.None || ordered.Count <= 2)
            return ordered;

        var tracks = ordered
            .Select(hash => (Hash: hash, Energy: fingerprints[hash].Energy.GlobalEnergy))
            .OrderBy(tuple => tuple.Energy)
            .ToList();

        return energyCurve switch
        {
            EnergyCurvePattern.Rising => tracks.Select(t => t.Hash).ToList(),
            EnergyCurvePattern.Wave => BuildWave(tracks),
            EnergyCurvePattern.Peak => BuildPeak(tracks),
            _ => ordered,
        };
    }

    private static List<string> BuildWave(List<(string Hash, float Energy)> tracks)
    {
        var mid = tracks.Count / 2;
        var rising = tracks.Take(mid + (tracks.Count % 2)).Select(t => t.Hash);
        var falling = tracks.Skip(mid + (tracks.Count % 2)).OrderByDescending(t => t.Energy).Select(t => t.Hash);
        return rising.Concat(falling).ToList();
    }

    private static List<string> BuildPeak(List<(string Hash, float Energy)> tracks)
    {
        var peakStart = tracks.Count * 2 / 3;
        return tracks.Take(peakStart)
            .Select(t => t.Hash)
            .Concat(tracks.Skip(peakStart).OrderByDescending(t => t.Energy).Select(t => t.Hash))
            .ToList();
    }

    /// <summary>
    /// Checks whether a track has an analysis fingerprint yet, so callers (e.g. Smart Insert)
    /// can tell the user "these tracks haven't been analyzed" instead of a generic
    /// "no match found" when the real reason is missing analysis data.
    /// </summary>
    public async Task<bool> HasFingerprintAsync(string trackHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return false;
        var lookup = await LoadFingerprintLookupAsync(new[] { trackHash }, ct).ConfigureAwait(false);
        return lookup.ContainsKey(trackHash);
    }

    /// <summary>Counts how many of the given tracks have an analysis fingerprint available.</summary>
    public async Task<int> CountFingerprintedAsync(IEnumerable<string> trackHashes, CancellationToken ct = default)
    {
        var hashes = trackHashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count == 0) return 0;
        var lookup = await LoadFingerprintLookupAsync(hashes, ct).ConfigureAwait(false);
        return lookup.Count;
    }

    private async Task<Dictionary<string, TrackFingerprint>> LoadFingerprintLookupAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        // A10.6: load all fingerprints concurrently — cache hits in TrackFingerprintStore
        // are lock-free, so this fan-out is safe and dramatically faster for large sets.
        var distinct = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        var bag = new ConcurrentBag<(string Hash, TrackFingerprint Fingerprint)>();

        await Task.WhenAll(distinct.Select(async hash =>
        {
            ct.ThrowIfCancellationRequested();
            var fingerprint = await _fingerprintStore.GetAsync(hash, ct).ConfigureAwait(false);
            if (fingerprint is not null)
                bag.Add((hash, fingerprint));
        })).ConfigureAwait(false);

        return bag.ToDictionary(t => t.Hash, t => t.Fingerprint, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, IReadOnlyList<SectionFeatureVector>>> LoadSectionLookupAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var distinct = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        await _sectionVectorService.PreloadAsync(distinct, ct).ConfigureAwait(false);
        return distinct.ToDictionary(hash => hash, hash => _sectionVectorService.GetCached(hash), StringComparer.Ordinal);
    }

    private static IReadOnlyList<SectionFeatureVector> GetSections(
        IReadOnlyDictionary<string, IReadOnlyList<SectionFeatureVector>> lookup,
        string trackHash)
        => lookup.TryGetValue(trackHash, out var sections) ? sections : Array.Empty<SectionFeatureVector>();

    private static double ComputeTransitionFeasibility(
        IReadOnlyList<SectionFeatureVector> fromSections,
        IReadOnlyList<SectionFeatureVector> toSections)
    {
        var outro = fromSections.Where(s => s.SectionType == PhraseType.Outro).OrderByDescending(s => s.Confidence).FirstOrDefault();
        var intro = toSections.Where(s => s.SectionType == PhraseType.Intro).OrderByDescending(s => s.Confidence).FirstOrDefault();
        if (outro is null || intro is null)
            return 0.5;

        return outro.TransitionScore(intro);
    }

    private static double ComputeInsertEnergyFit(TrackFingerprint from, TrackFingerprint candidate, TrackFingerprint to)
    {
        var directGap = Math.Abs(from.Energy.GlobalEnergy - to.Energy.GlobalEnergy);
        var splitGap = Math.Abs(from.Energy.GlobalEnergy - candidate.Energy.GlobalEnergy) +
                       Math.Abs(candidate.Energy.GlobalEnergy - to.Energy.GlobalEnergy);

        var smoothness = directGap <= 0.001
            ? 1.0 - Math.Abs(candidate.Energy.GlobalEnergy - from.Energy.GlobalEnergy)
            : 1.0 - Math.Clamp((splitGap - directGap + 1.0) * 0.5, 0.0, 1.0);

        var bounded = from.Energy.GlobalEnergy <= to.Energy.GlobalEnergy
            ? candidate.Energy.GlobalEnergy >= from.Energy.GlobalEnergy && candidate.Energy.GlobalEnergy <= to.Energy.GlobalEnergy
            : candidate.Energy.GlobalEnergy <= from.Energy.GlobalEnergy && candidate.Energy.GlobalEnergy >= to.Energy.GlobalEnergy;

        return Clamp01((smoothness * 0.7) + (bounded ? 0.3 : 0.0));
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}