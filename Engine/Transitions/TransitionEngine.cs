using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Engine.Transitions;

public sealed class TransitionSuggestion
{
    public string Description { get; set; } = string.Empty;
    public double SourceTriggerTime { get; set; }
    public double TargetTriggerTime { get; set; }
    public double CompatibilityScore { get; set; } // 0 - 100

    /// <summary>The analyzed cue point (if any) that <see cref="SourceTriggerTime"/> corresponds
    /// to — null when the trigger time is a computed/ambient position (e.g. the tempo-jump
    /// branch's VocalEndSeconds fallback) with no backing CuePointEntity. Lets a UI highlight
    /// which cue marker the suggestion picked, rather than just showing a bare timestamp.</summary>
    public CuePointEntity? SelectedSourceCue { get; set; }

    /// <summary>Same as <see cref="SelectedSourceCue"/>, for <see cref="TargetTriggerTime"/>.</summary>
    public CuePointEntity? SelectedTargetCue { get; set; }
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

        // Find standard cues. Match by CuePointType, not label substring — CueGenerationService's
        // real schema labels the approach markers "32 Beats to Drop 1"/"16 Beats to Drop 1" with
        // CuePointType.Build, and those sort chronologically BEFORE "Drop 1" itself. A
        // Label.Contains("Drop") check (as this used to be) matches the approach marker first
        // (cues arrive time-ordered from CuePointService), landing every "drop-in" transition on
        // the phrase 8 bars before the actual drop instead of the drop itself.
        // A marked outro section (Cue Forge / Flow Builder "Outro") is where the DJ mixes out.
        var mixOutCue = sourceCues.LastOrDefault(c => c.Type == CuePointType.MixOut) ??
                        sourceCues.LastOrDefault(c => c.Type == CuePointType.OutroStart) ??
                        sourceCues.FirstOrDefault(c => c.Label.Contains("Mix-Out")) ??
                        sourceCues.LastOrDefault(c => c.Type == CuePointType.Outro);
        var mixInCue = targetCues.FirstOrDefault(c => c.Type == CuePointType.MixIn) ??
                       targetCues.FirstOrDefault(c => c.Label.Contains("Mix-In")) ??
                       targetCues.FirstOrDefault(c => c.Type == CuePointType.Intro);
        var firstDropCue = targetCues.FirstOrDefault(c => c.Type == CuePointType.Drop);

        // CanonicalDuration is in milliseconds: the "30 s before the end" fallback used to come
        // out at e.g. 195000 - 30 = 194,970 s, a mix-out point playback never reaches.
        double sourceTime = mixOutCue?.TimestampInSeconds
            ?? (source.CanonicalDuration is > 0 ? source.CanonicalDuration.Value / 1000.0 : 240.0) - 30.0;
        double targetTime = mixInCue?.TimestampInSeconds ?? 15.0;

        double score = 100.0;

        if (isTempoJump)
        {
            // Suggest transition in a drum-only or ambient outro zone to hide the tempo shift
            score -= 30.0;
            double ambientOutroStart = source.VocalEndSeconds ?? sourceTime;
            
            suggestion.Description = "Tempo Jump (>6%): Blend in ambient/instrumental outro zone. ";
            suggestion.SourceTriggerTime = ambientOutroStart;
            // Ambient outro zone is a computed position (VocalEndSeconds or the standard mix-out
            // fallback), not necessarily an analyzed cue — only attribute it to mixOutCue when its
            // timestamp is genuinely what was used.
            suggestion.SelectedSourceCue = mixOutCue != null && Math.Abs(mixOutCue.TimestampInSeconds - ambientOutroStart) < 0.01 ? mixOutCue : null;
            // Never enter on the drop itself — come in 8 or 16 bars before it (the build/approach)
            // so the incoming build-up carries the blend instead of a jarring cut into the drop.
            if (firstDropCue is { } drop)
            {
                var approach = ApproachBeforeDrop(drop, targetCues, targetBpm);
                suggestion.TargetTriggerTime = approach.Time;
                suggestion.SelectedTargetCue = approach.Cue;
            }
            else
            {
                suggestion.TargetTriggerTime = targetTime;
                suggestion.SelectedTargetCue = mixInCue;
            }
        }
        else
        {
            suggestion.SourceTriggerTime = sourceTime;
            suggestion.SelectedSourceCue = mixOutCue;
            suggestion.TargetTriggerTime = targetTime;
            suggestion.SelectedTargetCue = mixInCue;
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
                // Shift target mix-in later to avoid vocal clash — onto the drop's approach, not the
                // drop itself (entering on the drop cuts into it).
                if (firstDropCue != null)
                {
                    var approach = ApproachBeforeDrop(firstDropCue, targetCues, targetBpm);
                    suggestion.TargetTriggerTime = approach.Time;
                    suggestion.SelectedTargetCue = approach.Cue;
                    suggestion.Description += "Harmonic Clash + Vocal Overlap: Shift target start to the drop approach (8/16 bars out). ";
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
    /// The "in" point for a drop-anchored transition: 8 or 16 bars before the incoming drop (its
    /// approach/build), never on the drop itself — entering exactly on a drop cuts into it.
    /// Prefers 16 bars (the longest, smoothest blend), then 8, matching the countdown layout's
    /// "[IN -16] [IN -8]" markers. Returns the analysed countdown cue when one sits on that bar so
    /// the UI can still highlight which marker it picked; otherwise the computed time.
    /// </summary>
    private static (double Time, CuePointEntity? Cue) ApproachBeforeDrop(
        CuePointEntity dropCue, List<CuePointEntity> targetCues, double targetBpm)
    {
        double bar = 4.0 * 60.0 / (targetBpm > 0 ? targetBpm : 120.0);
        double drop = dropCue.TimestampInSeconds;

        var countdowns = targetCues
            .Where(c => c.Type == CuePointType.Build && c.TimestampInSeconds < drop)
            .Select(c => (Cue: c, Bars: BarsBeforeDrop(c.Label)))
            .Where(x => x.Bars is 16 or 8)
            .ToList();

        foreach (var bars in new[] { 16, 8 })
        {
            var match = countdowns.FirstOrDefault(x => x.Bars == bars);
            if (match.Cue != null) return (match.Cue.TimestampInSeconds, match.Cue);
        }

        // No analysed marker on an 8/16-bar approach (genre template Off, or a hand-placed drop):
        // land 8 bars out, the shortest standard build.
        return (Math.Max(0, drop - 8 * bar), null);
    }

    /// <summary>Bars-before-drop a countdown label states, or null when it isn't one. Handles the
    /// current "[IN -16]" / "[IN2 -8]" and "N Bars to Drop 1" labels, plus older beat-labelled
    /// "32 Beats to Drop 1" ones (4 beats per bar).</summary>
    private static int? BarsBeforeDrop(string? label)
    {
        if (DropCountdownCues.CountdownBars(label) is { } bars) return bars;
        var m = Regex.Match(label ?? string.Empty, @"(\d+)\s*beats?\b", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var beats) && beats > 0 ? beats / 4 : null;
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
                var mixOut = currentCues.FirstOrDefault(c => c.Label.StartsWith("Mix-Out Warning")); // may carry a " ✓AI" suffix
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
