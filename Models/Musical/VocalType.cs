namespace SLSKDONET.Models
{
    /// <summary>
    /// Classification of vocal content in a track based on Vocal Density analysis.
    /// Calculated from the ratio of 3-second patches containing voice activity (>0.6 probability).
    ///
    /// Density Thresholds:
    ///   &lt; 0.05  → Instrumental  (silence or purely electronic)
    ///   0.05–0.35 → VocalChops  (sparse hits, hype phrases, sampled words)
    ///   ≥ 0.35  → LeadVocal    (sustained singing / full phrases)
    ///
    /// Special values:
    ///   Acapella = near-zero instrumental energy with high vocal density
    ///   SpokenWord = vocal density ≥ 0.35 but low Arousal/Energy (spoken poetry, etc.)
    ///
    /// Legacy values (SparseVocals, HookOnly, FullLyrics) are preserved as numeric aliases
    /// so existing rows are not orphaned during migration.
    ///
    /// Used by SonicMatchService for Vocal Clash Avoidance scoring.
    /// </summary>
    public enum VocalType
    {
        // --- Phase 3.5 Legacy Values (integer-stable) ---

        /// <summary>No vocals detected. Purely electronic / instrumental.</summary>
        Instrumental = 0,

        /// <summary>[Legacy] Occasional words, hype phrases, or shouts. Alias for VocalChops.</summary>
        SparseVocals = 1,

        /// <summary>[Legacy] Repeating hook or chorus, but lacks full-song structure. Alias for VocalChops.</summary>
        HookOnly = 2,

        /// <summary>[Legacy] Standard song structure with verses and chorus. Alias for LeadVocal.</summary>
        FullLyrics = 3,

        // --- Phase 5.0 Density-Classified Values (unambiguous names) ---

        /// <summary>
        /// Sparse vocal hits (5–35% of patches). Short words, ad-libs, rhythmic chops.
        /// Safe to layer over a LeadVocal track without causing a full clash.
        /// </summary>
        VocalChops = 4,

        /// <summary>
        /// Dense sustained vocals (≥35% of patches). Full verses, continuous singing.
        /// Mixing two LeadVocal tracks simultaneously causes a "vocal trainwreck".
        /// </summary>
        LeadVocal = 5,

        /// <summary>
        /// Vocal-only track with negligible instrumental content.
        /// RMS of instrumental stem is near zero; vocal density ≥ 0.80.
        /// </summary>
        Acapella = 6,

        /// <summary>
        /// Spoken-word / podcast / poetry. High vocal density but low arousal/danceability.
        /// </summary>
        SpokenWord = 7,

        /// <summary>
        /// Classification has not been performed on this track yet.
        /// Treated as Instrumental for safety in clash avoidance.
        /// </summary>
        Unknown = 99
    }

    /// <summary>
    /// Extension helpers for VocalType — human-readable labels for UI binding.
    /// </summary>
    public static class VocalTypeExtensions
    {
        public static string ToDisplayLabel(this VocalType vt) => vt switch
        {
            VocalType.Instrumental => "Instrumental",
            VocalType.SparseVocals => "Sparse Vocals",
            VocalType.HookOnly     => "Hook / Chorus",
            VocalType.FullLyrics   => "Full Lyrics",
            VocalType.VocalChops   => "Vocal Chops",
            VocalType.LeadVocal    => "Lead Vocals",
            VocalType.Acapella     => "Acapella",
            VocalType.SpokenWord   => "Spoken Word",
            _                      => "Unknown"
        };

        /// <summary>Returns true if this track type can safely be layered over a LeadVocal source.</summary>
        public static bool IsVocalClashSafe(this VocalType vt) =>
            vt is VocalType.Instrumental or VocalType.VocalChops or VocalType.SparseVocals or VocalType.Unknown;

        /// <summary>Returns true if this track carries dense sustained vocals.</summary>
        public static bool IsDenseVocal(this VocalType vt) =>
            vt is VocalType.LeadVocal or VocalType.FullLyrics or VocalType.Acapella;
    }
}
