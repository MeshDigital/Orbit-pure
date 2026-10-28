using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Picks which stored phrase annotation drives cue placement (and Cue Forge's phrase map and
/// section suggestions).
///
/// Two phrase stores exist:
/// <list type="bullet">
/// <item><see cref="AudioFeaturesEntity.PhraseSegmentsJson"/> — the legacy column Rekordbox's
/// own PSSI analysis (or, failing that, ORBIT's structural analysis) is mirrored into. It carries
/// <see cref="AudioFeaturesEntity.PhraseSegmentsSource"/> so stale rows can be recognised.</item>
/// <item>The <c>TrackPhrases</c> table — what <c>AnalyzeTrackStructureJob</c> writes on every
/// structural run, with fresh durations from the current beat grid.</item>
/// </list>
///
/// The JSON column was only ever written when it was empty, so tracks analysed before the
/// half-time BPM correction (or before the column existed) kept phrase boundaries laid out on
/// the old grid forever, while TrackPhrases held the correct ones. That stale JSON was also
/// gated out of cue generation by its empty source, which left the generator with DSP signals
/// alone — the "phrasing is clearly visible but the drop is way off" symptom.
/// </summary>
public static class PhraseSourceResolver
{
    /// <summary>Bare segment labels the cue generator understands (post-SanitizeSegments vocabulary).</summary>
    public static bool IsTrustedSource(string? source) =>
        string.Equals(source, "RekordboxPSSI", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "Heuristic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chooses the phrase list to use. Rekordbox's own analysis wins when it is present and
    /// plausible; otherwise the fresher <c>TrackPhrases</c> rows beat the legacy JSON column.
    /// Falls back to whatever exists when neither is complete.
    /// </summary>
    public static List<PhraseSegment> Resolve(
        string? source,
        IReadOnlyList<PhraseSegment>? fromJson,
        IReadOnlyList<PhraseSegment>? fromTrackPhrases,
        double duration)
    {
        var json = Sanitize(fromJson, duration);
        var stored = Sanitize(fromTrackPhrases, duration);

        if (IsTrustedSource(source) && json.Count >= 2) return json;
        if (stored.Count >= 2) return stored;
        return json;
    }

    /// <summary>Converts persisted <c>TrackPhrases</c> rows to the generator's segment model.</summary>
    public static List<PhraseSegment> FromTrackPhrases(IEnumerable<TrackPhraseEntity>? phrases)
    {
        if (phrases is null) return new List<PhraseSegment>();
        return phrases
            .OrderBy(p => p.StartTimeSeconds)
            .Select(p => new PhraseSegment
            {
                Label = string.IsNullOrWhiteSpace(p.Label) ? p.Type.ToString() : p.Label,
                Start = (float)p.StartTimeSeconds,
                Duration = (float)Math.Max(0, p.EndTimeSeconds - p.StartTimeSeconds),
                Confidence = p.Confidence,
            })
            .ToList();
    }

    /// <summary>
    /// Drops segments that cannot belong to this track (starting past the end, as stale
    /// analyses of a wrong duration do) and clamps durations to the remaining audio. A segment's
    /// Start is a real detected event; its Duration is often an extrapolation, so only the
    /// duration is clamped.
    /// </summary>
    public static List<PhraseSegment> Sanitize(IReadOnlyList<PhraseSegment>? segments, double duration)
    {
        if (segments is null || segments.Count == 0) return new List<PhraseSegment>();

        const double ToleranceSeconds = 5.0;
        var result = new List<PhraseSegment>(segments.Count);
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (duration > 0 && s.Start > duration + ToleranceSeconds) continue;
            if (s.Duration <= 0.05f) continue;

            double maxDuration = duration > 0 ? Math.Max(0, duration - s.Start) : s.Duration;
            double clamped = duration > 0 && s.Duration > maxDuration + ToleranceSeconds ? maxDuration : s.Duration;
            if (clamped <= 0.05) continue;

            result.Add(new PhraseSegment
            {
                Label = s.Label,
                Start = s.Start,
                Duration = (float)clamped,
                Bars = s.Bars,
                Beats = s.Beats,
                Confidence = s.Confidence,
                Color = s.Color,
            });
        }
        return result;
    }
}
