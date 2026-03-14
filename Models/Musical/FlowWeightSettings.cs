using System;

namespace SLSKDONET.Models.Musical
{
    /// <summary>
    /// Tunable weights for calculating Set Flow Continuity.
    /// Allows DJs to customize how ORBIT scores set "health".
    /// </summary>
    public class FlowWeightSettings
    {
        /// <summary>
        /// Importance of Harmonic compatibility (0.0 - 1.0).
        /// High values prioritize Camelot mixing.
        /// </summary>
        public double HarmonicWeight { get; set; } = 0.8;

        /// <summary>
        /// Importance of Energy alignment (0.0 - 1.0).
        /// High values penalize large energy gaps.
        /// </summary>
        public double EnergyWeight { get; set; } = 0.7;

        /// <summary>
        /// Importance of Rhythm/BPM proximity (0.0 - 1.0).
        /// High values penalize tracks with large tempo differences.
        /// </summary>
        public double BpmWeight { get; set; } = 0.5;

        /// <summary>
        /// Penalty for overlapping vocal sections (0.0 - 1.0).
        /// High values strictly prevent lyric clashes.
        /// </summary>
        public double VocalOverlapPenalty { get; set; } = 0.9;

        /// <summary>
        /// Global genre-consistency importance (0.0 - 1.0).
        /// </summary>
        public double GenreWeight { get; set; } = 0.4;

        /// <summary>
        /// Returns a "Smooth Blend" preset (Harmonic focus).
        /// </summary>
        public static FlowWeightSettings SmoothBlend => new()
        {
            HarmonicWeight = 1.0,
            EnergyWeight = 0.6,
            BpmWeight = 0.4,
            VocalOverlapPenalty = 0.8,
            GenreWeight = 0.5
        };

        /// <summary>
        /// Returns a "High Energy" preset (Intensity focus).
        /// </summary>
        public static FlowWeightSettings HighEnergy => new()
        {
            HarmonicWeight = 0.5,
            EnergyWeight = 1.0,
            BpmWeight = 0.7,
            VocalOverlapPenalty = 0.6,
            GenreWeight = 0.3
        };

        /// <summary>
        /// Returns a "Radio Style" preset (Strict vocal protection).
        /// </summary>
        public static FlowWeightSettings RadioStyle => new()
        {
            HarmonicWeight = 0.7,
            EnergyWeight = 0.5,
            BpmWeight = 0.6,
            VocalOverlapPenalty = 1.0,
            GenreWeight = 0.4
        };

        /// <summary>
        /// Returns a "Genre Bender" preset (Rhythm/BPM focus for genre-crossing sets).
        /// </summary>
        public static FlowWeightSettings GenreBender => new()
        {
            HarmonicWeight = 0.4,
            EnergyWeight = 0.6,
            BpmWeight = 1.0,
            VocalOverlapPenalty = 0.5,
            GenreWeight = 0.2
        };
    }
}
