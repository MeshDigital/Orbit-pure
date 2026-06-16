using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Engine.Transitions;

public sealed class TransitionSuggestion
{
    public string Description { get; set; } = string.Empty;
    public double SourceTriggerTime { get; set; }
    public double TargetTriggerTime { get; set; }
    public double CompatibilityScore { get; set; } // 0 - 100
}

/// <summary>
/// Provides cross-track transition optimization (tempo jumps, harmonic bridges, vocal overlap avoidance).
/// </summary>
public sealed class TransitionEngine
{
    /// <summary>
    /// Computes transition suggestions between a source track and a target track.
    /// </summary>
    public TransitionSuggestion OptimizeTransition(
        TrackEntity source, 
        TrackEntity target,
        List<CuePointEntity> sourceCues,
        List<CuePointEntity> targetCues)
    {
        var suggestion = new TransitionSuggestion();
        double sourceBpm = source.BPM ?? 120.0;
        double targetBpm = target.BPM ?? 120.0;

        double bpmDiffPct = Math.Abs(sourceBpm - targetBpm) / sourceBpm;
        bool isTempoJump = bpmDiffPct > 0.06;

        string sourceKey = source.MusicalKey ?? "8A";
        string targetKey = target.MusicalKey ?? "8A";
        bool keysCompatible = AreCamelotKeysCompatible(sourceKey, targetKey);

        // Find standard cues
        var mixOutCue = sourceCues.FirstOrDefault(c => c.Label.Contains("Mix-Out")) ?? 
                        sourceCues.LastOrDefault(c => c.Type == CuePointType.Outro);
        var mixInCue = targetCues.FirstOrDefault(c => c.Label.Contains("Mix-In")) ?? 
                       targetCues.FirstOrDefault(c => c.Type == CuePointType.Intro);
        var firstDropCue = targetCues.FirstOrDefault(c => c.Label.Contains("Drop"));

        double sourceTime = mixOutCue?.TimestampInSeconds ?? (source.CanonicalDuration ?? 240.0) - 30.0;
        double targetTime = mixInCue?.TimestampInSeconds ?? 15.0;

        double score = 100.0;

        if (isTempoJump)
        {
            // Suggest transition in a drum-only or ambient outro zone to hide the tempo shift
            score -= 30.0;
            double ambientOutroStart = source.VocalEndSeconds ?? sourceTime;
            
            suggestion.Description = "Tempo Jump (>6%): Blend in ambient/instrumental outro zone. ";
            suggestion.SourceTriggerTime = ambientOutroStart;
            // Suggest dropping in the next track directly at its first drop (instant drop transition)
            suggestion.TargetTriggerTime = firstDropCue?.TimestampInSeconds ?? targetTime;
        }
        else
        {
            suggestion.SourceTriggerTime = sourceTime;
            suggestion.TargetTriggerTime = targetTime;
            suggestion.Description = "Standard Transition. ";
        }

        // Harmonic compatibility adjustments
        if (!keysCompatible)
        {
            score -= 40.0;
            // Suggest shifting target cue points if vocals overlap
            bool sourceHasVocalsAtEnd = source.VocalEndSeconds.HasValue && source.VocalEndSeconds.Value > sourceTime;
            bool targetHasVocalsAtStart = target.VocalStartSeconds.HasValue && target.VocalStartSeconds.Value < targetTime + 15.0;

            if (sourceHasVocalsAtEnd && targetHasVocalsAtStart)
            {
                score -= 20.0;
                // Shift target mix-in later to avoid vocal clash
                if (firstDropCue != null)
                {
                    suggestion.TargetTriggerTime = firstDropCue.TimestampInSeconds;
                    suggestion.Description += "Harmonic Clash + Vocal Overlap: Shift target start to drop-in. ";
                }
                else
                {
                    suggestion.Description += "Harmonic Clash: Vocal overlap detected. ";
                }
            }
            else
            {
                suggestion.Description += "Harmonic Bridge: Keys not adjacent on Camelot wheel. ";
            }
        }
        else
        {
            suggestion.Description += "Harmonic match. ";
        }

        suggestion.CompatibilityScore = Math.Clamp(score, 0.0, 100.0);
        return suggestion;
    }

    /// <summary>
    /// Recomputes curation cue point positions dynamically depending on playlist transition order.
    /// </summary>
    public void AdjustPlaylistCues(List<TrackEntity> playlist, Dictionary<string, List<CuePointEntity>> trackCues)
    {
        for (int i = 0; i < playlist.Count - 1; i++)
        {
            var current = playlist[i];
            var next = playlist[i + 1];

            if (!trackCues.TryGetValue(current.GlobalId, out var currentCues) ||
                !trackCues.TryGetValue(next.GlobalId, out var nextCues))
            {
                continue;
            }

            var suggestion = OptimizeTransition(current, next, currentCues, nextCues);
            
            // If the suggestion demands shifting the mix-out/mix-in trigger points,
            // we dynamically adjust the respective CuePointEntity timestamp
            if (suggestion.CompatibilityScore < 50.0)
            {
                var mixOut = currentCues.FirstOrDefault(c => c.Label == "Mix-Out Warning");
                if (mixOut != null)
                {
                    mixOut.TimestampInSeconds = suggestion.SourceTriggerTime;
                }
            }
        }
    }

    /// <summary>
    /// Compares two Camelot keys (e.g. "8A" and "9A" or "8B") for harmonic compatibility.
    /// Compatible keys differ by at most 1 unit in number, and have the same code letter,
    /// or share the same number and swap A/B.
    /// </summary>
    public static bool AreCamelotKeysCompatible(string keyA, string keyB)
    {
        if (string.Equals(keyA, keyB, StringComparison.OrdinalIgnoreCase)) return true;

        if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB)) return false;

        // Parse key A
        if (!ParseCamelotKey(keyA, out int numA, out char codeA)) return false;
        // Parse key B
        if (!ParseCamelotKey(keyB, out int numB, out char codeB)) return false;

        if (codeA == codeB)
        {
            // Same letter (A/A or B/B) - difference must be +/- 1 (including 12 to 1 wrap)
            int diff = Math.Abs(numA - numB);
            return diff == 1 || diff == 11;
        }
        else
        {
            // Different letters (A to B) - number must be identical (relative major/minor)
            return numA == numB;
        }
    }

    private static bool ParseCamelotKey(string raw, out int number, out char code)
    {
        number = 0;
        code = ' ';
        raw = raw.Trim().ToUpperInvariant();
        if (raw.Length < 2) return false;

        char last = raw[^1];
        if (last != 'A' && last != 'B') return false;

        code = last;
        string numStr = raw[..^1];
        return int.TryParse(numStr, out number) && number >= 1 && number <= 12;
    }
}
