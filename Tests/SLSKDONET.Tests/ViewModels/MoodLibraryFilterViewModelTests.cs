using System;
using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="MoodLibraryFilterViewModel"/>.
/// Validates Phase 13 AI-mood-based filtering with probability thresholds.
/// </summary>
public class MoodLibraryFilterViewModelTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal <see cref="PlaylistTrackViewModel"/> with a given mood tag and confidence.</summary>
    private static PlaylistTrackViewModel MakeTrack(string? moodTag, float moodConfidence = 0.8f)
    {
        var model = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test Artist",
            Title = "Test Track",
            MoodTag = moodTag,
            MoodConfidence = moodConfidence,
        };
        return new PlaylistTrackViewModel(model);
    }

    // ── AvailableMoods ────────────────────────────────────────────────────

    [Fact]
    public void AvailableMoods_ContainsAllPhase13Labels()
    {
        var expected = new[] { "Happy", "Aggressive", "Sad", "Relaxed", "Party", "Electronic" };
        foreach (var mood in expected)
            Assert.Contains(mood, MoodLibraryFilterViewModel.AvailableMoods);
    }

    // ── Default state ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultState_AllTracksPass()
    {
        var vm = new MoodLibraryFilterViewModel();
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));
        Assert.True(predicate(MakeTrack("Aggressive", 0.7f)));
        Assert.True(predicate(MakeTrack(null)));           // unclassified
        Assert.True(predicate(MakeTrack("Neutral")));      // neutral / no mood
    }

    // ── SelectedMoods filter ──────────────────────────────────────────────

    [Fact]
    public void SelectedMoods_FiltersOutNonMatchingTracks()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("Happy");
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));
        Assert.False(predicate(MakeTrack("Aggressive", 0.9f)));
        Assert.False(predicate(MakeTrack("Sad", 0.9f)));
    }

    [Fact]
    public void SelectedMoods_MultipleSelections_PassesAllMatching()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("Happy");
        vm.SelectedMoods.Add("Relaxed");
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));
        Assert.True(predicate(MakeTrack("Relaxed", 0.9f)));
        Assert.False(predicate(MakeTrack("Aggressive", 0.9f)));
    }

    [Fact]
    public void SelectedMoods_IsCaseInsensitive()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("happy");  // lowercase
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));   // capitalised in data
    }

    // ── MinMoodProbability ────────────────────────────────────────────────

    [Fact]
    public void MinMoodProbability_ExcludesLowConfidenceTracks()
    {
        var vm = new MoodLibraryFilterViewModel { MinMoodProbability = 0.7f };
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));
        Assert.False(predicate(MakeTrack("Happy", 0.5f)));   // below threshold
    }

    [Fact]
    public void MinMoodProbability_ClampedTo0And1()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.MinMoodProbability = 2.0f;
        Assert.Equal(1.0f, vm.MinMoodProbability);

        vm.MinMoodProbability = -1.0f;
        Assert.Equal(0.0f, vm.MinMoodProbability);
    }

    [Fact]
    public void MinMoodProbability_WithMoodFilter_BothConditionsMustPass()
    {
        var vm = new MoodLibraryFilterViewModel { MinMoodProbability = 0.8f };
        vm.SelectedMoods.Add("Happy");
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack("Happy", 0.9f)));    // right mood, high confidence
        Assert.False(predicate(MakeTrack("Happy", 0.6f)));   // right mood, low confidence
        Assert.False(predicate(MakeTrack("Sad", 0.9f)));     // wrong mood
    }

    // ── IncludeUnclassified ───────────────────────────────────────────────

    [Fact]
    public void IncludeUnclassified_False_ExcludesTracksWithNoMood()
    {
        var vm = new MoodLibraryFilterViewModel { IncludeUnclassified = false };
        var predicate = vm.GetFilterPredicate();

        Assert.False(predicate(MakeTrack(null)));
        Assert.False(predicate(MakeTrack(string.Empty)));
        Assert.False(predicate(MakeTrack("Neutral")));

        // Tracks WITH a mood tag should still pass
        Assert.True(predicate(MakeTrack("Happy", 0.9f)));
    }

    [Fact]
    public void IncludeUnclassified_True_PassesTracksWithNoMood()
    {
        var vm = new MoodLibraryFilterViewModel { IncludeUnclassified = true };
        var predicate = vm.GetFilterPredicate();

        Assert.True(predicate(MakeTrack(null)));
        Assert.True(predicate(MakeTrack("Neutral")));
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllFilters()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("Happy");
        vm.MinMoodProbability = 0.9f;
        vm.IncludeUnclassified = false;

        vm.Reset();

        Assert.Empty(vm.SelectedMoods);
        Assert.Equal(0.5f, vm.MinMoodProbability);
        Assert.True(vm.IncludeUnclassified);
    }

    // ── ToggleMood ────────────────────────────────────────────────────────

    [Fact]
    public void ToggleMood_AddsMoodWhenNotSelected()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.ToggleMood("Happy");
        Assert.Contains("Happy", vm.SelectedMoods);
    }

    [Fact]
    public void ToggleMood_RemovesMoodWhenAlreadySelected()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("Happy");
        vm.ToggleMood("Happy");
        Assert.DoesNotContain("Happy", vm.SelectedMoods);
    }

    [Fact]
    public void ToggleMood_IsCaseInsensitive()
    {
        var vm = new MoodLibraryFilterViewModel();
        vm.SelectedMoods.Add("Happy");
        vm.ToggleMood("happy");   // lowercase toggle should remove "Happy"
        Assert.Empty(vm.SelectedMoods);
    }

    // ── IsFilterActive ────────────────────────────────────────────────────

    [Fact]
    public void IsFilterActive_FalseByDefault_WhenNoSelectionsAndDefaultThreshold()
    {
        // With default threshold of 0.5 (not > 0.5), no moods selected, and unclassified included,
        // the filter is inactive – all tracks pass.
        var vm = new MoodLibraryFilterViewModel();
        Assert.False(vm.IsFilterActive);
    }

    [Fact]
    public void IsFilterActive_TrueWhenThresholdAboveDefault()
    {
        var vm = new MoodLibraryFilterViewModel { MinMoodProbability = 0.8f };
        Assert.True(vm.IsFilterActive);
    }

    [Fact]
    public void IsFilterActive_TrueWhenUnclassifiedExcluded()
    {
        var vm = new MoodLibraryFilterViewModel { IncludeUnclassified = false };
        Assert.True(vm.IsFilterActive);
    }
}
