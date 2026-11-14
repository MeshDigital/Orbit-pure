using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.Engine.Transitions;

namespace SLSKDONET.Services.Sets;

/// <summary>Where a "Suggest next" entry comes from, strongest evidence first.</summary>
public enum NextSuggestionBasis
{
    /// <summary>A knowledge edge with evidence: played live (e.g. Rekordbox history) or rated.</summary>
    Played,
    /// <summary>A candidate found by the background knowledge scan.</summary>
    KnowledgeScan,
    /// <summary>No history: key, BPM and how the outro flows into the intro.</summary>
    Algorithmic,
}

/// <summary>One "Suggest next" entry: the track, a 0–100 score, the reason and its data basis.</summary>
public sealed record NextSuggestion(SetPoolTrack Track, double Score, NextSuggestionBasis Basis, string Reason)
{
    public string BasisLabel => Basis switch
    {
        NextSuggestionBasis.Played => "Your history",
        NextSuggestionBasis.KnowledgeScan => "Knowledge scan",
        _ => "Key · BPM · sections",
    };
}

/// <summary>
/// Ranks "what to play after this slot" (rebuild plan P4.2, #205) the way the plan orders the
/// evidence: transitions the DJ actually played or rated first, then knowledge-scan candidates,
/// then algorithmic matches (BPM within <see cref="BpmWindow"/>, Camelot compatibility, and the
/// outro→intro section fit the bridge finder uses). Every entry says why it's there. Pure: the
/// Sets view model gathers the inputs. Only ever suggests; inserting is the DJ's click.
/// </summary>
public static class NextTrackRanker
{
    public const double BpmWindow = 0.06;
    public const int MaxSuggestions = 10;

    /// <param name="from">The slot's track (BPM and key may be missing when unanalysed).</param>
    /// <param name="pool">Library tracks that may be suggested (already excludes the set's tracks).</param>
    /// <param name="known">Knowledge edges and scan candidates out of <paramref name="from"/>.</param>
    /// <param name="sectionFit">Outro→intro fit 0–1 per candidate hash (missing = no phrase data).</param>
    public static IReadOnlyList<NextSuggestion> Rank(
        SetPoolTrack from,
        IReadOnlyList<SetPoolTrack> pool,
        IReadOnlyList<KnownNextTransition> known,
        IReadOnlyDictionary<string, double> sectionFit)
    {
        var byHash = pool.GroupBy(t => t.TrackUniqueHash, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<NextSuggestion>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        // 1) Played / rated: the DJ's own evidence.
        foreach (var k in known.Where(k => k.FromEvidence).OrderByDescending(k => k.Score).ThenByDescending(k => k.EvidenceCount))
        {
            if (!byHash.TryGetValue(k.TargetHash, out var track) || !used.Add(k.TargetHash)) continue;
            var times = k.EvidenceCount == 1 ? "once" : $"{k.EvidenceCount.ToString(CultureInfo.InvariantCulture)} times";
            result.Add(new NextSuggestion(track, Math.Round(k.Score), NextSuggestionBasis.Played,
                k.EvidenceCount > 0 ? $"You've mixed into it {times}" + (k.Verified ? " · verified" : string.Empty) : "Verified transition"));
        }

        // 2) Knowledge-scan candidates.
        foreach (var k in known.Where(k => !k.FromEvidence).OrderByDescending(k => k.Score))
        {
            if (!byHash.TryGetValue(k.TargetHash, out var track) || !used.Add(k.TargetHash)) continue;
            result.Add(new NextSuggestion(track, Math.Round(k.Score), NextSuggestionBasis.KnowledgeScan, "Found by the knowledge scan"));
        }

        // 3) Algorithmic: needs the slot's BPM; key and sections refine it.
        if (from.Bpm is double fromBpm && fromBpm > 0)
        {
            var algorithmic = new List<NextSuggestion>();
            foreach (var track in pool)
            {
                if (used.Contains(track.TrackUniqueHash) || track.Bpm is not double bpm || bpm <= 0) continue;
                var drift = Math.Abs(bpm - fromBpm) / fromBpm;
                if (drift > BpmWindow) continue;

                var keyCompatible = from.CamelotKey is { Length: > 0 } fk && track.CamelotKey is { Length: > 0 } tk
                    ? TransitionEngine.AreCamelotKeysCompatible(fk, tk)
                    : (bool?)null;
                sectionFit.TryGetValue(track.TrackUniqueHash, out var fitValue);
                var hasFit = sectionFit.ContainsKey(track.TrackUniqueHash);

                var bpmScore = 1 - drift / BpmWindow;
                var keyScore = keyCompatible switch { true => 1.0, false => 0.2, null => 0.5 };
                var score = hasFit
                    ? 100 * (0.5 * fitValue + 0.3 * keyScore + 0.2 * bpmScore)
                    : 100 * (0.6 * keyScore + 0.4 * bpmScore) * 0.8; // no phrase data: capped below a real fit

                var parts = new List<string>
                {
                    keyCompatible switch
                    {
                        true => $"{from.CamelotKey} → {track.CamelotKey} compatible",
                        false => $"{from.CamelotKey} → {track.CamelotKey} clashes",
                        null => "key unknown",
                    },
                    $"{fromBpm.ToString("0", CultureInfo.InvariantCulture)} → {bpm.ToString("0", CultureInfo.InvariantCulture)} BPM",
                };
                if (hasFit) parts.Add($"outro→intro fit {(100 * fitValue).ToString("0", CultureInfo.InvariantCulture)}");
                algorithmic.Add(new NextSuggestion(track, Math.Round(score), NextSuggestionBasis.Algorithmic, string.Join(" · ", parts)));
            }
            result.AddRange(algorithmic.OrderByDescending(s => s.Score).ThenBy(s => s.Track.Title, StringComparer.OrdinalIgnoreCase));
        }

        return result.Take(MaxSuggestions).ToList();
    }
}
