using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Decides, per track, which analysis engines have actually produced output.
///
/// The source of truth is <see cref="AudioFeaturesEntity"/> — the record the pipeline writes.
/// Nothing here infers a value that was not stored: a field that still holds its type default
/// counts as absent, which is the whole point of this class. The Analysis page used to build a
/// row for every library track with a fabricated analysis object, so "analysed" was always true
/// and a missing key or a missing waveform could not be reported at all.
///
/// Pure and side-effect free so the predicates can be tested without a database or a file.
/// </summary>
public static class AnalysisCoverageAnalyzer
{
    /// <summary>Engines that count towards "fully analysed" by default (everything but stems).</summary>
    public const AnalysisEngine DefaultMask =
        AnalysisEngine.Waveform | AnalysisEngine.Loudness | AnalysisEngine.Tempo | AnalysisEngine.Key |
        AnalysisEngine.Mood | AnalysisEngine.Structure | AnalysisEngine.Cues | AnalysisEngine.Fingerprint;

    /// <summary>
    /// Which engines have output for this track. A null entity (no analysis row at all) has none.
    /// </summary>
    /// <param name="features">The stored analysis record, or null when the track was never analysed.</param>
    /// <param name="cuePointCount">
    /// Rows in the <c>CuePoints</c> table for this track, or a negative number when the caller did
    /// not count them. The cue engine writes rows, not the legacy JSON columns, so without this the
    /// only cues that could ever be detected are the ones an older pipeline left behind.
    /// </param>
    public static AnalysisEngine Detect(AudioFeaturesEntity? features, int cuePointCount = -1, bool hasFingerprint = false)
    {
        if (features is null) return AnalysisEngine.None;

        var present = AnalysisEngine.None;

        if (HasWaveform(features)) present |= AnalysisEngine.Waveform;
        if (HasLoudness(features)) present |= AnalysisEngine.Loudness;
        if (HasTempo(features)) present |= AnalysisEngine.Tempo;
        if (HasKey(features)) present |= AnalysisEngine.Key;
        if (HasMood(features)) present |= AnalysisEngine.Mood;
        if (HasStructure(features)) present |= AnalysisEngine.Structure;
        if (HasCues(features, cuePointCount)) present |= AnalysisEngine.Cues;
        if (HasFingerprint(features, hasFingerprint)) present |= AnalysisEngine.Fingerprint;

        return present;
    }

    /// <summary>Coverage of one track against <paramref name="mask"/> (default: everything but stems).</summary>
    public static TrackAnalysisCoverage Analyze(
        AudioFeaturesEntity? features,
        AnalysisEngine mask = DefaultMask,
        int cuePointCount = -1,
        bool hasFingerprint = false) =>
        new(mask, Detect(features, cuePointCount, hasFingerprint));

    /// <summary>
    /// Waveform blobs, or at least the sample count the extractor records with them. Either
    /// proves the extractor ran; a zero-length blob with a positive count means a truncated
    /// write, which still counts as present so the user is not asked to re-run it forever.
    /// </summary>
    public static bool HasWaveform(AudioFeaturesEntity features) =>
        features.WaveformBlob is { Length: > 0 } || features.WaveformBlobSampleCount > 0;

    /// <summary>
    /// Integrated loudness. 0 is the "not measured" sentinel (the column is NOT NULL) — a measured
    /// track is a negative LUFS value, or <see cref="IntegratedLoudnessMeter.SilentMarker"/> for
    /// digital silence, which still counts as measured.
    /// </summary>
    public static bool HasLoudness(AudioFeaturesEntity features) => features.LoudnessLUFS != 0;

    /// <summary>
    /// A BPM is not enough: snapping needs the beat grid, so the tempo engine only counts as
    /// done when the grid exists too.
    /// </summary>
    public static bool HasTempo(AudioFeaturesEntity features) =>
        features.Bpm > 0 && !IsEmptyJson(features.BeatGridJson);

    /// <summary>Key is present when either the Camelot code or a confident musical key exists.</summary>
    public static bool HasKey(AudioFeaturesEntity features) =>
        !string.IsNullOrWhiteSpace(features.CamelotKey) ||
        (!string.IsNullOrWhiteSpace(features.Key) && features.KeyConfidence > 0);

    /// <summary>
    /// Mood/genre counts as done only when something beyond the constructor defaults was written:
    /// an EnergyScore or a real genre distribution. Valence/arousal cannot be used — the entity
    /// initialises both to 0.5f, which is also a legitimate measured value, so it proves nothing.
    /// </summary>
    public static bool HasMood(AudioFeaturesEntity features) =>
        features.EnergyScore > 0 ||
        !IsEmptyJson(features.GenreDistributionJson);

    /// <summary>Structure is present when phrases, an energy curve or a drop time was written.</summary>
    public static bool HasStructure(AudioFeaturesEntity features) =>
        !IsEmptyJson(features.PhraseSegmentsJson) ||
        !IsEmptyJson(features.EnergyCurveJson) ||
        features.DropTimeSeconds is not null;

    /// <summary>
    /// Cues exist when the track has cue rows, or when a legacy pipeline left points in the JSON
    /// columns. The live pipeline persists <c>CuePoints</c> rows, so the row count is the real check.
    /// </summary>
    public static bool HasCues(AudioFeaturesEntity features, int cuePointCount = -1) =>
        cuePointCount > 0 ||
        (!IsEmptyJson(features.CuePointsJson) || !IsEmptyJson(features.CueDetrJson));

    /// <summary>
    /// A fingerprint is a non-empty string once built, or a fingerprint file written by
    /// <see cref="TrackFingerprintStore"/> — the live pipeline stores it on disk, not in the row.
    /// </summary>
    public static bool HasFingerprint(AudioFeaturesEntity features, bool hasFingerprintFile = false) =>
        hasFingerprintFile || !string.IsNullOrWhiteSpace(features.Fingerprint);

    /// <summary>
    /// Aggregates per-track coverage into "how many tracks are missing engine X".
    /// Returns one row per engine in <paramref name="mask"/>, in catalog order.
    /// </summary>
    public static IReadOnlyList<EngineCoverageSummary> Summarize(
        IEnumerable<TrackAnalysisCoverage> coverage,
        AnalysisEngine mask = DefaultMask)
    {
        var rows = new List<EngineCoverageSummary>();
        var all = new List<TrackAnalysisCoverage>(coverage);

        foreach (var engine in AnalysisEngines.ToList(mask))
        {
            var withOutput = 0;
            var missing = 0;
            foreach (var track in all)
            {
                if (track.Has(engine)) withOutput++;
                else if ((track.Requested & engine) != 0) missing++;
            }

            rows.Add(new EngineCoverageSummary(engine, all.Count, withOutput, missing));
        }

        return rows;
    }

    /// <summary>
    /// The union of what is missing across the given tracks — the mask a "run what's missing"
    /// action should queue.
    /// </summary>
    public static AnalysisEngine MissingUnion(IEnumerable<TrackAnalysisCoverage> coverage)
    {
        var missing = AnalysisEngine.None;
        foreach (var track in coverage) missing |= track.RunnableMissing;
        return missing;
    }

    /// <summary>True when the text is null, blank, "[]", "{}" or "null" — the defaults an unwritten column carries.</summary>
    internal static bool IsEmptyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;

        var trimmed = json.Trim();
        return trimmed is "[]" or "{}" or "null" or "\"\"" or "0";
    }
}

/// <summary>One row of the playlist coverage table: how many tracks have this engine's output.</summary>
/// <param name="Engine">Which engine.</param>
/// <param name="Total">Tracks considered.</param>
/// <param name="Complete">Tracks with output for this engine.</param>
/// <param name="Missing">Tracks without it.</param>
public sealed record EngineCoverageSummary(AnalysisEngine Engine, int Total, int Complete, int Missing)
{
    public bool IsComplete => Missing == 0;

    public double Fraction => Total == 0 ? 1 : (double)Complete / Total;

    /// <summary>"118 / 140" for a monospaced column.</summary>
    public string CompleteOfTotal => $"{Complete} / {Total}";
}
