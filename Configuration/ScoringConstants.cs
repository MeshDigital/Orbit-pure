namespace SLSKDONET.Configuration;

/// <summary>
/// Phase 2.1: Scoring constants organized by domain.
/// Uses static readonly for future runtime configuration (JSON/user settings).
/// Nested classes group related constants for clarity.
/// </summary>
public static class ScoringConstants
{
    /// <summary>
    /// Critical gating thresholds - these determine if a file is even considered
    /// </summary>
    public static class Identity
    {
        /// <summary>Duration tolerance in milliseconds for strict gating</summary>
        public static readonly int DurationToleranceMs = 30000; // 30 seconds
        
        /// <summary>Smart duration tolerance for version matching</summary>
        public static readonly int SmartDurationToleranceMs = 15000; // 15 seconds
        
        /// <summary>VBR validation threshold (80% of expected filesize)</summary>
        public static readonly double VbrValidationThreshold = 0.8;
        
        /// <summary>Silent tail exception threshold for FLAC (60-80% efficiency)</summary>
        public static readonly double SilentTailThreshold = 0.6;
        
        /// <summary>Minimum bitrate threshold for silent tail exception</summary>
        public static readonly int SilentTailMinBitrate = 1000; // FLAC territory
        
        /// <summary>Artwork buffer in bytes (ID3 tags + embedded cover art)</summary>
        public static readonly int ArtworkBufferBytes = 32768; // 32 KB
        
        /// <summary>Minimum bytes per second for filesize validation (~64kbps)</summary>
        public static readonly int MinBytesPerSecond = 8000;
        
        /// <summary>Filesize suspicion threshold (50% of expected)</summary>
        public static readonly double FilesizeSuspicionThreshold = 0.5;
    }
    
    /// <summary>
    /// Quality tier scoring - the "currency" of the ranking system
    /// </summary>
    public static class Quality
    {
        /// <summary>Base score for lossless formats (FLAC, WAV, ALAC, APE)</summary>
        public static readonly int LosslessBase = 450;
        
        /// <summary>Base score for high-quality lossy (320kbps MP3, 256kbps AAC)</summary>
        public static readonly int HighQualityBase = 300;
        
        /// <summary>Bitrate threshold for high-quality tier (with VBR buffer)</summary>
        public static readonly int HighQualityThreshold = 315; // Catches VBR V0
        
        /// <summary>Base score for medium-quality lossy (192-256kbps)</summary>
        public static readonly int MediumQualityBase = 150;
        
        /// <summary>Bitrate threshold for medium-quality tier</summary>
        public static readonly int MediumQualityThreshold = 192;
        
        /// <summary>Proportional scoring multiplier for low-quality files</summary>
        public static readonly double LowQualityMultiplier = 0.5; // 128kbps = 64 pts
        
        /// <summary>Bonus for high sample rate lossless (96kHz+)</summary>
        public static readonly int HighSampleRateBonus = 25;
        
        /// <summary>Sample rate threshold for bonus</summary>
        public static readonly int HighSampleRateThreshold = 96000;
    }
    
    /// <summary>
    /// Musical intelligence scoring - tiebreakers for equal quality
    /// </summary>
    public static class Musical
    {
        /// <summary>Bonus for exact BPM match in filename</summary>
        public static readonly int BpmMatchBonus = 100;
        
        /// <summary>Neutral score when no BPM found (prevents penalty)</summary>
        public static readonly double BpmNeutralScore = 0.5;
        
        /// <summary>BPM difference threshold for perfect match (within 2 BPM)</summary>
        public static readonly double BpmPerfectThreshold = 2.0;
        
        /// <summary>BPM difference threshold for close match (within 5 BPM)</summary>
        public static readonly double BpmCloseThreshold = 5.0;
        
        /// <summary>BPM difference threshold for acceptable match (within 10 BPM)</summary>
        public static readonly double BpmAcceptableThreshold = 10.0;
        
        /// <summary>Confidence decay for BPM found in parent directory</summary>
        public static readonly double PathDecayFactor = 0.9;
        
        /// <summary>Confidence decay for BPM found 2+ levels up</summary>
        public static readonly double DeepPathDecayFactor = 0.7;
        
        /// <summary>Bonus for exact musical key match</summary>
        public static readonly int KeyMatchBonus = 75;
        
        /// <summary>Bonus for harmonic key compatibility (Camelot wheel)</summary>
        public static readonly int HarmonicKeyBonus = 50;
    }
    
    /// <summary>
    /// Availability scoring - speed and uploader trust
    /// </summary>
    public static class Availability
    {
        /// <summary>Bonus for free upload slot</summary>
        public static readonly int FreeSlotBonus = 2000;
        
        /// <summary>Penalty per item in queue</summary>
        public static readonly int QueuePenaltyPerItem = 10;
        
        /// <summary>Bonus for empty queue</summary>
        public static readonly int EmptyQueueBonus = 10;
        
        /// <summary>Queue length threshold for heavy penalty</summary>
        public static readonly int LongQueueThreshold = 50;
        
        /// <summary>Heavy penalty for very long queues (>50 items)</summary>
        public static readonly int LongQueuePenalty = 500;
    }
    
    /// <summary>
    /// Metadata and string matching weights
    /// </summary>
    public static class Metadata
    {
        /// <summary>Bonus for having valid length metadata</summary>
        public static readonly int ValidLengthBonus = 100;
        
        /// <summary>Weight for length match score</summary>
        public static readonly int LengthMatchWeight = 100;
        
        /// <summary>Weight for title similarity</summary>
        public static readonly int TitleSimilarityWeight = 200;
        
        /// <summary>Weight for artist similarity</summary>
        public static readonly int ArtistSimilarityWeight = 100;
        
        /// <summary>Weight for album similarity</summary>
        public static readonly int AlbumSimilarityWeight = 50;
    }
    
    /// <summary>
    /// Required and preferred condition weights
    /// </summary>
    public static class Conditions
    {
        /// <summary>Bonus for passing required conditions</summary>
        public static readonly int RequiredPassBonus = 1000;
        
        /// <summary>Weight for preferred conditions score</summary>
        public static readonly int PreferredWeight = 500;
    }
    
    /// <summary>
    /// Tiebreaker constants
    /// </summary>
    public static class Tiebreaker
    {
        /// <summary>Divisor for random tiebreaker (keeps contribution small)</summary>
        public static readonly double RandomDivisor = 1_000_000_000.0;
    }
}
