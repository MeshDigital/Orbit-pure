using System;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services.Transitions;

/// <summary>
/// Display label and provenance metadata for one outgoing→incoming transition in the queue or
/// Now Playing view (Epic #517 / #530).
/// </summary>
public sealed record TransitionLabelInfo(
    string Text,
    bool IsFromEarlierMix,
    string? EarlierMixSourceName = null);

/// <summary>
/// Pure formatter turning a <see cref="ResolvedTransition"/> into the concise human label shown
/// in the queue and in Now Playing ("Auto · phrase-aligned", "Auto · echo-out · tempo jump",
/// "Yours · cue to cue", "From your earlier mix").
/// </summary>
public static class TransitionLabelFormatter
{
    public static TransitionLabelInfo Format(
        ResolvedTransition resolved,
        double? fallbackOutgoingBpm = null,
        double? fallbackIncomingBpm = null)
    {
        if (resolved is null)
            return new TransitionLabelInfo("Auto · phrase-aligned", IsFromEarlierMix: false);

        return resolved.Source switch
        {
            ResolvedTransitionSource.PairDefault => new TransitionLabelInfo(
                "From your earlier mix",
                IsFromEarlierMix: true,
                EarlierMixSourceName: resolved.EarlierMixSourceName),

            ResolvedTransitionSource.Container => FormatContainer(resolved),

            _ => FormatAuto(resolved, fallbackOutgoingBpm, fallbackIncomingBpm),
        };
    }

    private static TransitionLabelInfo FormatContainer(ResolvedTransition resolved)
    {
        if (string.Equals(resolved.Technique, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return FormatAuto(resolved, null, null);
        }

        if (resolved.SavedTransition is { SourceTriggerSeconds: not null, TargetTriggerSeconds: not null })
        {
            return new TransitionLabelInfo("Yours · cue to cue", IsFromEarlierMix: false);
        }

        return new TransitionLabelInfo($"Yours · {FriendlyTechnique(resolved.Technique)}", IsFromEarlierMix: false);
    }

    private static TransitionLabelInfo FormatAuto(
        ResolvedTransition resolved,
        double? fallbackOutgoingBpm,
        double? fallbackIncomingBpm)
    {
        double outBpm = resolved.OutgoingStructure?.Bpm is > 0 ? resolved.OutgoingStructure.Bpm : (fallbackOutgoingBpm ?? 0);
        double inBpm = resolved.IncomingStructure?.Bpm is > 0 ? resolved.IncomingStructure.Bpm : (fallbackIncomingBpm ?? 0);

        if (outBpm > 0 && inBpm > 0 && Math.Abs(outBpm - inBpm) / outBpm > 0.06)
        {
            return new TransitionLabelInfo("Auto · echo-out · tempo jump", IsFromEarlierMix: false);
        }

        if (resolved.AutoPlan is not null)
        {
            if (resolved.OutgoingStructure is { BeatGrid: null or { Count: 0 } }
                || resolved.IncomingStructure is { BeatGrid: null or { Count: 0 } })
            {
                return new TransitionLabelInfo("Auto · echo-out", IsFromEarlierMix: false);
            }

            return new TransitionLabelInfo("Auto · phrase-aligned", IsFromEarlierMix: false);
        }

        if (resolved.OutgoingStructure is { BeatGrid: null or { Count: 0 } }
            || resolved.IncomingStructure is { BeatGrid: null or { Count: 0 } })
        {
            return new TransitionLabelInfo("Auto · echo-out", IsFromEarlierMix: false);
        }

        return new TransitionLabelInfo("Auto · phrase-aligned", IsFromEarlierMix: false);
    }

    internal static string FriendlyTechnique(string? technique) => technique switch
    {
        "Cut" => "cut",
        "Crossfade" or "Fade" => "crossfade",
        "EchoOut" or "Melt" => "echo-out",
        "FilterSweep" or "Rise" => "filter sweep",
        "EqSwap" or "Blend" or "Rolling" or "Drop Sync" or "Relaxed" or "1-Bar Drop Swap" => "EQ swap",
        "Loop" or "DoubleDrop" => "loop",
        "Wave" or "WaveDuck" => "wave",
        null or "" or "Auto" => "phrase-aligned",
        _ => technique.ToLowerInvariant(),
    };
}
