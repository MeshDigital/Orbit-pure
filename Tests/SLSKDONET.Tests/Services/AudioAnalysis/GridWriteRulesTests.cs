using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// The write-rule table from issue #221: a grid the DJ owns (GridSource=User or GridVerified) is
/// never silently replaced by a new analysis or Rekordbox grid — the new grid is stored as a
/// suggestion / conflict in AnalysedGridJson instead.
/// </summary>
public class GridWriteRulesTests
{
    [Theory]
    [InlineData("Analysis", false, "Analysis", GridWriteRules.Decision.Apply)]
    [InlineData("Analysis", false, "Rekordbox", GridWriteRules.Decision.Apply)]
    [InlineData("Rekordbox", false, "Analysis", GridWriteRules.Decision.KeepSuggestion)]
    [InlineData("Rekordbox", false, "Rekordbox", GridWriteRules.Decision.Apply)]
    [InlineData("User", false, "Analysis", GridWriteRules.Decision.KeepSuggestion)]
    [InlineData("User", false, "Rekordbox", GridWriteRules.Decision.KeepConflict)]
    [InlineData("Analysis", true, "Analysis", GridWriteRules.Decision.KeepSuggestion)]
    [InlineData("Analysis", true, "Rekordbox", GridWriteRules.Decision.KeepConflict)]
    [InlineData(null, false, "Analysis", GridWriteRules.Decision.Apply)]
    [InlineData(null, false, "Rekordbox", GridWriteRules.Decision.Apply)]
    public void Decide_ImplementsTheWriteRuleTable(string? currentSource, bool verified, string newSource, GridWriteRules.Decision expected)
    {
        Assert.Equal(expected, GridWriteRules.Decide(currentSource, verified, newSource));
    }

    [Fact]
    public void TryApplyAnalysisGrid_Applies_WhenCurrentIsAnalysis()
    {
        var f = new AudioFeaturesEntity { GridSource = "Analysis" };
        var beats = new[] { 0.0, 0.5, 1.0 };

        Assert.True(GridWriteRules.TryApplyAnalysisGrid(f, 120, beats, 0.0, 0.9f));

        Assert.Equal(120, f.Bpm);
        Assert.Equal(0.0, f.DownbeatOffsetSeconds, 4);
        Assert.Equal("Analysis", f.GridSource);
        Assert.Equal(0.9f, f.GridConfidence!.Value, 3);
        Assert.Null(f.AnalysedGridJson);
    }

    [Fact]
    public void TryApplyAnalysisGrid_KeepsUserGrid_AndStoresSuggestion()
    {
        var f = new AudioFeaturesEntity
        {
            GridSource = "User", GridVerified = true, Bpm = 174, BeatGridJson = "[0.4,0.7,1.0]",
            DownbeatOffsetSeconds = 0.4,
        };
        var beats = new[] { 0.0, 0.5, 1.0 };

        Assert.False(GridWriteRules.TryApplyAnalysisGrid(f, 120, beats, 0.0, 0.9f));

        Assert.Equal(174, f.Bpm);              // untouched
        Assert.Equal("[0.4,0.7,1.0]", f.BeatGridJson); // untouched
        Assert.Equal(0.4, f.DownbeatOffsetSeconds, 4); // untouched
        Assert.NotNull(f.AnalysedGridJson);    // suggestion kept
        Assert.Contains("120", f.AnalysedGridJson);
    }

    [Fact]
    public void TryApplyRekordboxGrid_KeepsUserGrid_AndFlagsConflict()
    {
        var f = new AudioFeaturesEntity
        {
            GridSource = "User", GridVerified = true, Bpm = 174, BeatGridJson = "[0.4,0.7,1.0]",
            DownbeatOffsetSeconds = 0.4,
        };
        var beats = new[] { 0.1, 0.6, 1.1 };

        Assert.False(GridWriteRules.TryApplyRekordboxGrid(f, 128, beats, 0.1));

        Assert.Equal(174, f.Bpm);
        Assert.Equal(0.4, f.DownbeatOffsetSeconds, 4);
        Assert.NotNull(f.AnalysedGridJson); // conflict suggestion kept
    }

    [Fact]
    public void TryApplyRekordboxGrid_Applies_WhenCurrentIsAnalysis()
    {
        var f = new AudioFeaturesEntity { GridSource = "Analysis" };
        var beats = new[] { 0.1, 0.6, 1.1 };

        Assert.True(GridWriteRules.TryApplyRekordboxGrid(f, 128, beats, 0.1));

        Assert.Equal(128, f.Bpm);
        Assert.Equal("Rekordbox", f.GridSource);
        Assert.Equal(1.0f, f.GridConfidence!.Value, 3); // Rekordbox gets fixed high confidence
    }
}
