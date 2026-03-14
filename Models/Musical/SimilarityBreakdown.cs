using System.Collections.Generic;

namespace SLSKDONET.Models.Musical
{
    // ============================================================
    // Phase 5.0: Transparent Match Engine — Output & Control Models
    // ============================================================

    /// <summary>
    /// Categorised breakdown of a sonic similarity result.
    /// Replaces the flat "double matchScore" return value so the UI can
    /// explain *why* a match scored as it did, not just *how much*.
    ///
    /// UI contract: MatchTags is a ready-to-render list of short strings
    /// (e.g. "🎵 Perfect Harmonic Match", "⚡ Energy Boost (+12%)", "⚠️ Vocal Clash Warning")
    /// that can be bound directly to an ItemsControl in the Similarity Sidebar.
    /// </summary>
    public class SimilarityBreakdown
    {
        // --------------------------------------------------------
        // Dimensional Scores (each 0.0 – 1.0, higher = more similar)
        // --------------------------------------------------------

        /// <summary>
        /// Key / harmonic compatibility.
        /// Scored via Camelot Wheel distance:
        ///   Same key = 1.00, Relative maj/min = 0.90, Adjacent 5th = 0.85, Incompatible = 0.00
        /// </summary>
        public double HarmonicScore { get; set; }

        /// <summary>
        /// BPM / rhythmic compatibility.
        /// Scored on a steep bell curve: ±3 BPM ≈ 0.95, ±10 BPM ≈ 0.00.
        /// Half/double-time matches are treated as 0.90 (DJ-compatible).
        /// </summary>
        public double RhythmScore { get; set; }

        /// <summary>
        /// Vibe / mood compatibility.
        /// Uses Cosine Similarity between the source's and candidate's mood probability arrays
        /// (Happy, Aggressive, Sad, Relaxed, Party, Electronic).
        /// </summary>
        public double VibeScore { get; set; }

        /// <summary>
        /// Timbral / genre similarity.
        /// Cosine Similarity between 128-D AI vector embeddings when available,
        /// or ElectronicSubgenre string match otherwise.
        /// </summary>
        public double TimbreScore { get; set; }

        /// <summary>
        /// Deep Texture / Sonic DNA similarity (Phase 5).
        /// SIMD-accelerated Cosine Similarity between 512-D deep embeddings
        /// from discogs-effnet model. Captures actual sonic texture rather than
        /// genre metadata. Null/0 when embedding data unavailable.
        /// </summary>
        public double TextureScore { get; set; }

        // --------------------------------------------------------
        // Final Aggregated Confidence (0.0 – 1.0)
        // --------------------------------------------------------

        /// <summary>
        /// Weighted combination of HarmonicScore, RhythmScore, VibeScore, TimbreScore
        /// according to the active MatchProfile.
        /// This is what drives the displayed percentage in the UI.
        /// </summary>
        public double TotalConfidence { get; set; }

        // --------------------------------------------------------
        // Raw Diagnostic Numbers (for advanced UI / debug overlay)
        // --------------------------------------------------------

        /// <summary>BPM of the candidate track for inline UI display.</summary>
        public float CandidateBpm { get; set; }

        /// <summary>Camelot key of the candidate track (e.g. "8A").</summary>
        public string CandidateCamelot { get; set; } = string.Empty;

        /// <summary>Difference in BPM vs the source track (signed, e.g. +4.2 or -8.0).</summary>
        public float BpmDelta { get; set; }

        /// <summary>Difference in Energy vs the source (e.g. +0.12 = 12% more energetic).</summary>
        public float EnergyDelta { get; set; }

        // --------------------------------------------------------
        // Match Tags — primary UI surface
        // --------------------------------------------------------

        /// <summary>
        /// Human-readable, emoji-annotated reasons for this match.
        /// Examples:
        ///   "🎵 Perfect Harmonic Match"
        ///   "⚡ Energy Boost (+12%)"
        ///   "🥁 BPM Gap (−8 BPM)"
        ///   "⚠️ Vocal Clash Warning"
        ///   "✅ Instrumental — Avoids Vocal Clash"
        /// </summary>
        public List<string> MatchTags { get; set; } = new();

        // --------------------------------------------------------
        // Vocal Clash Intelligence
        // --------------------------------------------------------

        /// <summary>Penalty applied (0.0 – 0.30) due to a Lead-Vocal → Lead-Vocal clash.</summary>
        public double VocalClashPenalty { get; set; }

        /// <summary>Boost applied (0.0 – 0.10) because candidate complements a vocal source.</summary>
        public double VocalComplementBoost { get; set; }

        // --------------------------------------------------------
        // Match Profile Used
        // --------------------------------------------------------

        public MatchProfile ProfileUsed { get; set; } = MatchProfile.Mixable;
    }

    /// <summary>
    /// Contextual weighting profile for the matching algorithm.
    ///
    /// Mixable (DJ Mode):
    ///   Harmonic 40% + Rhythm 40% + Vibe 20% — ensures physical mixability.
    ///
    /// VibeMatch (Playlist Mode):
    ///   Vibe 60% + Timbre 30% + Rhythm 10% — ignores key entirely,
    ///   allows wide BPM variation as long as mood and texture match.
    /// </summary>
    public enum MatchProfile
    {
        /// <summary>
        /// DJ / Mixing mode. Prioritises harmonic and rhythmic compatibility.
        /// Use when recommending the *next track to play* live.
        /// </summary>
        Mixable = 0,

        /// <summary>
        /// Playlist / Discovery mode. Prioritises mood, timbre, and emotional arc.
        /// Use when building a listening playlist or mood board.
        /// </summary>
        VibeMatch = 1
    }
}
