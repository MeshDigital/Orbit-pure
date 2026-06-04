using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class PlaylistIntelligenceSparseMetadataTests
{
    [Fact]
    public void SuggestNextCandidateViewModel_WithSparseMetadata_UsesSafeDisplayFallbacks()
    {
        var sparseTrack = CreateSparseTrack();
        var candidate = new SuggestNextCandidateViewModel(sparseTrack, baseScore: 0.61, bonus: 0.03, isSavedDoubleSuggested: true);

        Assert.Equal("Unknown Title", candidate.TrackTitle);
        Assert.Equal("Unknown Artist", candidate.ArtistName);
        Assert.Equal("—", candidate.CamelotDisplay);
        Assert.Equal("—", candidate.BpmDisplay);
        Assert.Equal("—", candidate.EnergyRating);
        Assert.Equal("61%", candidate.BaseScoreDisplay);
        Assert.True(candidate.HasSuggestionReason);
        Assert.Equal("Boosted by your saved double history", candidate.SuggestionReason);
    }

    [Fact]
    public void SuggestNextCandidateViewModel_WithNullTrack_UsesSafeDisplayFallbacks()
    {
        var candidate = new SuggestNextCandidateViewModel(track: null);

        Assert.Equal("Unknown Title", candidate.TrackTitle);
        Assert.Equal("Unknown Artist", candidate.ArtistName);
        Assert.Equal("-", candidate.CamelotDisplay);
        Assert.Equal("-", candidate.BpmDisplay);
        Assert.Equal("-", candidate.EnergyRating);
        Assert.False(candidate.HasSuggestionReason);
        Assert.Equal(string.Empty, candidate.SuggestionReason);
    }

    [Fact]
    public void PlaylistUpgradeCandidateViewModel_WithSparseMetadata_UsesSafeDisplayFallbacks()
    {
        var sparseTrack = CreateSparseTrack();
        var candidate = new PlaylistUpgradeCandidateViewModel(
            sparseTrack,
            isSavedDoubleAligned: false,
            isBridgeCandidate: false,
            isReplacementCandidate: false,
            upgradeReason: null,
            baseScore: 0.58,
            priorBonus: 0.0);

        Assert.Equal("Unknown Title", candidate.ReplacementTrackTitle);
        Assert.Equal("Unknown Artist", candidate.ReplacementTrackArtist);
        Assert.Equal("—", candidate.ReplacementBpm);
        Assert.Equal("—", candidate.ReplacementEnergy);
        Assert.Equal("—", candidate.ReplacementKey);
        Assert.Equal("Scaffold candidate", candidate.UpgradeReason);
        Assert.Equal("Score 58%", candidate.ScoreLabel);
        Assert.Equal("Base 58%", candidate.ScoreBreakdownLabel);
    }

    [Fact]
    public void PlaylistUpgradeCandidateViewModel_ClampsSparseScoreInputs_ForStableOutput()
    {
        var sparseTrack = CreateSparseTrack();
        var candidate = new PlaylistUpgradeCandidateViewModel(
            sparseTrack,
            baseScore: 4.2,
            priorBonus: 0.8,
            upgradeReason: "   ");

        Assert.Equal(1.0, candidate.BaseScore, 6);
        Assert.Equal(0.2, candidate.PriorBonus, 6);
        Assert.True(candidate.HasPriorBonus);
        Assert.Equal("+20% prior", candidate.PriorBonusLabel);
        Assert.Equal("Scaffold candidate", candidate.UpgradeReason);
    }

    private static PlaylistTrackViewModel CreateSparseTrack()
    {
        var track = new PlaylistTrack
        {
            Artist = string.Empty,
            Title = string.Empty,
            MusicalKey = null,
            BPM = null,
            Energy = null,
            TrackUniqueHash = "sparse-track-hash",
        };

        return new PlaylistTrackViewModel(track);
    }
}
