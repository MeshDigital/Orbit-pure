using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression coverage for a real, live-verified bug: TrackListView.axaml's track list
/// ItemsControl binds to FilteredTracks, not CurrentProjectTracks. For a real DB-backed
/// playlist, RefreshFilteredTracks assigns FilteredTracks a brand-new VirtualizedTrackCollection
/// loaded straight from the database — entirely different PlaylistTrackViewModel instances from
/// whatever CurrentProjectTracks held at the time. UpdateMixTransitionBadgesAsync used to only
/// ever mutate CurrentProjectTracks' instances, so toggling "+ Mix" on then silently produced zero
/// visible badges for any real playlist (confirmed live: launched the app, opened a real playlist,
/// toggled Mix mode on — no badges rendered, and none of the six preset names appeared anywhere in
/// the UI Automation tree). This only worked for the separate in-memory/smart-playlist path, which
/// happens to reuse CurrentProjectTracks' own instances for FilteredTracks.
/// </summary>
public class TrackListViewModelMixBadgeTests
{
    private static TrackListViewModel CreateSut(ITransitionRepository transitionRepository)
    {
        var sut = (TrackListViewModel)RuntimeHelpers_GetUninitializedObject(typeof(TrackListViewModel));
        SetField(sut, "_transitionRepository", transitionRepository);
        return sut;
    }

    private static object RuntimeHelpers_GetUninitializedObject(Type t)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);

    private static PlaylistTrackViewModel CreateTrack(Guid playlistId)
        => new(new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            Artist = "Artist",
            Title = "Title",
        });

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static object? GetField(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        return field.GetValue(instance);
    }

    private static Task InvokeUpdateMixTransitionBadgesAsync(TrackListViewModel sut)
    {
        var method = typeof(TrackListViewModel).GetMethod("UpdateMixTransitionBadgesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UpdateMixTransitionBadgesAsync not found");
        return (Task)method.Invoke(sut, null)!;
    }

    [Fact]
    public async Task UpdateMixTransitionBadgesAsync_UsesFilteredTracks_NotCurrentProjectTracks()
    {
        var transitionRepo = new Mock<ITransitionRepository>();
        transitionRepo.Setup(r => r.GetTransitionsForPlaylistAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<PlaylistTrackTransition>());

        var sut = CreateSut(transitionRepo.Object);

        var playlistId = Guid.NewGuid();
        var first = CreateTrack(playlistId);
        var second = CreateTrack(playlistId);

        // FilteredTracks is what the view actually renders — set directly (bypassing the property
        // setter's own scheduling, so this test controls exactly when the badge computation runs).
        SetField(sut, "_filteredTracks", new ObservableCollection<PlaylistTrackViewModel> { first, second });
        SetField(sut, "_isMixModeEnabled", true);

        // CurrentProjectTracks deliberately stays empty (GetUninitializedObject skips field
        // initializers, so it must be set explicitly rather than left null) — the old, buggy
        // implementation read from here and would find nothing, leaving both tracks' badges off.
        SetField(sut, "_currentProjectTracks", new ObservableCollection<PlaylistTrackViewModel>());
        Assert.Empty(sut.CurrentProjectTracks);

        await InvokeUpdateMixTransitionBadgesAsync(sut);

        Assert.True(first.ShowMixTransitionBadge, "First track's badge should be visible — it's not the last track in FilteredTracks.");
        Assert.Equal("Auto", first.TransitionPresetLabel);
        Assert.Equal(second.Id, first.NextPlaylistTrackId);

        Assert.False(second.ShowMixTransitionBadge, "Last track in the list must not show a badge — there's no next track to transition into.");
    }

    [Fact]
    public async Task UpdateMixTransitionBadgesAsync_SavedTransition_UsesItsPresetLabel()
    {
        var playlistId = Guid.NewGuid();
        var first = CreateTrack(playlistId);
        var second = CreateTrack(playlistId);

        var saved = new PlaylistTrackTransition
        {
            OutgoingPlaylistTrackId = first.Id,
            IncomingPlaylistTrackId = second.Id,
            PresetName = "Wave",
        };

        var transitionRepo = new Mock<ITransitionRepository>();
        transitionRepo.Setup(r => r.GetTransitionsForPlaylistAsync(playlistId))
            .ReturnsAsync(new List<PlaylistTrackTransition> { saved });

        var sut = CreateSut(transitionRepo.Object);
        SetField(sut, "_filteredTracks", new ObservableCollection<PlaylistTrackViewModel> { first, second });
        SetField(sut, "_isMixModeEnabled", true);

        await InvokeUpdateMixTransitionBadgesAsync(sut);

        Assert.Equal("Wave", first.TransitionPresetLabel);
    }

    [Fact]
    public async Task IsMixModeEnabled_TurnedOff_HidesAllBadges()
    {
        // Regression test for a real, live-verified bug: the IsMixModeEnabled setter only
        // recomputed badges when turning Mix mode ON (`if (changed && value)`), so turning "+ Mix"
        // back off never re-ran UpdateMixTransitionBadgesAsync — every row's badge stayed visibly
        // stuck on even though Mix mode was now off.
        var transitionRepo = new Mock<ITransitionRepository>();
        transitionRepo.Setup(r => r.GetTransitionsForPlaylistAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<PlaylistTrackTransition>());

        var sut = CreateSut(transitionRepo.Object);
        var playlistId = Guid.NewGuid();
        var first = CreateTrack(playlistId);
        var second = CreateTrack(playlistId);

        SetField(sut, "_filteredTracks", new ObservableCollection<PlaylistTrackViewModel> { first, second });
        SetField(sut, "_currentProjectTracks", new ObservableCollection<PlaylistTrackViewModel>());

        // Simulate the state right before the user clicks "+ Mix" again to turn it off: mode was
        // on, and the badge was already showing.
        SetField(sut, "_isMixModeEnabled", true);
        first.ShowMixTransitionBadge = true;

        sut.IsMixModeEnabled = false;
        // The setter's recompute is fire-and-forget (`_ = UpdateMixTransitionBadgesAsync()`) —
        // give it a beat to complete against the fully in-memory mock.
        await Task.Delay(50);

        Assert.False(first.ShowMixTransitionBadge, "Badge must hide once Mix mode is turned off.");
    }
}
