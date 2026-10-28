using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Models;

/// <summary>
/// An individual analysis engine: one independently useful output of the analysis pipeline.
///
/// The pipeline used to be all-or-nothing from the user's point of view: one "Start analysis"
/// button ran every step, and a track was either "analysed" or not. That hides real states —
/// a track can have a waveform and a BPM but no key, or cues but no fingerprint — and it makes
/// a partial failure unrecoverable without redoing every expensive step.
///
/// Each flag names an output that can be proved present or absent from
/// <see cref="SLSKDONET.Data.Entities.AudioFeaturesEntity"/>, which is what
/// <see cref="SLSKDONET.Services.AudioAnalysis.AnalysisCoverageAnalyzer"/> checks.
/// Flags are combinable so a run can be requested for exactly the engines that are missing.
/// </summary>
[Flags]
public enum AnalysisEngine
{
    None = 0,

    /// <summary>Peak/RMS/band waveform blobs used by every waveform view.</summary>
    Waveform = 1 << 0,

    /// <summary>Integrated loudness (EBU R128), true peak and dynamic range.</summary>
    Loudness = 1 << 1,

    /// <summary>BPM, beat grid and downbeat phase — the grid every snap and cue depends on.</summary>
    Tempo = 1 << 2,

    /// <summary>Musical key, scale and Camelot code.</summary>
    Key = 1 << 3,

    /// <summary>Energy, valence, arousal, mood and genre distribution (includes the AI embedding).</summary>
    Mood = 1 << 4,

    /// <summary>Energy curve, phrase segments and drop timestamps — the structural read of the track.</summary>
    Structure = 1 << 5,

    /// <summary>Auto-generated cue points (DSP and/or CUE-DETR).</summary>
    Cues = 1 << 6,

    /// <summary>Audio fingerprint used for duplicate detection and similarity.</summary>
    Fingerprint = 1 << 7,

    /// <summary>Separated stem files. Produced by stem separation, not by the analysis pipeline.</summary>
    Stems = 1 << 8,

    /// <summary>Everything at once, including stems.</summary>
    All = Waveform | Loudness | Tempo | Key | Mood | Structure | Cues | Fingerprint | Stems,

    /// <summary>
    /// Everything a single in-process pipeline pass can produce. Excludes stems, which run
    /// through their own job and are requested separately.
    /// </summary>
    Pipeline = All & ~Stems,
}

/// <summary>
/// Flag helpers for <see cref="AnalysisEngine"/> so call sites read as intent
/// ("does this run include the key engine?") rather than bit arithmetic.
/// </summary>
public static class AnalysisEngineExtensions
{
    /// <summary>True when every flag in <paramref name="engine"/> is set in <paramref name="mask"/>.</summary>
    public static bool Has(this AnalysisEngine mask, AnalysisEngine engine) =>
        engine != AnalysisEngine.None && (mask & engine) == engine;

    /// <summary>True when any flag in <paramref name="engines"/> is set in <paramref name="mask"/>.</summary>
    public static bool HasAny(this AnalysisEngine mask, AnalysisEngine engines) =>
        (mask & engines) != AnalysisEngine.None;

    /// <summary>This mask without <paramref name="engine"/>, for "everything except" runs.</summary>
    public static AnalysisEngine Without(this AnalysisEngine mask, AnalysisEngine engine) =>
        mask & ~engine;
}

/// <summary>
/// What one engine is for, in the words the UI uses. Kept next to the enum so the page never
/// hard-codes a second copy of these descriptions.
/// </summary>
/// <param name="Engine">The flag this describes.</param>
/// <param name="Name">Short name, used as a row label.</param>
/// <param name="Purpose">One sentence: what this output is used for in the product.</param>
/// <param name="Produces">The concrete outputs, in the order they appear in the DB record.</param>
/// <param name="Cost">Rough per-track cost, relative to the others.</param>
/// <param name="RunsInPipeline">False for outputs produced outside the analysis pipeline (stems).</param>
public sealed record AnalysisEngineInfo(
    AnalysisEngine Engine,
    string Name,
    string Purpose,
    string Produces,
    AnalysisEngineCost Cost,
    bool RunsInPipeline = true);

/// <summary>Relative cost of an engine, so the UI can warn before an expensive run.</summary>
public enum AnalysisEngineCost
{
    /// <summary>Reads or derives from data already in memory.</summary>
    Negligible = 0,

    /// <summary>Seconds per track; the decode is shared with other steps.</summary>
    Small = 1,

    /// <summary>The dominant cost of a full pass.</summary>
    Heavy = 2,
}

/// <summary>The engine catalog in display order, with the copy the Analysis page shows.</summary>
public static class AnalysisEngines
{
    /// <summary>
    /// Display order is the order the pipeline produces them, so the coverage list reads
    /// top-to-bottom like a run does. Stems sit last because they are a separate job.
    /// </summary>
    public static IReadOnlyList<AnalysisEngineInfo> Catalog { get; } = new[]
    {
        new AnalysisEngineInfo(
            AnalysisEngine.Waveform,
            "Waveform",
            "The peak and band picture every waveform view draws.",
            "peaks, RMS, low/mid/high bands",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Loudness,
            "Loudness",
            "Integrated loudness for matching levels between tracks.",
            "LUFS, true peak, dynamic range",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Tempo,
            "Tempo & grid",
            "The beat grid every snap, cue and transition aligns to.",
            "BPM, beat grid, downbeat",
            AnalysisEngineCost.Heavy),

        new AnalysisEngineInfo(
            AnalysisEngine.Key,
            "Key",
            "Harmonic mixing: Camelot code and key compatibility.",
            "key, scale, Camelot code",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Mood,
            "Mood & genre",
            "Energy, mood and genre, plus the embedding similarity search uses.",
            "energy, valence, arousal, genre distribution, embedding",
            AnalysisEngineCost.Heavy),

        new AnalysisEngineInfo(
            AnalysisEngine.Structure,
            "Structure",
            "Where the intro, build, drop and outro are.",
            "energy curve, phrase segments, drop times",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Cues,
            "Cues",
            "Auto cue points placed on the grid and the structure.",
            "hot cues, memory cues, cue labels",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Fingerprint,
            "Fingerprint",
            "Identifies this exact recording for duplicates and similarity.",
            "audio fingerprint",
            AnalysisEngineCost.Small),

        new AnalysisEngineInfo(
            AnalysisEngine.Stems,
            "Stems",
            "Separated vocals, drums, bass and other, for the Edit Lab and deck stems.",
            "stem audio files on disk",
            AnalysisEngineCost.Heavy,
            RunsInPipeline: false),
    };

    /// <summary>Every engine the analysis pipeline itself can produce.</summary>
    public static IReadOnlyList<AnalysisEngineInfo> PipelineEngines { get; } =
        Catalog.Where(e => e.RunsInPipeline).ToArray();

    /// <summary>Looks up one engine's description.</summary>
    public static AnalysisEngineInfo Info(AnalysisEngine engine) =>
        Catalog.First(e => e.Engine == engine);

    /// <summary>Splits a mask into its individual flags, in catalog order.</summary>
    public static IReadOnlyList<AnalysisEngine> ToList(AnalysisEngine mask) =>
        Catalog.Where(e => (mask & e.Engine) != 0).Select(e => e.Engine).ToArray();

    /// <summary>
    /// "Key, Cues and Fingerprint" — for a sentence, never for a row label.
    /// Returns an empty string for <see cref="AnalysisEngine.None"/>.
    /// </summary>
    public static string Describe(AnalysisEngine mask)
    {
        var names = Catalog.Where(e => (mask & e.Engine) != 0).Select(e => e.Name).ToArray();
        return names.Length switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Length - 1))} and {names[^1]}",
        };
    }

    /// <summary>Short count label: "3 engines".</summary>
    public static string DescribeCount(AnalysisEngine mask)
    {
        var count = Catalog.Count(e => (mask & e.Engine) != 0);
        return count == 1 ? "1 engine" : $"{count} engines";
    }
}

/// <summary>
/// What one track has and what it lacks, per engine. This is the answer to "what exactly is
/// missing" for a single track, and the row the playlist summary aggregates.
/// </summary>
public sealed class TrackAnalysisCoverage
{
    public TrackAnalysisCoverage(AnalysisEngine requested, AnalysisEngine present)
    {
        Requested = requested;
        Present = present & requested;
        Missing = requested & ~present;
    }

    /// <summary>The engines that were asked about (normally <see cref="AnalysisEngine.All"/>).</summary>
    public AnalysisEngine Requested { get; }

    /// <summary>Engines whose output was found.</summary>
    public AnalysisEngine Present { get; }

    /// <summary>Engines whose output is absent.</summary>
    public AnalysisEngine Missing { get; }

    public bool IsComplete => Missing == AnalysisEngine.None;

    public bool HasAnyOutput => Present != AnalysisEngine.None;

    /// <summary>Engines in display order, for a chip row.</summary>
    public IReadOnlyList<AnalysisEngine> MissingEngines => AnalysisEngines.ToList(Missing);

    /// <summary>Engines in display order, for a chip row.</summary>
    public IReadOnlyList<AnalysisEngine> PresentEngines => AnalysisEngines.ToList(Present);

    /// <summary>"Missing Key, Cues and Fingerprint", or "Complete".</summary>
    public string MissingSummary =>
        IsComplete ? "Complete" : $"Missing {AnalysisEngines.Describe(Missing)}";

    /// <summary>How far through the requested engines this track is, 0–1.</summary>
    public double CompletionFraction
    {
        get
        {
            var total = AnalysisEngines.ToList(Requested).Count;
            if (total == 0) return 1;
            return (double)AnalysisEngines.ToList(Present).Count / total;
        }
    }

    /// <summary>True when <paramref name="engine"/> was found.</summary>
    public bool Has(AnalysisEngine engine) => (Present & engine) != 0;

    /// <summary>
    /// The pipeline mask that would fill this track's gaps. Excludes engines that cannot be
    /// run in the pipeline (stems), so the caller never queues a run that would do nothing.
    /// </summary>
    public AnalysisEngine RunnableMissing =>
        Missing & AnalysisEngine.Pipeline;
}
