using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Transition types for mixing between tracks.
/// </summary>
public enum TransitionType
{
    /// <summary>Simple crossfade between tracks.</summary>
    Crossfade,
    
    /// <summary>EQ swap: fade lows out of A while fading lows in on B.</summary>
    EqSwap,
    
    /// <summary>Cut transition (no fade).</summary>
    Cut,
    
    /// <summary>Echo out with reverb tail.</summary>
    EchoOut,
    
    /// <summary>Filter sweep down on outgoing track.</summary>
    FilterSweep,
    
    /// <summary>Backspin effect on outgoing track.</summary>
    Backspin
}

/// <summary>
/// Curve types for transition automation.
/// </summary>
public enum TransitionCurve
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
    SCurve,
    ExponentialIn,
    ExponentialOut
}

/// <summary>
/// Represents a transition region between two tracks in the timeline.
/// </summary>
public class TransitionRegion
{
    /// <summary>ID of the outgoing (A) track.</summary>
    public string OutgoingTrackId { get; set; } = "";
    
    /// <summary>ID of the incoming (B) track.</summary>
    public string IncomingTrackId { get; set; } = "";
    
    /// <summary>Start sample position of the transition.</summary>
    public long StartSample { get; set; }
    
    /// <summary>End sample position of the transition.</summary>
    public long EndSample { get; set; }
    
    /// <summary>Duration in samples.</summary>
    public long DurationSamples => EndSample - StartSample;
    
    /// <summary>Duration in seconds (at 44.1kHz).</summary>
    public double DurationSeconds => DurationSamples / 44100.0;
    
    /// <summary>Type of transition to apply.</summary>
    public TransitionType Type { get; set; } = TransitionType.EqSwap;
    
    /// <summary>Curve for the transition automation.</summary>
    public TransitionCurve Curve { get; set; } = TransitionCurve.SCurve;
    
    /// <summary>
    /// For EQ swap: which bands to swap (default: Low only).
    /// </summary>
    public EqBandSwapConfig EqConfig { get; set; } = new();
}

/// <summary>
/// Configuration for EQ swap transitions.
/// </summary>
public class EqBandSwapConfig
{
    /// <summary>Whether to swap the Low band (bass).</summary>
    public bool SwapLow { get; set; } = true;
    
    /// <summary>Whether to swap the Mid band.</summary>
    public bool SwapMid { get; set; } = false;
    
    /// <summary>Whether to swap the High band.</summary>
    public bool SwapHigh { get; set; } = false;
    
    /// <summary>Low band crossover frequency (Hz).</summary>
    public float LowCrossover { get; set; } = 250f;
    
    /// <summary>High band crossover frequency (Hz).</summary>
    public float HighCrossover { get; set; } = 4000f;
}

/// <summary>
/// Engine for calculating and applying transition automation values.
/// Provides real-time gain/EQ values for smooth track mixing.
/// </summary>
public class TransitionEngine
{
    private readonly List<TransitionRegion> _transitions = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Registered transitions.
    /// </summary>
    public IReadOnlyList<TransitionRegion> Transitions => _transitions;
    
    /// <summary>
    /// Adds a transition region.
    /// </summary>
    public void AddTransition(TransitionRegion region)
    {
        lock (_lock)
        {
            _transitions.Add(region);
            _transitions.Sort((a, b) => a.StartSample.CompareTo(b.StartSample));
        }
    }
    
    /// <summary>
    /// Removes a transition by track IDs.
    /// </summary>
    public void RemoveTransition(string outgoingTrackId, string incomingTrackId)
    {
        lock (_lock)
        {
            _transitions.RemoveAll(t => 
                t.OutgoingTrackId == outgoingTrackId && 
                t.IncomingTrackId == incomingTrackId);
        }
    }
    
    /// <summary>
    /// Clears all transitions.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _transitions.Clear();
        }
    }
    
    /// <summary>
    /// Gets the active transition at a given sample position, if any.
    /// </summary>
    public TransitionRegion? GetActiveTransition(long samplePosition)
    {
        lock (_lock)
        {
            return _transitions.FirstOrDefault(t => 
                samplePosition >= t.StartSample && 
                samplePosition < t.EndSample);
        }
    }
    
    /// <summary>
    /// Calculates automation values for a transition at a given sample position.
    /// </summary>
    public TransitionAutomation CalculateAutomation(TransitionRegion region, long samplePosition)
    {
        // Normalized position within transition (0.0 to 1.0)
        double progress = (double)(samplePosition - region.StartSample) / region.DurationSamples;
        progress = Math.Clamp(progress, 0.0, 1.0);
        
        // Apply curve
        double curved = ApplyCurve(progress, region.Curve);
        
        return region.Type switch
        {
            TransitionType.Crossfade => CalculateCrossfade(curved),
            TransitionType.EqSwap => CalculateEqSwap(curved, region.EqConfig),
            TransitionType.Cut => CalculateCut(progress),
            TransitionType.FilterSweep => CalculateFilterSweep(curved),
            _ => CalculateCrossfade(curved)
        };
    }
    
    /// <summary>
    /// Calculates automation for a track at a sample position (convenience method).
    /// </summary>
    public TransitionAutomation GetAutomationForTrack(string trackId, long samplePosition)
    {
        var transition = GetActiveTransition(samplePosition);
        if (transition == null)
        {
            return TransitionAutomation.NoTransition();
        }
        
        var automation = CalculateAutomation(transition, samplePosition);
        
        // Determine if this is the outgoing or incoming track
        if (trackId == transition.OutgoingTrackId)
        {
            return automation.ForOutgoing();
        }
        else if (trackId == transition.IncomingTrackId)
        {
            return automation.ForIncoming();
        }
        
        return TransitionAutomation.NoTransition();
    }

    private double ApplyCurve(double t, TransitionCurve curve)
    {
        return curve switch
        {
            TransitionCurve.Linear => t,
            TransitionCurve.EaseIn => t * t,
            TransitionCurve.EaseOut => 1 - (1 - t) * (1 - t),
            TransitionCurve.EaseInOut => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2,
            TransitionCurve.SCurve => t * t * (3 - 2 * t), // Smoothstep
            TransitionCurve.ExponentialIn => t == 0 ? 0 : Math.Pow(2, 10 * (t - 1)),
            TransitionCurve.ExponentialOut => t == 1 ? 1 : 1 - Math.Pow(2, -10 * t),
            _ => t
        };
    }

    private TransitionAutomation CalculateCrossfade(double progress)
    {
        return new TransitionAutomation
        {
            OutgoingGain = (float)(1.0 - progress),
            IncomingGain = (float)progress,
            OutgoingLowGain = 1.0f,
            OutgoingMidGain = 1.0f,
            OutgoingHighGain = 1.0f,
            IncomingLowGain = 1.0f,
            IncomingMidGain = 1.0f,
            IncomingHighGain = 1.0f
        };
    }

    private TransitionAutomation CalculateEqSwap(double progress, EqBandSwapConfig config)
    {
        // EQ Swap: The key DJ technique
        // As progress increases, we fade OUT the bass on track A and fade IN the bass on track B
        // This prevents muddy bass collision during the transition
        
        float outLow = config.SwapLow ? (float)(1.0 - progress) : 1.0f;
        float inLow = config.SwapLow ? (float)progress : 1.0f;
        
        float outMid = config.SwapMid ? (float)(1.0 - progress) : 1.0f;
        float inMid = config.SwapMid ? (float)progress : 1.0f;
        
        float outHigh = config.SwapHigh ? (float)(1.0 - progress) : 1.0f;
        float inHigh = config.SwapHigh ? (float)progress : 1.0f;
        
        return new TransitionAutomation
        {
            // Main gains stay at 1.0 during EQ swap - the EQ bands do the work
            OutgoingGain = 1.0f,
            IncomingGain = 1.0f,
            OutgoingLowGain = outLow,
            OutgoingMidGain = outMid,
            OutgoingHighGain = outHigh,
            IncomingLowGain = inLow,
            IncomingMidGain = inMid,
            IncomingHighGain = inHigh
        };
    }

    private TransitionAutomation CalculateCut(double progress)
    {
        // Cut at the midpoint
        bool isAfterCut = progress >= 0.5;
        return new TransitionAutomation
        {
            OutgoingGain = isAfterCut ? 0.0f : 1.0f,
            IncomingGain = isAfterCut ? 1.0f : 0.0f,
            OutgoingLowGain = 1.0f,
            OutgoingMidGain = 1.0f,
            OutgoingHighGain = 1.0f,
            IncomingLowGain = 1.0f,
            IncomingMidGain = 1.0f,
            IncomingHighGain = 1.0f
        };
    }

    private TransitionAutomation CalculateFilterSweep(double progress)
    {
        // Filter sweep: gradually reduce highs, then mids, then lows on outgoing
        // While bringing in the incoming track
        
        float outHigh = (float)Math.Max(0, 1.0 - progress * 2); // Highs go first
        float outMid = (float)Math.Max(0, 1.0 - (progress - 0.25) * 2);
        float outLow = (float)Math.Max(0, 1.0 - (progress - 0.5) * 2);
        
        return new TransitionAutomation
        {
            OutgoingGain = (float)(1.0 - progress * 0.3), // Slight volume reduction
            IncomingGain = (float)progress,
            OutgoingLowGain = outLow,
            OutgoingMidGain = outMid,
            OutgoingHighGain = outHigh,
            IncomingLowGain = 1.0f,
            IncomingMidGain = 1.0f,
            IncomingHighGain = 1.0f
        };
    }

    /// <summary>
    /// Creates an EQ swap transition between two tracks at their overlap point.
    /// </summary>
    public TransitionRegion CreateEqSwapTransition(
        string outgoingTrackId, 
        string incomingTrackId,
        long overlapStartSample,
        long overlapEndSample,
        bool swapBassOnly = true)
    {
        var region = new TransitionRegion
        {
            OutgoingTrackId = outgoingTrackId,
            IncomingTrackId = incomingTrackId,
            StartSample = overlapStartSample,
            EndSample = overlapEndSample,
            Type = TransitionType.EqSwap,
            Curve = TransitionCurve.SCurve,
            EqConfig = new EqBandSwapConfig
            {
                SwapLow = true,
                SwapMid = !swapBassOnly,
                SwapHigh = false
            }
        };
        
        AddTransition(region);
        return region;
    }
}

/// <summary>
/// Automation values for a single frame of a transition.
/// </summary>
public struct TransitionAutomation
{
    public float OutgoingGain;
    public float IncomingGain;
    
    public float OutgoingLowGain;
    public float OutgoingMidGain;
    public float OutgoingHighGain;
    
    public float IncomingLowGain;
    public float IncomingMidGain;
    public float IncomingHighGain;
    
    /// <summary>
    /// Returns automation with no transition active (full gain on both).
    /// </summary>
    public static TransitionAutomation NoTransition() => new()
    {
        OutgoingGain = 1.0f,
        IncomingGain = 1.0f,
        OutgoingLowGain = 1.0f,
        OutgoingMidGain = 1.0f,
        OutgoingHighGain = 1.0f,
        IncomingLowGain = 1.0f,
        IncomingMidGain = 1.0f,
        IncomingHighGain = 1.0f
    };
    
    /// <summary>
    /// Returns automation values for the outgoing track.
    /// </summary>
    public TransitionAutomation ForOutgoing() => new()
    {
        OutgoingGain = OutgoingGain,
        IncomingGain = 0,
        OutgoingLowGain = OutgoingLowGain,
        OutgoingMidGain = OutgoingMidGain,
        OutgoingHighGain = OutgoingHighGain,
        IncomingLowGain = 0,
        IncomingMidGain = 0,
        IncomingHighGain = 0
    };
    
    /// <summary>
    /// Returns automation values for the incoming track.
    /// </summary>
    public TransitionAutomation ForIncoming() => new()
    {
        OutgoingGain = 0,
        IncomingGain = IncomingGain,
        OutgoingLowGain = 0,
        OutgoingMidGain = 0,
        OutgoingHighGain = 0,
        IncomingLowGain = IncomingLowGain,
        IncomingMidGain = IncomingMidGain,
        IncomingHighGain = IncomingHighGain
    };
}
