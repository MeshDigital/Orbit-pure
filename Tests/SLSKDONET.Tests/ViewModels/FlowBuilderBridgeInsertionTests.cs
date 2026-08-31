using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Models.Flow;
using SLSKDONET.Models.Musical;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class FlowBuilderBridgeInsertionTests
{
    [Fact]
    public void ExpandOrderedTracksWithDuplicates_SkipDuplicateTracksUnchecked_KeepsBothCopies()
    {
        // Combine Playlists' "Skip duplicate tracks" checkbox was a no-op when unchecked: the
        // post-optimize remap collapsed to one instance per hash regardless. This pins the fix —
        // both copies of a hash that appears twice in combinedTracks must survive.
        var shared = new PlaylistTrack { TrackUniqueHash = "shared", Title = "A" };
        var sharedCopy = new PlaylistTrack { TrackUniqueHash = "shared", Title = "A" };
        var unique = new PlaylistTrack { TrackUniqueHash = "unique", Title = "B" };
        var combinedTracks = new List<PlaylistTrack> { shared, unique, sharedCopy };
        var orderedHashes = new List<string> { "unique", "shared" };

        var result = FlowBuilderViewModel.ExpandOrderedTracksWithDuplicates(orderedHashes, combinedTracks);

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Count(t => t.TrackUniqueHash == "shared"));
        Assert.Single(result, t => t.TrackUniqueHash == "unique");
    }

    [Fact]
    public void ExpandOrderedTracksWithDuplicates_NoDuplicateHashes_DegradesToOldBehavior()
    {
        var a = new PlaylistTrack { TrackUniqueHash = "a", Title = "A" };
        var b = new PlaylistTrack { TrackUniqueHash = "b", Title = "B" };
        var combinedTracks = new List<PlaylistTrack> { a, b };
        var orderedHashes = new List<string> { "b", "a" };

        var result = FlowBuilderViewModel.ExpandOrderedTracksWithDuplicates(orderedHashes, combinedTracks);

        Assert.Equal(new[] { "b", "a" }, result.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public void ExpandOrderedTracksWithDuplicates_HashMissingFromOrder_IsAppendedAtEnd()
    {
        // Mirrors the optimizer's "unanalyzed tracks appended at end" contract.
        var ordered = new PlaylistTrack { TrackUniqueHash = "ordered", Title = "A" };
        var unanalyzed = new PlaylistTrack { TrackUniqueHash = "unanalyzed", Title = "B" };
        var combinedTracks = new List<PlaylistTrack> { ordered, unanalyzed };
        var orderedHashes = new List<string> { "ordered" };

        var result = FlowBuilderViewModel.ExpandOrderedTracksWithDuplicates(orderedHashes, combinedTracks);

        Assert.Equal(new[] { "ordered", "unanalyzed" }, result.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public void DetermineBridgeInsertIndex_ReturnsMinusTwo_WhenBridgeAlreadyExists()
    {
        var hashes = new List<string> { "A", "X", "B" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(-2, index);
    }

    [Fact]
    public void DetermineBridgeInsertIndex_ReturnsMinusOne_WhenNeitherEndpointExists()
    {
        var hashes = new List<string> { "K", "L", "M" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(-1, index);
    }

    [Fact]
    public void DetermineBridgeInsertIndex_InsertsBeforeTo_WhenPairInOrder()
    {
        var hashes = new List<string> { "A", "B", "C" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(1, index);
    }

    [Fact]
    public void DetermineBridgeInsertIndex_InsertsAfterFrom_WhenToNotAfterFrom()
    {
        var hashes = new List<string> { "B", "A", "C" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(2, index);
    }

    [Fact]
    public void DetermineBridgeInsertIndex_InsertsAfterFrom_WhenOnlyFromExists()
    {
        var hashes = new List<string> { "A", "C", "D" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(1, index);
    }

    [Fact]
    public void DetermineBridgeInsertIndex_InsertsAtTo_WhenOnlyToExists()
    {
        var hashes = new List<string> { "C", "B", "D" };

        var index = FlowBuilderViewModel.DetermineBridgeInsertIndex(hashes, "A", "B", "X");

        Assert.Equal(1, index);
    }

    [Fact]
    public void BridgeMatchesTransitionStyleFilter_ReturnsTrue_ForAllStyles()
    {
        var matches = FlowBuilderViewModel.BridgeMatchesTransitionStyleFilter(TransitionStyle.DropSwap, "All styles");

        Assert.True(matches);
    }

    [Fact]
    public void BridgeMatchesTransitionStyleFilter_ReturnsTrue_WhenStyleIsNull()
    {
        var matches = FlowBuilderViewModel.BridgeMatchesTransitionStyleFilter(null, "Risky Clash");

        Assert.True(matches);
    }

    [Fact]
    public void BridgeMatchesTransitionStyleFilter_ReturnsTrue_ForMatchingStyle()
    {
        var matches = FlowBuilderViewModel.BridgeMatchesTransitionStyleFilter(TransitionStyle.EnergyLift, "Energy Lift");

        Assert.True(matches);
    }

    [Fact]
    public void BridgeMatchesTransitionStyleFilter_ReturnsFalse_ForNonMatchingStyle()
    {
        var matches = FlowBuilderViewModel.BridgeMatchesTransitionStyleFilter(TransitionStyle.DropSwap, "Smooth Blend");

        Assert.False(matches);
    }

    [Fact]
    public void SummarizeTransitionStyleDelta_FormatsPositiveAndNegativeStorytelling()
    {
        var current = new Dictionary<TransitionStyle, int>
        {
            [TransitionStyle.SmoothBlend] = 1,
            [TransitionStyle.RiskyClash] = 3,
            [TransitionStyle.EnergyLift] = 0,
        };
        var proposed = new Dictionary<TransitionStyle, int>
        {
            [TransitionStyle.SmoothBlend] = 4,
            [TransitionStyle.RiskyClash] = 1,
            [TransitionStyle.EnergyLift] = 1,
        };

        var summary = FlowBuilderViewModel.SummarizeTransitionStyleDelta(current, proposed);

        Assert.Equal("+3 smooth blends  ·  -2 risky clashes  ·  +1 energy lift", summary);
    }

    [Fact]
    public void SummarizeTransitionStyleDelta_ReturnsFallbackWhenMixUnchanged()
    {
        var current = new Dictionary<TransitionStyle, int>
        {
            [TransitionStyle.SmoothBlend] = 2,
            [TransitionStyle.RiskyClash] = 1,
        };
        var proposed = new Dictionary<TransitionStyle, int>
        {
            [TransitionStyle.SmoothBlend] = 2,
            [TransitionStyle.RiskyClash] = 1,
        };

        var summary = FlowBuilderViewModel.SummarizeTransitionStyleDelta(current, proposed);

        Assert.Equal("A10 keeps the same transition-style mix while improving overall flow.", summary);
    }

    [Fact]
    public void ComputeSuggestedFlowStyleImpact_ReturnsCorrectCounts_ForSimpleSwap()
    {
        var currentTransitions = new[]
        {
            new FlowBuilderViewModel.TransitionStyleEvaluation(0, "A", "B", TransitionStyle.SmoothBlend, "Smooth Blend", "steady"),
            new FlowBuilderViewModel.TransitionStyleEvaluation(1, "B", "C", TransitionStyle.RiskyClash, "Risky Clash", "jump"),
        };
        var proposedTransitions = new[]
        {
            new FlowBuilderViewModel.TransitionStyleEvaluation(0, "A", "C", TransitionStyle.SmoothBlend, "Smooth Blend", "steady"),
            new FlowBuilderViewModel.TransitionStyleEvaluation(1, "C", "B", TransitionStyle.EnergyLift, "Energy Lift", "lift"),
        };

        var impact = FlowBuilderViewModel.ComputeSuggestedFlowStyleImpact(
            currentTransitions,
            proposedTransitions,
            0.72,
            TimeSpan.FromMilliseconds(123));

        Assert.Equal(1, impact.CurrentCounts[TransitionStyle.SmoothBlend]);
        Assert.Equal(1, impact.CurrentCounts[TransitionStyle.RiskyClash]);
        Assert.Equal(1, impact.ProposedCounts[TransitionStyle.SmoothBlend]);
        Assert.Equal(1, impact.ProposedCounts[TransitionStyle.EnergyLift]);
        Assert.Equal(-1, impact.DeltaCounts[TransitionStyle.RiskyClash]);
        Assert.Equal(1, impact.DeltaCounts[TransitionStyle.EnergyLift]);
        Assert.Equal(2, impact.AffectedTransitions.Count);
        Assert.Equal(123d, impact.RefreshElapsed?.TotalMilliseconds);
    }

    [Fact]
    public void SuggestedFlowImpactViewModel_ConstructsRowsAndTransitions()
    {
        var impact = new SuggestedFlowStyleImpact(
            new Dictionary<TransitionStyle, int> { [TransitionStyle.SmoothBlend] = 1 },
            new Dictionary<TransitionStyle, int> { [TransitionStyle.SmoothBlend] = 3 },
            new Dictionary<TransitionStyle, int> { [TransitionStyle.SmoothBlend] = 2 },
            new[]
            {
                new SuggestedFlowAffectedTransition(0, "A", "B", TransitionStyle.RiskyClash, TransitionStyle.SmoothBlend, "Smooth Blend", "steadier handoff")
            },
            "+2 smooth blends",
            0.74);

        var vm = new SuggestedFlowImpactViewModel(impact);

        Assert.Equal("+2 smooth blends", vm.SummaryText);
        Assert.Equal("Avg flow 74%", vm.AverageTransitionScoreDisplay);
        Assert.Single(vm.Rows);
        Assert.Single(vm.AffectedTransitions);
        Assert.Contains("Risky Clash", vm.AffectedTransitions.Single().StyleChangeLabel);
    }

    [Fact]
    public void IsFlowBuilderPreviewEnabledForThisInstall_IsDeterministicAndClamped()
    {
        var config = new AppConfig
        {
            EnableFlowBuilderSuggestedFlowPreview = true,
            FlowBuilderSuggestedFlowPreviewRolloutPercent = 100,
        };

        Assert.True(config.IsFlowBuilderPreviewEnabledForThisInstall("orbit-install"));

        config.FlowBuilderSuggestedFlowPreviewRolloutPercent = 0;
        Assert.False(config.IsFlowBuilderPreviewEnabledForThisInstall("orbit-install"));
    }
}
