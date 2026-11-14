using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Transitions;
using Xunit;
using DspType = SLSKDONET.Models.Timeline.TransitionType;

namespace SLSKDONET.Tests.Services.Transitions;

/// <summary>#204 D5: one intent tag over the four "kind of transition" vocabularies.</summary>
public class TransitionIntentsTests
{
    [Theory]
    [InlineData(DspType.Cut, "Fade", TransitionIntent.Cut)]
    [InlineData(DspType.EchoOut, "Custom", TransitionIntent.EchoOut)]
    [InlineData(DspType.DoubleDrop, "Custom", TransitionIntent.DropSwap)]
    [InlineData(DspType.EqSwap, "Drop Sync", TransitionIntent.DropSwap)]
    [InlineData(DspType.EqSwap, "1-Bar Drop Swap", TransitionIntent.DropSwap)]
    [InlineData(DspType.EqSwap, "Rolling", TransitionIntent.Blend)]
    [InlineData(DspType.FilterSweep, "Melt", TransitionIntent.Blend)]
    [InlineData(DspType.WaveDuck, null, TransitionIntent.Blend)]
    public void EditorTransitions_MapByTypeAndPreset(DspType type, string? preset, TransitionIntent expected)
        => Assert.Equal(expected, TransitionIntents.Of(type, preset));

    [Theory]
    [InlineData(KnowledgeTransitionType.Backspin, TransitionIntent.Cut)]
    [InlineData(KnowledgeTransitionType.DoubleDrop, TransitionIntent.DropSwap)]
    [InlineData(KnowledgeTransitionType.GenreJump, TransitionIntent.EnergyReset)]
    [InlineData(KnowledgeTransitionType.StemMix, TransitionIntent.Blend)]
    public void KnowledgeTypes_Collapse(KnowledgeTransitionType type, TransitionIntent expected)
        => Assert.Equal(expected, TransitionIntents.Of(type));

    [Theory]
    [InlineData(TransitionArchetype.QuickCut, TransitionIntent.Cut)]
    [InlineData(TransitionArchetype.BuildToDrop, TransitionIntent.DropSwap)]
    [InlineData(TransitionArchetype.VocalToInstrumental, TransitionIntent.Blend)]
    public void LegacyArchetypes_Map(TransitionArchetype archetype, TransitionIntent expected)
        => Assert.Equal(expected, TransitionIntents.Of(archetype));

    [Fact]
    public void EveryIntent_RoundTripsThroughItsKnowledgeType_AndHasALabel()
    {
        foreach (var intent in System.Enum.GetValues<TransitionIntent>())
        {
            Assert.Equal(intent, TransitionIntents.Of(intent.ToKnowledgeType()));
            Assert.False(string.IsNullOrWhiteSpace(intent.Label()));
        }
        Assert.Equal("Drop swap", TransitionIntent.DropSwap.Label());
    }
}
