using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Maintenance;

public class StylePlaylistPlannerTests
{
    private static StyleCandidate C(string hash, string? key, float conf, bool hasGenre = false) =>
        new(hash, key, conf, hasGenre);

    [Fact]
    public void Plan_GroupsByTopStyle_AndOrdersByConfidenceDescending()
    {
        var candidates = new[]
        {
            C("a", "Electronic---Deep House", 0.9f),
            C("b", "Electronic---Deep House", 0.7f),
            C("c", "Electronic---Deep House", 0.5f),
            C("d", "Electronic---Deep House", 0.6f),
            C("e", "Electronic---Deep House", 0.8f),
        };

        var plan = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions());

        var pl = Assert.Single(plan.Playlists);
        Assert.Equal("Electronic---Deep House", pl.StyleKey);
        Assert.Equal("Style · Deep House", pl.DisplayName);
        Assert.Equal(new[] { "a", "e", "b", "d", "c" }, pl.Hashes);
        Assert.Null(pl.ExistingPlaylistId);
    }

    [Fact]
    public void Plan_ExcludesAlreadyGenredTracks_AndCountsMissingAndBelowThreshold()
    {
        var candidates = new[]
        {
            C("a", "Electronic---Deep House", 0.9f),
            C("b", "Electronic---Deep House", 0.9f, hasGenre: true),   // already genred → excluded
            C("c", null, 0f),                                          // missing styles
            C("d", "Electronic---Deep House", 0.2f),                   // below threshold
            C("e", "Electronic---Deep House", 0.9f),
            C("f", "Electronic---Deep House", 0.9f),
            C("g", "Electronic---Deep House", 0.9f),
            C("h", "Electronic---Deep House", 0.9f),                   // 5 unclassified total (a,e,f,g,h)
        };

        var plan = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions());

        Assert.Equal(1, plan.BelowThreshold);
        Assert.Equal(1, plan.MissingStyles);
        Assert.Single(plan.Playlists);
        Assert.Equal(new[] { "a", "e", "f", "g", "h" }, plan.Playlists[0].Hashes);
    }

    [Fact]
    public void Plan_ReusesExistingPlaylist_AndLeavesOutTooSmallGroups()
    {
        var existing = new Dictionary<string, Guid> { ["Electronic---Deep House"] = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var candidates = new[]
        {
            C("a", "Electronic---Deep House", 0.9f),
            C("b", "Electronic---Deep House", 0.9f),
            C("c", "Electronic---Deep House", 0.9f),
            C("d", "Electronic---Deep House", 0.9f),
            C("e", "Electronic---Deep House", 0.9f),
            C("f", "Electronic---Techno", 0.9f),       // too small (group of 1)
        };

        var plan = StylePlaylistPlanner.Plan(candidates, existing, new StylePlanOptions());

        Assert.Equal(1, plan.TooSmallGroups);
        var pl = Assert.Single(plan.Playlists);
        Assert.Equal(existing.Values.First(), pl.ExistingPlaylistId);
    }

    [Fact]
    public void Plan_NameCollisionAcrossParents_GetsParentSuffix()
    {
        var candidates = new[]
        {
            C("a", "Electronic---Ambient", 0.9f), C("b", "Electronic---Ambient", 0.9f),
            C("c", "Electronic---Ambient", 0.9f), C("d", "Electronic---Ambient", 0.9f),
            C("e", "Electronic---Ambient", 0.9f),
            C("f", "Experimental---Ambient", 0.9f), C("g", "Experimental---Ambient", 0.9f),
            C("h", "Experimental---Ambient", 0.9f), C("i", "Experimental---Ambient", 0.9f),
            C("j", "Experimental---Ambient", 0.9f),
        };

        var plan = StylePlaylistPlanner.Plan(candidates, new Dictionary<string, Guid>(), new StylePlanOptions());

        Assert.Equal(2, plan.Playlists.Count);
        Assert.Contains(plan.Playlists, p => p.DisplayName == "Style · Ambient (Electronic)");
        Assert.Contains(plan.Playlists, p => p.DisplayName == "Style · Ambient (Experimental)");
    }

    [Fact]
    public void GenreFillProposals_MapConfidenceAndSkipGenredTracks()
    {
        var candidates = new[]
        {
            C("a", "Electronic---Deep House", 0.8f),
            C("b", "Electronic---Techno", 0.4f),
            C("c", "Electronic---Deep House", 0.9f, hasGenre: true),
            C("d", null, 0f),
        };

        var proposals = StylePlaylistPlanner.GenreFillProposals(candidates);

        Assert.Equal(2, proposals.Count);
        var high = proposals.First(p => p.Hash == "a");
        Assert.Equal(ProposalConfidence.High, high.Confidence);
        Assert.True(high.DefaultSelected);
        Assert.Equal("Deep House", high.Changes[0].Value);
        Assert.Equal(TagField.Genre, high.Changes[0].Field);

        var medium = proposals.First(p => p.Hash == "b");
        Assert.Equal(ProposalConfidence.Medium, medium.Confidence);
        Assert.False(medium.DefaultSelected);
    }
}
