using System;
using System.Collections.Generic;
using System.IO;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Ranking;

/// <summary>
/// Track quality tiers used by <see cref="TieredTrackComparer"/>.
/// Higher numeric value = better tier.
/// </summary>
public enum TrackTier
{
    Trash   = 0, // Rejected / suspicious
    Bronze  = 1, // Bare minimum quality
    Silver  = 2, // Acceptable
    Gold    = 3, // Good quality or sonic-match
    Diamond = 4  // Best: high quality + available (+ sonic match in DJ mode)
}

/// <summary>
/// Policy-driven two-stage track comparer:
///   1. Tier assignment  — deterministic, hard breaks by quality / availability / sonic match
///   2. Score tie-break — continuous double within the same tier
///
/// Tier 1 (primary goal): qalityFirst vs DjReady — drives Diamond / Gold decisions.
/// Tier 2 (reliability &amp; speed): free slots, queue depth.
/// Tier 3 (polish): bitrate, format aesthetics — reflected in the continuous score.
/// </summary>
public sealed class TieredTrackComparer : IComparer<Track>
{
    private static readonly HashSet<string> _losslessFormats =
        new(StringComparer.OrdinalIgnoreCase) { "flac", "wav", "aif", "aiff", "ape", "alac" };

    private readonly SearchPolicy _policy;
    private readonly Track _searchTrack;

    public TieredTrackComparer(SearchPolicy policy, Track searchTrack)
    {
        _policy = policy;
        _searchTrack = searchTrack;
    }

    // ── IComparer<Track> ──────────────────────────────────────────────────

    /// <summary>
    /// Returns &lt; 0 if <paramref name="x"/> should appear before
    /// <paramref name="y"/> (x ranks higher).
    /// </summary>
    public int Compare(Track? x, Track? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return 1;
        if (y == null) return -1;

        var tierX = CalculateTier(x);
        var tierY = CalculateTier(y);

        if (tierX != tierY)
            return tierY.CompareTo(tierX); // higher tier value = smaller compare result = first

        // Same tier: continuous score tie-break
        double sX = CalculateRankScore(x);
        double sY = CalculateRankScore(y);
        return sY.CompareTo(sX); // higher score first
    }

    // ── Public helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a 0–1 continuous rank score for the candidate.
    /// Used for tie-breaking within the same tier and by external callers.
    /// </summary>
    public double CalculateRankScore(Track candidate)
    {
        double quality      = GetQualityScore(candidate);
        double availability = GetAvailabilityScore(candidate);
        double sonic        = GetSonicScore(candidate);

        return _policy.Priority == SearchPriority.DjReady
            ? (sonic * 0.50) + (quality * 0.25) + (availability * 0.25)
            : (quality * 0.50) + (availability * 0.35) + (sonic * 0.15);
    }

    // ── Tier logic ────────────────────────────────────────────────────────

    private TrackTier CalculateTier(Track candidate)
    {
        // Integrity gate: suspicious duration drops to Trash
        if (_searchTrack.Length.HasValue && candidate.Length.HasValue)
        {
            double ratio = candidate.Length.Value / (double)_searchTrack.Length.Value;
            if (ratio < 0.5 || ratio > 2.0)
                return TrackTier.Trash;
        }

        bool isLossless  = IsLossless(candidate);
        bool highBitrate = candidate.Bitrate >= 256;
        bool freeSlot    = candidate.HasFreeUploadSlot;
        bool shortQueue  = candidate.QueueLength < 20;

        if (_policy.Priority == SearchPriority.DjReady)
            return DjReadyTier(candidate, isLossless, highBitrate, freeSlot);

        // QualityFirst tier
        if ((isLossless || highBitrate) && freeSlot)  return TrackTier.Diamond;
        if (isLossless || highBitrate)                return shortQueue ? TrackTier.Gold : TrackTier.Silver;
        if (candidate.Bitrate >= 192)                 return TrackTier.Silver;
        if (candidate.Bitrate >= 128)                 return TrackTier.Bronze;
        return TrackTier.Trash;
    }

    private TrackTier DjReadyTier(Track candidate, bool isLossless, bool highBitrate, bool freeSlot)
    {
        bool sonicHit = IsBpmMatch(candidate) || IsKeyMatch(candidate) || IsEnergyMatch(candidate);

        // Diamond: full package
        if (sonicHit && (isLossless || highBitrate) && freeSlot) return TrackTier.Diamond;
        // Gold: sonic match + available, OR sonic match + high quality (but queued)
        if (sonicHit && freeSlot)                                return TrackTier.Gold;
        if (sonicHit && (isLossless || highBitrate))             return TrackTier.Gold;
        // Silver: sonic match without great quality/availability, OR high quality without sonic
        if (sonicHit)                                            return TrackTier.Silver;
        if ((isLossless || highBitrate) && freeSlot)             return TrackTier.Silver;
        // Bronze: decent quality
        if (isLossless || highBitrate || candidate.Bitrate >= 192) return TrackTier.Bronze;
        return TrackTier.Trash;
    }

    // ── Sonic matchers ────────────────────────────────────────────────────

    private bool IsBpmMatch(Track candidate)
    {
        if (!_searchTrack.BPM.HasValue || !candidate.BPM.HasValue) return false;
        double ratio = candidate.BPM.Value / _searchTrack.BPM.Value;
        return ratio >= 0.95 && ratio <= 1.05; // ±5 %
    }

    private bool IsKeyMatch(Track candidate)
    {
        return !string.IsNullOrEmpty(_searchTrack.MusicalKey)
            && !string.IsNullOrEmpty(candidate.MusicalKey)
            && _searchTrack.MusicalKey.Equals(candidate.MusicalKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEnergyMatch(Track candidate)
    {
        return _searchTrack.Energy.HasValue
            && candidate.Energy.HasValue
            && Math.Abs(_searchTrack.Energy.Value - candidate.Energy.Value) <= 0.2;
    }

    // ── Score sub-components ──────────────────────────────────────────────

    private static double GetQualityScore(Track t)
    {
        if (IsLossless(t)) return 1.0;
        if (t.Bitrate <= 0) return 0.0;
        return Math.Clamp(t.Bitrate / 320.0, 0.0, 1.0);
    }

    private static double GetAvailabilityScore(Track t)
    {
        double slotScore  = t.HasFreeUploadSlot ? 1.0 : 0.3;
        double queueScore = 1.0 - Math.Clamp(t.QueueLength / 50.0, 0.0, 1.0);
        return (slotScore * 0.6) + (queueScore * 0.4);
    }

    private double GetSonicScore(Track candidate)
    {
        // If the search target has no sonic metadata, be lenient — don't penalise candidates
        bool targetHasMeta = _searchTrack.BPM.HasValue
            || !string.IsNullOrEmpty(_searchTrack.MusicalKey)
            || _searchTrack.Energy.HasValue;
        if (!targetHasMeta) return 0.7;

        // Candidates with no sonic metadata get a neutral (not punished) score per slot
        double bpmScore    = IsBpmMatch(candidate) ? 1.0
                             : (candidate.BPM.HasValue ? 0.2 : 0.7);
        double keyScore    = IsKeyMatch(candidate) ? 1.0
                             : (string.IsNullOrEmpty(candidate.MusicalKey) ? 0.7 : 0.2);
        double energyScore = IsEnergyMatch(candidate) ? 1.0
                             : (candidate.Energy.HasValue ? 0.3 : 0.7);

        return (bpmScore * 0.35) + (keyScore * 0.40) + (energyScore * 0.25);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsLossless(Track t)
    {
        var fmt = (t.Format
            ?? Path.GetExtension(t.Filename)?.TrimStart('.')
            ?? string.Empty).ToLowerInvariant();
        return _losslessFormats.Contains(fmt);
    }
}
