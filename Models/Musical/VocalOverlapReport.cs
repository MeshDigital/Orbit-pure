using System;
using System.Collections.Generic;

namespace SLSKDONET.Models.Musical
{
    public class VocalOverlapReport
    {
        public bool HasConflict { get; set; }
        public double ConflictIntensity { get; set; }
        public double VocalSafetyScore { get; set; }
        public string? WarningMessage { get; set; }
        public List<double> ConflictPoints { get; set; } = new();
    }
}
