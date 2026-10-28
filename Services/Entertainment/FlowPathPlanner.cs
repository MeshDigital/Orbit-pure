using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Pure planning maths behind <see cref="FlowModeService"/>: given the track that is playing and the
/// tracks available to it, produces an ordered path of the best next hops — each one scored on
/// harmonic fit (Camelot wheel), tempo fit (ratio-aware, so 87 → 174 BPM counts as a match), energy
/// movement toward the requested direction, and mood/genre affinity.
///
/// Deliberately static and database-free: it runs on the UI thread inside the space between "track
/// started" and "preload the next deck", so it must be instant and deterministic (a stable tie-break
/// keeps the same plan for the same inputs instead of shuffling on every rebuild).
///
/// Every hop carries its own sub-scores and human labels, which is what the Now Playing panel shows
/// as the "why" behind a pick.
/// </summary>
public static class FlowPathPlanner
{
    /// <summary>How many hops ahead a plan looks.</summary>
    public const int DefaultMaxSteps = 5;

    /// <summary>Energy axis saturates at this distance — half the 0–1 range either way.</summary>
    private const double EnergyHalfWidth = 0.4;

    /// <summary>A track played within the last few hops is legal but not preferred, as on a mix.</summary>
    private const double RecentTrackPenalty = 0.62;

    /// <summary>Keeps one artist from owning the whole path.</summary>
    private const double SameArtistPenalty = 0.90;

    public static FlowPlanResult Plan(
        FlowCandidate? current,
        IReadOnlyList<FlowCandidate>? pool,
        FlowWeightSettings? weights = null,
        double energyNudge = 0.0,
        int maxSteps = DefaultMaxSteps,
        IReadOnlyCollection<string>? recentlyPlayed = null)
    {
        weights ??= FlowWeightSettings.SmoothBlend;
        maxSteps = Math.Clamp(maxSteps, 1, 10);
        energyNudge = Math.Clamp(energyNudge, -1.0, 1.0);

        if (current is null)
        {
            return FlowPlanResult.Empty(
                FlowPlanStatus.NoTrack,
                "Nothing is playing. Start a track and Flow will plan from it.");
        }

        if (!current.HasAnyFeature)
        {
            return FlowPlanResult.Empty(
                FlowPlanStatus.NoAnalysis,
                "This track has no BPM, key or energy yet. Analyse it and Flow can plan the set around it.");
        }

        // The playing track is never a candidate, and a repeated hash (same file twice in the queue)
        // must not take two slots in the path.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Hash };
        var candidates = (pool ?? Array.Empty<FlowCandidate>())
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Hash))
            .Where(c => seen.Add(c.Hash))
            .ToList();

        if (candidates.Count == 0)
        {
            return FlowPlanResult.Empty(
                FlowPlanStatus.NoCandidates,
                "Nothing else is queued. Queue a few more tracks and Flow will plan their order.");
        }

        var usable = candidates.Where(c => c.HasAnyFeature).ToList();
        if (usable.Count == 0)
        {
            return FlowPlanResult.Empty(
                FlowPlanStatus.NoCandidateData,
                "None of the other queued tracks are analysed yet. Analyse them and Flow will order the set.",
                candidates.Count);
        }

        var recent = new HashSet<string>(recentlyPlayed ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        bool hasStartEnergy = current.HasEnergy;
        double startEnergy = hasStartEnergy ? Math.Clamp(current.Energy, 0.0, 1.0) : 0.5;
        double goalEnergy = Math.Clamp(startEnergy + energyNudge * 0.4, 0.0, 1.0);

        var steps = new List<FlowStep>(maxSteps);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Hash };
        var anchor = current;

        for (int hop = 0; hop < maxSteps; hop++)
        {
            // The path eases from where the set is now toward where the nudge points, so a nudge
            // shapes the whole arc instead of only the very next pick.
            double stepTarget = hasStartEnergy
                ? startEnergy + (goalEnergy - startEnergy) * ((hop + 1) / (double)maxSteps)
                : 0.5;

            FlowStep? best = null;
            FlowCandidate? bestCandidate = null;
            double bestScore = double.NegativeInfinity;

            foreach (var candidate in usable)
            {
                if (used.Contains(candidate.Hash)) continue;

                var step = ScoreHop(anchor, candidate, weights, stepTarget, recent, out double rawScore);

                // Stable, content-based tie-break: identical inputs must always produce the same
                // path (a random tie-break made the panel's order flip on every rebuild).
                if (rawScore > bestScore + 1e-9 ||
                    (Math.Abs(rawScore - bestScore) <= 1e-9 && best is not null &&
                     string.CompareOrdinal(candidate.Hash, best.Hash) < 0))
                {
                    best = step;
                    bestCandidate = candidate;
                    bestScore = rawScore;
                }
            }

            if (best is null || bestCandidate is null) break;

            used.Add(best.Hash);
            steps.Add(best with { Rank = steps.Count + 1 });
            anchor = bestCandidate;
        }

        if (steps.Count == 0)
        {
            return FlowPlanResult.Empty(
                FlowPlanStatus.NoCandidates,
                "Nothing else is queued. Queue a few more tracks and Flow will plan their order.",
                candidates.Count,
                usable.Count);
        }

        return new FlowPlanResult
        {
            Status = FlowPlanStatus.Ready,
            Steps = steps,
            EvaluatedCandidates = candidates.Count,
            AnalysedCandidates = usable.Count,
        };
    }

    // ── One hop ────────────────────────────────────────────────────────────

    private static FlowStep ScoreHop(
        FlowCandidate outgoing,
        FlowCandidate incoming,
        FlowWeightSettings weights,
        double targetEnergy,
        HashSet<string> recent,
        out double score)
    {
        // Harmony — Camelot wheel distance, so 8A → 8B and 8A → 9A are recognised as usable
        // neighbours instead of being treated like a key clash.
        double keyScore;
        string keyLabel;
        if (!outgoing.HasKey || !incoming.HasKey)
        {
            keyScore = 0.5;
            keyLabel = incoming.HasKey ? "Current key unknown" : "No key";
        }
        else
        {
            double distance = PlaylistOptimizer.CamelotDistancePublic(outgoing.CamelotKey, incoming.CamelotKey);
            keyScore = Math.Clamp(1.0 - distance / 6.0, 0.0, 1.0);
            keyLabel = (int)Math.Round(distance) switch
            {
                0 => "Same key",
                1 => "Compatible (+1)",
                2 => "Energy shift (+2)",
                3 => "Tension (+3)",
                var d => $"Key clash ({d} steps)",
            };
        }

        // Tempo — TrackMatchScorer's ratio-aware matcher (1:1, 2:1, 1:2, 3:2, 2:3), so a 87 BPM
        // breakbeat is a good partner for a 174 BPM roller rather than a 100% mismatch.
        double bestBpmDiff;
        string tempoLabel;
        double tempoScore;
        if (!outgoing.HasTempo || !incoming.HasTempo)
        {
            tempoScore = 0.5;
            bestBpmDiff = 0;
            tempoLabel = incoming.HasTempo ? "Current tempo unknown" : "No tempo";
        }
        else
        {
            tempoScore = TrackMatchScorer.ComputeBeat(
                (float)outgoing.Bpm,
                (float)incoming.Bpm,
                ScoringConstants.Matching.BeatDecayBpmWidth,
                out string beatLabel,
                out bestBpmDiff,
                out _);
            tempoLabel = beatLabel;
        }

        // Energy — closeness to this hop's target on the requested arc.
        double energyScore;
        string energyLabel;
        if (!incoming.HasEnergy)
        {
            energyScore = 0.5;
            energyLabel = "No energy data";
        }
        else if (!outgoing.HasEnergy)
        {
            energyScore = 0.5;
            energyLabel = NumFormat.Ui($"Energy {incoming.Energy:0.00}");
        }
        else
        {
            energyScore = Math.Clamp(1.0 - Math.Abs(incoming.Energy - targetEnergy) / EnergyHalfWidth, 0.0, 1.0);
            double delta = incoming.Energy - outgoing.Energy;
            energyLabel = Math.Abs(delta) < 0.03
                ? NumFormat.Ui($"Holds energy ({incoming.Energy:0.00})")
                : delta > 0
                    ? NumFormat.Ui($"Lifts energy (+{delta:0.00} → {incoming.Energy:0.00})")
                    : NumFormat.Ui($"Drops energy ({delta:0.00} → {incoming.Energy:0.00})");
        }

        // Vibe — mood agreement (primary) plus genre agreement (secondary). Unknown on either side is
        // neutral rather than a penalty: unanalysed metadata must not silently blackball a track.
        bool moodComparable = !string.IsNullOrWhiteSpace(outgoing.Mood) && !string.IsNullOrWhiteSpace(incoming.Mood);
        bool moodMatch = moodComparable && string.Equals(outgoing.Mood, incoming.Mood, StringComparison.OrdinalIgnoreCase);

        bool genreComparable = !string.IsNullOrWhiteSpace(outgoing.Genre) && !string.IsNullOrWhiteSpace(incoming.Genre);
        bool genreMatch = genreComparable && string.Equals(outgoing.Genre, incoming.Genre, StringComparison.OrdinalIgnoreCase);

        double moodScore = moodComparable ? (moodMatch ? 1.0 : 0.45) : 0.5;
        double genreScore = genreComparable ? (genreMatch ? 1.0 : 0.40) : 0.5;
        double vibeScore = (moodScore * 0.65) + (genreScore * 0.35);

        string vibeLabel = moodComparable
            ? moodMatch
                ? $"Mood match ({incoming.Mood})"
                : $"Mood shift ({outgoing.Mood} → {incoming.Mood})"
            : genreComparable && !genreMatch
                ? $"Genre shift ({outgoing.Genre} → {incoming.Genre})"
                : genreComparable
                    ? "Same genre"
                    : "No mood data";

        // ── Composite ──────────────────────────────────────────────────────
        double weightSum = weights.HarmonicWeight + weights.BpmWeight + weights.EnergyWeight + weights.GenreWeight;
        if (weightSum <= 0) weightSum = 1.0;

        score = ((keyScore * weights.HarmonicWeight) +
                 (tempoScore * weights.BpmWeight) +
                 (energyScore * weights.EnergyWeight) +
                 (vibeScore * weights.GenreWeight)) / weightSum;

        bool wasRecent = recent.Contains(incoming.Hash);
        bool sameArtist = !string.IsNullOrWhiteSpace(outgoing.Artist) &&
                          string.Equals(outgoing.Artist, incoming.Artist, StringComparison.OrdinalIgnoreCase);

        if (wasRecent) score *= RecentTrackPenalty;
        if (sameArtist) score *= SameArtistPenalty;

        score = Math.Clamp(score, 0.0, 1.0);

        var reasons = new List<string>(4);
        if (keyScore >= 0.83) reasons.Add(keyScore >= 0.99 ? "same key" : "harmonic neighbour");
        else if (keyScore < 0.5) reasons.Add("key clash");
        if (tempoScore >= 0.72) reasons.Add(bestBpmDiff < 1.0 ? "same tempo" : "tight tempo");
        else if (tempoScore < 0.45) reasons.Add("tempo gap");
        // Caveats before descriptors: these are the ones the user must not miss, so they must not be
        // the ones trimmed by Take().
        if (wasRecent) reasons.Add("recently played");
        if (sameArtist) reasons.Add("same artist");
        if (incoming.HasEnergy && outgoing.HasEnergy)
        {
            double delta = incoming.Energy - outgoing.Energy;
            reasons.Add(Math.Abs(delta) < 0.03 ? "holds energy" : delta > 0 ? "lifts energy" : "drops energy");
        }
        if (moodMatch) reasons.Add("same mood");

        return new FlowStep
        {
            Hash = incoming.Hash,
            Artist = incoming.Artist,
            Title = incoming.Title,
            Score = score,
            Bpm = incoming.Bpm,
            CamelotKey = incoming.CamelotKey,
            Energy = incoming.Energy,
            KeyScore = keyScore,
            TempoScore = tempoScore,
            EnergyScore = energyScore,
            VibeScore = vibeScore,
            KeyLabel = keyLabel,
            TempoLabel = tempoLabel,
            EnergyLabel = energyLabel,
            VibeLabel = vibeLabel,
            CrossfadeSeconds = ComputeCrossfadeSeconds(bestBpmDiff, tempoScore),
            PreviousBpm = outgoing.Bpm,
            PreviousCamelotKey = outgoing.CamelotKey,
            ReasonSummary = string.Join(" · ", reasons.Take(4)),
        };
    }

    /// <summary>
    /// Longer fades where tempo does not already line up: the bed has to mask the drift. Uses the
    /// ratio-aware best tempo difference, so a double-time match still gets a short, clean fade.
    /// </summary>
    private static double ComputeCrossfadeSeconds(double bestBpmDiff, double tempoScore)
    {
        if (tempoScore <= 0) return 8.0;
        return Math.Clamp(4.0 + (bestBpmDiff / 10.0), 2.0, 12.0);
    }
}
