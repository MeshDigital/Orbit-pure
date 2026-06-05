using System;
using ReactiveUI;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Configurable constraints for the automix playlist generation flow.
/// </summary>
public class AutomixConstraints : ReactiveObject
{
    private double _minBpm  = 100;
    private double _maxBpm  = 160;
    private int    _maxTracks = 20;
    private bool   _matchKey = true;
    private int    _maxEnergyJump = 3;
    private string _energyCurve = "Wave";
    private double _harmonicWeight = 3.0;
    private double _tempoWeight    = 1.0;
    private double _energyWeight   = 0.5;

    /// <summary>Minimum BPM allowed in the generated playlist.</summary>
    public double MinBpm
    {
        get => _minBpm;
        set => this.RaiseAndSetIfChanged(ref _minBpm, value);
    }

    /// <summary>Maximum BPM allowed in the generated playlist.</summary>
    public double MaxBpm
    {
        get => _maxBpm;
        set => this.RaiseAndSetIfChanged(ref _maxBpm, value);
    }

    /// <summary>Maximum number of tracks to include in the generated playlist.</summary>
    public int MaxTracks
    {
        get => _maxTracks;
        set => this.RaiseAndSetIfChanged(ref _maxTracks, value);
    }

    /// <summary>When true, only include harmonically compatible key transitions.</summary>
    public bool MatchKey
    {
        get => _matchKey;
        set => this.RaiseAndSetIfChanged(ref _matchKey, value);
    }

    /// <summary>
    /// Maximum energy jump (1–10 scale) allowed between consecutive tracks.
    /// </summary>
    public int MaxEnergyJump
    {
        get => _maxEnergyJump;
        set => this.RaiseAndSetIfChanged(ref _maxEnergyJump, Math.Clamp(value, 1, 9));
    }

    /// <summary>"None" | "Rising" | "Wave" | "Peak" — post-pass energy shaping.</summary>
    public string EnergyCurve
    {
        get => _energyCurve;
        set => this.RaiseAndSetIfChanged(ref _energyCurve, value);
    }

    /// <summary>Multiplier for Camelot key distance in the optimizer edge cost.</summary>
    public double HarmonicWeight
    {
        get => _harmonicWeight;
        set => this.RaiseAndSetIfChanged(ref _harmonicWeight, Math.Clamp(value, 0.1, 10.0));
    }

    /// <summary>Multiplier for BPM difference in the optimizer edge cost.</summary>
    public double TempoWeight
    {
        get => _tempoWeight;
        set => this.RaiseAndSetIfChanged(ref _tempoWeight, Math.Clamp(value, 0.1, 10.0));
    }

    /// <summary>Multiplier for energy score difference in the optimizer edge cost.</summary>
    public double EnergyWeight
    {
        get => _energyWeight;
        set => this.RaiseAndSetIfChanged(ref _energyWeight, Math.Clamp(value, 0.0, 10.0));
    }
}
