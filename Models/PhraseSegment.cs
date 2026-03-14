using System;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Represents a detected structural segment of a track (e.g. Intro, Drop).
    /// </summary>
    public class PhraseSegment
    {
        public string Label { get; set; } = string.Empty;
        public float Start { get; set; }
        public float Duration { get; set; }
        public int Bars { get; set; }
        public int Beats { get; set; }
        public float Confidence { get; set; }
        public string Color { get; set; } = string.Empty;
    }
}
