using System;

namespace SLSKDONET.Models;

/// <summary>
/// Phase 23: DTO for serializing smart crate rules.
/// </summary>
public class SmartCrateRules
{
    public string? Mood { get; set; }
    public string? SubGenre { get; set; }
    
    public double? MinBpm { get; set; }
    public double? MaxBpm { get; set; }
    
    public double? MinEnergy { get; set; }
    public double? MaxEnergy { get; set; }
    
    public bool OnlyInstrumental { get; set; }
}
