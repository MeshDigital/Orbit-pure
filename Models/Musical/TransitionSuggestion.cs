using System;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Models.Musical
{
    public class TransitionSuggestion
    {
        public SLSKDONET.Data.Entities.TransitionArchetype Archetype { get; set; }
        public string Reasoning { get; set; } = string.Empty;
        public double BpmDrift { get; set; }
        public double HarmonicCompatibility { get; set; }
        public double? OptimalTransitionDuration { get; set; }
        public double? OptimalTransitionTime { get; set; }
        public string? OptimalTransitionReason { get; set; }
    }
}
