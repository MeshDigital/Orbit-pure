using SLSKDONET.Data.Entities;
using DspType = SLSKDONET.Models.Timeline.TransitionType;

namespace SLSKDONET.Services.Transitions;

/// <summary>
/// What a transition is for, in DJ words (rebuild plan D5, #204). One small vocabulary shared by
/// knowledge edges, ratings and the Sets lane. The executable "how" stays the DSP
/// <see cref="DspType"/> plus the editor's presets; this tag only says which kind of move it is.
/// </summary>
public enum TransitionIntent
{
    Blend,
    Cut,
    DropSwap,
    EchoOut,
    EnergyReset,
}

/// <summary>Maps the four older "kind of transition" vocabularies onto <see cref="TransitionIntent"/>.</summary>
public static class TransitionIntents
{
    public static string Label(this TransitionIntent intent) => intent switch
    {
        TransitionIntent.Cut => "Cut",
        TransitionIntent.DropSwap => "Drop swap",
        TransitionIntent.EchoOut => "Echo out",
        TransitionIntent.EnergyReset => "Energy reset",
        _ => "Blend",
    };

    /// <summary>The intent of an editor transition: its DSP type, refined by the preset that built it.</summary>
    public static TransitionIntent Of(DspType type, string? presetName = null) =>
        presetName is "Drop Sync" or "1-Bar Drop Swap"
            ? TransitionIntent.DropSwap
            : type switch
            {
                DspType.Cut => TransitionIntent.Cut,
                DspType.EchoOut => TransitionIntent.EchoOut,
                DspType.DoubleDrop => TransitionIntent.DropSwap,
                _ => TransitionIntent.Blend, // Crossfade, FilterSweep, EqSwap, WaveDuck
            };

    /// <summary>The intent a knowledge edge's (finer) type belongs to.</summary>
    public static TransitionIntent Of(KnowledgeTransitionType type) => type switch
    {
        KnowledgeTransitionType.Cut or KnowledgeTransitionType.Backspin => TransitionIntent.Cut,
        KnowledgeTransitionType.EchoOut => TransitionIntent.EchoOut,
        KnowledgeTransitionType.DropSwap or KnowledgeTransitionType.DoubleDrop => TransitionIntent.DropSwap,
        KnowledgeTransitionType.EnergyReset or KnowledgeTransitionType.GenreJump => TransitionIntent.EnergyReset,
        _ => TransitionIntent.Blend, // Blend, Mashup, AcapellaOverlay, StemMix
    };

    /// <summary>The intent of a legacy set-slot archetype (<c>SetTrackEntity.TransitionType</c>).</summary>
    public static TransitionIntent Of(TransitionArchetype archetype) => archetype switch
    {
        TransitionArchetype.QuickCut => TransitionIntent.Cut,
        TransitionArchetype.DropSwap or TransitionArchetype.BuildToDrop => TransitionIntent.DropSwap,
        TransitionArchetype.EnergyReset => TransitionIntent.EnergyReset,
        _ => TransitionIntent.Blend, // LongBlend, VocalToInstrumental, Custom
    };

    /// <summary>The knowledge-edge type an intent is stored under (one edge per intent and pair).</summary>
    public static KnowledgeTransitionType ToKnowledgeType(this TransitionIntent intent) => intent switch
    {
        TransitionIntent.Cut => KnowledgeTransitionType.Cut,
        TransitionIntent.DropSwap => KnowledgeTransitionType.DropSwap,
        TransitionIntent.EchoOut => KnowledgeTransitionType.EchoOut,
        TransitionIntent.EnergyReset => KnowledgeTransitionType.EnergyReset,
        _ => KnowledgeTransitionType.Blend,
    };
}
