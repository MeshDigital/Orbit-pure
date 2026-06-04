using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Similarity;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class SidebarViewModelSyncTests
{
    [Fact]
    public void RightPanelPlayerContent_SetsActiveTabToPlayer()
    {
        var rightPanel = new RightPanelService();
        var playerVm = CreateUninitializedPlayerVm();
        var similarVm = CreateSimilarTracksVm();
        using var sut = new SidebarViewModel(rightPanel, playerVm, similarVm);

        rightPanel.OpenPanel(playerVm, "NOW PLAYING", "🎵");

        Assert.Equal(SidebarTab.Player, sut.ActiveTab);
        Assert.True(sut.IsPlayerTab);
    }

    [Fact]
    public void InspectorContext_PrimesSimilarityAndCanBeRestored()
    {
        var rightPanel = new RightPanelService();
        var playerVm = CreateUninitializedPlayerVm();
        var similarVm = CreateSimilarTracksVm();
        using var sut = new SidebarViewModel(rightPanel, playerVm, similarVm);

        var inspectorTrack = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Inspector Artist",
            Title = "Inspector Track",
            TrackUniqueHash = "inspector-hash"
        });

        rightPanel.OpenPanel(inspectorTrack, "TRACK INSPECTOR", "🔬");

        Assert.Equal(SidebarTab.Inspector, sut.ActiveTab);
        Assert.Equal("inspector-hash", similarVm.SeedTrackHash);

        sut.SwitchToSimilarityCommand.Execute().Subscribe();

        Assert.Equal(SidebarTab.Similarity, sut.ActiveTab);
        Assert.Same(similarVm, rightPanel.CurrentPanelVm);

        sut.SwitchToInspectorCommand.Execute().Subscribe();

        Assert.Equal(SidebarTab.Inspector, sut.ActiveTab);
        Assert.Same(inspectorTrack, rightPanel.CurrentPanelVm);
    }

    [Fact]
    public void PrimeFromInspectorContext_LibraryDoubleInspectorWithoutTracks_ShowsGuidance()
    {
        var similarVm = CreateSimilarTracksVm();
        var libraryVm = CreateUninitializedLibraryVm();

        similarVm.PrimeFromInspectorContext(new LibraryDoubleInspectorViewModel(libraryVm, NullLogger.Instance));

        Assert.Null(similarVm.SeedTrackHash);
        Assert.Equal("Double Inspector is open. Select a library track to generate similar suggestions.", similarVm.StatusMessage);
    }

    [Fact]
    public void PrimeFromInspectorContext_PlaylistIntelligenceWithoutTracks_ShowsGuidance()
    {
        var similarVm = CreateSimilarTracksVm();
        var libraryVm = CreateUninitializedLibraryVm();

        similarVm.PrimeFromInspectorContext(new PlaylistIntelligenceViewModel(libraryVm));

        Assert.Null(similarVm.SeedTrackHash);
        Assert.Equal("Playlist Intelligence is open. Select a library track to generate similar suggestions.", similarVm.StatusMessage);
    }

    [Fact]
    public void PrimeFromInspectorContext_LibraryDoubleInspector_UsesSelectedTrack()
    {
        var similarVm = CreateSimilarTracksVm();
        var selected = CreateTrack("selected-hash", "Selected Artist", "Selected Track");
        var libraryVm = CreateLibraryVmWithTracks(selected: selected);

        similarVm.PrimeFromInspectorContext(new LibraryDoubleInspectorViewModel(libraryVm, NullLogger.Instance));

        Assert.Equal("selected-hash", similarVm.SeedTrackHash);
        Assert.Equal("Finding matches for Selected Artist — Selected Track", similarVm.StatusMessage);
    }

    [Fact]
    public void PrimeFromInspectorContext_PlaylistIntelligence_UsesFilteredFallbackTrack()
    {
        var similarVm = CreateSimilarTracksVm();
        var filtered = CreateTrack("filtered-hash", "Filtered Artist", "Filtered Track");
        var libraryVm = CreateLibraryVmWithTracks(filtered: filtered);

        similarVm.PrimeFromInspectorContext(new PlaylistIntelligenceViewModel(libraryVm));

        Assert.Equal("filtered-hash", similarVm.SeedTrackHash);
        Assert.Equal("Finding matches for Filtered Artist — Filtered Track", similarVm.StatusMessage);
    }

    [Fact]
    public void PrimeFromInspectorContext_PlaylistIntelligence_UsesCurrentProjectFallbackTrack()
    {
        var similarVm = CreateSimilarTracksVm();
        var currentProject = CreateTrack("project-hash", "Project Artist", "Project Track");
        var libraryVm = CreateLibraryVmWithTracks(currentProject: currentProject);

        similarVm.PrimeFromInspectorContext(new PlaylistIntelligenceViewModel(libraryVm));

        Assert.Equal("project-hash", similarVm.SeedTrackHash);
        Assert.Equal("Finding matches for Project Artist — Project Track", similarVm.StatusMessage);
    }

    [Fact]
    public void PlaylistIntelligenceInspectorVm_PrimesSimilarityThroughSidebarFlow()
    {
        var rightPanel = new RightPanelService();
        var playerVm = CreateUninitializedPlayerVm();
        var similarVm = CreateSimilarTracksVm();
        using var sut = new SidebarViewModel(rightPanel, playerVm, similarVm);

        var selected = CreateTrack("wrapper-selected-hash", "Wrapper Artist", "Wrapper Track");
        var libraryVm = CreateLibraryVmWithTracks(selected: selected);
        var intelligence = new PlaylistIntelligenceViewModel(libraryVm);

        rightPanel.OpenPanel(intelligence, "INTELLIGENCE", "🧠");

        Assert.Equal(SidebarTab.Inspector, sut.ActiveTab);
        Assert.Equal("wrapper-selected-hash", similarVm.SeedTrackHash);

        sut.SwitchToSimilarityCommand.Execute().Subscribe();

        Assert.Equal(SidebarTab.Similarity, sut.ActiveTab);
        Assert.Same(similarVm, rightPanel.CurrentPanelVm);
        Assert.Equal("wrapper-selected-hash", similarVm.SeedTrackHash);
    }

    private static PlayerViewModel CreateUninitializedPlayerVm()
        => (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

    private static LibraryViewModel CreateUninitializedLibraryVm()
        => (LibraryViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryViewModel));

    private static LibraryViewModel CreateLibraryVmWithTracks(
        PlaylistTrackViewModel? selected = null,
        PlaylistTrackViewModel? filtered = null,
        PlaylistTrackViewModel? currentProject = null)
    {
        var vm = CreateUninitializedLibraryVm();
        var trackList = CreateUninitializedTrackListVm(selected, filtered, currentProject);
        SetBackingField(vm, "<Tracks>k__BackingField", trackList);
        return vm;
    }

    private static TrackListViewModel CreateUninitializedTrackListVm(
        PlaylistTrackViewModel? selected,
        PlaylistTrackViewModel? filtered,
        PlaylistTrackViewModel? currentProject)
    {
        var vm = (TrackListViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TrackListViewModel));

        var selectedTracks = selected is null
            ? new ObservableCollection<PlaylistTrackViewModel>()
            : new ObservableCollection<PlaylistTrackViewModel>(new[] { selected });
        var filteredTracks = filtered is null
            ? new ObservableCollection<PlaylistTrackViewModel>()
            : new ObservableCollection<PlaylistTrackViewModel>(new[] { filtered });
        var currentProjectTracks = currentProject is null
            ? new ObservableCollection<PlaylistTrackViewModel>()
            : new ObservableCollection<PlaylistTrackViewModel>(new[] { currentProject });

        SetField(vm, "_selectedTracks", selectedTracks);
        SetField(vm, "_filteredTracks", (IList<PlaylistTrackViewModel>)filteredTracks);
        SetField(vm, "_currentProjectTracks", currentProjectTracks);
        return vm;
    }

    private static PlaylistTrackViewModel CreateTrack(string hash, string artist, string title)
        => new(new PlaylistTrack
        {
            Artist = artist,
            Title = title,
            TrackUniqueHash = hash
        });

    private static void SetBackingField(object target, string fieldName, object? value)
        => SetField(target, fieldName, value);

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static SimilarTracksViewModel CreateSimilarTracksVm()
    {
        var index = new SimilarityIndex(NullLogger<SimilarityIndex>.Instance);
        var db = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));

        return new SimilarTracksViewModel(
            index,
            db,
            NullLogger<SimilarTracksViewModel>.Instance);
    }
}