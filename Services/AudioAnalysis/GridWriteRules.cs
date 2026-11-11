using System;
using System.Collections.Generic;
using System.Text.Json;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// The single choke point that decides whether a newly-computed grid (from ORBIT's fitter or from
/// Rekordbox's PQTZ) may replace the grid already stored on a track, per issue #221. A grid the DJ
/// owns (<see cref="AudioFeaturesEntity.GridSource"/> == "User", or <see cref="AudioFeaturesEntity.GridVerified"/>)
/// is never silently overwritten: the new grid is kept as a suggestion in
/// <see cref="AudioFeaturesEntity.AnalysedGridJson"/> instead.
/// </summary>
public static class GridWriteRules
{
    public const string SourceAnalysis = "Analysis";
    public const string SourceRekordbox = "Rekordbox";
    public const string SourceUser = "User";

    /// <summary>What to do with a newly-computed grid given the grid the track currently owns.</summary>
    public enum Decision
    {
        /// <summary>Replace Bpm, the beat list and Bar 1 with the new grid.</summary>
        Apply,
        /// <summary>Keep the current grid and store the new one as a suggestion.</summary>
        KeepSuggestion,
        /// <summary>Keep the current grid and store the new Rekordbox grid as a conflict to offer.</summary>
        KeepConflict,
    }

    /// <summary>
    /// The write-rule table:
    /// <code>
    ///   current \ new     | Analysis          | Rekordbox
    ///   ------------------+-------------------+------------------
    ///   Analysis          | apply             | apply (Rekordbox wins)
    ///   Rekordbox         | keep + suggest    | apply (edited in Rekordbox)
    ///   User / verified   | keep + suggest    | keep + flag conflict
    /// </code>
    /// </summary>
    public static Decision Decide(string? currentSource, bool gridVerified, string newSource)
    {
        bool userOwns = string.Equals(currentSource, SourceUser, StringComparison.Ordinal) || gridVerified;
        if (userOwns)
            return newSource == SourceRekordbox ? Decision.KeepConflict : Decision.KeepSuggestion;

        if (string.Equals(currentSource, SourceRekordbox, StringComparison.Ordinal) && newSource == SourceAnalysis)
            return Decision.KeepSuggestion;

        return Decision.Apply;
    }

    /// <summary>
    /// Applies an ORBIT-fitted grid if the rules allow it. Returns true when applied; false when the
    /// current grid was kept and the suggestion stored in <see cref="AudioFeaturesEntity.AnalysedGridJson"/>.
    /// </summary>
    public static bool TryApplyAnalysisGrid(
        AudioFeaturesEntity features, double bpm, IReadOnlyList<double> beats, double downbeat, float confidence)
    {
        if (Decide(features.GridSource, features.GridVerified, SourceAnalysis) != Decision.Apply)
        {
            StoreSuggestion(features, bpm, beats, downbeat, SourceAnalysis);
            return false;
        }

        features.Bpm = (float)Math.Round(bpm, 2);
        features.BeatGridJson = JsonSerializer.Serialize(beats);
        features.DownbeatOffsetSeconds = Math.Round(downbeat, 4);
        features.GridSource = SourceAnalysis;
        features.GridConfidence = confidence;
        features.AnalysedGridJson = null;
        return true;
    }

    /// <summary>
    /// Applies a Rekordbox-sourced grid if the rules allow it. Rekordbox grids get a fixed high
    /// confidence (they carry real downbeats). Returns true when applied; false when kept + flagged.
    /// </summary>
    public static bool TryApplyRekordboxGrid(
        AudioFeaturesEntity features, double bpm, IReadOnlyList<double> beats, double downbeat)
    {
        if (Decide(features.GridSource, features.GridVerified, SourceRekordbox) != Decision.Apply)
        {
            StoreSuggestion(features, bpm, beats, downbeat, SourceRekordbox);
            return false;
        }

        features.Bpm = (float)Math.Round(bpm, 2);
        features.BeatGridJson = JsonSerializer.Serialize(beats);
        features.DownbeatOffsetSeconds = Math.Round(downbeat, 4);
        features.GridSource = SourceRekordbox;
        features.GridConfidence = 1.0f;
        features.AnalysedGridJson = null;
        return true;
    }

    /// <summary>
    /// Confidence formula (documented): <c>0.5·(TicksUsed / totalTicks) + 0.5·(1 − ResidualRmsBeats / 0.12)</c>,
    /// clamped to [0, 1]. A clean, steady fit (most ticks inliers, near-zero residual) scores high;
    /// a jittered or half-time fit scores low. Bar-phase agreement is used to pick the downbeat in
    /// <see cref="BeatGridFitter.EstimateBarPhase"/> rather than folded into this scalar.
    /// </summary>
    public static float ComputeConfidence(BeatGridFitter.Fit fit, int totalTicks)
    {
        const double MaxResidualRmsBeats = 0.12; // matches BeatGridFitter's no-steady-tempo cutoff
        double inlier = totalTicks > 0 ? fit.TicksUsed / (double)totalTicks : 0.0;
        double residual = 1.0 - Math.Clamp(fit.ResidualRmsBeats / MaxResidualRmsBeats, 0.0, 1.0);
        return (float)Math.Clamp(0.5 * inlier + 0.5 * residual, 0.0, 1.0);
    }

    /// <summary>True when the current grid is owned by the user (edited or verified).</summary>
    public static bool IsUserOwned(AudioFeaturesEntity features) =>
        string.Equals(features.GridSource, SourceUser, StringComparison.Ordinal) || features.GridVerified;

    /// <summary>A not-applied grid kept in <see cref="AudioFeaturesEntity.AnalysedGridJson"/>.</summary>
    public sealed record GridSuggestion(double Bpm, double Downbeat, double[] Beats, string Source);

    /// <summary>Parses <see cref="AudioFeaturesEntity.AnalysedGridJson"/>; null when absent/malformed.</summary>
    public static GridSuggestion? ParseSuggestion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("bpm", out var bpm) || !root.TryGetProperty("downbeat", out var downbeat) || !root.TryGetProperty("beats", out var beats))
                return null;
            var beatArray = beats.EnumerateArray().Select(b => b.GetDouble()).ToArray();
            if (beatArray.Length == 0) return null;
            string source = root.TryGetProperty("source", out var s) ? s.GetString() ?? SourceAnalysis : SourceAnalysis;
            return new GridSuggestion(bpm.GetDouble(), downbeat.GetDouble(), beatArray, source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void StoreSuggestion(
        AudioFeaturesEntity features, double bpm, IReadOnlyList<double> beats, double downbeat, string source)
    {
        features.AnalysedGridJson = JsonSerializer.Serialize(new
        {
            bpm = Math.Round(bpm, 2),
            downbeat = Math.Round(downbeat, 4),
            beats,
            source,
        });
    }
}
