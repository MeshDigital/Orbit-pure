using System;
using System.Collections.ObjectModel;
using System.Reflection;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class LibraryViewModelPlaylistUpgradeCommandTests
{
    [Fact]
    public void ExecutePlaylistUpgradeCandidate_SelectsTrackAndFocusesUpgradeTab()
    {
        var sut = CreateUninitializedVm();
        var tracks = CreateUninitializedTrackListVm();
        var candidateTrack = CreateTrack("A-Track", "A-Artist");
        var candidate = new PlaylistUpgradeCandidateViewModel(candidateTrack);
        var intelligence = new PlaylistIntelligenceViewModel(sut);

        SetBackingField(sut, "<Tracks>k__BackingField", tracks);
        SetBackingField(sut, "<Intelligence>k__BackingField", intelligence);
        intelligence.FocusLibraryIntelligenceTab("SmartInsert");

        InvokePrivate(sut, "ExecutePlaylistUpgradeCandidate", candidate);

        Assert.Single(tracks.SelectedTracks);
        Assert.Same(candidateTrack, tracks.SelectedTracks[0]);
        Assert.Equal("Upgrade", sut.SelectedLibraryIntelligenceTab);
    }

    private static LibraryViewModel CreateUninitializedVm()
        => (LibraryViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryViewModel));

    private static TrackListViewModel CreateUninitializedTrackListVm()
    {
        var vm = (TrackListViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TrackListViewModel));
        SetField(vm, "_selectedTracks", new ObservableCollection<PlaylistTrackViewModel>());
        SetField(vm, "_currentProjectTracks", new ObservableCollection<PlaylistTrackViewModel>());
        return vm;
    }

    private static PlaylistTrackViewModel CreateTrack(string title, string artist)
        => new PlaylistTrackViewModel(new PlaylistTrack
        {
            Title = title,
            Artist = artist,
            TrackUniqueHash = Guid.NewGuid().ToString("N")
        });

    private static void InvokePrivate(object instance, string methodName, object? parameter)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        method.Invoke(instance, new[] { parameter });
    }

    private static void SetBackingField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }
}