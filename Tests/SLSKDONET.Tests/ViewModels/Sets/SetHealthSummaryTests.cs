using System;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using SLSKDONET.ViewModels.Sets;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Sets;

/// <summary>Set Health on the Sets page must never invent a score for missing inputs (F7).</summary>
public class SetHealthSummaryTests
{
    private static SlotCardViewModel Card(int position, string hash, double? bpm = 174, string? key = "8A",
        SetSlotRole role = SetSlotRole.Builder, bool inLibrary = true, double? target = null, double? energy = null)
        => new(new SetSlotInfo(Guid.NewGuid(), position, hash, hash.ToUpperInvariant(), "Artist", inLibrary, role, target, null, bpm, key, energy));

    private static SetHealthSummary Summarise(params SlotCardViewModel[] cards)
    {
        var chips = cards.Zip(cards.Skip(1), (a, b) => new TransitionChipViewModel(a, b, null)).ToList();
        return new SetHealthSummary(cards, chips);
    }

    [Fact]
    public void UnanalysedSet_WithoutRoles_HasNoScores_AndSaysWhatsMissing()
    {
        var health = Summarise(Card(0, "a", bpm: null, key: null), Card(1, "b", bpm: null, key: null));

        Assert.All(health.Metrics, m => Assert.False(m.HasValue));
        Assert.Equal("—", health.Overall.ValueText);
        Assert.Contains("Analyse", health.Transitions.Hint);
        Assert.Contains("Peak", health.PeakPlacement.Hint);
        Assert.Equal(2, health.Issues.Count(i => i.Text.Contains("not analysed")));
    }

    [Fact]
    public void AnalysedSet_ScoresTransitions_AndRolesWhenAPeakExists()
    {
        var health = Summarise(
            Card(0, "a", role: SetSlotRole.Opening),
            Card(1, "b", role: SetSlotRole.Peak),
            Card(2, "c", role: SetSlotRole.Reset),
            Card(3, "d", role: SetSlotRole.Closer));

        Assert.True(health.Transitions.HasValue);
        Assert.Equal(100, health.PeakPlacement.Value); // the Peak sits in the middle 60%
        Assert.Equal(100, health.Recovery.Value);      // and a Reset follows it
        Assert.True(health.Overall.HasValue);
    }

    [Fact]
    public void Issues_FlagMissingFiles_Duplicates_AndTransitionWarnings_WithSlotLinks()
    {
        var missing = Card(0, "gone", inLibrary: false);
        var first = Card(1, "a", bpm: 174);
        var clash = Card(2, "b", bpm: 120, key: "3B");
        var repeat = Card(3, "a", bpm: 174);

        var health = Summarise(missing, first, clash, repeat);

        Assert.Contains(health.Issues, i => i.SlotId == missing.SlotId && i.Text.Contains("not in the library"));
        Assert.Contains(health.Issues, i => i.SlotId == repeat.SlotId && i.Text.Contains("already earlier"));
        Assert.Contains(health.Issues, i => i.SlotId == clash.SlotId && i.Text.Contains("BPM"));
        Assert.Equal("4 issues", health.IssuesText);
    }

    [Fact]
    public void EnergyArc_KeepsGapsForUnknownValues()
    {
        var health = Summarise(Card(0, "a", target: 40, energy: 55), Card(1, "b"), Card(2, "c", target: 90));

        Assert.Equal(new double?[] { 40, null, 90 }, health.EnergyTargets);
        Assert.Equal(new double?[] { 55, null, null }, health.TrackEnergies);
    }
}
