using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Playlist;

namespace SLSKDONET.Services.Timeline;

/// <summary>
/// Maps the six Spotify-Mix-style preset names (Auto/Fade/Rise/Blend/Wave/Melt) onto a real
/// <see cref="TransitionModel"/> that <see cref="TransitionDsp"/> can build into an actual
/// NAudio sample-provider chain. "Auto" additionally consults
/// <see cref="TrackPairCompatibilityScorer"/> to pick a duration/type suited to how compatible
/// the two tracks actually are, rather than a single fixed default.
/// </summary>
public static class TransitionPresetLibrary
{
    public static readonly string[] PresetNames = { "Auto", "Fade", "Rise", "Blend", "Wave", "Melt", "Custom" };

    public static TransitionModel Build(string presetName, TrackPairCompatibilityScorer.PairScore? pairScore = null, int? durationBarsOverride = null)
    {
        var model = presetName switch
        {
            "Fade" => new TransitionModel { Type = TransitionType.Crossfade, DurationBeats = 16 * 4 },
            "Rise" => new TransitionModel
            {
                Type = TransitionType.FilterSweep,
                DurationBeats = 8 * 4,
                FilterSweepRising = true,
                FilterStartFrequency = 20_000f,
                FilterEndFrequency = 300f,
            },
            "Blend" => new TransitionModel { Type = TransitionType.EqSwap, DurationBeats = 16 * 4 },
            "Wave" => new TransitionModel { Type = TransitionType.WaveDuck, DurationBeats = 8 * 4, WaveDuckDepth = 0.45f },
            "Melt" => new TransitionModel
            {
                Type = TransitionType.EchoOut,
                DurationBeats = 16 * 4,
                EchoDecayFactor = 0.75f,
            },
            _ => BuildAuto(pairScore), // "Auto" and unrecognized/"Custom" (custom overrides are applied by the caller) fall back to Auto's heuristic.
        };

        if (durationBarsOverride.HasValue)
        {
            model.DurationBeats = durationBarsOverride.Value * 4.0;
        }

        return model;
    }

    private static TransitionModel BuildAuto(TrackPairCompatibilityScorer.PairScore? pairScore)
    {
        // No analysis data for the pair yet — safe, generic default.
        if (pairScore is not { } score)
        {
            return new TransitionModel { Type = TransitionType.Crossfade, DurationBeats = 16 * 4 };
        }

        // Highly compatible (harmonically locked, similar energy): a long, smooth crossfade
        // reads as the "natural" choice. Low compatibility: shorter, more decisive handoff.
        return score.CombinedScore switch
        {
            >= 70 => new TransitionModel { Type = TransitionType.Crossfade, DurationBeats = 16 * 4 },
            >= 45 => new TransitionModel { Type = TransitionType.EqSwap, DurationBeats = 8 * 4 },
            _ => new TransitionModel { Type = TransitionType.Crossfade, DurationBeats = 4 * 4 },
        };
    }

    /// <summary>
    /// Maps the DSP-facing <see cref="TransitionType"/> (Cut/Crossfade/EchoOut/FilterSweep/EqSwap/WaveDuck)
    /// onto <see cref="SLSKDONET.Services.Audio.TransitionType"/>, the smaller vocabulary
    /// <see cref="SLSKDONET.Services.Audio.TransitionEngine"/>'s automation-curve math understands —
    /// used for both the Mix editor's waveform overlay curves and AudioPlayerService's live
    /// per-tick gain during real playback. EchoOut/WaveDuck each have a dedicated automation
    /// curve (echo taps / duck pulses) approximating their real TransitionDsp provider's math.
    /// </summary>
    public static SLSKDONET.Services.Audio.TransitionType ToAutomationType(this TransitionType type) => type switch
    {
        TransitionType.Cut => SLSKDONET.Services.Audio.TransitionType.Cut,
        TransitionType.Crossfade => SLSKDONET.Services.Audio.TransitionType.Crossfade,
        TransitionType.FilterSweep => SLSKDONET.Services.Audio.TransitionType.FilterSweep,
        TransitionType.EqSwap => SLSKDONET.Services.Audio.TransitionType.EqSwap,
        TransitionType.EchoOut => SLSKDONET.Services.Audio.TransitionType.EchoOut,
        TransitionType.WaveDuck => SLSKDONET.Services.Audio.TransitionType.WaveDuck,
        _ => SLSKDONET.Services.Audio.TransitionType.Crossfade,
    };

    /// <summary>Applies custom-mode overrides (nullable fields — only present ones replace the preset default) on top of a built model.</summary>
    public static void ApplyCustomOverrides(
        TransitionModel model,
        float? echoDecayFactor, float? filterStartFrequency, float? filterEndFrequency)
    {
        if (echoDecayFactor.HasValue) model.EchoDecayFactor = echoDecayFactor.Value;
        if (filterStartFrequency.HasValue) model.FilterStartFrequency = filterStartFrequency.Value;
        if (filterEndFrequency.HasValue) model.FilterEndFrequency = filterEndFrequency.Value;
    }
}
