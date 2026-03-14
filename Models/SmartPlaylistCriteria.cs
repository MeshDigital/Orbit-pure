using System;

namespace SLSKDONET.Models
{
    public class SmartPlaylistCriteria
    {
        // Numeric Ranges (Null = Open ended)
        public double? MinEnergy { get; set; }
        public double? MaxEnergy { get; set; }
        
        public double? MinValence { get; set; }
        public double? MaxValence { get; set; }
        
        public double? MinDanceability { get; set; }
        public double? MaxDanceability { get; set; }
        
        public double? MinBPM { get; set; }
        public double? MaxBPM { get; set; }

        public int? MinRating { get; set; }
        public bool? IsLiked { get; set; }

        // Text Filters
        public string? Genre { get; set; } // Contains match
        public string? YearRange { get; set; } // e.g. "1990-2000"

        // Logic
        public bool MatchAll { get; set; } = true; // AND vs OR
    }
}
