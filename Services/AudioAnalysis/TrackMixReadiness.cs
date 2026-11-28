using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Per-track mix readiness snapshot shared by <see cref="PlaylistPrepService"/>, the playlist
/// readiness badge, and Workstation eligibility checks so every surface agrees on whether a track
/// is ready to mix cleanly.
/// </summary>
public sealed record TrackMixReadiness(
    bool HasBpm,
    bool HasBeatGrid,
    bool HasMixInOrIntro,
    bool HasMixOutOrOutro,
    bool HasLufs,
    bool HasWaveform,
    bool HasStructure)
{
    /// <summary>
    /// A track is ready to mix when it has a BPM, a beat grid, at least MixIn or Intro,
    /// at least MixOut or Outro, and integrated loudness (LUFS).
    /// </summary>
    public bool IsReady =>
        HasBpm && HasBeatGrid && HasMixInOrIntro && HasMixOutOrOutro && HasLufs;

    /// <summary>True when either an entry cue (MixIn/Intro) or exit cue (MixOut/Outro) is missing.</summary>
    public bool NeedsCues => !HasMixInOrIntro || !HasMixOutOrOutro;

    /// <summary>
    /// The audio-analysis engines still missing for mix readiness. Returns
    /// <see cref="AnalysisEngine.None"/> when all required audio features are already stored.
    /// </summary>
    public AnalysisEngine MissingAudioEngines
    {
        get
        {
            var missing = AnalysisEngine.None;
            if (!HasBpm || !HasBeatGrid) missing |= AnalysisEngine.Tempo;
            if (!HasLufs) missing |= AnalysisEngine.Loudness;
            if (!HasWaveform) missing |= AnalysisEngine.Waveform;
            return missing;
        }
    }

    /// <summary>Human-readable labels for each missing readiness criterion.</summary>
    public IReadOnlyList<string> MissingItems
    {
        get
        {
            if (IsReady) return Array.Empty<string>();
            var items = new List<string>(5);
            if (!HasBpm) items.Add("BPM");
            if (!HasBeatGrid) items.Add("Beat grid");
            if (!HasMixInOrIntro) items.Add("Mix-in / Intro cue");
            if (!HasMixOutOrOutro) items.Add("Mix-out / Outro cue");
            if (!HasLufs) items.Add("Loudness (LUFS)");
            return items;
        }
    }
}

/// <summary>
/// Pure, side-effect-free evaluator for <see cref="TrackMixReadiness"/>.
/// </summary>
public static class TrackMixReadinessEvaluator
{
    public static TrackMixReadiness Evaluate(
        AudioFeaturesEntity? features,
        IReadOnlyCollection<CuePointEntity>? cues)
    {
        bool hasBpm = features is not null && features.Bpm > 0;
        bool hasBeatGrid = features is not null && AnalysisCoverageAnalyzer.HasTempo(features);
        bool hasLufs = features is not null && AnalysisCoverageAnalyzer.HasLoudness(features);
        bool hasWaveform = features is not null && AnalysisCoverageAnalyzer.HasWaveform(features);
        bool hasStructure = features is not null && AnalysisCoverageAnalyzer.HasStructure(features);

        bool hasMixInOrIntro = cues is { Count: > 0 } && cues.Any(IsEntryCue);
        bool hasMixOutOrOutro = cues is { Count: > 0 } && cues.Any(IsExitCue);

        return new TrackMixReadiness(
            HasBpm: hasBpm,
            HasBeatGrid: hasBeatGrid,
            HasMixInOrIntro: hasMixInOrIntro,
            HasMixOutOrOutro: hasMixOutOrOutro,
            HasLufs: hasLufs,
            HasWaveform: hasWaveform,
            HasStructure: hasStructure);
    }

    public static bool IsEntryCue(CuePointEntity cue) => IsEntryCue(cue.Type);

    public static bool IsExitCue(CuePointEntity cue) => IsExitCue(cue.Type);

    public static bool IsEntryCue(CuePointType type) =>
        type is CuePointType.MixIn or CuePointType.Intro or CuePointType.IntroEnd;

    public static bool IsExitCue(CuePointType type) =>
        type is CuePointType.MixOut or CuePointType.Outro or CuePointType.OutroStart;
}
