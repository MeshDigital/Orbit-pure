using System;

namespace SLSKDONET.Engine.Cueing;

/// <summary>
/// Intents assigned to structural cue points.
/// </summary>
public enum CueIntent
{
    FirstDownbeat,
    MixIn,
    FirstBreakdown,
    FirstDrop,
    Bridge,
    SecondBreakdown,
    SecondDrop,
    MixOutWarning,
    FinalBeat,
    Unknown
}

/// <summary>
/// Classifies timeline points into DJ-relevant cue intents based on energy, timing, and pattern telemetry.
/// </summary>
public sealed class IntentClassifier
{
    /// <summary>
    /// Classifies the intent of a structural point.
    /// </summary>
    public CueIntent ClassifyIntent(
        double timestampSeconds,
        double totalDurationSeconds,
        double energyDelta,
        bool isHarmonicReset,
        bool isDrumDisappeared,
        bool isDrumReturned)
    {
        double trackRatio = timestampSeconds / totalDurationSeconds;

        // Intro / Outro timing overrides
        if (trackRatio < 0.15)
        {
            if (timestampSeconds < 5.0) return CueIntent.FirstDownbeat;
            return CueIntent.MixIn;
        }
        if (trackRatio > 0.85)
        {
            if (totalDurationSeconds - timestampSeconds < 5.0) return CueIntent.FinalBeat;
            return CueIntent.MixOutWarning;
        }

        // Drop conditions: Energy jump > 30%, or harmonic reset/drum return at high energy
        if (energyDelta > 0.30 || isDrumReturned)
        {
            return CueIntent.FirstDrop; // Will be mapped to first or second drop in CueGenerationService
        }

        // Breakdown conditions: Energy drop < -30%, or drum disappearance
        if (energyDelta < -0.30 || isDrumDisappeared)
        {
            return CueIntent.FirstBreakdown;
        }

        // Harmonic resets in the middle of track
        if (isHarmonicReset)
        {
            return CueIntent.Bridge;
        }

        return CueIntent.Unknown;
    }
}
