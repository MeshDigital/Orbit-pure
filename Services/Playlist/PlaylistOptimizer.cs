using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Playlist;

/// <summary>
/// Result returned by <see cref="PlaylistOptimizer.OptimizeAsync"/>.
/// </summary>
public sealed class PlaylistOptimizationResult
{
    /// <summary>Track hashes in the recommended play order.</summary>
    public IReadOnlyList<string> OrderedHashes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Total edge-cost of the optimized path (lower = better harmonic/energy coherence).
    /// Useful for comparing two orderings of the same track set.
    /// </summary>
    public double TotalCost { get; init; }

    /// <summary>
    /// Number of tracks that had no audio features stored and were appended at the end.
    /// </summary>
    public int UnanalyzedTrackCount { get; init; }
}

/// <summary>
/// Graph-based playlist optimizer that orders tracks for maximum harmonic and energy coherence.
///
/// Algorithm: Greedy Nearest-Neighbour on a complete directed graph.
///   Nodes  = tracks
///   Edges  = cost(a,b) = camelotDist(a,b)*wH + bpmDiff(a,b)*wT + energyDiff(a,b)*wE + jumpPenalty
///
/// Complexity: O(n²) — ideal for playlists up to ~500 tracks without any perceptible lag.
/// For n > 500 consider chunking or replacing with 2-opt refinement in a follow-up sprint.
///
/// After the greedy pass an optional energy-curve post-pass re-sorts the track list to
/// produce Rising / Wave / Peak shapes while preserving as much harmonic continuity as possible.
/// </summary>
public sealed class PlaylistOptimizer
{
    private readonly ILogger<PlaylistOptimizer> _logger;

    public PlaylistOptimizer(ILogger<PlaylistOptimizer> logger)
    {
        _logger = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Optimizes the given set of track hashes into a DJ-friendly play order.
    /// Fetches <see cref="AudioFeaturesEntity"/> from the database for each hash.
    /// </summary>
    public async Task<PlaylistOptimizationResult> OptimizeAsync(
        IEnumerable<string> trackHashes,
        PlaylistOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PlaylistOptimizerOptions();
        var hashes = trackHashes.Distinct().ToList();

        if (hashes.Count == 0)
            return new PlaylistOptimizationResult();

        // Load features for all tracks in one query.
        Dictionary<string, AudioFeaturesEntity> features;
        using (var db = new AppDbContext())
        {
            var loaded = await db.AudioFeatures
                .Where(f => hashes.Contains(f.TrackUniqueHash))
                .ToListAsync(cancellationToken);
            features = loaded.ToDictionary(f => f.TrackUniqueHash);
        }

        // Split tracks into analyzed and unanalyzed buckets.
        var analyzed = hashes.Where(h => features.ContainsKey(h)).ToList();
        var unanalyzed = hashes.Where(h => !features.ContainsKey(h)).ToList();

        if (unanalyzed.Count > 0)
            _logger.LogWarning("[PlaylistOptimizer] {Count} track(s) have no audio features and will be appended.", unanalyzed.Count);

        var ordered = GreedyOrder(analyzed, features, options);

        // Apply optional energy-curve post-pass.
        if (options.EnergyCurve != EnergyCurvePattern.None && ordered.Count > 2)
            ordered = ApplyEnergyCurve(ordered, features, options.EnergyCurve);

        double totalCost = ComputePathCost(ordered, features, options);

        var result = ordered.Concat(unanalyzed).ToList();
        return new PlaylistOptimizationResult
        {
            OrderedHashes = result,
            TotalCost = totalCost,
            UnanalyzedTrackCount = unanalyzed.Count,
        };
    }

    // ── Greedy ordering ────────────────────────────────────────────────────

    private List<string> GreedyOrder(
        List<string> hashes,
        Dictionary<string, AudioFeaturesEntity> features,
        PlaylistOptimizerOptions options)
    {
        if (hashes.Count == 0) return hashes;

        var remaining = new HashSet<string>(hashes);
        var path = new List<string>(hashes.Count);

        // Choose the starting node.
        string current = ChooseStartNode(hashes, features, options);
        path.Add(current);
        remaining.Remove(current);

        while (remaining.Count > 0)
        {
            // Find the unvisited track with the lowest edge cost from current.
            string? best = null;
            double bestCost = double.MaxValue;

            var currentFeature = features[current];
            foreach (var candidate in remaining)
            {
                double cost = EdgeCost(currentFeature, features[candidate], options);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                }
            }

            current = best!;
            path.Add(current);
            remaining.Remove(current);
        }

        return path;
    }

    private static string ChooseStartNode(
        List<string> hashes,
        Dictionary<string, AudioFeaturesEntity> features,
        PlaylistOptimizerOptions options)
    {
        // If caller specified a start track and it exists in our set, honour it.
        if (options.StartTrackHash != null && features.ContainsKey(options.StartTrackHash))
            return options.StartTrackHash;

        // Otherwise start with the track that has the lowest average edge cost to all others
        // (the most "central" track — best DJ introduction point).
        return hashes.MinBy(h =>
        {
            var f = features[h];
            return hashes
                .Where(other => other != h)
                .Average(other => EdgeCost(f, features[other], options));
        }) ?? hashes[0];
    }

    // ── Energy curve post-pass ─────────────────────────────────────────────

    /// <summary>
    /// Re-orders the greedy path to match a desired energy shape.
    /// Strategy: bucket tracks into energy terciles (low/mid/high) then
    /// interleave according to the pattern, keeping same-bucket tracks in their
    /// greedy-derived relative order to preserve harmonic flow.
    /// </summary>
    private static List<string> ApplyEnergyCurve(
        List<string> path,
        Dictionary<string, AudioFeaturesEntity> features,
        EnergyCurvePattern pattern)
    {
        int n = path.Count;

        // Sort the path into energy buckets (1-10 scale; 0 treated as 5).
        var withEnergy = path
            .Select(h => (Hash: h, Energy: features[h].EnergyScore == 0 ? 5 : features[h].EnergyScore))
            .ToList();

        int tercile = n / 3;

        return pattern switch
        {
            EnergyCurvePattern.Rising => withEnergy
                .OrderBy(x => x.Energy)
                .Select(x => x.Hash)
                .ToList(),

            EnergyCurvePattern.Wave => BuildWave(withEnergy),

            EnergyCurvePattern.Peak => BuildPeak(withEnergy),

            _ => path,
        };
    }

    /// <summary>Builds a low → high → low wave shape via merge of sorted halves.</summary>
    private static List<string> BuildWave(List<(string Hash, int Energy)> tracks)
    {
        var ascending = tracks.OrderBy(x => x.Energy).ToList();
        int mid = ascending.Count / 2;

        // First half goes up, second half comes down.
        var rising = ascending.Take(mid + ascending.Count % 2).ToList();
        var falling = ascending.Skip(mid + ascending.Count % 2).OrderByDescending(x => x.Energy).ToList();

        return rising.Concat(falling).Select(x => x.Hash).ToList();
    }

    /// <summary>Builds a steady → spike → steady peak shape.</summary>
    private static List<string> BuildPeak(List<(string Hash, int Energy)> tracks)
    {
        int n = tracks.Count;
        int peakStart = n * 2 / 3;

        var sorted = tracks.OrderBy(x => x.Energy).ToList();

        // Low-to-mid tracks fill first 2/3, high-energy tracks fill last 1/3.
        var body = sorted.Take(peakStart).ToList();
        var spike = sorted.Skip(peakStart).OrderByDescending(x => x.Energy).ToList();

        return body.Concat(spike).Select(x => x.Hash).ToList();
    }

    // ── Cost functions ─────────────────────────────────────────────────────

    /// <summary>
    /// Directed edge cost from track A to track B.
    /// Lower = better transition.
    /// </summary>
    internal static double EdgeCost(
        AudioFeaturesEntity a,
        AudioFeaturesEntity b,
        PlaylistOptimizerOptions opts)
    {
        double harmonic = CamelotDistance(a.CamelotKey, b.CamelotKey) * opts.HarmonicWeight;

        double bpmDiff = Math.Abs(a.Bpm - b.Bpm);
        double tempo = (bpmDiff / opts.TempoBpmDivisor) * opts.TempoWeight;
        double jumpPenalty = bpmDiff > opts.MaxBpmJump ? opts.BpmJumpPenalty : 0;

        double aEnergy = a.EnergyScore == 0 ? 5 : a.EnergyScore; // default to mid if unset
        double bEnergy = b.EnergyScore == 0 ? 5 : b.EnergyScore;
        double energy = Math.Abs(aEnergy - bEnergy) * opts.EnergyWeight;

        return harmonic + tempo + jumpPenalty + energy;
    }

    private static double ComputePathCost(
        List<string> path,
        Dictionary<string, AudioFeaturesEntity> features,
        PlaylistOptimizerOptions opts)
    {
        double cost = 0;
        for (int i = 0; i < path.Count - 1; i++)
            cost += EdgeCost(features[path[i]], features[path[i + 1]], opts);
        return cost;
    }

    // ── Camelot wheel distance ─────────────────────────────────────────────

    /// <summary>
    /// Measures compatibility distance on the Camelot wheel.
    ///
    /// Camelot notation: "[1-12][A|B]" where A = minor, B = major.
    ///   0 = same key (perfect match)
    ///   1 = ±1 step same type, or same number different type (compatible)
    ///   2 = ±2 steps (energy shift)
    ///   …up to 6 (worst harmonic clash)
    ///
    /// Unknown or empty keys return a neutral penalty of 3 so they don't
    /// dominate the ordering but are mildly discouraged.
    /// </summary>
    internal static double CamelotDistance(string? keyA, string? keyB)
    {
        if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB))
            return 3.0; // neutral penalty

        if (!TryParseCamelot(keyA, out int numA, out bool isMinorA) ||
            !TryParseCamelot(keyB, out int numB, out bool isMinorB))
            return 3.0;

        // Clock-wise distance on 12-point wheel.
        int rawDiff = Math.Abs(numA - numB);
        int circleDist = Math.Min(rawDiff, 12 - rawDiff);

        // Crossing the A↔B boundary (minor↔major) costs +1.
        int typePenalty = isMinorA == isMinorB ? 0 : 1;

        return circleDist + typePenalty;
    }

    private static bool TryParseCamelot(string key, out int number, out bool isMinor)
    {
        number = 0;
        isMinor = true;

        key = key.Trim().ToUpperInvariant();
        if (key.Length < 2) return false;

        char letter = key[^1]; // last char: 'A' or 'B'
        if (letter != 'A' && letter != 'B') return false;

        isMinor = letter == 'A';
        return int.TryParse(key[..^1], out number) && number >= 1 && number <= 12;
    }
}
