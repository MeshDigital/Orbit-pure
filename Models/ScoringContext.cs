using SLSKDONET.Utils;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Phase 2.3: Parameter Object for scoring operations.
    /// Encapsulates all data needed for intelligent search ranking.
    /// Enables lazy evaluation and cleaner method signatures.
    /// </summary>
    public class ScoringContext
    {
        // Required fields
        public required string Title { get; init; }
        public required string Artist { get; init; }
        
        // Optional musical intelligence fields
        public int? TargetBPM { get; init; }
        public int? TargetDuration { get; init; }
        public string? TargetKey { get; init; }
        
        // Lazy-evaluated normalized strings (computed once, cached)
        private string? _normalizedTitle;
        private string? _normalizedArtist;
        
        /// <summary>
        /// Normalized title with noise stripped (lazy evaluation).
        /// </summary>
        public string NormalizedTitle => _normalizedTitle ??= FilenameNormalizer.Normalize(Title);
        
        /// <summary>
        /// Normalized artist with noise stripped (lazy evaluation).
        /// </summary>
        public string NormalizedArtist => _normalizedArtist ??= FilenameNormalizer.Normalize(Artist);
        
        /// <summary>
        /// Creates a ScoringContext from a search query.
        /// </summary>
        public static ScoringContext FromSearchQuery(string artist, string title, int? bpm = null, int? duration = null, string? key = null)
        {
            return new ScoringContext
            {
                Title = title,
                Artist = artist,
                TargetBPM = bpm,
                TargetDuration = duration,
                TargetKey = key
            };
        }
        
        /// <summary>
        /// Creates a ScoringContext from Spotify metadata.
        /// </summary>
        public static ScoringContext FromSpotifyMetadata(string artist, string title, double? bpm, int? durationMs, string? key)
        {
            return new ScoringContext
            {
                Title = title,
                Artist = artist,
                TargetBPM = bpm.HasValue ? (int)Math.Round(bpm.Value) : null,
                TargetDuration = durationMs.HasValue ? durationMs.Value / 1000 : null,
                TargetKey = key
            };
        }
    }
}
